using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace TaxGateway.GoldenTests;

/// <summary>Endpoints de apoio às telas e documentação da API (D-W01 a D-W04).</summary>
[Collection(GoldenCollection.Name)]
public sealed class UiSupportTests(GoldenFixture env)
{
    [Fact]
    public async Task Documentacao_publica_sem_superficie_admin()
    {
        var v1 = JsonNode.Parse(await env.Api.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative)))!;
        var paths = v1["paths"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Contains("/v1/calculo/simular", paths);
        Assert.Contains("/oauth/token", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("/admin", StringComparison.Ordinal));
        Assert.Equal(["ApiKey", "OAuth2"], v1["components"]!["securitySchemes"]!.AsObject().Select(x => x.Key).Order());   // AdminKey só no documento admin

        // Toda operação tem resumo; corpo JSON documentado com exemplo; escopo exigido aparece na descrição.
        foreach (var (path, item) in v1["paths"]!.AsObject())
        {
            foreach (var (method, op) in item!.AsObject())
            {
                Assert.False(string.IsNullOrEmpty(op!["summary"]?.GetValue<string>()), $"{method} {path} sem resumo");
            }
        }

        var simular = v1["paths"]!["/v1/calculo/simular"]!["post"]!;
        Assert.NotNull(simular["requestBody"]!["content"]!["application/json"]!["example"]);
        Assert.Contains("simulacao:write", simular["description"]!.GetValue<string>(), StringComparison.Ordinal);

        // Fora de Development o documento admin não é publicado; a referência navegável responde.
        Assert.Equal(HttpStatusCode.NotFound, (await env.Api.GetAsync(new Uri("/openapi/admin.json", UriKind.Relative))).StatusCode);
        using var docs = await env.Api.GetAsync(new Uri("/docs/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, docs.StatusCode);
    }

    [Fact]
    public async Task Me_api_keys_e_lotes_do_proprio_tenant()
    {
        var (tenant, key) = await env.CreateTenantWithIdAsync("Portal", GoldenFixture.NewCnpj());
        var me = await GetAsync("/v1/me", key);
        Assert.Equal(tenant.ToString(), me["tenant_id"]!.GetValue<string>());
        Assert.Equal("Portal", me["nome"]!.GetValue<string>());
        Assert.Equal("api_key", me["credencial"]!.GetValue<string>());

        var keys = (await GetAsync("/v1/api-keys", key))["api_keys"]!.AsArray();
        var only = Assert.Single(keys)!;
        Assert.True(only["atual"]!.GetValue<bool>());
        Assert.Null(only["key_hash"]);
        Assert.DoesNotContain("tg_", only.ToJsonString().Replace(only["prefixo"]!.GetValue<string>(), "", StringComparison.Ordinal), StringComparison.Ordinal);

        Assert.Empty((await GetAsync("/v1/auditoria/lotes", key))["lotes"]!.AsArray());

        // Outro tenant não aparece (RLS).
        var (_, other) = await env.CreateTenantWithIdAsync("Portal B", GoldenFixture.NewCnpj());
        Assert.DoesNotContain((await GetAsync("/v1/api-keys", other))["api_keys"]!.AsArray(), k => k!["id"]!.GetValue<string>() == only["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Admin_lista_tenants_sem_segredos()
    {
        var (tenant, _) = await env.CreateTenantWithIdAsync("Backoffice lista", GoldenFixture.NewCnpj());
        using (var req = new HttpRequestMessage(HttpMethod.Post, $"/admin/v1/tenants/{tenant}/oauth-clients") { Content = JsonContent.Create(new JsonObject { ["nome"] = "erp" }) })
        {
            req.Headers.Add("X-Admin-Key", GoldenFixture.OperatorKey);
            Assert.Equal(HttpStatusCode.Created, (await env.Api.SendAsync(req)).StatusCode);
        }

        using var list = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/tenants?busca=Backoffice%20lista");
        list.Headers.Add("X-Admin-Key", GoldenFixture.OperatorKey);
        var body = JsonNode.Parse(await (await env.Api.SendAsync(list)).Content.ReadAsStringAsync())!;
        var t = Assert.Single(body["tenants"]!.AsArray())!;
        Assert.Equal("ACTIVE", t["situacao"]!.GetValue<string>());
        var client = Assert.Single(t["clientes_oauth"]!.AsArray())!;
        Assert.Equal("erp", client["nome"]!.GetValue<string>());
        Assert.DoesNotContain("secret", t.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        using var plans = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/plans");
        plans.Headers.Add("X-Admin-Key", GoldenFixture.OperatorKey);
        var p = JsonNode.Parse(await (await env.Api.SendAsync(plans)).Content.ReadAsStringAsync())!;
        Assert.Contains(p["planos"]!.AsArray(), x => x!["codigo"]!.GetValue<string>() == "OEM");

        using var noKey = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/tenants");
        Assert.Equal(HttpStatusCode.Unauthorized, (await env.Api.SendAsync(noKey)).StatusCode);
    }

    private async Task<JsonNode> GetAsync(string path, string key)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("X-Api-Key", key);
        using var resp = await env.Api.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"{path}: {(int)resp.StatusCode} {text}");
        return JsonNode.Parse(text)!;
    }
}
