using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;

using TaxGateway.Api.Http;
using TaxGateway.TestKit;

using Xunit.Abstractions;

namespace TaxGateway.GoldenTests;

/// <summary>
/// Contrato da documentação (D-W08): chama cada rota de verdade e compara com o exemplo publicado em /docs.
/// Regra: todo campo do exemplo existe na resposta real (a documentação não inventa campo) e todo campo de primeiro
/// nível da resposta real aparece no exemplo (a documentação não esconde campo). Nulos omitidos na resposta valem.
/// Também confere que o código de sucesso documentado é o devolvido.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class DocsContractTests(GoldenFixture env, ITestOutputHelper output)
{
    [Fact]
    public async Task Exemplos_da_documentacao_batem_com_as_respostas_reais()
    {
        var (tenant, key) = await env.CreateTenantWithIdAsync("Contrato docs", GoldenFixture.NewCnpj());
        var me = await Call(key, null, "GET", "/v1/me");
        var root = me.Body!["cnpj_raiz"]!.GetValue<string>();

        var checks = new List<(string Key, HttpStatusCode Status, JsonNode? Body)>
        {
            ("GET /v1/me", me.Status, me.Body),
            await Run(null, null, "GET /version"),
            await Run(key, null, "GET /v1/api-keys"),
            await Run(key, null, "POST /v1/api-keys", "/v1/api-keys", new JsonObject { ["nome"] = "doc", ["scopes"] = new JsonArray("usage:read"), ["expira_em"] = "2030-12-31T23:59:59Z" }),
            await Run(key, null, "GET /v1/usage"),
            await Run(key, null, "POST /v1/oem/cnpjs", "/v1/oem/cnpjs", new JsonObject { ["cnpj"] = "11444777000161" }),
            await Run(key, null, "GET /v1/oem/cnpjs"),
            await Run(key, null, "GET /v1/oem/usage"),
            await Run(key, null, "GET /v1/auditoria/lotes"),
            await Run(key, null, "GET /v1/classificacao", "/v1/classificacao?ncm=22021000&data=2027-01-01"),
            await Run(key, null, "GET /v1/avisos"),
            await Run(key, null, "GET /v1/primeiros-passos"),
            await Run(key, null, "POST /v1/titulares/localizar", "/v1/titulares/localizar", new JsonObject { ["cpf"] = "52998224725" }),
            await Run(key, null, "POST /v1/calculo/simular", "/v1/calculo/simular", JsonNode.Parse(OpenApiDocs.Catalog["POST /v1/calculo/simular"].Body!.Replace("11222333000181", root, StringComparison.Ordinal))!.AsObject()),
            await Run(key, null, "POST /v1/auditoria/xml", "/v1/auditoria/xml?direction=OUTBOUND", null, Xml()),
            await Run(key, null, "GET /v1/auditoria/documentos"),
            await Run(key, null, "GET /v1/auditoria/resumo"),
            await Run(null, GoldenFixture.OperatorKey, "GET /admin/v1/me"),
            await Run(null, GoldenFixture.OperatorKey, "GET /admin/v1/plans"),
            await Run(null, GoldenFixture.OperatorKey, "GET /admin/v1/tenants", "/admin/v1/tenants?busca=Contrato%20docs"),
            await Run(null, GoldenFixture.OperatorKey, "GET /admin/v1/params/rates"),
            await Run(null, GoldenFixture.OperatorKey, "GET /admin/v1/params/changes"),
            await Run(null, GoldenFixture.OperatorKey, "POST /admin/v1/tenants/{id}/suspend", $"/admin/v1/tenants/{tenant}/suspend"),
        };

        var problems = new List<string>();
        foreach (var (k, status, body) in checks)
        {
            var doc = OpenApiDocs.Catalog[k];
            if ((int)status != doc.Status)
            {
                problems.Add($"{k}: documentado {doc.Status}, real {(int)status} {body?.ToJsonString()[..Math.Min(200, body.ToJsonString().Length)]}");
                continue;
            }

            if (doc.Response is not null && body is not null)
            {
                Compare(k, JsonNode.Parse(doc.Response), body, topLevel: true, problems);
            }
        }

        foreach (var p in problems)
        {
            output.WriteLine(p);
        }

        Assert.Empty(problems);
        Assert.Equal(checks.Count, checks.Select(c => c.Key).Distinct().Count());
    }

    /// <summary>Toda rota do documento público tem entrada no catálogo (resumo, grupo e código corretos).</summary>
    [Fact]
    public async Task Toda_rota_publica_esta_no_catalogo()
    {
        var v1 = JsonNode.Parse(await env.Api.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative)))!;
        var missing = v1["paths"]!.AsObject()
            .SelectMany(p => p.Value!.AsObject().Select(m => $"{m.Key.ToUpperInvariant()} {p.Key}"))
            .Where(k => k != "POST /oauth/token" && !OpenApiDocs.Catalog.ContainsKey(k)).ToList();
        Assert.Empty(missing);
        var tags = v1["tags"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain(tags, t => t.EndsWith("Endpoints", StringComparison.Ordinal) || t.Contains('.', StringComparison.Ordinal));
        Assert.All(v1["tags"]!.AsArray(), t => Assert.False(string.IsNullOrEmpty(t!["description"]?.GetValue<string>())));
    }

    private static void Compare(string where, JsonNode? example, JsonNode? actual, bool topLevel, List<string> problems)
    {
        if (example is JsonObject e && actual is JsonObject a)
        {
            foreach (var (name, value) in e)
            {
                if (!a.ContainsKey(name))
                {
                    if (value is not null)
                    {
                        problems.Add($"{where}: exemplo tem '{name}', a resposta real não");
                    }

                    continue;
                }

                Compare($"{where}.{name}", value, a[name], false, problems);
            }

            if (topLevel)
            {
                foreach (var name in a.Select(x => x.Key).Where(n => !e.ContainsKey(n)))
                {
                    problems.Add($"{where}: resposta real tem '{name}', o exemplo não");
                }
            }
        }
        else if (example is JsonArray ea && actual is JsonArray aa && ea.Count > 0 && aa.Count > 0)
        {
            Compare($"{where}[0]", ea[0], aa[0], topLevel, problems);
        }
    }

    private Task<(string, HttpStatusCode, JsonNode?)> Run(string? apiKey, string? adminKey, string key, string? path = null, JsonObject? body = null, byte[]? xml = null) =>
        Call(apiKey, adminKey, key.Split(' ')[0], path ?? key.Split(' ')[1], body, xml).ContinueWith(t => (key, t.Result.Status, t.Result.Body), TaskScheduler.Default);

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Call(string? apiKey, string? adminKey, string method, string path, JsonObject? body = null, byte[]? xml = null)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }
        else if (xml is not null)
        {
            req.Content = new ByteArrayContent(xml);
            req.Content.Headers.ContentType = new("application/xml");
        }

        if (apiKey is not null)
        {
            req.Headers.Add("X-Api-Key", apiKey);
        }

        if (adminKey is not null)
        {
            req.Headers.Add("X-Admin-Key", adminKey);
        }

        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var resp = await env.Api.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private byte[] Xml()
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(Path.Combine(GoldenXmlTests.XmlDir, "A-000001-RO.xml"));
        GoldenXmlTests.Rekey(doc, 975_001);   // faixa 975_xxx reservada a este teste
        NfeBuilder.Sign(doc, env.Pki.Leaf);
        return Encoding.UTF8.GetBytes(doc.OuterXml);
    }
}
