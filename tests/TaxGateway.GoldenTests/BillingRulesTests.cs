using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Npgsql;

using StackExchange.Redis;

using TaxGateway.Api.Simulation;
using TaxGateway.Core.Tenancy;
using TaxGateway.Infrastructure.Billing;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.GoldenTests;

/// <summary>Teto de excedente (D-R08) e régua de inadimplência (D-R09) contra a pilha real.</summary>
[Collection(GoldenCollection.Name)]
public sealed class BillingRulesTests(GoldenFixture env)
{
    private async Task<(HttpStatusCode Status, JsonNode? Body)> AdminAsync(HttpMethod m, string path, object? body = null)
    {
        using var req = new HttpRequestMessage(m, path) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Add("X-Admin-Key", GoldenFixture.OperatorKey);
        using var resp = await env.Api.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> ApiAsync(string key, HttpMethod m, string path, JsonObject? body = null)
    {
        using var req = new HttpRequestMessage(m, path) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Add("X-Api-Key", key);
        using var resp = await env.Api.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private static JsonObject Simulation(string cnpj) =>
        JsonNode.Parse(TaxGateway.Api.Http.OpenApiDocs.Catalog["POST /v1/calculo/simular"].Body!.Replace("11222333000181", cnpj, StringComparison.Ordinal))!.AsObject();

    private async Task<long> LedgerSimsAsync(Guid tenant)
    {
        var db = env.Services.GetRequiredService<ITenantDb>();
        return await db.InTenantAsync(new TenantId(tenant), async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("SELECT count(*) FROM usage_ledger WHERE metric = 'SIMULATION'");
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }, CancellationToken.None);
    }

    /// <summary>Simula e espera a linha de cobrança (gravação assíncrona); zera o cache de franquia para a próxima decisão.</summary>
    private async Task<HttpStatusCode> SimulateAsync(Guid tenant, string key, string cnpj)
    {
        var before = await LedgerSimsAsync(tenant);
        var r = await ApiAsync(key, HttpMethod.Post, "/v1/calculo/simular", Simulation(cnpj));
        if (r.Status == HttpStatusCode.OK)
        {
            for (var i = 0; i < 50 && await LedgerSimsAsync(tenant) == before; i++)
            {
                await Task.Delay(100);
            }
        }

        var month = SimulationService.BillingMonth(env.Clock.GetUtcNow());
        env.Services.GetRequiredService<IMemoryCache>().Remove($"quota:{new TenantId(tenant)}:{month:yyyyMM}");
        return r.Status;
    }

    /// <summary>
    /// D-R08: fora da franquia, o plano pago só passa até o teto definido. Teto 0 (padrão) = para ao esgotar a franquia;
    /// teto de 2 centavos com excedente de 1 centavo por simulação = mais 2 simulações; avisos de franquia e de teto.
    /// </summary>
    [Fact]
    public async Task Overage_stops_at_the_cap_and_the_default_cap_is_zero()
    {
        var plan = $"TCAP{Random.Shared.Next(1000, 9999)}";
        Assert.Equal(HttpStatusCode.OK, (await AdminAsync(HttpMethod.Put, $"/admin/v1/planos/{plan}", new
        {
            name = "Teste teto",
            price_cents = 100,
            included_docs = 10,
            included_simulations = 2,
            overage_doc_millicents = 1000,
            overage_sim_millicents = 1000,
            rpm = 600,
            burst = 100,
            concurrency = 10,
            is_public = false,
            self_service = false,
            active = true,
        })).Status);
        var cnpj = GoldenFixture.NewCnpj();
        var (tenant, key) = await env.CreateTenantWithIdAsync("Teto", cnpj);
        Assert.Equal(HttpStatusCode.OK, (await AdminAsync(HttpMethod.Post, $"/admin/v1/tenants/{tenant}/plano", new { plano = plan })).Status);

        Assert.Equal(HttpStatusCode.OK, await SimulateAsync(tenant, key, cnpj));
        Assert.Equal(HttpStatusCode.OK, await SimulateAsync(tenant, key, cnpj));   // franquia (2) usada
        var blocked = await ApiAsync(key, HttpMethod.Post, "/v1/calculo/simular", Simulation(cnpj));
        Assert.Equal(HttpStatusCode.PaymentRequired, blocked.Status);
        Assert.Equal("ERR_TETO_EXCEDENTE", blocked.Body!["code"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.OK, (await AdminAsync(HttpMethod.Put, $"/admin/v1/tenants/{tenant}/teto", new { teto_centavos = 2 })).Status);
        Assert.Equal(HttpStatusCode.OK, await SimulateAsync(tenant, key, cnpj));   // excedente 1 centavo
        Assert.Equal(HttpStatusCode.OK, await SimulateAsync(tenant, key, cnpj));   // excedente 2 centavos (= teto)
        Assert.Equal(HttpStatusCode.PaymentRequired, await SimulateAsync(tenant, key, cnpj));

        var avisos = (await ApiAsync(key, HttpMethod.Get, "/v1/avisos")).Body!["avisos"]!.AsArray().Select(a => a!["tipo"]!.GetValue<string>()).ToList();
        Assert.Contains("FRANQUIA_ESGOTADA", avisos);
        Assert.Contains("TETO_ATINGIDO", avisos);
    }

    private static string Sign(string payload, long t)
    {
        byte[] signed = [.. Encoding.UTF8.GetBytes($"{t}."), .. Encoding.UTF8.GetBytes(payload)];
        return $"t={t},v1={Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(GoldenFixture.StripeWebhookSecret), signed))}";
    }

    /// <summary>
    /// D-R09: falha de pagamento abre a régua — aviso no dia 0, consumo bloqueado (consulta liberada) no dia 10,
    /// suspensão no dia 15 — e o pagamento devolve o acesso na hora.
    /// </summary>
    [Fact]
    public async Task Dunning_restricts_on_day_10_suspends_on_day_15_and_payment_restores()
    {
        var cnpj = GoldenFixture.NewCnpj();
        var (tenant, key) = await env.CreateTenantWithIdAsync("Inadimplente", cnpj);
        var customer = $"cus_dun_{Guid.NewGuid():N}"[..20];
        await using var billing = NpgsqlDataSource.Create(env.ConnectionFor("tg_billing"));
        await using (var set = billing.CreateCommand("UPDATE tenants SET billing_customer_ref = $2 WHERE id = $1"))
        {
            set.Parameters.Add(new() { Value = tenant });
            set.Parameters.Add(new() { Value = customer });
            await set.ExecuteNonQueryAsync();
        }

        await using var valkey = await ConnectionMultiplexer.ConnectAsync(env.ValkeyConnection);
        var processor = new StripeEventProcessor(new BillingDataSource(billing), valkey, NullLogger<StripeEventProcessor>.Instance);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        async Task Event(string type, string id)
        {
            var json = $$$$"""{"id":"evt_{{{{id}}}}_{{{{customer}}}}","type":"{{{{type}}}}","created":{{{{now}}}},"data":{"object":{"id":"in_{{{{id}}}}_{{{{customer}}}}","customer":"{{{{customer}}}}","status":"open","currency":"brl","amount_due":14900,"amount_paid":0}}}""";
            using var req = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            req.Headers.Add("Stripe-Signature", Sign(json, env.Clock.GetUtcNow().ToUnixTimeSeconds()));
            Assert.Equal(HttpStatusCode.OK, (await env.Api.SendAsync(req)).StatusCode);
            await processor.RunOnceAsync(CancellationToken.None);
        }

        async Task<string> StatusAsync()
        {
            await using var q = billing.CreateCommand("SELECT status FROM tenants WHERE id = $1");
            q.Parameters.Add(new() { Value = tenant });
            return (string)(await q.ExecuteScalarAsync())!;
        }

        DunningWorker Worker(int days) => new(new BillingDataSource(billing), valkey, new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(days).AddHours(1)), NullLogger<DunningWorker>.Instance);

        await Event("invoice.payment_failed", "f1");
        Assert.True(await Worker(0).RunOnceAsync(CancellationToken.None) >= 1);
        Assert.Contains((await ApiAsync(key, HttpMethod.Get, "/v1/avisos")).Body!["avisos"]!.AsArray(), a => a!["tipo"]!.GetValue<string>() == "PAGAMENTO_PENDENTE");
        Assert.Equal("ACTIVE", await StatusAsync());

        await Worker(10).RunOnceAsync(CancellationToken.None);
        Assert.Equal("RESTRICTED", await StatusAsync());
        await WaitAsync(async () => (await ApiAsync(key, HttpMethod.Post, "/v1/calculo/simular", Simulation(cnpj))).Status == HttpStatusCode.PaymentRequired);
        Assert.Equal("ERR_PAGAMENTO_PENDENTE", (await ApiAsync(key, HttpMethod.Post, "/v1/calculo/simular", Simulation(cnpj))).Body!["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await ApiAsync(key, HttpMethod.Get, "/v1/auditoria/documentos")).Status);   // consulta liberada

        await Worker(15).RunOnceAsync(CancellationToken.None);
        Assert.Equal("SUSPENDED", await StatusAsync());
        await WaitAsync(async () => (await ApiAsync(key, HttpMethod.Get, "/v1/me")).Status == HttpStatusCode.Forbidden);

        await Event("invoice.paid", "p1");
        Assert.Equal("ACTIVE", await StatusAsync());
        await WaitAsync(async () => (await ApiAsync(key, HttpMethod.Post, "/v1/calculo/simular", Simulation(cnpj))).Status == HttpStatusCode.OK);
        Assert.Contains((await ApiAsync(key, HttpMethod.Get, "/v1/avisos")).Body!["avisos"]!.AsArray(), a => a!["tipo"]!.GetValue<string>() == "PAGAMENTO_REGULARIZADO");
    }

    /// <summary>
    /// D-R32: a vida da assinatura vira aviso (portal + e-mail): ativação, troca de plano, fatura emitida e vencendo,
    /// pagamento recebido, renovação próxima e cancelamento — um por fato, mesmo com reentrega do evento.
    /// </summary>
    [Fact]
    public async Task Ciclo_da_assinatura_gera_um_aviso_por_fato()
    {
        var cnpj = GoldenFixture.NewCnpj();
        var (tenant, key) = await env.CreateTenantWithIdAsync("Ciclo de vida", cnpj);
        var customer = $"cus_cv_{Guid.NewGuid():N}"[..20];
        var sub = "sub_" + customer;
        await using var billing = NpgsqlDataSource.Create(env.ConnectionFor("tg_billing"));
        await using (var set = billing.CreateCommand("UPDATE tenants SET billing_customer_ref = $2, stripe_subscription_id = NULL, subscription_status = NULL WHERE id = $1"))
        {
            set.Parameters.Add(new() { Value = tenant });
            set.Parameters.Add(new() { Value = customer });
            await set.ExecuteNonQueryAsync();
        }

        await using var valkey = await ConnectionMultiplexer.ConnectAsync(env.ValkeyConnection);
        var processor = new StripeEventProcessor(new BillingDataSource(billing), valkey, NullLogger<StripeEventProcessor>.Instance);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var seq = 0;
        async Task Event(string type, string obj)
        {
            var json = $$$"""{"id":"evt_{{{++seq}}}_{{{customer}}}","type":"{{{type}}}","created":{{{now + seq}}},"data":{"object":{{{obj}}}}}""";
            for (var i = 0; i < 2; i++)   // reentrega: mesmo evento duas vezes
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                req.Headers.Add("Stripe-Signature", Sign(json, env.Clock.GetUtcNow().ToUnixTimeSeconds()));
                Assert.Equal(HttpStatusCode.OK, (await env.Api.SendAsync(req)).StatusCode);
            }

            await processor.RunOnceAsync(CancellationToken.None);
        }

        string Sub(string status, string plan) =>
            $$"""{"id":"{{sub}}","customer":"{{customer}}","status":"{{status}}","metadata":{"plan_code":"{{plan}}"},"current_period_end":{{now + 2_592_000}}}""";
        string Invoice(string id, string status, long due, long paid, string extra = "") =>
            $$"""{"id":"{{id}}_{{customer}}","customer":"{{customer}}","number":"TG-{{id}}","status":"{{status}}","currency":"brl","amount_due":{{due}},"amount_paid":{{paid}}{{extra}}}""";

        await Event("customer.subscription.created", Sub("active", "STARTER"));
        await Event("customer.subscription.updated", Sub("active", "GROWTH"));
        await Event("invoice.finalized", Invoice("in1", "open", 14900, 0, $",\"due_date\":{now + 2 * 86_400}"));
        await Event("invoice.upcoming", Invoice("up", "draft", 29900, 0, $",\"next_payment_attempt\":{now + 5 * 86_400},\"subscription\":\"{sub}\""));
        await using (var due = env.WorkerServices.GetRequiredService<NpgsqlDataSource>().CreateCommand("SELECT app.notify_invoices_due()"))
        {
            Assert.True((int)(await due.ExecuteScalarAsync())! >= 1);
        }

        await Event("invoice.paid", Invoice("in1", "paid", 14900, 14900));
        await Event("customer.subscription.deleted", Sub("canceled", "GROWTH"));

        var avisos = (await ApiAsync(key, HttpMethod.Get, "/v1/avisos")).Body!["avisos"]!.AsArray()
            .Select(a => (Tipo: a!["tipo"]!.GetValue<string>(), Titulo: a["titulo"]!.GetValue<string>(), Texto: a["mensagem"]?.GetValue<string>() ?? a["texto"]?.GetValue<string>() ?? "")).ToList();
        foreach (var kind in (string[])["ASSINATURA_ATIVA", "PLANO_ALTERADO", "FATURA_EMITIDA", "RENOVACAO_PROXIMA", "FATURA_VENCENDO", "PAGAMENTO_RECEBIDO", "ASSINATURA_CANCELADA"])
        {
            Assert.True(avisos.Count(a => a.Tipo == kind) == 1, $"{kind}: {string.Join(" | ", avisos.Select(a => a.Tipo))}");
        }

        Assert.Contains(avisos, a => a.Tipo == "FATURA_EMITIDA" && a.Texto.Contains("R$ 149,00", StringComparison.Ordinal));
        Assert.Contains(avisos, a => a.Tipo == "PLANO_ALTERADO" && a.Titulo.Contains("Growth", StringComparison.OrdinalIgnoreCase));

        // Chave de API nova: aviso de segurança para dono e administradores.
        var created = await ApiAsync(key, HttpMethod.Post, "/v1/api-keys", new JsonObject { ["nome"] = "erp-teste", ["scopes"] = new JsonArray("simulacao:write") });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Contains((await ApiAsync(key, HttpMethod.Get, "/v1/avisos")).Body!["avisos"]!.AsArray(),
            a => a!["tipo"]!.GetValue<string>() == "SEGURANCA" && a["titulo"]!.GetValue<string>() == "Nova chave de API criada");
    }

    [Fact]
    public void Valor_em_reais_sem_depender_de_cultura()
    {
        Assert.Equal("R$ 0,00", StripeEventProcessor.Money(0));
        Assert.Equal("R$ 1.234.567,89", StripeEventProcessor.Money(123_456_789));
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < until, "condição não atingida no tempo");
            await Task.Delay(300);
        }
    }
}
