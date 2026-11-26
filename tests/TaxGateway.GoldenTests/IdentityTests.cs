using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Xunit.Abstractions;

namespace TaxGateway.GoldenTests;

/// <summary>
/// Fase 8 (SDD §3.1, §3.3, §6.4, §12): client credentials, revogação, rotação de chave sem 401 numa frota mista,
/// suspensão de tenant efetiva em menos de 5 s em OUTRA instância, e endpoints OEM / X-On-Behalf-Of-CNPJ.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class IdentityTests(GoldenFixture env, ITestOutputHelper output)
{
    [Fact]
    public async Task Client_credentials_emite_jwt_assinado_e_revogacao_do_cliente_vale_na_hora()
    {
        var (tenant, _) = await env.CreateTenantWithIdAsync("OAuth A", NewCnpj());
        var (clientId, secret) = await CreateClientAsync(env.Api, tenant, ["simulacao:write", "usage:read"]);

        // Segredo errado e escopo não concedido.
        var bad = await TokenAsync(env.Api, clientId, secret + "x");
        Assert.Equal("invalid_client", bad.Body!["error"]!.GetValue<string>());
        // D-P05: a descrição vai para o header WWW-Authenticate; fora do ASCII o Kestrel lançava exceção (500 em produção,
        // o TestServer não valida). Toda rejeição precisa ser 4xx com corpo e header só ASCII.
        foreach (var (status, body, wwwAuth) in new[] { bad, await TokenAsync(env.Api, "tgc_naoexiste", "x"), await TokenAsync(env.Api, clientId, secret, "keys:manage") })
        {
            Assert.InRange((int)status, 400, 499);
            Assert.All(body!["error_description"]?.GetValue<string>() ?? "", c => Assert.InRange(c, ' ', '~'));
            Assert.All(wwwAuth, c => Assert.InRange(c, ' ', '~'));
        }
        var badScope = await TokenAsync(env.Api, clientId, secret, "keys:manage");
        Assert.Equal("invalid_scope", badScope.Body!["error"]!.GetValue<string>());

        var ok = await TokenAsync(env.Api, clientId, secret);
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        var token = ok.Body!["access_token"]!.GetValue<string>();
        Assert.InRange(ok.Body["expires_in"]!.GetValue<long>(), 800, 900);

        // JWT assinado (não JWE), typ at+jwt, claims do §3.1, sem limites no token.
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        var header = JsonNode.Parse(B64(parts[0]))!;
        var payload = JsonNode.Parse(B64(parts[1]))!;
        Assert.Equal("at+jwt", header["typ"]!.GetValue<string>());
        Assert.Equal("RS256", header["alg"]!.GetValue<string>());
        Assert.Equal(clientId, payload["sub"]!.GetValue<string>());
        Assert.Equal(tenant.ToString(), payload["tid"]!.GetValue<string>());
        Assert.Equal("OEM", payload["plan"]!.GetValue<string>());
        Assert.NotNull(payload["jti"]);
        Assert.Null(payload["rpm"]);

        Assert.Equal(HttpStatusCode.OK, await GetUsageAsync(env.Api, bearer: token));
        // Escopo ausente no token: cnpjs:manage.
        using (var req = new HttpRequestMessage(HttpMethod.Get, "/v1/oem/cnpjs"))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(HttpStatusCode.Forbidden, (await env.Api.SendAsync(req)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await GetUsageAsync(env.Api, bearer: token[..^4] + "AAAA"));

        // Revogação por jti.
        var second = (await TokenAsync(env.Api, clientId, secret)).Body!["access_token"]!.GetValue<string>();
        var jti = JsonNode.Parse(B64(second.Split('.')[1]))!["jti"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, await AdminAsync(env.Api, HttpMethod.Post, "/admin/v1/oauth/revoked-tokens",
            new JsonObject { ["jti"] = jti, ["expira_em"] = DateTimeOffset.UtcNow.AddHours(1) }));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetUsageAsync(env.Api, bearer: second));
        Assert.Equal(HttpStatusCode.OK, await GetUsageAsync(env.Api, bearer: token));

        // Revogação do cliente: o token já emitido para e não sai token novo.
        Assert.Equal(HttpStatusCode.NoContent, await AdminAsync(env.Api, HttpMethod.Delete, $"/admin/v1/oauth-clients/{clientId}", null));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetUsageAsync(env.Api, bearer: token));
        Assert.Equal("invalid_client", (await TokenAsync(env.Api, clientId, secret)).Body!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Rotacao_de_chave_sem_401_com_frota_mista()
    {
        using var keyA = RSA.Create(2048);
        using var keyB = RSA.Create(2048);
        string pemA = keyA.ExportPkcs8PrivateKeyPem(), pemB = keyB.ExportPkcs8PrivateKeyPem();

        // Etapa 1 do runbook: B publicada só para validar (A assina). Etapa 2: B assina, A segue validando.
        await using var antiga = env.NewApiInstance(Keys(("kA", pemA), ("kB", pemB)));
        await using var nova = env.NewApiInstance(Keys(("kB", pemB), ("kA", pemA)));
        using var httpAntiga = antiga.CreateClient();
        using var httpNova = nova.CreateClient();

        var (tenant, _) = await env.CreateTenantWithIdAsync("OAuth rotação", NewCnpj());
        var (clientId, secret) = await CreateClientAsync(env.Api, tenant, ["usage:read"]);
        var tokenA = (await TokenAsync(httpAntiga, clientId, secret)).Body!["access_token"]!.GetValue<string>();
        var tokenB = (await TokenAsync(httpNova, clientId, secret)).Body!["access_token"]!.GetValue<string>();
        Assert.Equal("kA", JsonNode.Parse(B64(tokenA.Split('.')[0]))!["kid"]!.GetValue<string>());
        Assert.Equal("kB", JsonNode.Parse(B64(tokenB.Split('.')[0]))!["kid"]!.GetValue<string>());

        // Qualquer token vale em qualquer instância durante a troca.
        foreach (var http in new[] { httpAntiga, httpNova })
        {
            Assert.Equal(HttpStatusCode.OK, await GetUsageAsync(http, bearer: tokenA));
            Assert.Equal(HttpStatusCode.OK, await GetUsageAsync(http, bearer: tokenB));
        }

        // O JWKS publica as duas chaves.
        var jwks = JsonNode.Parse(await httpNova.GetStringAsync(new Uri("/.well-known/jwks.json", UriKind.Relative)))!;
        var kids = jwks["keys"]!.AsArray().Select(k => k!["kid"]!.GetValue<string>()).ToHashSet();
        Assert.Superset(new HashSet<string> { "kA", "kB" }, kids);
    }

    [Fact]
    public async Task Suspensao_de_tenant_vale_em_outra_instancia_em_menos_de_5s()
    {
        var (tenant, apiKey) = await env.CreateTenantWithIdAsync("Suspensão", NewCnpj());
        var (clientId, secret) = await CreateClientAsync(env.Api, tenant, ["usage:read"]);
        var token = (await TokenAsync(env.Api, clientId, secret)).Body!["access_token"]!.GetValue<string>();

        await using var outra = env.NewApiInstance(new Dictionary<string, string?>());
        using var http = outra.CreateClient();
        // Caches da outra instância aquecidos (API key no L1, perfil do tenant).
        Assert.Equal(HttpStatusCode.OK, await GetUsageAsync(http, apiKey: apiKey));
        Assert.Equal(HttpStatusCode.OK, await GetUsageAsync(http, bearer: token));

        var sw = Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.OK, await AdminAsync(env.Api, HttpMethod.Post, $"/admin/v1/tenants/{tenant}/suspend", null));
        while ((await GetUsageAsync(http, apiKey: apiKey) != HttpStatusCode.Forbidden
                || await GetUsageAsync(http, bearer: token) == HttpStatusCode.OK) && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(50);
        }

        sw.Stop();
        output.WriteLine($"suspensão efetiva na outra instância em {sw.Elapsed.TotalMilliseconds:F0} ms");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"suspensão levou {sw.Elapsed.TotalMilliseconds:F0} ms");
        Assert.Equal("invalid_client", (await TokenAsync(env.Api, clientId, secret)).Body!["error"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.OK, await AdminAsync(env.Api, HttpMethod.Post, $"/admin/v1/tenants/{tenant}/reactivate", null));
        var back = Stopwatch.StartNew();
        while (await GetUsageAsync(http, apiKey: apiKey) != HttpStatusCode.OK && back.Elapsed < TimeSpan.FromSeconds(40))
        {
            await Task.Delay(100);
        }

        Assert.Equal(HttpStatusCode.OK, await GetUsageAsync(http, bearer: (await TokenAsync(env.Api, clientId, secret)).Body!["access_token"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Oem_cnpjs_on_behalf_of_e_uso_por_cnpj()
    {
        var (_, apiKey) = await env.CreateTenantWithIdAsync("OEM manual", NewCnpj(), autoProvision: false);
        var cliente = NewCnpj();
        var outro = NewCnpj();

        Assert.Equal(HttpStatusCode.Forbidden, (await SimulateAsync(apiKey, cliente, null)).Status);   // não cadastrado
        Assert.Equal(HttpStatusCode.Created, await SendAsync(apiKey, HttpMethod.Post, "/v1/oem/cnpjs", new JsonObject { ["cnpj"] = cliente }));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(apiKey, HttpMethod.Post, "/v1/oem/cnpjs", new JsonObject { ["cnpj"] = cliente }));

        var mismatch = await SimulateAsync(apiKey, cliente, outro);
        Assert.Equal(HttpStatusCode.Forbidden, mismatch.Status);
        Assert.Equal("ERR_CNPJ_SCOPE", mismatch.Body!["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await SimulateAsync(apiKey, cliente, "123")).Status);
        Assert.Equal(HttpStatusCode.OK, (await SimulateAsync(apiKey, cliente, cliente)).Status);

        // O ledger da simulação é gravado em lote (gravação assíncrona, D-213): espera até 10 s.
        JsonNode? row = null;
        for (var i = 0; i < 100 && row is null; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/oem/usage?month=" + env.Clock.GetUtcNow().ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
            req.Headers.Add("X-Api-Key", apiKey);
            var usage = JsonNode.Parse(await (await env.Api.SendAsync(req)).Content.ReadAsStringAsync())!;
            row = usage["por_cnpj"]!.AsArray().FirstOrDefault(r => r!["cnpj"]!.GetValue<string>() == cliente);
            if (row is null)
            {
                await Task.Delay(100);
            }
        }

        Assert.NotNull(row);
        Assert.Equal(1, row["simulacoes"]!.GetValue<long>());

        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(apiKey, HttpMethod.Delete, $"/v1/oem/cnpjs/{cliente}", null));
        Assert.Equal(HttpStatusCode.NotFound, await SendAsync(apiKey, HttpMethod.Delete, $"/v1/oem/cnpjs/{cliente}", null));
        var after = await SimulateAsync(apiKey, cliente, cliente);
        Assert.Equal("ERR_CNPJ_NOT_PROVISIONED", after.Body!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Auto_provisionamento_nao_reativa_cnpj_desativado()
    {
        var (_, apiKey) = await env.CreateTenantWithIdAsync("OEM auto", NewCnpj(), autoProvision: true);
        var cliente = NewCnpj();
        Assert.Equal(HttpStatusCode.OK, (await SimulateAsync(apiKey, cliente, cliente)).Status);   // auto-provisionado
        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(apiKey, HttpMethod.Delete, $"/v1/oem/cnpjs/{cliente}", null));
        Assert.Equal(HttpStatusCode.Forbidden, (await SimulateAsync(apiKey, cliente, null)).Status);
        Assert.Equal(HttpStatusCode.Created, await SendAsync(apiKey, HttpMethod.Post, "/v1/oem/cnpjs", new JsonObject { ["cnpj"] = cliente }));
        Assert.Equal(HttpStatusCode.OK, (await SimulateAsync(apiKey, cliente, null)).Status);
    }

    // ---------- apoio ----------

    private static Dictionary<string, string?> Keys(params (string Kid, string Pem)[] keys)
    {
        var d = new Dictionary<string, string?>();
        for (var i = 0; i < keys.Length; i++)
        {
            d[$"Identity:SigningKeys:{i}:Kid"] = keys[i].Kid;
            d[$"Identity:SigningKeys:{i}:Pem"] = keys[i].Pem;
        }

        return d;
    }

    private static async Task<(string ClientId, string Secret)> CreateClientAsync(HttpClient api, Guid tenant, string[] scopes)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/admin/v1/tenants/{tenant}/oauth-clients")
        {
            Content = JsonContent.Create(new JsonObject { ["nome"] = "teste", ["scopes"] = new JsonArray([.. scopes.Select(s => (JsonNode)s)]) }),
        };
        req.Headers.Add("X-Admin-Key", GoldenFixture.OperatorKey);
        using var resp = await api.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = (await resp.Content.ReadFromJsonAsync<JsonObject>())!;
        return (body["client_id"]!.GetValue<string>(), body["client_secret"]!.GetValue<string>());
    }

    private static async Task<(HttpStatusCode Status, JsonNode? Body, string WwwAuthenticate)> TokenAsync(HttpClient http, string clientId, string secret, string? scope = null)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secret };
        if (scope is not null)
        {
            form["scope"] = scope;
        }

        using var content = new FormUrlEncodedContent(form);
        using var resp = await http.PostAsync(new Uri("/oauth/token", UriKind.Relative), content);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text), resp.Headers.WwwAuthenticate.ToString());
    }

    private static async Task<HttpStatusCode> GetUsageAsync(HttpClient http, string? bearer = null, string? apiKey = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/usage");
        if (bearer is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (apiKey is not null)
        {
            req.Headers.Add("X-Api-Key", apiKey);
        }

        using var resp = await http.SendAsync(req);
        return resp.StatusCode;
    }

    private static async Task<HttpStatusCode> AdminAsync(HttpClient api, HttpMethod method, string path, JsonObject? body)
    {
        using var req = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Add("X-Admin-Key", GoldenFixture.OperatorKey);
        using var resp = await api.SendAsync(req);
        return resp.StatusCode;
    }

    private async Task<HttpStatusCode> SendAsync(string apiKey, HttpMethod method, string path, JsonObject? body)
    {
        using var req = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Add("X-Api-Key", apiKey);
        using var resp = await env.Api.SendAsync(req);
        return resp.StatusCode;
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> SimulateAsync(string apiKey, string cnpj, string? onBehalfOf)
    {
        var json = new JsonObject
        {
            ["cnpj_emitente"] = cnpj,
            ["regime_tributario"] = "REGIME_REGULAR",
            ["data_fato_gerador"] = "2027-06-15T14:00:00-03:00",
            ["municipio_ibge"] = 3550308,
            ["uf"] = "SP",
            ["itens"] = new JsonArray(new JsonObject
            {
                ["numero"] = 1,
                ["cst_ibs_cbs"] = "000",
                ["c_class_trib"] = "000001",
                ["valor_base_calculo"] = 1000,
                ["ncm"] = "84713012",
                ["quantidade"] = 1,
                ["unidade"] = "UN",
            }),
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/calculo/simular")
        {
            Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Api-Key", apiKey);
        if (onBehalfOf is not null)
        {
            req.Headers.Add("X-On-Behalf-Of-CNPJ", onBehalfOf);
        }

        using var resp = await env.Api.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private static string B64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Encoding.UTF8.GetString(Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=')));
    }

    private static string NewCnpj() => GoldenFixture.NewCnpj();
}
