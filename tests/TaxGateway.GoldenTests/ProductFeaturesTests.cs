using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace TaxGateway.GoldenTests;

/// <summary>D-R26/D-R27: apuração do mês, fornecedores, exportação, clientes OAuth pelo portal e registro de chamadas.</summary>
[Collection(GoldenCollection.Name)]
public sealed class ProductFeaturesTests(GoldenFixture env)
{
    private static readonly string[] AdminScope = ["admin:security"];
    private static readonly string[] UsageScope = ["usage:read"];

    private async Task<(HttpStatusCode Status, JsonNode? Body, string Text)> ApiAsync(string key, HttpMethod m, string path, object? body = null)
    {
        using var req = new HttpRequestMessage(m, path) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Add("X-Api-Key", key);
        using var resp = await env.Api.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        JsonNode? node = null;
        if (text.StartsWith('{') || text.StartsWith('['))
        {
            node = JsonNode.Parse(text);
        }

        return (resp.StatusCode, node, text);
    }

    [Fact]
    public async Task Apuracao_do_mes_soma_as_saidas_auditadas_pela_data_de_emissao()
    {
        var (_, key) = await env.CreateTenantWithIdAsync("Apuracao", GoldenFixture.NewCnpj());
        var doc = new System.Xml.XmlDocument { PreserveWhitespace = true };
        doc.Load(Path.Combine(GoldenXmlTests.XmlDir, "A-000001-RO.xml"));
        GoldenXmlTests.Rekey(doc, 746_001);
        TaxGateway.TestKit.NfeBuilder.Sign(doc, env.Pki.Leaf);
        using (var req = new HttpRequestMessage(HttpMethod.Post, "/v1/auditoria/xml?direction=OUTBOUND") { Content = new ByteArrayContent(TaxGateway.TestKit.NfeBuilder.Bytes(doc)) })
        {
            req.Headers.Add("X-Api-Key", key);
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            (await env.Api.SendAsync(req)).EnsureSuccessStatusCode();
        }

        var r = await ApiAsync(key, HttpMethod.Get, "/v1/apuracao?competencia=2027-06");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var saidas = r.Body!["saidas"]!;
        Assert.True(saidas["documentos"]!.GetValue<int>() > 0, r.Text);
        Assert.True(saidas["cbs"]!.GetValue<decimal>() > 0);
        Assert.Equal(saidas["cbs"]!.GetValue<decimal>() + saidas["ibs"]!.GetValue<decimal>(), r.Body["saldo"]!["debitos"]!.GetValue<decimal>());
        Assert.NotEmpty(r.Body["por_municipio"]!.AsArray());
        Assert.NotEmpty(r.Body["por_classificacao"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await ApiAsync(env.ApiKey, HttpMethod.Get, "/v1/apuracao?competencia=06-2027")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ApiAsync(env.ApiKey, HttpMethod.Get, "/v1/apuracao?competencia=2020-01")).Status);

        var vazio = await ApiAsync(key, HttpMethod.Get, "/v1/apuracao?competencia=2031-01");
        Assert.Equal(0, vazio.Body!["saidas"]!["documentos"]!.GetValue<int>());

        var forn = await ApiAsync(env.ZeroRetentionApiKey, HttpMethod.Get, "/v1/apuracao/fornecedores?competencia=2027-06");
        Assert.Equal(HttpStatusCode.OK, forn.Status);
        Assert.NotNull(forn.Body!["fornecedores"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await ApiAsync(env.ApiKey, HttpMethod.Get, "/v1/apuracao/fornecedores")).Status);

        var csv = await ApiAsync(key, HttpMethod.Get, "/v1/apuracao/exportar?competencia=2027-06");
        Assert.Equal(HttpStatusCode.OK, csv.Status);
        Assert.StartsWith("tipo;chave_acesso;emissao", csv.Text.TrimStart('﻿'), StringComparison.Ordinal);   // o BOM (Excel) some na leitura como texto
        Assert.Contains(";saida;", "\n" + csv.Text.Replace("\r\n", "\n;", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await ApiAsync(env.ApiKey, HttpMethod.Get, "/v1/apuracao/exportar?competencia=x")).Status);
    }

    [Fact]
    public async Task Cliente_OAuth_criado_no_portal_obtem_token_e_revogado_para_na_hora()
    {
        var (_, key) = await env.CreateTenantWithIdAsync("OAuth portal", GoldenFixture.NewCnpj());
        Assert.Equal(HttpStatusCode.BadRequest, (await ApiAsync(key, HttpMethod.Post, "/v1/oauth-clients", new { nome = "x" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ApiAsync(key, HttpMethod.Post, "/v1/oauth-clients", new { nome = "ERP", scopes = AdminScope })).Status);
        var created = await ApiAsync(key, HttpMethod.Post, "/v1/oauth-clients", new { nome = "ERP produção", scopes = UsageScope });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var clientId = created.Body!["client_id"]!.GetValue<string>();
        var secret = created.Body["client_secret"]!.GetValue<string>();

        using var tokenResp = await env.Api.PostAsync(new Uri("/oauth/token", UriKind.Relative), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
        }));
        Assert.Equal(HttpStatusCode.OK, tokenResp.StatusCode);
        var token = JsonNode.Parse(await tokenResp.Content.ReadAsStringAsync())!["access_token"]!.GetValue<string>();
        async Task<HttpStatusCode> Usage()
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/usage");
            req.Headers.Authorization = new("Bearer", token);
            using var resp = await env.Api.SendAsync(req);
            return resp.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Usage());
        var list = (await ApiAsync(key, HttpMethod.Get, "/v1/oauth-clients")).Body!["clientes"]!.AsArray();
        Assert.Contains(list, c => c!["client_id"]!.GetValue<string>() == clientId && c["revogado_em"] is null);

        Assert.Equal(HttpStatusCode.NoContent, (await ApiAsync(key, HttpMethod.Delete, $"/v1/oauth-clients/{clientId}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await ApiAsync(key, HttpMethod.Delete, $"/v1/oauth-clients/{clientId}")).Status);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (await Usage() == HttpStatusCode.OK && DateTime.UtcNow < until)
        {
            await Task.Delay(100);
        }

        Assert.NotEqual(HttpStatusCode.OK, await Usage());
    }

    [Fact]
    public async Task Registro_de_chamadas_guarda_erros_e_escritas_com_o_codigo()
    {
        var (_, key) = await env.CreateTenantWithIdAsync("Logs", GoldenFixture.NewCnpj());
        Assert.Equal(HttpStatusCode.BadRequest, (await ApiAsync(key, HttpMethod.Get, "/v1/apuracao?competencia=errada")).Status);
        Assert.Equal(HttpStatusCode.OK, (await ApiAsync(key, HttpMethod.Get, "/v1/me")).Status);   // leitura com sucesso: não registra
        JsonArray? calls = null;
        for (var i = 0; i < 40; i++)
        {
            calls = (await ApiAsync(key, HttpMethod.Get, "/v1/logs?status=erros")).Body!["chamadas"]!.AsArray();
            if (calls.Count > 0)
            {
                break;
            }

            await Task.Delay(250);
        }

        var call = Assert.Single(calls!);
        Assert.Equal(400, call!["status"]!.GetValue<int>());
        Assert.Equal("ERR_VALIDATION", call["erro"]!.GetValue<string>());
        Assert.Equal("/v1/apuracao", call["rota"]!.GetValue<string>());
        Assert.StartsWith("api-key:", call["credencial"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain((await ApiAsync(key, HttpMethod.Get, "/v1/logs")).Body!["chamadas"]!.AsArray(), c => c!["rota"]!.GetValue<string>() == "/v1/me");
    }
}
