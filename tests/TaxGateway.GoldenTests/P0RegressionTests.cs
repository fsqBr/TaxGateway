using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;

using StackExchange.Redis;

using TaxGateway.TestKit;

namespace TaxGateway.GoldenTests;

/// <summary>
/// Regressões dos itens P0 da revisão multidisciplinar (docs/revisao-multidisciplinar.md; D-P03 a D-P12). Cada teste
/// reproduz o cenário que a revisão demonstrou contra a pilha real. Faixa de numeração de notas 960_xxx.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class P0RegressionTests(GoldenFixture env)
{
    private static readonly string[] StrictStatuses = ["SCHEMA_ERROR", "ASSINATURA_INVALIDA"];

    private static XmlDocument Doc(int numero)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(Path.Combine(GoldenXmlTests.XmlDir, "A-000001-RO.xml"));
        GoldenXmlTests.Rekey(doc, numero);
        return doc;
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body, HttpResponseMessage Resp)> SendAsync(HttpMethod method, string path, HttpContent? content, string? idem = null, string? apiKey = null)
    {
        var req = new HttpRequestMessage(method, path) { Content = content };
        req.Headers.Add("X-Api-Key", apiKey ?? env.ApiKey);
        if (idem is not null)
        {
            req.Headers.Add("Idempotency-Key", idem);
        }

        var resp = await env.Api.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text), resp);
    }

    private static ByteArrayContent Zip(params byte[][] xmls)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < xmls.Length; i++)
            {
                using var s = z.CreateEntry($"n{i}.xml").Open();
                s.Write(xmls[i]);
            }
        }

        var c = new ByteArrayContent(ms.ToArray());
        c.Headers.ContentType = new("application/zip");
        return c;
    }

    private static ByteArrayContent Xml(XmlDocument doc)
    {
        var c = new ByteArrayContent(NfeBuilder.Bytes(doc));
        c.Headers.ContentType = new("application/xml");
        return c;
    }

    /// <summary>D-P03: a mesma Idempotency-Key devolve o MESMO lote; conteúdo diferente com a mesma chave é 422.</summary>
    [Fact]
    public async Task Lote_respeita_a_Idempotency_Key()
    {
        var a = Doc(960_001);
        NfeBuilder.Sign(a, env.Pki.Leaf);
        var b = Doc(960_002);
        NfeBuilder.Sign(b, env.Pki.Leaf);
        var key = Guid.NewGuid().ToString("N");

        var first = await SendAsync(HttpMethod.Post, "/v1/auditoria/lotes?direction=OUTBOUND", Zip(NfeBuilder.Bytes(a)), key);
        Assert.Equal(HttpStatusCode.Accepted, first.Status);
        var again = await SendAsync(HttpMethod.Post, "/v1/auditoria/lotes?direction=OUTBOUND", Zip(NfeBuilder.Bytes(a)), key);
        Assert.Equal(HttpStatusCode.Accepted, again.Status);
        Assert.Equal(first.Body!["batch_id"]!.GetValue<string>(), again.Body!["batch_id"]!.GetValue<string>());
        Assert.Equal("true", again.Resp.Headers.GetValues("Idempotent-Replayed").Single());

        var other = await SendAsync(HttpMethod.Post, "/v1/auditoria/lotes?direction=OUTBOUND", Zip(NfeBuilder.Bytes(b)), key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, other.Status);
        Assert.Equal("ERR_IDEMPOTENCY_KEY_REUSED", other.Body!["code"]!.GetValue<string>());

        var tooLong = await SendAsync(HttpMethod.Post, "/v1/auditoria/lotes", Zip(NfeBuilder.Bytes(a)), new string('k', 256));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.Status);
    }

    /// <summary>D-P04: reprocess=true numa nota nunca auditada é auditoria nova e cobrada; reprocessar de verdade é grátis.</summary>
    [Fact]
    public async Task Reprocess_de_nota_inedita_e_cobrado()
    {
        var doc = Doc(960_010);
        NfeBuilder.Sign(doc, env.Pki.Leaf);
        var inedita = await SendAsync(HttpMethod.Post, "/v1/auditoria/xml?direction=OUTBOUND&reprocess=true", Xml(doc), Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.OK, inedita.Status);
        Assert.True(inedita.Body!["is_billable"]!.GetValue<bool>());
        Assert.Contains(inedita.Body["avisos"]!.AsArray(), x => x!["codigo"]!.GetValue<string>() == "REPROCESSAMENTO_SEM_AUDITORIA_ANTERIOR");

        var repro = await SendAsync(HttpMethod.Post, "/v1/auditoria/xml?direction=OUTBOUND&reprocess=true", Xml(doc), Guid.NewGuid().ToString("N"));
        Assert.False(repro.Body!["is_billable"]!.GetValue<bool>());
        Assert.True(repro.Body["reprocessamento"]!.GetValue<bool>());
    }

    /// <summary>D-P04: replay por chave de acesso só com o MESMO conteúdo; XML alterado com a mesma chave é auditado de novo.</summary>
    [Fact]
    public async Task Replay_nao_devolve_resultado_antigo_para_conteudo_diferente()
    {
        var doc = Doc(960_020);
        NfeBuilder.Sign(doc, env.Pki.Leaf);
        var original = await SendAsync(HttpMethod.Post, "/v1/auditoria/xml", Xml(doc), Guid.NewGuid().ToString("N"));
        Assert.Equal("CONFORME", original.Body!["status"]!.GetValue<string>());

        // Mesmo XML: replay. XML adulterado (mesma chave, assinatura quebrada): auditado de novo, não "CONFORME" herdado.
        var same = await SendAsync(HttpMethod.Post, "/v1/auditoria/xml", Xml(doc), Guid.NewGuid().ToString("N"));
        Assert.True(same.Body!["replay"]!.GetValue<bool>());
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("n", NfeBuilder.Ns);
        var vProd = doc.SelectSingleNode("//n:det/n:prod/n:vProd", ns)!;
        vProd.InnerText = "1.00";
        var changed = await SendAsync(HttpMethod.Post, "/v1/auditoria/xml", Xml(doc), Guid.NewGuid().ToString("N"));
        Assert.NotEqual(true, changed.Body!["replay"]?.GetValue<bool>());
        Assert.Equal("ASSINATURA_INVALIDA", changed.Body["status"]!.GetValue<string>());
        Assert.Contains(changed.Body["avisos"]!.AsArray(), x => x!["codigo"]!.GetValue<string>() == "CONTEUDO_ALTERADO");
    }

    /// <summary>D-P11: assinatura=opcional aceita XML sem assinatura (antes: SCHEMA_ERROR cobrado).</summary>
    [Fact]
    public async Task Assinatura_opcional_audita_xml_sem_assinatura()
    {
        var doc = Doc(960_030);
        foreach (var sig in doc.GetElementsByTagName("Signature", "http://www.w3.org/2000/09/xmldsig#").Cast<XmlNode>().ToList())
        {
            sig.ParentNode!.RemoveChild(sig);   // XML sem ds:Signature
        }

        var r = await SendAsync(HttpMethod.Post, "/v1/auditoria/xml?assinatura=opcional", Xml(doc), Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal("CONFORME", r.Body!["status"]!.GetValue<string>());
        Assert.Contains(r.Body["avisos"]!.AsArray(), x => x!["codigo"]!.GetValue<string>() == "ASSINATURA_AUSENTE");

        // Sem o modo opcional, continua sendo recusado como assinatura ausente/schema.
        var semAssinatura = Doc(960_031);
        foreach (var sig in semAssinatura.GetElementsByTagName("Signature", "http://www.w3.org/2000/09/xmldsig#").Cast<XmlNode>().ToList())
        {
            sig.ParentNode!.RemoveChild(sig);
        }

        var strict = await SendAsync(HttpMethod.Post, "/v1/auditoria/xml", Xml(semAssinatura), Guid.NewGuid().ToString("N"));
        Assert.Contains(strict.Body!["status"]!.GetValue<string>(), StrictStatuses);
    }

    /// <summary>D-P06: chamadas simultâneas com um CNPJ novo (auto-provisionamento) não recebem 403 falso.</summary>
    [Fact]
    public async Task Auto_cadastro_de_cnpj_concorrente_nao_da_403()
    {
        var (_, key) = await env.CreateTenantWithIdAsync("P0 corrida", GoldenFixture.NewCnpj(), autoProvision: true);
        var cnpj = GoldenFixture.NewCnpj();
        var body = JsonNode.Parse(File.ReadAllText(Path.Combine(Paths.CorpusDir, "A-000001-RO.json")))!;
        var request = body["api_request"]!.AsObject();
        request["cnpj_emitente"] = cnpj;
        var payload = request.ToJsonString();

        var calls = Enumerable.Range(0, 12).Select(async _ =>
        {
            using var c = new StringContent(payload, Encoding.UTF8, "application/json");
            return (await SendAsync(HttpMethod.Post, "/v1/calculo/simular", c, apiKey: key)).Status;
        });
        var statuses = await Task.WhenAll(calls);
        Assert.DoesNotContain(HttpStatusCode.Forbidden, statuses);
    }

    /// <summary>D-P12: entrada inválida do cliente é 4xx, nunca 500.</summary>
    [Fact]
    public async Task Entradas_invalidas_dao_4xx_e_nao_500()
    {
        foreach (var mes in new[] { "abc", "2026-13", "2026-00", "2026-1", "20261", "2026-11-01" })
        {
            var r = await SendAsync(HttpMethod.Get, $"/v1/usage?mes={mes}", null);
            Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        }

        foreach (var json in new[] { "não é json", "{\"storage_object_key\": 123}", "{\"storage_object_key\": \"t/x/../batches/a\"}" })
        {
            var r = await SendAsync(HttpMethod.Post, "/v1/auditoria/lotes", new StringContent(json, Encoding.UTF8, "application/json"), Guid.NewGuid().ToString("N"));
            Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        }

        // Chave no formato certo, mas nada foi enviado para ela: 404.
        var up = await SendAsync(HttpMethod.Post, "/v1/auditoria/lotes/upload-url", null);
        var missing = await SendAsync(HttpMethod.Post, "/v1/auditoria/lotes",
            JsonContent.Create(new { storage_object_key = up.Body!["storage_object_key"]!.GetValue<string>() }), Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);

        using var tenantReq = new HttpRequestMessage(HttpMethod.Post, "/admin/v1/tenants")
        {
            Content = JsonContent.Create(new { nome = "X", cnpj_raiz = GoldenFixture.NewCnpj(), plano = "GROWTH", retencao_xml_dias = -5 }),
        };
        tenantReq.Headers.Add("X-Admin-Key", GoldenFixture.OperatorKey);
        Assert.Equal(HttpStatusCode.BadRequest, (await env.Api.SendAsync(tenantReq)).StatusCode);
    }

    /// <summary>D-P10: entrada adulterada no Valkey (troca de tenant e escopos) é descartada; vale o banco.</summary>
    [Fact]
    public async Task Cache_de_autenticacao_adulterado_no_Valkey_e_ignorado()
    {
        var (tenantA, keyA) = await env.CreateTenantWithIdAsync("P0 valkey A", GoldenFixture.NewCnpj());
        var (tenantB, _) = await env.CreateTenantWithIdAsync("P0 valkey B", GoldenFixture.NewCnpj());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/v1/me", null, apiKey: keyA)).Status);   // popula o cache

        await using var valkey = await ConnectionMultiplexer.ConnectAsync(env.ValkeyConnection);
        var server = valkey.GetServer(valkey.GetEndPoints()[0]);
        var db = valkey.GetDatabase();
        var forged = $$"""{"t":"{{tenantB}}","k":"{{Guid.NewGuid()}}","s":["keys:manage","titulares:manage","dfe:manage"],"p":"ENTERPRISE","st":"ACTIVE","o":"{}","a":true}""";
        var touched = 0;
        await foreach (var k in server.KeysAsync(pattern: "auth:apikey:*"))
        {
            await db.StringSetAsync(k, forged);   // sem selo: o atacante não tem a chave derivada do pepper
            touched++;
        }

        Assert.True(touched > 0);
        // Outra instância (sem L1) lê do Valkey: precisa ignorar a entrada forjada e resolver pelo banco.
        await using var factory = env.NewApiInstance(new Dictionary<string, string?>());
        using var other = factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/me");
        req.Headers.Add("X-Api-Key", keyA);
        var me = JsonNode.Parse(await (await other.SendAsync(req)).Content.ReadAsStringAsync())!;
        Assert.Equal(tenantA.ToString(), me["tenant_id"]!.GetValue<string>());   // antes: virava o tenant B com escopos forjados
        Assert.NotEqual(tenantB.ToString(), me["tenant_id"]!.GetValue<string>());
    }
}
