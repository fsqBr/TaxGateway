using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;

using Microsoft.Extensions.DependencyInjection;

using TaxGateway.Core.Tenancy;
using TaxGateway.Infrastructure.Persistence;
using TaxGateway.TestKit;

namespace TaxGateway.GoldenTests;

/// <summary>
/// Lacunas de produto (D-J01 a D-J05): histórico de documentos, consulta de classificação, avisos, primeiros passos e
/// as validações de contrato apontadas pelo QA. Faixa de numeração de notas 970_xxx.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class ProductJourneyTests(GoldenFixture env)
{
    private async Task<(HttpStatusCode Status, JsonNode? Body, string Text, HttpResponseMessage Resp)> SendAsync(HttpMethod method, string path, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, path) { Content = content };
        req.Headers.Add("X-Api-Key", env.ApiKey);
        if (method == HttpMethod.Post && path.StartsWith("/v1/auditoria/xml", StringComparison.Ordinal))
        {
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        }
        var resp = await env.Api.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        JsonNode? body = null;
        if (text.Length > 0 && resp.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.Ordinal) == true)
        {
            body = JsonNode.Parse(text);
        }

        return (resp.StatusCode, body, text, resp);
    }

    private async Task<string> AuditAsync(int numero)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(Path.Combine(GoldenXmlTests.XmlDir, "A-000001-RO.xml"));
        GoldenXmlTests.Rekey(doc, numero);
        NfeBuilder.Sign(doc, env.Pki.Leaf);
        var c = new ByteArrayContent(NfeBuilder.Bytes(doc));
        c.Headers.ContentType = new("application/xml");
        var r = await SendAsync(HttpMethod.Post, "/v1/auditoria/xml", c);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.Body!["chave_acesso"]!.GetValue<string>();
    }

    /// <summary>D-J01: listar com filtros, paginar por cursor, ver o detalhe com os itens e exportar CSV.</summary>
    [Fact]
    public async Task Audited_documents_can_be_listed_filtered_detailed_and_exported()
    {
        var chave1 = await AuditAsync(970_001);
        var chave2 = await AuditAsync(970_002);

        var one = await SendAsync(HttpMethod.Get, $"/v1/auditoria/documentos?chave={chave1}");
        Assert.Equal(HttpStatusCode.OK, one.Status);
        var docs = one.Body!["documentos"]!.AsArray();
        Assert.Single(docs);
        Assert.Equal("CONFORME", docs[0]!["status"]!.GetValue<string>());
        Assert.Equal("saida", docs[0]!["direcao"]!.GetValue<string>());

        // Cursor: duas páginas de 1 trazem documentos diferentes.
        var p1 = await SendAsync(HttpMethod.Get, "/v1/auditoria/documentos?limite=1");
        var cursor = p1.Body!["proximo_cursor"]!.GetValue<string>();
        var p2 = await SendAsync(HttpMethod.Get, $"/v1/auditoria/documentos?limite=1&cursor={cursor}");
        Assert.NotEqual(p1.Body["documentos"]![0]!["id"]!.GetValue<Guid>(), p2.Body!["documentos"]![0]!["id"]!.GetValue<Guid>());

        var id = docs[0]!["id"]!.GetValue<Guid>();
        var detail = await SendAsync(HttpMethod.Get, $"/v1/auditoria/documentos/{id}");
        Assert.Equal(HttpStatusCode.OK, detail.Status);
        Assert.NotEmpty(detail.Body!["itens_declarados"]!.AsArray());
        Assert.Equal(chave1, detail.Body["auditoria"]!["chave_acesso"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/v1/auditoria/documentos/{Guid.NewGuid()}")).Status);

        var csv = await SendAsync(HttpMethod.Get, "/v1/auditoria/documentos/exportar");
        Assert.Equal(HttpStatusCode.OK, csv.Status);
        Assert.StartsWith("text/csv", csv.Resp.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);
        Assert.Contains("chave_acesso;direcao;modelo;status", csv.Text, StringComparison.Ordinal);
        Assert.Contains(chave2, csv.Text, StringComparison.Ordinal);

        // D-R06: data extrema é 400 também no resumo e na exportação (antes 500 e CSV cortado).
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, "/v1/auditoria/resumo?ate=0001-01-05")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, "/v1/auditoria/documentos/exportar?de=9999-12-30&ate=9999-12-31")).Status);

        var resumo = await SendAsync(HttpMethod.Get, "/v1/auditoria/resumo");
        Assert.True(resumo.Body!["total"]!.GetValue<long>() >= 2);

        // Entradas inválidas: 400, nunca 500. Dígitos arábico-índicos não passam por dígito.
        foreach (var bad in new[] { "status=FOO", "de=2026-13-01", "de=2026-01-10&ate=2026-01-01", "cnpj=١١٢٢٢٣٣٣٠٠٠١٨١", "direcao=lateral", "cursor=xyz", "lote=1",
                                         "de=9999-12-30&ate=9999-12-31", "ate=0001-01-01", "limite=abc", "limite=0" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, $"/v1/auditoria/documentos?{bad}")).Status);
        }
    }

    /// <summary>D-J02: NCM com IS, regra geral primeiro, só classificações compatíveis; entradas inválidas = 400.</summary>
    [Fact]
    public async Task Classification_lookup_by_ncm_suggests_compatible_codes()
    {
        var r = await SendAsync(HttpMethod.Get, "/v1/classificacao?ncm=22021000&data=2027-01-01");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.True(r.Body!["item"]!["imposto_seletivo"]!.GetValue<bool>());
        var sugestoes = r.Body["sugestoes"]!.AsArray();
        Assert.Equal("000001", sugestoes[0]!["c_class_trib"]!.GetValue<string>());
        Assert.All(sugestoes, s => Assert.Equal(s!["c_class_trib"]!.GetValue<string>()[..3], s["cst"]!.GetValue<string>()));
        Assert.Contains("Não é parecer", r.Body["aviso"]!.GetValue<string>(), StringComparison.Ordinal);

        var nbs = await SendAsync(HttpMethod.Get, "/v1/classificacao?nbs=101011100&data=2027-01-01&q=constru%C3%A7%C3%A3o");
        Assert.Equal(HttpStatusCode.OK, nbs.Status);

        foreach (var bad in new[] { "ncm=2202", "ncm=٢٢٠٢١٠٠٠", "ncm=22021000&nbs=101011100", "", "ncm=22021000&modelo=57", "ncm=22021000&data=1999-01-01" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, $"/v1/classificacao?{bad}")).Status);
        }
    }

    /// <summary>D-J03/D-J04: aviso aparece, é marcado como lido e o worker o dá por enviado; primeiros passos refletem o uso.</summary>
    [Fact]
    public async Task Notices_are_listed_marked_and_mailed_and_first_steps_reflect_usage()
    {
        await AuditAsync(970_010);
        var me = await SendAsync(HttpMethod.Get, "/v1/me");
        var tenant = new TenantId(me.Body!["tenant_id"]!.GetValue<Guid>());
        var db = env.Services.GetRequiredService<ITenantDb>();
        var dedupe = "teste:" + Guid.NewGuid().ToString("N");
        await db.InTenantAsync(tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("INSERT INTO tenant_notifications (tenant_id, kind, severity, title, body, link, dedupe_key) VALUES ($1, 'FRANQUIA_80', 'aviso', 'Aviso de teste', 'Corpo', '/portal/cobranca', $2)");
            cmd.Parameters.Add(new() { Value = tenant.Value });
            cmd.Parameters.Add(new() { Value = dedupe });
            return await cmd.ExecuteNonQueryAsync(ct);
        }, CancellationToken.None);

        var list = await SendAsync(HttpMethod.Get, "/v1/avisos?nao_lidos=true");
        Assert.True(list.Body!["nao_lidos"]!.GetValue<long>() >= 1);
        var aviso = list.Body["avisos"]!.AsArray().First(a => a!["titulo"]!.GetValue<string>() == "Aviso de teste")!;
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, $"/v1/avisos/{aviso["id"]!.GetValue<Guid>()}/lido")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/v1/avisos/{aviso["id"]!.GetValue<Guid>()}/lido")).Status);

        var mailer = env.WorkerServices.GetRequiredService<TaxGateway.Workers.NotificationMailer>();
        Assert.True(await mailer.RunOnceAsync(CancellationToken.None) >= 1);
        var emailed = await db.InTenantAsync(tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("SELECT emailed_at IS NOT NULL FROM tenant_notifications WHERE dedupe_key = $1");
            cmd.Parameters.Add(new() { Value = dedupe });
            return (bool)(await cmd.ExecuteScalarAsync(ct))!;
        }, CancellationToken.None);
        Assert.True(emailed);   // tratado pelo worker (sem destinatário verificado, fica só no portal)

        var steps = await SendAsync(HttpMethod.Get, "/v1/primeiros-passos");
        Assert.Equal(6, steps.Body!["total"]!.GetValue<int>());
        Assert.True(steps.Body["passos"]!.AsArray().First(p => p!["id"]!.GetValue<string>() == "auditar")!["feito"]!.GetValue<bool>());
    }

    /// <summary>D-J07: backoffice com detalhe do cliente (sem segredo) e paginação da lista.</summary>
    [Fact]
    public async Task Backoffice_shows_tenant_detail_without_secrets_and_paginates()
    {
        var tenant = (await SendAsync(HttpMethod.Get, "/v1/me")).Body!["tenant_id"]!.GetValue<Guid>();
        async Task<(HttpStatusCode Status, string Text)> Admin(string path)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Add("X-Admin-Key", GoldenFixture.OperatorKey);
            using var resp = await env.Api.SendAsync(req);
            return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
        }

        var detail = await Admin($"/admin/v1/tenants/{tenant}");
        Assert.Equal(HttpStatusCode.OK, detail.Status);
        var json = JsonNode.Parse(detail.Text)!;
        Assert.Equal(tenant, json["tenant"]!["tenant_id"]!.GetValue<Guid>());
        Assert.NotEmpty(json["api_keys"]!.AsArray());
        Assert.NotEmpty(json["cnpjs"]!.AsArray());
        Assert.DoesNotContain("hash", detail.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("totp_secret", detail.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin($"/admin/v1/tenants/{Guid.NewGuid()}")).Status);

        var page = JsonNode.Parse((await Admin("/admin/v1/tenants?limite=1&pagina=2")).Text)!;
        Assert.Single(page["tenants"]!.AsArray());
        Assert.True(page["total"]!.GetValue<long>() >= 2);
        Assert.Equal(2, page["pagina"]!.GetValue<int>());
    }

    /// <summary>D-J05: campo desconhecido, data fora da reforma, dígito não ASCII, evento inexistente e rota inexistente.</summary>
    [Fact]
    public async Task Contract_validation_rejects_unknown_fields_bad_dates_foreign_digits_and_events()
    {
        var root = (await SendAsync(HttpMethod.Get, "/v1/me")).Body!["cnpj_raiz"]!.GetValue<string>();
        var sample = TaxGateway.Api.Http.OpenApiDocs.Catalog["POST /v1/calculo/simular"].Body!.Replace("11222333000181", root, StringComparison.Ordinal);

        var unknown = JsonNode.Parse(sample)!.AsObject();
        unknown["aliquota_cbs"] = 0.9m;
        var r = await SendAsync(HttpMethod.Post, "/v1/calculo/simular", JsonContent.Create(unknown));
        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.Equal("ERR_CAMPO_DESCONHECIDO", r.Body!["code"]?.GetValue<string>() ?? r.Body["codigo"]?.GetValue<string>());

        var oldDate = JsonNode.Parse(sample)!.AsObject();
        oldDate["data_fato_gerador"] = "2025-06-01T10:00:00-03:00";
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/v1/calculo/simular", JsonContent.Create(oldDate))).Status);

        // D-R06: limites do leiaute e quantidade positiva são validados antes do motor.
        var huge = JsonNode.Parse(sample)!.AsObject();
        huge["itens"]![0]!["valor_base_calculo"] = 1e15m;
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/v1/calculo/simular", JsonContent.Create(huge))).Status);
        var negQty = JsonNode.Parse(sample)!.AsObject();
        negQty["itens"]![0]!["quantidade"] = -5;
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/v1/calculo/simular", JsonContent.Create(negQty))).Status);

        // Motor recusa todos os itens (cClassTrib inexistente): 200 com erros por item, aviso explícito e sem cobrança.
        var rejected = JsonNode.Parse(sample)!.AsObject();
        rejected["itens"]![0]!["c_class_trib"] = "999999";
        var rej = await SendAsync(HttpMethod.Post, "/v1/calculo/simular", JsonContent.Create(rejected));
        Assert.Equal(HttpStatusCode.OK, rej.Status);
        Assert.Contains(rej.Body!["avisos"]!.AsArray(), a => a!["codigo"]!.GetValue<string>() == "NENHUM_ITEM_CALCULADO");
        var simId = Guid.ParseExact(rej.Body["simulation_id"]!.GetValue<string>()[4..], "N");
        var tenantId = new TenantId((await SendAsync(HttpMethod.Get, "/v1/me")).Body!["tenant_id"]!.GetValue<Guid>());
        var tdb = env.Services.GetRequiredService<ITenantDb>();
        (long Sims, long Ledger) counts = default;
        for (var i = 0; i < 50 && counts.Sims == 0; i++)
        {
            await Task.Delay(100);
            counts = await tdb.InTenantAsync(tenantId, async (s, ct) =>
            {
                await using var cmd = s.CreateCommand("SELECT (SELECT count(*) FROM simulations WHERE id = $1), (SELECT count(*) FROM usage_ledger WHERE source_id = $1)");
                cmd.Parameters.Add(new() { Value = simId });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                return (r.GetInt64(0), r.GetInt64(1));
            }, CancellationToken.None);
        }

        Assert.Equal((1L, 0L), counts);   // fica na trilha, não entra na cobrança

        var arabic = JsonNode.Parse(sample)!.AsObject();
        arabic["itens"]![0]!["ncm"] = "٢٢٠٢١٠٠٠";
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/v1/calculo/simular", JsonContent.Create(arabic))).Status);

        var hook = await SendAsync(HttpMethod.Post, "/v1/webhooks", new StringContent("""{"url":"https://erp.exemplo.com.br/h","eventos":["document.audit"]}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, hook.Status);
        Assert.Contains("document.audit", hook.Text, StringComparison.Ordinal);

        var missing = await SendAsync(HttpMethod.Get, "/v1/nao-existe");
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
        Assert.Equal("application/problem+json", missing.Resp.Content.Headers.ContentType!.MediaType);
    }
}
