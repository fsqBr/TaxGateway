using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using NpgsqlTypes;

namespace TaxGateway.Infrastructure.Billing;

/// <summary>Fonte de dados do role tg_billing (só tabelas de uso/faturamento, políticas nomeadas).</summary>
public sealed class BillingDataSource(NpgsqlDataSource dataSource)
{
    public NpgsqlDataSource DataSource { get; } = dataSource;
}

public sealed record BillingCharge(string CustomerRef, long AmountCents, string Description, string IdempotencyKey, DateOnly DueDate);

/// <summary>Provedor de cobrança (SDD §10.4). A chave idempotente garante que um reenvio nunca duplica a cobrança.</summary>
public interface IBillingProvider
{
    string Name { get; }
    Task<string> ChargeAsync(BillingCharge charge, CancellationToken ct);
    Task<(string Ref, long AmountCents)?> FindAsync(string idempotencyKey, CancellationToken ct);
}

public sealed record PeriodTotals(Guid Tenant, DateOnly Month, long Docs, long Simulations, int ActiveCnpjs, long AmountCents, string Status, string? ProviderRef);

public sealed record ReconcileLine(Guid Tenant, long LedgerDocs, long PeriodDocs, long LedgerSims, long PeriodSims, long PeriodAmountCents, long? ProviderAmountCents)
{
    public bool Ok => LedgerDocs == PeriodDocs && LedgerSims == PeriodSims && (ProviderAmountCents is null || ProviderAmountCents == PeriodAmountCents);
}

/// <summary>
/// Fechamento de ciclo (SDD §10.3; D-701 a D-704): agrega o ledger e a atividade de CNPJ do mês, aplica o plano
/// (mensalidade, franquia, excedente, CNPJs), grava billing_periods (só períodos ainda abertos: fechar de novo não
/// altera um mês já fechado) e envia ao provedor com a chave tenant:mês:v1. Ajustes posteriores são v2, v3...
/// </summary>
public sealed partial class BillingService(BillingDataSource billing, IBillingProvider provider, ILogger<BillingService> log)
{
    private NpgsqlDataSource Db => billing.DataSource;

    public static string Key(Guid tenant, DateOnly month, int version) => $"{tenant}:{month:yyyy-MM}:v{version}";

    /// <summary>Fecha o mês para todos os tenants com uso ou mensalidade. Idempotente.</summary>
    public async Task<int> CloseMonthAsync(DateOnly month, CancellationToken ct)
    {
        await using var cmd = Db.CreateCommand("""
            WITH usage AS (
                SELECT tenant_id, count(*) FILTER (WHERE metric = 'DOC_AUDIT') AS docs, count(*) FILTER (WHERE metric = 'SIMULATION') AS sims
                  FROM usage_ledger WHERE billing_month = $1 GROUP BY tenant_id
            ), cnpjs AS (
                SELECT tenant_id, count(*) AS n FROM cnpj_monthly_activity WHERE billing_month = $1 GROUP BY tenant_id
            ), calc AS (
                SELECT t.id AS tenant_id, coalesce(u.docs, 0) AS docs, coalesce(u.sims, 0) AS sims, coalesce(c.n, 0)::int AS cnpjs,
                       p.price_cents
                       + round(greatest(coalesce(u.docs, 0) - p.included_docs, 0) * p.overage_doc_millicents / 1000.0)::bigint
                       + round(coalesce(u.sims, 0) * p.overage_sim_millicents / 1000.0)::bigint
                       + greatest(coalesce(c.n, 0) - coalesce(p.included_cnpjs, 0), 0) * p.cnpj_price_cents AS amount
                  FROM tenants t JOIN plans p ON p.code = t.plan_code
                  LEFT JOIN usage u ON u.tenant_id = t.id LEFT JOIN cnpjs c ON c.tenant_id = t.id
                 WHERE t.status <> 'CLOSED' AND (u.tenant_id IS NOT NULL OR p.price_cents > 0)
            )
            INSERT INTO billing_periods (tenant_id, billing_month, status, docs_count, simulations_count, active_cnpjs, amount_cents, provider_idempotency_key, closed_at)
            SELECT tenant_id, $1, 'CLOSED', docs, sims, cnpjs, amount, tenant_id::text || ':' || to_char($1, 'YYYY-MM') || ':v1', now() FROM calc
            ON CONFLICT (tenant_id, billing_month) DO UPDATE
               SET status = 'CLOSED', docs_count = EXCLUDED.docs_count, simulations_count = EXCLUDED.simulations_count,
                   active_cnpjs = EXCLUDED.active_cnpjs, amount_cents = EXCLUDED.amount_cents,
                   provider_idempotency_key = EXCLUDED.provider_idempotency_key, closed_at = now()
             WHERE billing_periods.status IN ('OPEN', 'CLOSING')
            """);
        cmd.Parameters.Add(new() { Value = month });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Envia ao provedor os períodos fechados ainda não faturados (D-P07). Exclusão mútua em dois níveis: um advisory
    /// lock global (só uma réplica fatura por vez) e a transição CLOSED → CHARGING por período ANTES de chamar o
    /// provedor (quem não vence a transição não cobra). Falha do provedor devolve o período a CLOSED; queda no meio
    /// deixa CHARGING, reenviado com a mesma chave idempotente pelo <see cref="RecoverChargingAsync"/>.
    /// </summary>
    public async Task<int> InvoiceAsync(DateOnly month, CancellationToken ct)
    {
        await using var lockConn = await Db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (var l = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext('taxgateway:billing:invoice'))", lockConn))
        {
            if (!(bool)(await l.ExecuteScalarAsync(ct).ConfigureAwait(false))!)
            {
                return 0;   // outra réplica (ou o fechamento manual) está faturando
            }
        }

        try
        {
            return await InvoiceLockedAsync(month, ct).ConfigureAwait(false);
        }
        finally
        {
            await using var u = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtext('taxgateway:billing:invoice'))", lockConn);
            await u.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Períodos presos em CHARGING (queda durante a cobrança) voltam a CLOSED; a chave idempotente evita cobrança dupla.</summary>
    public async Task<int> RecoverChargingAsync(TimeSpan olderThan, CancellationToken ct)
    {
        await using var cmd = Db.CreateCommand("UPDATE billing_periods SET status = 'CLOSED' WHERE status = 'CHARGING' AND charging_since < now() - $1");
        cmd.Parameters.Add(new() { Value = olderThan });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<int> InvoiceLockedAsync(DateOnly month, CancellationToken ct)
    {
        var pending = new List<(Guid Tenant, long Amount, string Key, string? Customer)>();
        await using (var cmd = Db.CreateCommand("""
            SELECT b.tenant_id, b.amount_cents, b.provider_idempotency_key, t.billing_customer_ref
              FROM billing_periods b JOIN tenants t ON t.id = b.tenant_id
             WHERE b.billing_month = $1 AND b.status = 'CLOSED'
            """))
        {
            cmd.Parameters.Add(new() { Value = month });
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                pending.Add((r.GetGuid(0), r.GetInt64(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
            }
        }

        var invoiced = 0;
        foreach (var p in pending)
        {
            if (p.Amount != 0 && p.Customer is null)
            {
                LogNoCustomer(log, p.Tenant);
                continue;   // fica CLOSED até o cliente ser cadastrado no provedor
            }

            await using (var claim = Db.CreateCommand("UPDATE billing_periods SET status = 'CHARGING', charging_since = now() WHERE tenant_id = $1 AND billing_month = $2 AND status = 'CLOSED'"))
            {
                claim.Parameters.Add(new() { Value = p.Tenant });
                claim.Parameters.Add(new() { Value = month });
                if (await claim.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
                {
                    continue;   // outro processo já está cobrando este período
                }
            }

            string reference;
            try
            {
                reference = p.Amount == 0
                    ? "SEM_COBRANCA"
                    : await provider.ChargeAsync(new BillingCharge(p.Customer!, p.Amount,
                        $"TaxGateway {month:MM/yyyy}", p.Key, month.AddMonths(1).AddDays(9)), ct).ConfigureAwait(false);
            }
            catch
            {
                await using var back = Db.CreateCommand("UPDATE billing_periods SET status = 'CLOSED', charging_since = NULL WHERE tenant_id = $1 AND billing_month = $2 AND status = 'CHARGING'");
                back.Parameters.Add(new() { Value = p.Tenant });
                back.Parameters.Add(new() { Value = month });
                await back.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            await using var upd = Db.CreateCommand("UPDATE billing_periods SET status = 'INVOICED', provider_ref = $3 WHERE tenant_id = $1 AND billing_month = $2 AND status = 'CHARGING'");
            upd.Parameters.Add(new() { Value = p.Tenant });
            upd.Parameters.Add(new() { Value = month });
            upd.Parameters.Add(new() { Value = reference });
            invoiced += await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return invoiced;
    }

    /// <summary>Ledger × billing_periods × provedor (critério de saída da Fase 7: diferença zero).</summary>
    public async Task<List<ReconcileLine>> ReconcileAsync(DateOnly month, CancellationToken ct)
    {
        var lines = new List<(Guid Tenant, long LD, long PD, long LS, long PS, long Amount, string? Key, string? Ref)>();
        await using (var cmd = Db.CreateCommand("""
            WITH l AS (
                SELECT tenant_id, count(*) FILTER (WHERE metric = 'DOC_AUDIT') AS docs, count(*) FILTER (WHERE metric = 'SIMULATION') AS sims
                  FROM usage_ledger WHERE billing_month = $1 GROUP BY tenant_id
            )
            SELECT coalesce(l.tenant_id, b.tenant_id), coalesce(l.docs, 0), coalesce(b.docs_count, 0), coalesce(l.sims, 0), coalesce(b.simulations_count, 0),
                   coalesce(b.amount_cents, 0), b.provider_idempotency_key, b.provider_ref
              FROM l FULL JOIN (SELECT * FROM billing_periods WHERE billing_month = $1) b ON b.tenant_id = l.tenant_id
            """))
        {
            cmd.Parameters.Add(new() { Value = month });
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                lines.Add((r.GetGuid(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5),
                    r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
            }
        }

        var result = new List<ReconcileLine>();
        foreach (var l in lines)
        {
            long? providerAmount = null;
            if (l.Ref is not null && l.Ref != "SEM_COBRANCA" && l.Key is not null)
            {
                providerAmount = (await provider.FindAsync(l.Key, ct).ConfigureAwait(false))?.AmountCents ?? -1;
            }

            result.Add(new ReconcileLine(l.Tenant, l.LD, l.PD, l.LS, l.PS, l.Amount, providerAmount));
        }

        return result;
    }

    /// <summary>Nota de ajuste (v2, v3...): nunca edita a v1 faturada. Débito vai ao provedor; crédito fica para abatimento.</summary>
    public async Task<(int Version, string Status)> AdjustAsync(Guid tenant, DateOnly month, long deltaCents, string reason, string by, CancellationToken ct)
    {
        await using var conn = await Db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        int version;
        await using (var next = new NpgsqlCommand("""
            SELECT coalesce(max(version), 1) + 1 FROM billing_adjustments WHERE tenant_id = $1 AND billing_month = $2
            """, conn, tx))
        {
            next.Parameters.Add(new() { Value = tenant });
            next.Parameters.Add(new() { Value = month });
            version = (int)(await next.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        await using (var ins = new NpgsqlCommand("""
            INSERT INTO billing_adjustments (tenant_id, billing_month, version, amount_delta_cents, reason, created_by, provider_idempotency_key)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """, conn, tx))
        {
            ins.Parameters.Add(new() { Value = tenant });
            ins.Parameters.Add(new() { Value = month });
            ins.Parameters.Add(new() { Value = version });
            ins.Parameters.Add(new() { Value = deltaCents });
            ins.Parameters.Add(new() { Value = reason });
            ins.Parameters.Add(new() { Value = by });
            ins.Parameters.Add(new() { Value = Key(tenant, month, version) });
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);

        if (deltaCents <= 0)
        {
            return (version, "CREDITO_A_ABATER");   // crédito: abatido no próximo ciclo (Asaas não cria cobrança negativa)
        }

        string? customer;
        await using (var c = Db.CreateCommand("SELECT billing_customer_ref FROM tenants WHERE id = $1"))
        {
            c.Parameters.Add(new() { Value = tenant });
            customer = await c.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }

        if (customer is null)
        {
            return (version, "PENDING");
        }

        var reference = await provider.ChargeAsync(new BillingCharge(customer, deltaCents, $"TaxGateway ajuste {month:MM/yyyy} v{version}",
            Key(tenant, month, version), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10))), ct).ConfigureAwait(false);
        await using var upd = Db.CreateCommand("UPDATE billing_adjustments SET status = 'INVOICED', provider_ref = $4 WHERE tenant_id = $1 AND billing_month = $2 AND version = $3");
        upd.Parameters.Add(new() { Value = tenant });
        upd.Parameters.Add(new() { Value = month });
        upd.Parameters.Add(new() { Value = version });
        upd.Parameters.Add(new() { Value = reference });
        await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return (version, "INVOICED");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant {Tenant} sem cliente no provedor de cobrança: período fica CLOSED.")]
    private static partial void LogNoCustomer(ILogger logger, Guid tenant);
}

/// <summary>
/// Agenda do fechamento (§10.3): a cada hora fecha e fatura os meses anteriores ao corrente (America/Sao_Paulo)
/// que tenham uso. Rodar de novo não muda nada (idempotente); a partir do dia 1, 03:00, o mês anterior fecha.
/// </summary>
public sealed partial class BillingCloseWorker(BillingService billing, BillingDataSource db, TimeProvider clock, ILogger<BillingCloseWorker> log) : BackgroundService
{
    private static readonly TimeZoneInfo Brt = FindBrt();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
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

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Brt);
        if (local.Day == 1 && local.Hour < 3)
        {
            return;   // §10.3: fecha a partir das 03:00 do dia 1 (margem para eventos atrasados do mês anterior)
        }

        await billing.RecoverChargingAsync(TimeSpan.FromMinutes(30), ct).ConfigureAwait(false);
        var current = new DateOnly(local.Year, local.Month, 1);
        var months = new List<DateOnly>();
        await using (var cmd = db.DataSource.CreateCommand("""
            SELECT DISTINCT l.billing_month FROM usage_ledger l
             WHERE l.billing_month < $1
               AND NOT EXISTS (SELECT 1 FROM billing_periods b WHERE b.tenant_id = l.tenant_id AND b.billing_month = l.billing_month AND b.status = 'INVOICED')
             ORDER BY 1
            """))
        {
            cmd.Parameters.Add(new() { Value = current, NpgsqlDbType = NpgsqlDbType.Date });
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                months.Add(r.GetFieldValue<DateOnly>(0));
            }
        }

        foreach (var m in months)
        {
            await billing.CloseMonthAsync(m, ct).ConfigureAwait(false);
            await billing.InvoiceAsync(m, ct).ConfigureAwait(false);
        }
    }

    private static TimeZoneInfo FindBrt()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "Brasília", "Brasília");
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha no fechamento de faturamento.")]
    private static partial void LogError(ILogger logger, Exception ex);
}
