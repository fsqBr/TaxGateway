using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Xml;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using TaxGateway.Core.Tenancy;
using TaxGateway.Infrastructure.Batches;
using TaxGateway.TestKit;

namespace TaxGateway.GoldenTests;

/// <summary>
/// D-R12 a D-R14: cada limite do plano vale em todos os caminhos — lote (o envio inteiro tem de caber), cada documento
/// auditado pelos workers (lote e DF-e), captura de DF-e por plano e CNPJs incluídos como limite.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class PlanEnforcementTests(GoldenFixture env)
{
    private async Task<(HttpStatusCode Status, JsonNode? Body)> AdminAsync(HttpMethod m, string path, object? body = null)
    {
        using var req = new HttpRequestMessage(m, path) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Add("X-Admin-Key", GoldenFixture.OperatorKey);
        using var resp = await env.Api.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> ApiAsync(string key, HttpMethod m, string path, object? body = null)
    {
        using var req = new HttpRequestMessage(m, path) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Add("X-Api-Key", key);
        using var resp = await env.Api.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    /// <summary>Plano de teste (não público) e um tenant novo nele, com autoprovisionamento de CNPJ.</summary>
    private async Task<(Guid Tenant, string Key)> TenantOnPlanAsync(string prefix, object plan)
    {
        var code = $"{prefix}{Random.Shared.Next(10000, 99999)}";
        var saved = await AdminAsync(HttpMethod.Put, $"/admin/v1/planos/{code}", plan);
        Assert.True(saved.Status == HttpStatusCode.OK, saved.Body?.ToJsonString());
        var (tenant, key) = await env.CreateTenantWithIdAsync(prefix, GoldenFixture.NewCnpj());
        Assert.Equal(HttpStatusCode.OK, (await AdminAsync(HttpMethod.Post, $"/admin/v1/tenants/{tenant}/plano", new { plano = code })).Status);
        await WaitAsync(async () => (await ApiAsync(key, HttpMethod.Get, "/v1/me")).Body?["plano"]?.GetValue<string>() == code);
        return (tenant, key);
    }

    private static object Plan(long includedDocs, bool hard, long docMilli = 0, int includedCnpjs = 0, long cnpjPrice = 0, bool dfe = true) => new
    {
        name = "Teste de limites",
        price_cents = hard ? 0 : 100,
        included_docs = includedDocs,
        included_simulations = 100,
        overage_doc_millicents = docMilli,
        overage_sim_millicents = 0,
        hard_quota = hard,
        dfe_enabled = dfe,
        included_cnpjs = includedCnpjs,
        cnpj_price_cents = cnpjPrice,
        rpm = 600,
        burst = 100,
        concurrency = 10,
        batch_max_docs = 100,
        batch_max_bytes = 10_000_000,
        is_public = false,
        self_service = false,
        active = true,
    };

    private static byte[] Xml(int numero, GoldenFixture env)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(Path.Combine(GoldenXmlTests.XmlDir, "A-000001-RO.xml"));
        GoldenXmlTests.Rekey(doc, numero);
        NfeBuilder.Sign(doc, env.Pki.Leaf);
        return NfeBuilder.Bytes(doc);
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> BatchAsync(string key, int first, int count)
    {
        using var zip = new MemoryStream();
        using (var z = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < count; i++)
            {
                await using var s = z.CreateEntry($"nota-{i}.xml").Open();
                await s.WriteAsync(Xml(first + i, env));
            }
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/auditoria/lotes?direction=OUTBOUND") { Content = new ByteArrayContent(zip.ToArray()) };
        req.Content.Headers.ContentType = new("application/zip");
        req.Headers.Add("X-Api-Key", key);
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var resp = await env.Api.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < until, "condição não atingida no tempo");
            await Task.Delay(250);
        }
    }

    [Fact]
    public async Task Lote_inteiro_precisa_caber_na_franquia_rigida()
    {
        var (_, key) = await TenantOnPlanAsync("TLOTE", Plan(includedDocs: 3, hard: true));
        var tooBig = await BatchAsync(key, 741_000, 5);
        Assert.Equal(HttpStatusCode.PaymentRequired, tooBig.Status);
        Assert.Equal("ERR_FRANQUIA_ESGOTADA", tooBig.Body!["code"]!.GetValue<string>());
        Assert.Contains("restam 3", tooBig.Body["detail"]!.GetValue<string>(), StringComparison.Ordinal);

        var fits = await BatchAsync(key, 741_100, 3);
        Assert.Equal(HttpStatusCode.Accepted, fits.Status);

        // Os 3 documentos do lote ficam reservados: outro lote na sequência já não cabe, mesmo antes de processar.
        var reserved = await BatchAsync(key, 741_200, 1);
        Assert.Equal(HttpStatusCode.PaymentRequired, reserved.Status);
    }

    [Fact]
    public async Task Cada_documento_do_worker_respeita_teto_e_conta_restrita()
    {
        // Plano pago: 1 documento na franquia, excedente a 1 centavo, teto 0 → o segundo documento é recusado.
        var (tenant, _) = await TenantOnPlanAsync("TDOC", Plan(includedDocs: 1, hard: false, docMilli: 1000));
        var auditor = env.Services.GetRequiredService<IBatchDocumentAuditor>();
        var first = await auditor.AuditAsync(new TenantId(tenant), Xml(742_001, env), "OUTBOUND", CancellationToken.None);
        Assert.NotEqual("REJEITADO", first.Status);
        var second = await auditor.AuditAsync(new TenantId(tenant), Xml(742_002, env), "OUTBOUND", CancellationToken.None);
        Assert.Equal("REJEITADO", second.Status);
        Assert.StartsWith("ERR_TETO_EXCEDENTE", second.Erro, StringComparison.Ordinal);

        // Conta com pagamento pendente: a captura e os lotes param de auditar.
        var (restricted, _) = await env.CreateTenantWithIdAsync("Restrita", GoldenFixture.NewCnpj());
        await using (var billing = NpgsqlDataSource.Create(env.ConnectionFor("tg_billing")))
        await using (var cmd = billing.CreateCommand("UPDATE tenants SET status = 'RESTRICTED' WHERE id = $1"))
        {
            cmd.Parameters.Add(new() { Value = restricted });
            await cmd.ExecuteNonQueryAsync();
        }

        var blocked = await auditor.AuditAsync(new TenantId(restricted), Xml(742_101, env), "OUTBOUND", CancellationToken.None);
        Assert.Equal("REJEITADO", blocked.Status);
        Assert.StartsWith("ERR_PAGAMENTO_PENDENTE", blocked.Erro, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Captura_de_DFe_so_nos_planos_que_a_incluem()
    {
        var (_, key) = await TenantOnPlanAsync("TNODFE", Plan(includedDocs: 10, hard: true, dfe: false));
        var me = (await ApiAsync(key, HttpMethod.Get, "/v1/me")).Body!;
        var sync = await ApiAsync(key, HttpMethod.Post, $"/v1/cnpjs/{me["cnpj_raiz"]!.GetValue<string>()}/dfe/sync", new { c_uf_autor = 35 });
        Assert.Equal(HttpStatusCode.Forbidden, sync.Status);
        Assert.Equal("ERR_PLANO_SEM_DFE", sync.Body!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task CNPJs_incluidos_sao_limite_quando_o_plano_nao_vende_CNPJ_extra()
    {
        var (_, key) = await TenantOnPlanAsync("TCNPJ", Plan(includedDocs: 10, hard: true, includedCnpjs: 1));
        var extra = await ApiAsync(key, HttpMethod.Post, "/v1/oem/cnpjs", new { cnpj = GoldenFixture.NewCnpj() });
        Assert.Equal(HttpStatusCode.PaymentRequired, extra.Status);
        Assert.Equal("ERR_LIMITE_CNPJS", extra.Body!["code"]!.GetValue<string>());

        // Plano que cobra o CNPJ adicional: permitido (e cobrado).
        var (_, paid) = await TenantOnPlanAsync("TCNPJP", Plan(includedDocs: 10, hard: false, includedCnpjs: 1, cnpjPrice: 2900));
        Assert.Equal(HttpStatusCode.Created, (await ApiAsync(paid, HttpMethod.Post, "/v1/oem/cnpjs", new { cnpj = GoldenFixture.NewCnpj() })).Status);
    }

    [Fact]
    public async Task Limites_de_contrato_pelo_backoffice_valem_na_hora()
    {
        var (tenant, key) = await env.CreateTenantWithIdAsync("Contrato", GoldenFixture.NewCnpj());
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminAsync(HttpMethod.Put, $"/admin/v1/tenants/{tenant}/limites", new { rpm = -1 })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminAsync(HttpMethod.Put, $"/admin/v1/tenants/{tenant}/limites", new { desconhecido = 5 })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync(HttpMethod.Put, $"/admin/v1/tenants/{Guid.NewGuid()}/limites", new { rpm = 10 })).Status);
        Assert.Equal(HttpStatusCode.OK, (await AdminAsync(HttpMethod.Put, $"/admin/v1/tenants/{tenant}/limites", new { rpm = 4321, concurrency = 7, batch_max_docs = 3 })).Status);
        await WaitAsync(async () => (await ApiAsync(key, HttpMethod.Get, "/v1/me")).Body?["limites"]?["rpm"]?.GetValue<int>() == 4321);
        var tooMany = await BatchAsync(key, 743_000, 4);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMany.Status);
    }

    [Fact]
    public async Task OEM_cada_CNPJ_atendido_tem_o_proprio_limite()
    {
        var plan = new
        {
            name = "OEM limite por CNPJ",
            price_cents = 0,
            included_docs = 0,
            included_simulations = 0,
            hard_quota = false,
            dfe_enabled = true,
            rpm = 6000,
            burst = 200,
            concurrency = 20,
            per_cnpj_rpm = 4,
            batch_max_docs = 100,
            batch_max_bytes = 10_000_000,
            is_public = false,
            self_service = false,
            active = true,
        };
        var (_, key) = await TenantOnPlanAsync("TCNPJRL", plan);
        async Task<HttpResponseMessage> Me(string cnpj)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/me");
            req.Headers.Add("X-Api-Key", key);
            req.Headers.Add("X-On-Behalf-Of-CNPJ", cnpj);
            return await env.Api.SendAsync(req);
        }

        var a = GoldenFixture.NewCnpj();
        var b = GoldenFixture.NewCnpj();
        using (var first = await Me(a))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Contains("\"burst\"", first.Headers.GetValues("RateLimit-Policy").Single(), StringComparison.Ordinal);
        }

        using (var second = await Me(a))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);   // burst por CNPJ = 4/4 = 1
            Assert.Contains("CNPJ atendido", await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using var other = await Me(b);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);   // o outro cliente final não foi afetado
    }

    [Fact]
    public async Task Lote_custa_mais_do_limite_que_uma_chamada_simples()
    {
        var (_, key) = await TenantOnPlanAsync("TLOTERL", new
        {
            name = "Lote pesa",
            price_cents = 0,
            included_docs = 0,
            included_simulations = 0,
            hard_quota = false,
            dfe_enabled = true,
            rpm = 12,
            burst = 12,
            concurrency = 10,
            batch_max_docs = 100,
            batch_max_bytes = 10_000_000,
            is_public = false,
            self_service = false,
            active = true,
        });
        Assert.Equal(HttpStatusCode.Accepted, (await BatchAsync(key, 745_000, 1)).Status);   // 10 dos 12 tokens
        Assert.Equal(HttpStatusCode.OK, (await ApiAsync(key, HttpMethod.Get, "/v1/me")).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await BatchAsync(key, 745_100, 1)).Status);
    }


    /// <summary>D-R29: a suspensão vem do BANCO (não só do Valkey) — app.revocation_state() devolve o tenant suspenso.</summary>
    [Fact]
    public async Task Suspensao_e_revogacao_sao_lidas_do_banco()
    {
        var (tenant, _) = await env.CreateTenantWithIdAsync("Suspensa", GoldenFixture.NewCnpj());
        Assert.Equal(HttpStatusCode.OK, (await AdminAsync(HttpMethod.Post, $"/admin/v1/tenants/{tenant}/suspend")).Status);
        await using var app = NpgsqlDataSource.Create(env.AppConnection);
        await using var cmd = app.CreateCommand("SELECT count(*) FROM app.revocation_state() WHERE kind = 'tenant' AND value = $1");
        cmd.Parameters.Add(new() { Value = tenant.ToString() });
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
        Assert.Equal(HttpStatusCode.OK, (await AdminAsync(HttpMethod.Post, $"/admin/v1/tenants/{tenant}/reactivate")).Status);
    }


    /// <summary>D-R29: corpo malformado ou campo com tipo errado é 400 (antes 500); tenant inexistente em rota de admin é 404.</summary>
    [Fact]
    public async Task Entrada_malformada_e_tenant_inexistente_tem_resposta_certa()
    {
        using (var bad = new HttpRequestMessage(HttpMethod.Post, $"/admin/v1/params/changes/{Guid.NewGuid()}/reject") { Content = new StringContent("{", System.Text.Encoding.UTF8, "application/json") })
        {
            bad.Headers.Add("X-Admin-Key", GoldenFixture.OperatorKey);
            using var resp = await env.Api.SendAsync(bad);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }

        var wrongType = await AdminAsync(HttpMethod.Post, $"/admin/v1/params/changes/{Guid.NewGuid()}/reject", new { motivo = 12345 });
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync(HttpMethod.Put, $"/admin/v1/tenants/{Guid.NewGuid()}/teto", new { teto_centavos = 100 })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync(HttpMethod.Post, $"/admin/v1/tenants/{Guid.NewGuid()}/verificar", new { observacao = "contrato social conferido" })).Status);
    }

}
