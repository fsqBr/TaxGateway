using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

namespace TaxGateway.GoldenTests;

/// <summary>
/// D-R16 a D-R22: alíquotas claras e seguras — consulta pública por município e data, busca de município pelo nome,
/// propostas validadas contra o IBGE, prévia e recusa, alíquota própria do ente aplicada, cobertura do catálogo e
/// alíquota do cliente marcada como NÃO OFICIAL.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class RatesTests(GoldenFixture env)
{
    private const string SecondOperatorKey = "tg_admin_golden_operador_b_000000000000";

    private async Task<(HttpStatusCode Status, JsonNode? Body)> GetAsync(string path, HttpClient? client = null)
    {
        using var resp = await (client ?? env.Api).GetAsync(new Uri(path, UriKind.Relative));
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> AdminAsync(HttpMethod m, string path, object? body = null, string? key = null, HttpClient? client = null)
    {
        using var req = new HttpRequestMessage(m, path) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Add("X-Admin-Key", key ?? GoldenFixture.OperatorKey);
        using var resp = await (client ?? env.Api).SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private static JsonObject Line(string tributo, string tipo, string esfera, decimal aliquota, string from, string? ente = null, string? forma = null, string? produto = null) => new()
    {
        ["tributo"] = tributo,
        ["tipo"] = tipo,
        ["esfera"] = esfera,
        ["ente_codigo"] = ente,
        ["forma_aplicacao"] = forma,
        ["produto"] = produto,
        ["aliquota"] = aliquota,
        ["valid_from"] = from,
        ["ato_legal"] = "Lei de teste",
        ["fonte"] = tipo == "REFERENCIA" ? "LEI_FEDERAL" : "LEI_ENTE",
    };

    [Fact]
    public async Task Busca_de_municipio_pelo_nome_ignora_acento_e_caixa()
    {
        var r = await GetAsync("/v1/publico/municipios?busca=SAO%20paulo");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var first = r.Body!.AsArray()[0]!;
        Assert.Equal(3550308, first["codigo"]!.GetValue<int>());
        Assert.Equal("São Paulo", first["nome"]!.GetValue<string>());
        Assert.Equal("SP", first["uf"]!.GetValue<string>());
        Assert.All((await GetAsync("/v1/publico/municipios?busca=porto&uf=RO")).Body!.AsArray(), m => Assert.Equal("RO", m!["uf"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync("/v1/publico/municipios?busca=a")).Status);
    }

    [Fact]
    public async Task Consulta_publica_mostra_aliquota_origem_ato_e_provisoria_por_municipio_e_data()
    {
        var r = await GetAsync("/v1/publico/aliquotas?municipio=3550308&data=2027-03-01");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var tributos = r.Body!["tributos"]!.AsArray().ToDictionary(t => t!["tributo"]!.GetValue<string>(), t => t!);
        Assert.Equal(11.2m, tributos["CBS"]["aliquota"]!.GetValue<decimal>());
        Assert.True(tributos["CBS"]["provisoria"]!.GetValue<bool>());
        Assert.Equal("REFERENCIA", tributos["IBS_MUN"]["origem"]!.GetValue<string>());
        Assert.Equal(11.3m, r.Body["total"]!.GetValue<decimal>());
        Assert.Equal("São Paulo", r.Body["municipio"]!["nome"]!.GetValue<string>());

        var teste = await GetAsync("/v1/publico/aliquotas?municipio=3550308&data=2026-06-01");
        Assert.Contains("ano de teste", teste.Body!["observacao"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync("/v1/publico/aliquotas?municipio=9999999")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync("/v1/publico/aliquotas?municipio=3550308&data=01-03-2027")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync("/v1/publico/aliquotas")).Status);

        var tabela = (await GetAsync("/v1/publico/aliquotas/tabela")).Body!;
        Assert.Contains(tabela["referencias"]!.AsArray(), x => x!["tributo"]!.GetValue<string>() == "CBS" && x["vigencia_inicio"]!.GetValue<string>() == "2027-01-01");
    }

    [Fact]
    public async Task Proposta_invalida_e_recusada_com_o_motivo_e_prévia_mostra_o_vigente()
    {
        async Task<string> ProposeError(JsonObject line)
        {
            var r = await AdminAsync(HttpMethod.Post, "/admin/v1/params/rates/changes", new JsonObject { ["justificativa"] = "teste", ["linhas"] = new JsonArray(line) });
            Assert.Equal(HttpStatusCode.BadRequest, r.Status);
            return r.Body!["detail"]!.GetValue<string>();
        }

        Assert.Contains("não existe no cadastro do IBGE", await ProposeError(Line("IBS_MUN", "PROPRIA_ENTE", "MUNICIPIO", 0.1m, "2031-01-01", "1234567", "SUBSTITUICAO")), StringComparison.Ordinal);
        Assert.Contains("7 dígitos", await ProposeError(Line("IBS_MUN", "PROPRIA_ENTE", "MUNICIPIO", 0.1m, "2031-01-01", "42", "SUBSTITUICAO")), StringComparison.Ordinal);
        Assert.Contains("CBS e IS são nacionais", await ProposeError(Line("CBS", "PROPRIA_ENTE", "UF", 1m, "2031-01-01", "42", "ACRESCIMO")), StringComparison.Ordinal);
        Assert.Contains("forma de aplicação", await ProposeError(Line("IBS_UF", "PROPRIA_ENTE", "UF", 1m, "2031-01-01", "42")), StringComparison.Ordinal);
        Assert.Contains("produto", await ProposeError(Line("IS", "REFERENCIA", "UNIAO", 10m, "2031-01-01")), StringComparison.Ordinal);
        Assert.Contains("entre 0% e 100%", await ProposeError(Line("CBS", "REFERENCIA", "UNIAO", 150m, "2031-01-01")), StringComparison.Ordinal);

        // Prévia: a CBS de 2027 já vigente aparece como conflito (precisaria ir em "substitui").
        var preview = await AdminAsync(HttpMethod.Post, "/admin/v1/params/rates/changes/preview",
            new JsonObject { ["linhas"] = new JsonArray(Line("CBS", "REFERENCIA", "UNIAO", 8.8m, "2027-01-01")) });
        Assert.Equal(HttpStatusCode.OK, preview.Status);
        Assert.Equal(1, preview.Body!["conflitos_sem_substituicao"]!.GetValue<int>());
        Assert.Equal(11.2m, preview.Body["itens"]![0]!["vigentes"]![0]!["aliquota"]!.GetValue<decimal>());

        // Proposta válida, recusada com motivo: não pode mais ser aprovada.
        var ok = await AdminAsync(HttpMethod.Post, "/admin/v1/params/rates/changes",
            new JsonObject { ["justificativa"] = "teste de recusa", ["linhas"] = new JsonArray(Line("CBS", "REFERENCIA", "UNIAO", 9m, "2050-01-01")) });
        Assert.Equal(HttpStatusCode.Created, ok.Status);
        var id = ok.Body!["change_id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminAsync(HttpMethod.Post, $"/admin/v1/params/changes/{id}/reject", new { motivo = "x" })).Status);
        Assert.Equal(HttpStatusCode.OK, (await AdminAsync(HttpMethod.Post, $"/admin/v1/params/changes/{id}/reject", new { motivo = "Ato ainda não publicado no DOU" })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await AdminAsync(HttpMethod.Post, $"/admin/v1/params/changes/{id}/reject", new { motivo = "de novo, não pode" })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await AdminAsync(HttpMethod.Post, $"/admin/v1/params/changes/{id}/approve")).Status);
    }

    [Fact]
    public async Task Aliquota_propria_do_municipio_aprovada_por_duas_pessoas_vale_na_consulta()
    {
        await using var other = env.NewApiInstance(new Dictionary<string, string?>
        {
            ["Admin:Operators:1:Name"] = "golden-b",
            ["Admin:Operators:1:KeyHash"] = GoldenFixture.OperatorHash(SecondOperatorKey),
            ["Admin:Operators:1:Scopes:0"] = "admin:fiscal",
        });
        using var otherHttp = other.CreateClient();

        // Cachoeiro de Itapemirim (3201209): nenhum caso golden audita notas deste município.
        var proposed = await AdminAsync(HttpMethod.Post, "/admin/v1/params/rates/changes", new JsonObject
        {
            ["justificativa"] = "Lei municipal de teste",
            ["linhas"] = new JsonArray(Line("IBS_MUN", "PROPRIA_ENTE", "MUNICIPIO", 0.07m, "2027-01-01", "3201209", "SUBSTITUICAO")),
        });
        Assert.Equal(HttpStatusCode.Created, proposed.Status);
        var approved = await AdminAsync(HttpMethod.Post, $"/admin/v1/params/changes/{proposed.Body!["change_id"]!.GetValue<string>()}/approve", key: SecondOperatorKey, client: otherHttp);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body?.ToJsonString());

        JsonNode? mun = null;
        for (var i = 0; i < 40; i++)
        {
            mun = (await GetAsync("/v1/publico/aliquotas?municipio=3201209&data=2027-06-01")).Body!["tributos"]!.AsArray()
                .First(t => t!["tributo"]!.GetValue<string>() == "IBS_MUN");
            if (mun!["origem"]!.GetValue<string>() == "PROPRIA_DO_ENTE")
            {
                break;
            }

            await Task.Delay(250);
        }

        Assert.Equal("PROPRIA_DO_ENTE", mun!["origem"]!.GetValue<string>());
        Assert.Equal(0.07m, mun["aliquota"]!.GetValue<decimal>());
        Assert.Equal("SUBSTITUICAO", mun["forma_aplicacao"]!.GetValue<string>());
    }

    [Fact]
    public async Task Cobertura_do_catalogo_por_tributo()
    {
        var r = await AdminAsync(HttpMethod.Get, "/admin/v1/params/cobertura");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var cbs = r.Body!["tributos"]!.AsArray().First(t => t!["tributo"]!.GetValue<string>() == "CBS")!;
        Assert.True(cbs["dias"]!.GetValue<int>() > 0);
        Assert.NotNull(cbs["coberto_ate"]);
    }

    /// <summary>D-R22: o catálogo de 2026 confere com a base do próprio motor oficial (conferência periódica dos workers).</summary>
    [Fact]
    public async Task Conferencia_catalogo_x_motor_oficial_sem_divergencia()
    {
        var check = env.WorkerServices.GetRequiredService<TaxGateway.Workers.CatalogEngineCheck>();
        var problems = await check.RunOnceAsync(CancellationToken.None);
        Assert.NotNull(problems);
        Assert.Empty(problems!);
    }

    /// <summary>
    /// D-R19: pedido de reprocessamento gerado por mudança retroativa é executado pelos workers só para o ente afetado,
    /// sem cobrança, com aviso no portal.
    /// </summary>
    [Fact]
    public async Task Reprocessamento_automatico_refaz_as_auditorias_do_ente_sem_cobrar()
    {
        var (tenant, key) = await env.CreateTenantWithIdAsync("Reprocesso", GoldenFixture.NewCnpj(), retention: 30);
        var doc = new System.Xml.XmlDocument { PreserveWhitespace = true };
        doc.Load(Path.Combine(GoldenXmlTests.XmlDir, "A-000001-RO.xml"));
        GoldenXmlTests.Rekey(doc, 744_001);
        TaxGateway.TestKit.NfeBuilder.Sign(doc, env.Pki.Leaf);
        using (var req = new HttpRequestMessage(HttpMethod.Post, "/v1/auditoria/xml?direction=OUTBOUND") { Content = new ByteArrayContent(TaxGateway.TestKit.NfeBuilder.Bytes(doc)) })
        {
            req.Headers.Add("X-Api-Key", key);
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            (await env.Api.SendAsync(req)).EnsureSuccessStatusCode();
        }

        var month = TaxGateway.Api.Simulation.SimulationService.BillingMonth(env.Clock.GetUtcNow());
        var requestId = Guid.NewGuid();
        await using (var owner = Npgsql.NpgsqlDataSource.Create(env.ConnectionFor("tg_owner")))
        await using (var conn = await owner.OpenConnectionAsync())
        {
            var change = Guid.NewGuid();
            await using (var pc = new Npgsql.NpgsqlCommand("""
                INSERT INTO param_changes (id, domain, scope, payload, justification, proposed_by, approved_by, status, effective_at, notified_at)
                VALUES ($1, 'RATE_CATALOG', 'GLOBAL', '{}', 'teste de reprocessamento', 'a', 'b', 'APPLIED', now(), now())
                """, conn))
            {
                pc.Parameters.Add(new() { Value = change });
                await pc.ExecuteNonQueryAsync();
            }

            // Como o app.notify_param_changes faz (dono tg_scheduler, política própria em reprocess_requests).
            await using (var role = new Npgsql.NpgsqlCommand("SET ROLE tg_scheduler", conn))
            {
                await role.ExecuteNonQueryAsync();
            }

            await using var cmd = new Npgsql.NpgsqlCommand(
                "INSERT INTO reprocess_requests (id, tenant_id, change_id, from_month, to_month, municipio) VALUES ($1, $2, $3, $4, $4, 1100205)", conn);
            cmd.Parameters.Add(new() { Value = requestId });
            cmd.Parameters.Add(new() { Value = tenant });
            cmd.Parameters.Add(new() { Value = change });
            cmd.Parameters.Add(new() { Value = month, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date });
            await cmd.ExecuteNonQueryAsync();
        }

        await env.WorkerServices.GetRequiredService<TaxGateway.Workers.ReprocessWorker>().RunOnceAsync(CancellationToken.None);
        var db = env.Services.GetRequiredService<TaxGateway.Infrastructure.Persistence.ITenantDb>();
        var (status, reprocessados, billable) = await db.InTenantAsync(new TaxGateway.Core.Tenancy.TenantId(tenant), async (s, ct) =>
        {
            await using var q = s.CreateCommand("""
                SELECT r.status, (r.resumo->>'reprocessados')::int,
                       (SELECT count(*) FROM fiscal_documents f WHERE f.reprocess_of IS NOT NULL AND f.is_billable)
                  FROM reprocess_requests r WHERE r.id = $1
                """);
            q.Parameters.Add(new() { Value = requestId });
            await using var r = await q.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return (r.GetString(0), r.GetInt32(1), r.GetInt64(2));
        }, CancellationToken.None);
        Assert.Equal("CONCLUIDO", status);
        Assert.Equal(1, reprocessados);
        Assert.Equal(0, billable);
        using var avisos = new HttpRequestMessage(HttpMethod.Get, "/v1/avisos");
        avisos.Headers.Add("X-Api-Key", key);
        var body = JsonNode.Parse(await (await env.Api.SendAsync(avisos)).Content.ReadAsStringAsync())!;
        Assert.Contains(body["avisos"]!.AsArray(), a => a!["titulo"]!.GetValue<string>().Contains("recalculadas", StringComparison.Ordinal));
    }

}
