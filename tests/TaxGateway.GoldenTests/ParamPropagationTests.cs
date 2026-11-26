using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

using Xunit.Abstractions;

namespace TaxGateway.GoldenTests;

/// <summary>
/// SLO do §16.3 (D-H02): mudança de parâmetro aprovada numa instância chega às OUTRAS em ≤ 5 s. A mudança usada é uma
/// linha de CBS para 2040 (não afeta os cálculos de 2027 dos demais testes), mas muda o conteúdo e, portanto, o snapshot.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class ParamPropagationTests(GoldenFixture env, ITestOutputHelper output)
{
    private const string SecondOperatorKey = "tg_admin_golden_operador_b_000000000000";

    [Fact]
    public async Task Aprovacao_em_uma_instancia_propaga_para_outra_em_ate_5s()
    {
        await using var other = env.NewApiInstance(new Dictionary<string, string?>
        {
            ["Admin:Operators:1:Name"] = "golden-b",
            ["Admin:Operators:1:KeyHash"] = GoldenFixture.OperatorHash(SecondOperatorKey),
            ["Admin:Operators:1:Scopes:0"] = "admin:fiscal",
        });
        using var otherHttp = other.CreateClient();
        Assert.True((await otherHttp.GetAsync(new Uri("/health/live", UriKind.Relative))).IsSuccessStatusCode);

        var before = await SnapshotIdAsync();

        // Operador A propõe na instância principal; operador B aprova na outra (duas pessoas, D-204).
        using var propose = new HttpRequestMessage(HttpMethod.Post, "/admin/v1/params/rates/changes")
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["justificativa"] = "teste de propagação (linha futura sem efeito nos casos de 2027)",
                ["linhas"] = new JsonArray(new JsonObject
                {
                    ["tributo"] = "CBS",
                    ["tipo"] = "REFERENCIA",
                    ["esfera"] = "UNIAO",
                    ["aliquota"] = 9.9,
                    ["valid_from"] = "2040-01-01",
                    ["valid_to"] = "2041-01-01",
                    ["ato_legal"] = "teste",
                    ["fonte"] = "LEI_FEDERAL",
                }),
            }),
        };
        propose.Headers.Add("X-Admin-Key", GoldenFixture.OperatorKey);
        using var proposed = await env.Api.SendAsync(propose);
        var proposedText = await proposed.Content.ReadAsStringAsync();
        Assert.True(proposed.StatusCode == HttpStatusCode.Created, proposedText);
        var changeId = JsonNode.Parse(proposedText)!["change_id"]!.GetValue<string>();

        using var approve = new HttpRequestMessage(HttpMethod.Post, $"/admin/v1/params/changes/{changeId}/approve");
        approve.Headers.Add("X-Admin-Key", SecondOperatorKey);
        var sw = Stopwatch.StartNew();
        using var approved = await otherHttp.SendAsync(approve);
        Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync());

        long now;
        while ((now = await SnapshotIdAsync()) == before && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(50);
        }

        sw.Stop();
        output.WriteLine($"snapshot {before} → {now} na instância principal em {sw.Elapsed.TotalMilliseconds:F0} ms");
        Assert.NotEqual(before, now);
        Assert.True(sw.Elapsed <= TimeSpan.FromSeconds(5), $"propagação levou {sw.Elapsed.TotalMilliseconds:F0} ms");
    }

    /// <summary>Id do snapshot que a instância principal usa, lido de uma simulação real (mesma rota do cliente).</summary>
    private async Task<long> SnapshotIdAsync()
    {
        var json = """
            {"cnpj_emitente":"11222333000181","regime_tributario":"REGIME_REGULAR","data_fato_gerador":"2027-06-15T14:00:00-03:00",
             "municipio_ibge":3550308,"uf":"SP","itens":[{"numero":1,"cst_ibs_cbs":"000","c_class_trib":"000001",
             "valor_base_calculo":1000,"ncm":"84713012","quantidade":1,"unidade":"UN"}]}
            """;
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/calculo/simular") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Api-Key", env.ApiKey);
        using var resp = await env.Api.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, text);
        return JsonNode.Parse(text)!["param_snapshot_id"]!.GetValue<long>();
    }
}
