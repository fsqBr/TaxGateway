using System.Globalization;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using NpgsqlTypes;

namespace TaxGateway.Infrastructure.Billing;

/// <summary>
/// Eventos da Stripe (D-C04). A API só verifica a assinatura e grava em <c>stripe_events</c>; aqui, com o papel
/// tg_billing, cada evento é aplicado uma vez (idempotente pelo id): assinatura ativa muda o plano do tenant, cancelada
/// volta ao Sandbox, faturas são espelhadas para o portal.
/// </summary>
public sealed partial class StripeEventProcessor(BillingDataSource db, StackExchange.Redis.IConnectionMultiplexer valkey, ILogger<StripeEventProcessor> log) : BackgroundService
{
    public const string FallbackPlan = "SANDBOX";

    /// <summary>Mesmo canal do cache de perfil do tenant na API (TenantProfileCache): o plano novo vale na hora.</summary>
    public const string TenantChangedChannel = "tenant:changed";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogError(log, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var n = 0;
        while (true)
        {
            await using var conn = await db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            string id, type;
            JsonObject payload;
            await using (var pick = new NpgsqlCommand("""
                SELECT id, type, payload::text FROM stripe_events
                 WHERE processed_at IS NULL AND attempts < 10 ORDER BY received_at LIMIT 1 FOR UPDATE SKIP LOCKED
                """, conn, tx))
            await using (var r = await pick.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    return n;
                }

                (id, type, payload) = (r.GetString(0), r.GetString(1), (JsonObject)JsonNode.Parse(r.GetString(2))!);
            }

            try
            {
                var changed = await ApplyAsync(conn, tx, type, payload["data"]?["object"] as JsonObject ?? [], ct).ConfigureAwait(false);
                await using var done = new NpgsqlCommand("UPDATE stripe_events SET processed_at = now(), attempts = attempts + 1, last_error = NULL WHERE id = $1", conn, tx);
                done.Parameters.Add(new() { Value = id });
                await done.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                TaxGatewayMetrics.StripeEvents.Add(1, new KeyValuePair<string, object?>("tipo", type), new KeyValuePair<string, object?>("resultado", "ok"));
                foreach (var tenant in changed)
                {
                    try
                    {
                        await valkey.GetSubscriber().PublishAsync(StackExchange.Redis.RedisChannel.Literal(TenantChangedChannel), tenant.ToString()).ConfigureAwait(false);
                    }
                    catch (StackExchange.Redis.RedisException)
                    {
                        // o cache de perfil expira em 30 s
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                await using var fail = db.DataSource.CreateCommand("UPDATE stripe_events SET attempts = attempts + 1, last_error = $2 WHERE id = $1");
                fail.Parameters.Add(new() { Value = id });
                fail.Parameters.Add(new() { Value = StripeText.Truncate(ex.Message, 500) });
                await fail.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                LogEventFailed(log, ex, id, type);
                TaxGatewayMetrics.StripeEvents.Add(1, new KeyValuePair<string, object?>("tipo", type), new KeyValuePair<string, object?>("resultado", "erro"));
            }

            n++;
        }
    }

    /// <summary>Aplica o evento; devolve os tenants cujo plano/assinatura mudou (para avisar as instâncias da API).</summary>
    private static async Task<List<Guid>> ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string type, JsonObject o, CancellationToken ct)
    {
        var changed = new List<Guid>();
        switch (type)
        {
            case "checkout.session.completed":
                if (Str(o, "mode") == "subscription" && Guid.TryParse(o["metadata"]?["tenant_id"]?.GetValue<string>(), out var t))
                {
                    changed.AddRange(await ExecAsync(conn, tx, """
                        UPDATE tenants SET billing_provider = 'STRIPE', billing_customer_ref = $2, stripe_subscription_id = $3,
                               plan_code = coalesce((SELECT code FROM plans WHERE code = $4), plan_code), subscription_status = 'active'
                         WHERE id = $1 RETURNING id
                        """, ct, t, Str(o, "customer"), Str(o, "subscription"), o["metadata"]?["plan_code"]?.GetValue<string>()).ConfigureAwait(false));
                }

                break;
            case "customer.subscription.created" or "customer.subscription.updated" or "customer.subscription.deleted":
                {
                    var status = Str(o, "status");
                    var deleted = type.EndsWith("deleted", StringComparison.Ordinal) || status is "canceled" or "incomplete_expired";
                    var plan = o["metadata"]?["plan_code"]?.GetValue<string>();
                    long? periodEnd = o["current_period_end"]?.GetValue<long>()
                        ?? o["items"]?["data"]?.AsArray().Select(i => i?["current_period_end"]?.GetValue<long>()).FirstOrDefault(x => x is not null);
                    // Ativa/em teste: aplica o plano da assinatura. Cancelada: volta ao Sandbox. Inadimplente: mantém o plano e
                    // registra o status (o aviso e a eventual suspensão seguem a régua de cobrança da Stripe).
                    changed.AddRange(await ExecAsync(conn, tx, """
                    UPDATE tenants SET stripe_subscription_id = CASE WHEN $5 THEN NULL ELSE $2 END,
                           subscription_status = $3,
                           current_period_end = CASE WHEN $6::bigint IS NULL THEN current_period_end ELSE to_timestamp($6::bigint) END,
                           plan_code = CASE WHEN $5 THEN $7
                                            WHEN $3 IN ('active','trialing') THEN coalesce((SELECT code FROM plans WHERE code = $4), plan_code)
                                            ELSE plan_code END
                     WHERE billing_customer_ref = $1 OR stripe_subscription_id = $2
                    RETURNING id
                    """, ct, Str(o, "customer"), Str(o, "id"), status, plan, deleted, periodEnd, FallbackPlan).ConfigureAwait(false));
                    break;
                }

            case "invoice.created" or "invoice.finalized" or "invoice.paid" or "invoice.payment_failed" or "invoice.voided" or "invoice.marked_uncollectible" or "invoice.updated":
                await ExecAsync(conn, tx, """
                    INSERT INTO invoices (id, tenant_id, number, status, currency, amount_due_cents, amount_paid_cents, hosted_invoice_url, invoice_pdf, period_start, period_end)
                    SELECT $1, t.id, $3, $4, $5, $6, $7, $8, $9, to_timestamp($10), to_timestamp($11) FROM tenants t WHERE t.billing_customer_ref = $2
                    ON CONFLICT (id) DO UPDATE SET number = EXCLUDED.number, status = EXCLUDED.status, amount_due_cents = EXCLUDED.amount_due_cents,
                        amount_paid_cents = EXCLUDED.amount_paid_cents, hosted_invoice_url = EXCLUDED.hosted_invoice_url, invoice_pdf = EXCLUDED.invoice_pdf,
                        updated_at = now()
                    """, ct, Str(o, "id"), Str(o, "customer"), Str(o, "number"), Str(o, "status") ?? "draft", Str(o, "currency") ?? "brl",
                    o["amount_due"]?.GetValue<long>() ?? 0, o["amount_paid"]?.GetValue<long>() ?? 0, Str(o, "hosted_invoice_url"), Str(o, "invoice_pdf"),
                    (double)(o["period_start"]?.GetValue<long>() ?? 0), (double)(o["period_end"]?.GetValue<long>() ?? 0)).ConfigureAwait(false);
                break;
        }

        return changed;
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static async Task<List<Guid>> ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct, params object?[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var a in args)
        {
            cmd.Parameters.Add(a switch
            {
                null => new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text },
                long l => new NpgsqlParameter { Value = l, NpgsqlDbType = NpgsqlDbType.Bigint },
                _ => new NpgsqlParameter { Value = a },
            });
        }

        var ids = new List<Guid>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            if (r.FieldCount > 0 && r.GetFieldType(0) == typeof(Guid))
            {
                ids.Add(r.GetGuid(0));
            }
        }

        return ids;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha no processador de eventos da Stripe.")]
    private static partial void LogError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Evento {Id} ({Type}) da Stripe falhou; nova tentativa no próximo ciclo.")]
    private static partial void LogEventFailed(ILogger logger, Exception ex, string id, string type);
}

/// <summary>
/// Catálogo na Stripe (D-C05): medidores de documentos (soma) e de CNPJs (último valor), e para cada plano pago um
/// produto com três preços — mensalidade, excedente de documentos e de CNPJs (graduados: a franquia sai a zero). Preço
/// na Stripe é imutável: quando o plano muda no backoffice, cria preços novos (assinaturas existentes seguem no preço
/// antigo até trocarem de plano; documentado na central de ajuda).
/// </summary>
public sealed partial class StripeCatalogSync(BillingDataSource db, StripeClient stripe, IOptions<StripeOptions> options, ILogger<StripeCatalogSync> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!stripe.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogError(log, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var o = options.Value;
        var docsMeter = await stripe.EnsureMeterAsync(o.DocumentsMeterEvent, "Documentos auditados", "sum", ct).ConfigureAwait(false);
        var cnpjMeter = await stripe.EnsureMeterAsync(o.CnpjsMeterEvent, "CNPJs ativos", "last", ct).ConfigureAwait(false);
        var pending = new List<(string Code, string Name, string Desc, long Price, long IncDocs, long DocMilli, int IncCnpjs, long CnpjCents, string? Product, DateTimeOffset Updated)>();
        await using (var cmd = db.DataSource.CreateCommand("""
            SELECT code, name, description, price_cents, included_docs, overage_doc_millicents, coalesce(included_cnpjs, 0), cnpj_price_cents,
                   stripe_product_id, updated_at
              FROM plans WHERE price_cents > 0 AND (stripe_synced_at IS NULL OR stripe_synced_at < updated_at)
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                pending.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt32(6), r.GetInt64(7),
                    r.IsDBNull(8) ? null : r.GetString(8), r.GetFieldValue<DateTimeOffset>(9)));
            }
        }

        foreach (var p in pending)
        {
            var version = p.Updated.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var product = p.Product ?? await stripe.CreateProductAsync(p.Code, p.Name, p.Desc, ct).ConfigureAwait(false);
            var fixedPrice = await stripe.CreateFixedPriceAsync(product, p.Code, p.Price, version, ct).ConfigureAwait(false);
            var docsPrice = await stripe.CreateMeteredPriceAsync(product, p.Code, "documentos", docsMeter, p.IncDocs, p.DocMilli, version, ct).ConfigureAwait(false);
            string? cnpjPrice = p.CnpjCents > 0
                ? await stripe.CreateMeteredPriceAsync(product, p.Code, "cnpjs", cnpjMeter, p.IncCnpjs, p.CnpjCents * 1000, version, ct).ConfigureAwait(false)
                : null;
            await using var upd = db.DataSource.CreateCommand("""
                UPDATE plans SET stripe_product_id = $2, stripe_price_id = $3, stripe_metered_price_id = $4, stripe_cnpj_price_id = $5, stripe_synced_at = now()
                 WHERE code = $1
                """);
            upd.Parameters.Add(new() { Value = p.Code });
            upd.Parameters.Add(new() { Value = product });
            upd.Parameters.Add(new() { Value = fixedPrice });
            upd.Parameters.Add(new() { Value = docsPrice });
            upd.Parameters.Add(new() { Value = (object?)cnpjPrice ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            LogSynced(log, p.Code);
        }

        return pending.Count;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha ao sincronizar o catálogo com a Stripe.")]
    private static partial void LogError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plano {Code} sincronizado com a Stripe.")]
    private static partial void LogSynced(ILogger logger, string code);
}

/// <summary>
/// Consumo medido (D-C06): a cada hora, para cada tenant com assinatura, envia à Stripe o incremento de documentos
/// cobráveis do mês (a partir do ledger) e o total de CNPJs ativos. O identificador de cada evento deriva do total já
/// reportado, então um reenvio depois de falha não duplica; <c>stripe_usage_reports</c> guarda o que já foi enviado
/// para a reconciliação com o fechamento do mês.
/// </summary>
public sealed partial class StripeUsageReporter(BillingDataSource db, StripeClient stripe, IOptions<StripeOptions> options, TimeProvider clock, ILogger<StripeUsageReporter> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!stripe.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogError(log, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "E. South America Standard Time" : "America/Sao_Paulo"));
        var month = new DateOnly(local.Year, local.Month, 1);
        var rows = new List<(Guid Tenant, string Customer, long Docs, long ReportedDocs, long Cnpjs)>();
        await using (var cmd = db.DataSource.CreateCommand("""
            SELECT t.id, t.billing_customer_ref,
                   (SELECT count(*) FROM usage_ledger l WHERE l.tenant_id = t.id AND l.billing_month = $1 AND l.metric = 'DOC_AUDIT'),
                   coalesce((SELECT r.reported_qty FROM stripe_usage_reports r WHERE r.tenant_id = t.id AND r.billing_month = $1 AND r.metric = 'documentos'), 0),
                   (SELECT count(*) FROM cnpj_monthly_activity a WHERE a.tenant_id = t.id AND a.billing_month = $1)
              FROM tenants t
             WHERE t.stripe_subscription_id IS NOT NULL AND t.billing_customer_ref IS NOT NULL
               AND t.subscription_status IN ('active','trialing','past_due')
            """))
        {
            cmd.Parameters.Add(new() { Value = month, NpgsqlDbType = NpgsqlDbType.Date });
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add((r.GetGuid(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4)));
            }
        }

        var o = options.Value;
        foreach (var row in rows)
        {
            var delta = row.Docs - row.ReportedDocs;
            if (delta > 0)
            {
                await stripe.SendMeterEventAsync(o.DocumentsMeterEvent, row.Customer, delta,
                    $"{row.Tenant:N}-{month:yyyyMM}-doc-{row.Docs}", now, ct).ConfigureAwait(false);
                await SaveAsync(row.Tenant, month, "documentos", row.Docs, ct).ConfigureAwait(false);
            }

            await stripe.SendMeterEventAsync(o.CnpjsMeterEvent, row.Customer, row.Cnpjs,
                $"{row.Tenant:N}-{month:yyyyMM}-cnpj-{now:yyyyMMddHH}", now, ct).ConfigureAwait(false);
            await SaveAsync(row.Tenant, month, "cnpjs", row.Cnpjs, ct).ConfigureAwait(false);
        }

        return rows.Count;
    }

    private async Task SaveAsync(Guid tenant, DateOnly month, string metric, long qty, CancellationToken ct)
    {
        await using var cmd = db.DataSource.CreateCommand("""
            INSERT INTO stripe_usage_reports (tenant_id, billing_month, metric, reported_qty) VALUES ($1, $2, $3, $4)
            ON CONFLICT (tenant_id, billing_month, metric) DO UPDATE SET reported_qty = EXCLUDED.reported_qty, updated_at = now()
            """);
        cmd.Parameters.Add(new() { Value = tenant });
        cmd.Parameters.Add(new() { Value = month, NpgsqlDbType = NpgsqlDbType.Date });
        cmd.Parameters.Add(new() { Value = metric });
        cmd.Parameters.Add(new() { Value = qty });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha ao reportar consumo à Stripe.")]
    private static partial void LogError(ILogger logger, Exception ex);
}
