using TaxGateway.Core.Params;
using TaxGateway.Core.Tenancy;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Infrastructure.Billing;

/// <summary>Consumo do mês (ledger) e documentos reservados por lotes ainda na fila.</summary>
public sealed record QuotaUsage(long Docs, long Sims, long ReservedDocs = 0);

/// <summary>Recusa de consumo com o código e a mensagem que o cliente vê (problem+json).</summary>
public sealed record QuotaDenial(int Status, string Code, string Title, string Detail);

/// <summary>
/// Regra ÚNICA de consumo por plano (D-R12): a mesma decisão vale para a chamada avulsa (QuotaFilter), para a criação
/// de lote (o lote inteiro tem de caber) e para cada documento auditado pelos workers (lote e DF-e).
/// <list type="bullet">
/// <item>Conta RESTRICTED (D-R09): 402 <c>ERR_PAGAMENTO_PENDENTE</c>; SUSPENDED/CLOSED: 403 <c>ERR_TENANT_SUSPENDED</c>.</item>
/// <item>Franquia rígida (Sandbox, D-C07): passa só o que cabe na franquia — 402 <c>ERR_FRANQUIA_ESGOTADA</c>.</item>
/// <item>Plano pago: fora da franquia, só até o teto de excedente autorizado (D-R08) — 402 <c>ERR_TETO_EXCEDENTE</c>.</item>
/// <item>Sem franquia e sem preço de excedente (planos de contrato): sem limite de consumo.</item>
/// </list>
/// </summary>
public static class PlanQuota
{
    public const string DocMetric = "DOC_AUDIT";
    public const string SimMetric = "SIMULATION";

    public static QuotaDenial? Check(PlanLimits limits, string tenantStatus, long capCents, QuotaUsage usage, string metric, long units = 1)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(usage);
        if (tenantStatus == "RESTRICTED")
        {
            return new(402, "ERR_PAGAMENTO_PENDENTE", "Pagamento pendente: consumo suspenso",
                "Consulta e exportação continuam liberadas. Regularize o pagamento em Plano e cobrança para voltar a simular e auditar.");
        }

        if (tenantStatus is "SUSPENDED" or "CLOSED")
        {
            return new(403, "ERR_TENANT_SUSPENDED", "Conta suspensa", "Fale com o suporte do TaxGateway ou regularize o pagamento em Plano e cobrança.");
        }

        var isDoc = metric == DocMetric;
        var included = isDoc ? limits.IncludedDocs : limits.IncludedSimulations;
        var unitMillicents = isDoc ? limits.OverageDocMillicents : limits.OverageSimMillicents;
        if (included <= 0 && !limits.HardQuota && unitMillicents <= 0)
        {
            return null;
        }

        var used = (isDoc ? usage.Docs + usage.ReservedDocs : usage.Sims);
        var what = isDoc ? "auditorias" : "simulações";
        var lote = units > 1 ? $" Este envio tem {units} documentos; restam {Math.Max(0, included - used)} na franquia." : "";
        if (limits.HardQuota)
        {
            return used + units > included
                ? new(402, "ERR_FRANQUIA_ESGOTADA", $"Franquia de {what} do plano esgotada neste mês",
                    $"O plano {limits.PlanCode} inclui {included} {what} por mês, sem excedente.{lote} Troque de plano em Plano e cobrança para continuar.")
                : null;
        }

        if (used + units <= included || unitMillicents <= 0)
        {
            return null;
        }

        var after = isDoc
            ? OverageCents(limits, usage.Docs + usage.ReservedDocs + units, usage.Sims)
            : OverageCents(limits, usage.Docs + usage.ReservedDocs, usage.Sims + units);
        if (after <= capCents)
        {
            return null;
        }

        return capCents == 0
            ? new(402, "ERR_TETO_EXCEDENTE", $"Franquia de {what} esgotada e sem teto de excedente",
                $"O plano {limits.PlanCode} inclui {included} {what} por mês.{lote} Para continuar este mês, defina um teto de excedente (cobrança automática até o valor escolhido) ou troque de plano em Plano e cobrança.")
            : new(402, "ERR_TETO_EXCEDENTE", "Teto de excedente atingido",
                $"O excedente do mês passaria do teto de R$ {capCents / 100.0m:0.00}.{lote} Aumente o teto ou troque de plano em Plano e cobrança.");
    }

    /// <summary>Excedente acumulado no mês, em centavos (mesma regra do fechamento: o que passa da franquia × preço).</summary>
    public static decimal OverageCents(PlanLimits limits, long docs, long sims)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return (Math.Max(0, docs - limits.IncludedDocs) * limits.OverageDocMillicents / 1000.0m)
               + (Math.Max(0, sims - limits.IncludedSimulations) * limits.OverageSimMillicents / 1000.0m);
    }

    /// <summary>Consumo do mês pelo ledger (mesma fonte da cobrança) e, opcionalmente, documentos de lotes ainda na fila.</summary>
    public static Task<QuotaUsage> LoadAsync(ITenantDb db, TenantId tenant, DateOnly month, bool includeReserved, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand($"""
                SELECT (SELECT count(*) FROM usage_ledger WHERE billing_month = $1 AND metric = 'DOC_AUDIT'),
                       (SELECT count(*) FROM usage_ledger WHERE billing_month = $1 AND metric = 'SIMULATION'),
                       {(includeReserved
                           ? "(SELECT coalesce(sum(greatest(total_docs - processed_docs - failed_docs, 0)), 0) FROM batches WHERE status IN ('QUEUED','RUNNING'))"
                           : "0")}
                """);
            cmd.Parameters.Add(new() { Value = month, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date });
            await using var r = await cmd.ExecuteReaderAsync(c).ConfigureAwait(false);
            await r.ReadAsync(c).ConfigureAwait(false);
            return new QuotaUsage(r.GetInt64(0), r.GetInt64(1), Convert.ToInt64(r.GetValue(2), System.Globalization.CultureInfo.InvariantCulture));
        }, ct);
    }
}
