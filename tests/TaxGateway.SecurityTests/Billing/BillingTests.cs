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

/// <summary>Provedor em memória com a mesma semântica de deduplicação do provedor real (chave idempotente).</summary>
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

    /// <summary>
    /// D-R01: assinante da Stripe é cobrado pela assinatura — o fechamento registra o período como SUBSCRIPTION e não
    /// emite fatura avulsa (antes cobrava em dobro). Simulações descontam a franquia do plano antes do excedente.
    /// </summary>
    [Fact]
    public async Task Subscriber_gets_no_standalone_invoice_and_simulations_respect_the_allowance()
    {
        var month = new DateOnly(2026, 10, 1);
        var e = Guid.CreateVersion7();
        var f = Guid.CreateVersion7();
        await using (var su = new NpgsqlConnection(db.SuperuserConnection))
        {
            await su.OpenAsync();
            await using var cmd = new NpgsqlCommand($"""
                INSERT INTO plans (code, rpm, burst, concurrency, batch_max_docs, batch_max_bytes, queue_priority, included_docs, included_cnpjs, price_cents,
                                   overage_doc_millicents, overage_sim_millicents, cnpj_price_cents, included_simulations)
                VALUES ('BILLSUB', 60, 10, 2, 500, 1000000, 'normal', 1000, 5, 14900, 4500, 1000, 1500, 100) ON CONFLICT (code) DO NOTHING;
                INSERT INTO tenants (id, name, root_cnpj, plan_code, kms_key_ref, billing_customer_ref, stripe_subscription_id, subscription_status) VALUES
                    ('{e}', 'Assinante', '{Cnpj(5)}', 'BILLSUB', 'k', 'cus_E', 'sub_E', 'active'),
                    ('{f}', 'Avulso', '{Cnpj(6)}', 'BILLSUB', 'k', 'cus_F', NULL, NULL);
                INSERT INTO usage_ledger (billing_month, tenant_id, metric, cnpj_subject, source_id)
                SELECT '{month:yyyy-MM-dd}', CASE WHEN g <= 10 THEN '{e}'::uuid ELSE '{f}'::uuid END, 'SIMULATION', '{Cnpj(5)}', md5('sub' || g)::uuid
                  FROM generate_series(1, 160) g;
                """, su);
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            await using var billing = NpgsqlDataSource.Create(db.ConnectionFor("tg_billing"));
            var provider = new FakeProvider();
            var service = new BillingService(new BillingDataSource(billing), provider, NullLogger<BillingService>.Instance);
            await service.CloseMonthAsync(month, CancellationToken.None);
            await service.InvoiceAsync(month, CancellationToken.None);

            async Task<(string Status, long Amount)> P(Guid t)
            {
                await using var q = billing.CreateCommand("SELECT status, amount_cents FROM billing_periods WHERE tenant_id = $1 AND billing_month = $2");
                q.Parameters.Add(new() { Value = t });
                q.Parameters.Add(new() { Value = month });
                await using var r = await q.ExecuteReaderAsync();
                await r.ReadAsync();
                return (r.GetString(0), r.GetInt64(1));
            }

            Assert.Equal("SUBSCRIPTION", (await P(e)).Status);
            Assert.DoesNotContain(provider.Charges.Keys, k => k.StartsWith(e.ToString(), StringComparison.Ordinal));
            // Avulso: 149,00 + (150 − 100 incluídas) × 1 centavo.
            Assert.Equal(("INVOICED", 14900L + 50), await P(f));
        }
        finally
        {
            await using var su = new NpgsqlConnection(db.SuperuserConnection);
            await su.OpenAsync();
            await using var clean = new NpgsqlCommand($"""
                DELETE FROM billing_periods WHERE tenant_id IN ('{e}', '{f}');
                DELETE FROM usage_ledger WHERE tenant_id IN ('{e}', '{f}');
                DELETE FROM tenants WHERE id IN ('{e}', '{f}');
                """, su);
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

/// <summary>Cliente da Stripe (D-C03) contra um servidor HTTP falso com a semântica que importa: Idempotency-Key, busca, formulário.</summary>
public sealed class StripeClientTests
{
    private sealed class FakeStripe : HttpMessageHandler
    {
        public List<(string Method, string Path, Dictionary<string, string> Form, string? Idem, string? Version)> Calls { get; } = [];
        public Dictionary<string, JsonObject> ByIdempotency { get; } = [];
        public List<JsonObject> Invoices { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization?.Parameter != "sk_test_fake")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = JsonContent.Create(new JsonObject { ["error"] = new JsonObject { ["message"] = "chave" } }) };
            }

            var form = request.Content is null ? [] : (await request.Content.ReadAsStringAsync(cancellationToken)).Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
            var idem = request.Headers.TryGetValues("Idempotency-Key", out var i) ? i.Single() : null;
            var version = request.Headers.TryGetValues("Stripe-Version", out var v) ? v.Single() : null;
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add((request.Method.Method, path, form, idem, version));
            if (idem is not null && ByIdempotency.TryGetValue(idem, out var cached))
            {
                return Ok(cached);
            }

            JsonObject result = path switch
            {
                "/v1/billing/meters" when request.Method == HttpMethod.Get => new JsonObject { ["data"] = new JsonArray() },
                "/v1/invoices/search" => new JsonObject { ["data"] = new JsonArray([.. Invoices.Where(x => request.RequestUri.Query.Contains(Uri.EscapeDataString(x["metadata"]!["idempotency_key"]!.GetValue<string>()), StringComparison.Ordinal)).Select(x => (JsonNode)x.DeepClone())]) },
                "/v1/invoices" => NewInvoice(form),
                "/v1/checkout/sessions" => new JsonObject { ["id"] = "cs_1", ["url"] = "https://checkout.stripe.test/c/pay/cs_1" },
                _ => new JsonObject { ["id"] = $"obj_{Calls.Count}" },
            };
            if (idem is not null)
            {
                ByIdempotency[idem] = result;
            }

            return Ok(result);
        }

        private JsonObject NewInvoice(Dictionary<string, string> form)
        {
            var inv = new JsonObject { ["id"] = $"in_{Invoices.Count + 1}", ["amount_due"] = 0, ["status"] = "draft", ["metadata"] = new JsonObject { ["idempotency_key"] = form["metadata[idempotency_key]"] } };
            Invoices.Add(inv);
            return inv;
        }

        private static HttpResponseMessage Ok(JsonObject o) => new(HttpStatusCode.OK) { Content = JsonContent.Create(o) };
    }

    private static (StripeClient Client, FakeStripe Fake) New()
    {
        var fake = new FakeStripe();
        return (new StripeClient(new HttpClient(fake), Options.Create(new StripeOptions { SecretKey = "sk_test_fake", ApiBase = new Uri("https://stripe.test/") })), fake);
    }

    [Fact]
    public async Task Preco_medido_e_graduado_com_franquia_a_zero_e_versao_da_api_fixa()
    {
        var (stripe, fake) = New();
        await stripe.CreateMeteredPriceAsync("prod_1", "PROFISSIONAL", "documentos", "mtr_1", 15000, 5000, "v1", CancellationToken.None);
        var call = fake.Calls.Single();
        Assert.Equal("2025-03-31.basil", call.Version);
        Assert.Equal("price:PROFISSIONAL:documentos:v1", call.Idem);
        Assert.Equal("metered", call.Form["recurring[usage_type]"]);
        Assert.Equal("mtr_1", call.Form["recurring[meter]"]);
        Assert.Equal("graduated", call.Form["tiers_mode"]);
        Assert.Equal("15000", call.Form["tiers[0][up_to]"]);
        Assert.Equal("0", call.Form["tiers[0][unit_amount]"]);
        Assert.Equal("inf", call.Form["tiers[1][up_to]"]);
        Assert.Equal("5", call.Form["tiers[1][unit_amount_decimal]"]);   // 5000 milésimos de centavo = 5 centavos = R$ 0,05
    }

    [Fact]
    public async Task Checkout_leva_tenant_e_plano_nos_metadados_e_preco_medido_sem_quantidade()
    {
        var (stripe, fake) = New();
        var tenant = Guid.NewGuid();
        var url = await stripe.CreateCheckoutSessionAsync("cus_1", tenant, "ESSENCIAL", [("price_fixed", false), ("price_docs", true)],
            "https://app/ok", "https://app/cancel", CancellationToken.None);
        Assert.StartsWith("https://checkout.stripe.test/", url, StringComparison.Ordinal);
        var f = fake.Calls.Single().Form;
        Assert.Equal("subscription", f["mode"]);
        Assert.Equal(tenant.ToString(), f["subscription_data[metadata][tenant_id]"]);
        Assert.Equal("ESSENCIAL", f["subscription_data[metadata][plan_code]"]);
        Assert.Equal("1", f["line_items[0][quantity]"]);
        Assert.False(f.ContainsKey("line_items[1][quantity]"));   // preço medido não aceita quantidade
        Assert.Equal("pt-BR", f["locale"]);
    }

    [Fact]
    public async Task Cobranca_avulsa_reenviada_com_a_mesma_chave_nao_duplica()
    {
        var (stripe, fake) = New();
        var provider = new StripeBillingProvider(stripe);
        var charge = new BillingCharge("cus_1", 123456, "TaxGateway ajuste 11/2026", "t:2026-11:v2", new DateOnly(2026, 12, 10));
        var first = await provider.ChargeAsync(charge, CancellationToken.None);
        var again = await provider.ChargeAsync(charge, CancellationToken.None);
        Assert.Equal(first, again);
        Assert.Single(fake.Invoices);
        Assert.Equal("123456", fake.Calls.Single(c => c.Path == "/v1/invoiceitems").Form["amount"]);
        Assert.All(fake.Calls.Where(c => c.Method == "POST"), c => Assert.NotNull(c.Idem));   // toda criação é idempotente
    }

    [Fact]
    public void Assinatura_do_webhook_da_Stripe_e_verificada_com_tolerancia()
    {
        const string secret = "whsec_teste_stripe";
        var body = "{\"id\":\"evt_1\",\"type\":\"invoice.paid\"}"u8.ToArray();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        byte[] signed = [.. "1800000000."u8.ToArray(), .. body];
        var mac = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), signed));
        var header = $"t=1800000000,v1={mac}";
        Assert.True(StripeClient.VerifyWebhook(header, body, secret, now));
        Assert.False(StripeClient.VerifyWebhook(header, body, secret + "x", now));
        Assert.False(StripeClient.VerifyWebhook(header, [.. body, 32], secret, now));
        Assert.False(StripeClient.VerifyWebhook(header, body, secret, now.AddMinutes(6)));
        Assert.False(StripeClient.VerifyWebhook(null, body, secret, now));
        Assert.False(StripeClient.VerifyWebhook(header, body, "", now));   // sem segredo configurado, nada passa
    }

    [Fact]
    public async Task Sem_chave_configurada_o_cliente_recusa_com_503()
    {
        var stripe = new StripeClient(new HttpClient(new FakeStripe()), Options.Create(new StripeOptions()));
        Assert.False(stripe.Enabled);
        var ex = await Assert.ThrowsAsync<StripeException>(() => stripe.CreateCustomerAsync(Guid.NewGuid(), "x", null, null, CancellationToken.None));
        Assert.Equal(503, ex.Status);
    }
}
