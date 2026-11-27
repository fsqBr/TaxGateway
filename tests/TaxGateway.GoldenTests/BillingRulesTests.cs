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
