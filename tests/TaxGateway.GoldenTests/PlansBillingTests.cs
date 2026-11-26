using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using StackExchange.Redis;

using TaxGateway.Infrastructure.Billing;

namespace TaxGateway.GoldenTests;

/// <summary>Planos no backoffice com histórico, vitrine pública, webhook da Stripe e franquia rígida (D-C01 a D-C08).</summary>
[Collection(GoldenCollection.Name)]
public sealed class PlansBillingTests(GoldenFixture env)
{
    private static readonly string[] VitrineFeatures = ["Teste de vitrine"];

    private async Task<(HttpStatusCode Status, JsonNode? Body)> AdminAsync(HttpMethod m, string path, object? body = null)
    {
        using var req = new HttpRequestMessage(m, path) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Add("X-Admin-Key", GoldenFixture.OperatorKey);
        using var resp = await env.Api.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    [Fact]
    public async Task Operador_edita_plano_com_historico_e_a_vitrine_reflete()
    {
        var plans = (await AdminAsync(HttpMethod.Get, "/admin/v1/planos")).Body!.AsArray();
        Assert.Contains(plans, p => p!["code"]!.GetValue<string>() == "PROFISSIONAL");

        var price = Random.Shared.Next(50_000, 90_000);
        var saved = await AdminAsync(HttpMethod.Put, "/admin/v1/planos/PROFISSIONAL", new { price_cents = price, features = VitrineFeatures });
        Assert.Equal(HttpStatusCode.OK, saved.Status);

        var history = (await AdminAsync(HttpMethod.Get, "/admin/v1/planos/PROFISSIONAL/historico")).Body!.AsArray();
        Assert.Equal("golden", history[0]!["por"]!.GetValue<string>());
        Assert.Equal("update", history[0]!["acao"]!.GetValue<string>());

        var publicPlans = JsonNode.Parse(await env.Api.GetStringAsync(new Uri("/v1/conta/planos", UriKind.Relative)))!.AsArray();
        var pro = publicPlans.First(p => p!["codigo"]!.GetValue<string>() == "PROFISSIONAL")!;
        Assert.Equal(price, pro["preco_centavos"]!.GetValue<long>());
        Assert.DoesNotContain(publicPlans, p => p!["codigo"]!.GetValue<string>() is "STARTER" or "GROWTH");   // legados fora da vitrine

        // Validação: campo desconhecido, número negativo, burst > rpm e código inválido.
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminAsync(HttpMethod.Put, "/admin/v1/planos/PROFISSIONAL", new { preco = 1 })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminAsync(HttpMethod.Put, "/admin/v1/planos/PROFISSIONAL", new { price_cents = -1 })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminAsync(HttpMethod.Put, "/admin/v1/planos/PROFISSIONAL", new { rpm = 10, burst = 50 })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminAsync(HttpMethod.Put, "/admin/v1/planos/ruim-1", new { name = "X y" })).Status);
    }

    [Fact]
    public async Task Franquia_rigida_bloqueia_ao_esgotar_e_vale_na_hora_ao_trocar_de_plano()
    {
        var code = $"QUOTA_{Random.Shared.Next(1000, 9999)}";
        Assert.Equal(HttpStatusCode.OK, (await AdminAsync(HttpMethod.Put, $"/admin/v1/planos/{code}", new
        {
            name = "Teste de franquia",
            rpm = 600,
            burst = 30,
            concurrency = 10,
            hard_quota = true,
            included_simulations = 1,
            included_docs = 1,
        })).Status);
        var (tenant, key) = await env.CreateTenantWithIdAsync("Franquia", GoldenFixture.NewCnpj());

        // O snapshot de parâmetros recarrega pelo pub/sub; a troca de plano do tenant vale na hora (perfil invalidado).
        await WaitAsync(async () => (await AdminAsync(HttpMethod.Post, $"/admin/v1/tenants/{tenant}/plano", new { plano = code })).Status == HttpStatusCode.OK);
        var body = JsonNode.Parse(File.ReadAllText(Path.Combine(Paths.CorpusDir, "A-000001-RO.json")))!["api_request"]!.ToJsonString();
        async Task<HttpStatusCode> Simulate()
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/calculo/simular") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Api-Key", key);
            using var resp = await env.Api.SendAsync(req);
            return resp.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Simulate());
        // O ledger é gravado de forma assíncrona e a contagem tem cache de 30 s: espera a franquia aparecer esgotada.
        await WaitAsync(async () => await Simulate() == HttpStatusCode.PaymentRequired, seconds: 45);

        // Volta a um plano com excedente: libera.
        Assert.Equal(HttpStatusCode.OK, (await AdminAsync(HttpMethod.Post, $"/admin/v1/tenants/{tenant}/plano", new { plano = "PROFISSIONAL" })).Status);
        await WaitAsync(async () => await Simulate() == HttpStatusCode.OK);
    }

    private static string Sign(string payload, long t)
    {
        byte[] signed = [.. Encoding.UTF8.GetBytes($"{t}."), .. Encoding.UTF8.GetBytes(payload)];
        return $"t={t},v1={Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(GoldenFixture.StripeWebhookSecret), signed))}";
    }

    private async Task<HttpStatusCode> PostWebhookAsync(string payload, string signature)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        req.Headers.Add("Stripe-Signature", signature);
        using var resp = await env.Api.SendAsync(req);
        return resp.StatusCode;
    }

    [Fact]
    public async Task Webhook_da_Stripe_assinado_muda_o_plano_e_espelha_a_fatura_cancelamento_volta_ao_sandbox()
    {
        var (tenant, _) = await env.CreateTenantWithIdAsync("Stripe", GoldenFixture.NewCnpj());
        var now = env.Clock.GetUtcNow().ToUnixTimeSeconds();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var checkout = $$$$$"""{"id":"evt_co_{{{{{suffix}}}}}","type":"checkout.session.completed","data":{"object":{"mode":"subscription","customer":"cus_{{{{{suffix}}}}}","subscription":"sub_{{{{{suffix}}}}}","metadata":{"tenant_id":"{{{{{tenant}}}}}","plan_code":"ESSENCIAL"}}}}""";

        Assert.Equal(HttpStatusCode.BadRequest, await PostWebhookAsync(checkout, "t=1,v1=00"));                       // assinatura inválida
        Assert.Equal(HttpStatusCode.BadRequest, await PostWebhookAsync(checkout, Sign(checkout, now - 3600)));        // fora da tolerância
        Assert.Equal(HttpStatusCode.OK, await PostWebhookAsync(checkout, Sign(checkout, now)));
        Assert.Equal(HttpStatusCode.OK, await PostWebhookAsync(checkout, Sign(checkout, now)));                       // reentrega: idempotente

        var invoice = $$$$$"""{"id":"evt_in_{{{{{suffix}}}}}","type":"invoice.paid","data":{"object":{"id":"in_{{{{{suffix}}}}}","customer":"cus_{{{{{suffix}}}}}","number":"TG-0001","status":"paid","currency":"brl","amount_due":14900,"amount_paid":14900,"hosted_invoice_url":"https://invoice.stripe.test/x","period_start":{{{{{now}}}}},"period_end":{{{{{now + 2_592_000}}}}}}}}""";
        Assert.Equal(HttpStatusCode.OK, await PostWebhookAsync(invoice, Sign(invoice, now)));

        await using var billing = NpgsqlDataSource.Create(env.ConnectionFor("tg_billing"));
        await using var valkey = await ConnectionMultiplexer.ConnectAsync(env.ValkeyConnection);
        var processor = new StripeEventProcessor(new BillingDataSource(billing), valkey, NullLogger<StripeEventProcessor>.Instance);
        await processor.RunOnceAsync(CancellationToken.None);

        var su = billing;   // tg_billing lê tenants e faturas (RLS forçado vale até para o dono das tabelas)
        async Task<(string Plan, string? Status, string? Sub)> Tenant()
        {
            await using var cmd = su.CreateCommand("SELECT plan_code, subscription_status, stripe_subscription_id FROM tenants WHERE id = $1");
            cmd.Parameters.Add(new() { Value = tenant });
            await using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            return (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
        }

        Assert.Equal(("ESSENCIAL", "active", $"sub_{suffix}"), await Tenant());
        await using (var inv = su.CreateCommand("SELECT amount_paid_cents, status FROM invoices WHERE id = $1"))
        {
            inv.Parameters.Add(new() { Value = $"in_{suffix}" });
            await using var r = await inv.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            Assert.Equal((14900L, "paid"), (r.GetInt64(0), r.GetString(1)));
        }

        var deleted = $$$$$"""{"id":"evt_del_{{{{{suffix}}}}}","type":"customer.subscription.deleted","data":{"object":{"id":"sub_{{{{{suffix}}}}}","customer":"cus_{{{{{suffix}}}}}","status":"canceled","metadata":{"tenant_id":"{{{{{tenant}}}}}","plan_code":"ESSENCIAL"}}}}""";
        Assert.Equal(HttpStatusCode.OK, await PostWebhookAsync(deleted, Sign(deleted, now)));
        await processor.RunOnceAsync(CancellationToken.None);
        Assert.Equal(("SANDBOX", "canceled", (string?)null), await Tenant());
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < until, "condição não atingida no tempo");
            await Task.Delay(300);
        }
    }
}
