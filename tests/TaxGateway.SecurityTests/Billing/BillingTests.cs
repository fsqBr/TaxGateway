using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Npgsql;

using TaxGateway.Infrastructure.Billing;
using TaxGateway.SecurityTests.Rls;

namespace TaxGateway.SecurityTests.Billing;

/// <summary>Provedor em memória com a mesma semântica de deduplicação do Asaas (externalReference).</summary>
public sealed class FakeProvider : IBillingProvider
{
    public ConcurrentDictionary<string, (string Ref, long Amount)> Charges { get; } = new();
    private int _createCalls;

    public int CreateCalls => _createCalls;

    public string Name => "FAKE";

    public Task<string> ChargeAsync(BillingCharge charge, CancellationToken ct)
    {
        var created = Charges.GetOrAdd(charge.IdempotencyKey, _ =>
        {
            Interlocked.Increment(ref _createCalls);
            return ($"pay_{Charges.Count + 1}", charge.AmountCents);
        });
        return Task.FromResult(created.Ref);
    }

    public Task<(string Ref, long AmountCents)?> FindAsync(string idempotencyKey, CancellationToken ct) =>
        Task.FromResult(Charges.TryGetValue(idempotencyKey, out var c) ? ((string, long)?)(c.Ref, c.Amount) : null);
}

/// <summary>Critério de saída da Fase 7 (D-701 a D-705).</summary>
[Collection(PostgresCollection.Name)]
public sealed class BillingTests(PostgresFixture db)
{
    private static readonly DateOnly Month = new(2026, 12, 1);

    [Fact]
    public async Task Month_with_one_million_events_reconciles_to_zero_and_resend_does_not_duplicate()
    {
        var c = Guid.CreateVersion7();
        var d = Guid.CreateVersion7();
        await using (var su = new NpgsqlConnection(db.SuperuserConnection))
        {
            await su.OpenAsync();
            var sw = Stopwatch.StartNew();
            await using var cmd = new NpgsqlCommand($"""
                INSERT INTO plans (code, rpm, burst, concurrency, batch_max_docs, batch_max_bytes, queue_priority, included_docs, included_cnpjs, price_cents,
                                   overage_doc_millicents, overage_sim_millicents, cnpj_price_cents)
                VALUES ('BILLTEST', 60, 10, 2, 500, 1000000, 'normal', 100000, 2, 49900, 4500, 800, 1500) ON CONFLICT (code) DO NOTHING;
                INSERT INTO tenants (id, name, root_cnpj, plan_code, kms_key_ref, billing_customer_ref) VALUES
                    ('{c}', 'Billing C', '{Cnpj(1)}', 'BILLTEST', 'k', 'cus_C'), ('{d}', 'Billing D', '{Cnpj(2)}', 'BILLTEST', 'k', NULL);
                -- 1.000.000 de eventos no mês: C = 700 mil auditorias + 150 mil simulações; D = 100 mil + 50 mil.
                INSERT INTO usage_ledger (billing_month, tenant_id, metric, cnpj_subject, source_id)
                SELECT '2026-12-01', CASE WHEN g <= 850000 THEN '{c}'::uuid ELSE '{d}'::uuid END,
                       CASE WHEN g <= 700000 OR (g > 850000 AND g <= 950000) THEN 'DOC_AUDIT' ELSE 'SIMULATION' END,
                       '{Cnpj(1)}', md5('bill' || g)::uuid
                  FROM generate_series(1, 1000000) g;
                INSERT INTO cnpj_monthly_activity (tenant_id, billing_month, cnpj)
                SELECT '{c}'::uuid, '2026-12-01'::date, '{Cnpj(1)}' UNION ALL SELECT '{c}', '2026-12-01', '{Cnpj(3)}' UNION ALL
                SELECT '{c}', '2026-12-01', '{Cnpj(4)}' UNION ALL SELECT '{d}', '2026-12-01', '{Cnpj(2)}';
                """, su);
            cmd.CommandTimeout = 300;
            await cmd.ExecuteNonQueryAsync();
            Assert.True(sw.Elapsed < TimeSpan.FromMinutes(5));
        }

        try
        {
            await RunAsync(c, d);
        }
        finally
        {
            // Fixture compartilhada: remove o que o teste criou (superusuário; o ledger é append-only para os roles).
            await using var su = new NpgsqlConnection(db.SuperuserConnection);
            await su.OpenAsync();
            await using var clean = new NpgsqlCommand($"""
                DELETE FROM billing_adjustments WHERE tenant_id IN ('{c}', '{d}');
                DELETE FROM billing_periods WHERE tenant_id IN ('{c}', '{d}');
                DELETE FROM usage_ledger WHERE tenant_id IN ('{c}', '{d}');
                DELETE FROM cnpj_monthly_activity WHERE tenant_id IN ('{c}', '{d}');
                DELETE FROM tenants WHERE id IN ('{c}', '{d}');
                """, su);
            clean.CommandTimeout = 300;
            await clean.ExecuteNonQueryAsync();
        }
    }

    private async Task RunAsync(Guid c, Guid d)
    {
        await using var billing = NpgsqlDataSource.Create(db.ConnectionFor("tg_billing"));
        var provider = new FakeProvider();
        var service = new BillingService(new BillingDataSource(billing), provider, NullLogger<BillingService>.Instance);

        Assert.True(await service.CloseMonthAsync(Month, CancellationToken.None) >= 2);
        var invoiced = await service.InvoiceAsync(Month, CancellationToken.None);
        Assert.True(invoiced >= 1);

        // Valor de C: 499,00 + (700.000 − 100.000) × 4,5 centavos + 150.000 × 0,8 centavo + (3 − 2) CNPJ × 15,00.
        var expectedC = 49900L + (600000L * 4500 / 1000) + (150000L * 800 / 1000) + 1500;
        var periodC = await Period(billing, c);
        Assert.Equal((700000L, 150000L, 3, expectedC, "INVOICED"), periodC);
        Assert.Equal(expectedC, provider.Charges[BillingService.Key(c, Month, 1)].Amount);
        Assert.Equal("CLOSED", (await Period(billing, d)).Status);   // sem cliente no provedor: fica fechado, não faturado

        var reconcile = await service.ReconcileAsync(Month, CancellationToken.None);
        Assert.All(reconcile, l => Assert.True(l.Ok, $"divergência {l}"));
        Assert.Contains(reconcile, l => l.Tenant == c && l.LedgerDocs == 700000 && l.ProviderAmountCents == expectedC);

        // Reenvio (ex.: worker caiu depois de cobrar e antes de marcar INVOICED): nenhuma cobrança nova.
        var calls = provider.CreateCalls;
        await using (var reset = billing.CreateCommand($"UPDATE billing_periods SET status = 'CLOSED' WHERE tenant_id = '{c}' AND billing_month = '2026-12-01'"))
        {
            await reset.ExecuteNonQueryAsync();
        }

        await service.InvoiceAsync(Month, CancellationToken.None);
        Assert.Equal(calls, provider.CreateCalls);
        Assert.Single(provider.Charges.Keys, k => k.StartsWith(c.ToString(), StringComparison.Ordinal));

        // Fechar de novo não altera um mês faturado.
        await service.CloseMonthAsync(Month, CancellationToken.None);
        Assert.Equal("INVOICED", (await Period(billing, c)).Status);

        // Ajustes: v2 (débito, vai ao provedor) e v3 (crédito, abatido depois); a v1 nunca é editada.
        var v2 = await service.AdjustAsync(c, Month, 2500, "uso faturado a menor", "teste", CancellationToken.None);
        var v3 = await service.AdjustAsync(c, Month, -1000, "desconto comercial", "teste", CancellationToken.None);
        Assert.Equal((2, "INVOICED"), v2);
        Assert.Equal((3, "CREDITO_A_ABATER"), v3);
        Assert.Equal(2500, provider.Charges[BillingService.Key(c, Month, 2)].Amount);
        Assert.Equal(expectedC, (await Period(billing, c)).Amount);
    }

    [Fact]
    public async Task Billing_role_cannot_touch_documents_or_change_the_ledger()
    {
        await using var billing = NpgsqlDataSource.Create(db.ConnectionFor("tg_billing"));
        foreach (var sql in new[] { "SELECT count(*) FROM fiscal_documents", "SELECT count(*) FROM api_keys", "DELETE FROM usage_ledger", "UPDATE usage_ledger SET quantity = 2" })
        {
            await using var cmd = billing.CreateCommand(sql);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    private static async Task<(long Docs, long Sims, int Cnpjs, long Amount, string Status)> Period(NpgsqlDataSource ds, Guid tenant)
    {
        await using var cmd = ds.CreateCommand($"SELECT docs_count, simulations_count, active_cnpjs, amount_cents, status FROM billing_periods WHERE tenant_id = '{tenant}' AND billing_month = '2026-12-01'");
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return (r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetInt64(3), r.GetString(4));
    }

    private static string Cnpj(int n)
    {
        var b = $"{90000000 + n:D8}0001";
        static int Dv(string s, int[] w) { var r = s.Select((ch, i) => (ch - 48) * w[i]).Sum() % 11; return r < 2 ? 0 : 11 - r; }
        var d1 = Dv(b, [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]);
        var d2 = Dv(b + d1, [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]);
        return $"{b}{d1}{d2}";
    }
}

/// <summary>Adaptador do Asaas contra um servidor HTTP falso com a mesma API (GET/POST /payments).</summary>
public sealed class AsaasProviderTests
{
    private sealed class FakeAsaas : HttpMessageHandler
    {
        public List<JsonObject> Payments { get; } = [];
        public int Posts;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.GetValues("access_token").Single() != "chave-teste")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (request.Method == HttpMethod.Get)
            {
                var reference = Uri.UnescapeDataString(request.RequestUri!.Query.Split("externalReference=")[1]);
                var found = Payments.Where(p => p["externalReference"]!.GetValue<string>() == reference).Select(p => (JsonNode)p.DeepClone()).ToArray();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new JsonObject { ["data"] = new JsonArray(found) }) };
            }

            Posts++;
            var body = (await request.Content!.ReadFromJsonAsync<JsonObject>(cancellationToken))!;
            body["id"] = $"pay_{Payments.Count + 1}";
            Payments.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
    }

    [Fact]
    public async Task Resending_the_same_key_returns_the_existing_payment()
    {
        var fake = new FakeAsaas();
        var asaas = new AsaasBillingProvider(new HttpClient(fake), Options.Create(new AsaasOptions { BaseUrl = new Uri("https://asaas.teste/v3/"), ApiKey = "chave-teste" }));
        var charge = new BillingCharge("cus_1", 123456, "TaxGateway 11/2026", "t:2026-11:v1", new DateOnly(2026, 12, 10));

        var first = await asaas.ChargeAsync(charge, CancellationToken.None);
        var again = await asaas.ChargeAsync(charge, CancellationToken.None);
        Assert.Equal(first, again);
        Assert.Equal(1, fake.Posts);
        Assert.Equal(1234.56m, fake.Payments[0]["value"]!.GetValue<decimal>());
        Assert.Equal("t:2026-11:v1", fake.Payments[0]["externalReference"]!.GetValue<string>());
        Assert.Equal((first, 123456L), (await asaas.FindAsync("t:2026-11:v1", CancellationToken.None))!.Value);
    }
}
