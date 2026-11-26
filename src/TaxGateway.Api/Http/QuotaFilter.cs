using Microsoft.Extensions.Caching.Memory;

using TaxGateway.Api.Simulation;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Api.Http;

public sealed record QuotaMetric(string Metric);

/// <summary>
/// Franquia rígida (D-C07): planos com <c>hard_quota</c> (Sandbox) não geram excedente — ao esgotar documentos ou
/// simulações do mês, a operação responde 402 <c>ERR_FRANQUIA_ESGOTADA</c> com o caminho para trocar de plano. A contagem
/// vem do ledger (mesma fonte da cobrança) com cache de 30 s por tenant: pode passar alguns itens além do limite numa
/// rajada, nunca bloqueia antes.
/// </summary>
public sealed class QuotaFilter(ITenantDb db, IMemoryCache cache, TimeProvider clock) : IEndpointFilter
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var metric = http.GetEndpoint()?.Metadata.GetMetadata<QuotaMetric>()?.Metric;
        var caller = http.GetCaller();
        var included = metric == "DOC_AUDIT" ? caller.Limits.IncludedDocs : caller.Limits.IncludedSimulations;
        if (metric is null || included <= 0)
        {
            return await next(context).ConfigureAwait(false);
        }

        var month = SimulationService.BillingMonth(clock.GetUtcNow());
        var key = $"quota:{caller.Identity.Tenant}:{month:yyyyMM}:{metric}";
        if (!cache.TryGetValue(key, out long used))
        {
            used = await db.InTenantAsync(caller.Identity.Tenant, async (s, ct) =>
            {
                await using var cmd = s.CreateCommand("SELECT count(*) FROM usage_ledger WHERE billing_month = $1 AND metric = $2");
                cmd.Parameters.Add(new() { Value = month, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date });
                cmd.Parameters.Add(new() { Value = metric });
                return (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            }, http.RequestAborted).ConfigureAwait(false);
            cache.Set(key, used, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl, Size = 1 });
            if (used * 5 >= included * 4)
            {
                await NotifyAsync(caller, metric, month, used, included, http.RequestAborted).ConfigureAwait(false);
            }
        }

        if (!caller.Limits.HardQuota)
        {
            return await next(context).ConfigureAwait(false);   // planos pagos: o excedente é cobrado, sem bloqueio
        }

        if (used >= included)
        {
            TaxGateway.Infrastructure.TaxGatewayMetrics.QuotaExhausted.Add(1, new KeyValuePair<string, object?>("metrica", metric));
            var what = metric == "DOC_AUDIT" ? "auditorias" : "simulações";
            return ApiProblem.Result(402, "ERR_FRANQUIA_ESGOTADA", $"Franquia de {what} do plano esgotada neste mês",
                $"O plano {caller.Limits.PlanCode} inclui {included} {what} por mês, sem excedente. Troque de plano em Plano e cobrança para continuar.");
        }

        return await next(context).ConfigureAwait(false);
    }

    /// <summary>Aviso de franquia (D-J03): 80% e esgotada, uma vez por mês e métrica (dedupe no banco).</summary>
    private async Task NotifyAsync(Caller caller, string metric, DateOnly month, long used, long included, CancellationToken ct)
    {
        var esgotada = used >= included;
        var what = metric == "DOC_AUDIT" ? "auditorias" : "simulações";
        var body = caller.Limits.HardQuota
            ? $"O plano {caller.Limits.PlanCode} inclui {included} {what} por mês, sem excedente. {(esgotada ? "Novas chamadas recebem 402 até o próximo mês." : "Ao chegar a 100%, novas chamadas recebem 402.")} Troque de plano em Plano e cobrança."
            : $"O plano {caller.Limits.PlanCode} inclui {included} {what} por mês. {(esgotada ? "A partir de agora" : "Ao passar de 100%")}, cada unidade extra é cobrada como excedente na próxima fatura.";
        await db.InTenantAsync(caller.Identity.Tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("""
                INSERT INTO tenant_notifications (tenant_id, kind, severity, title, body, link, audience, dedupe_key)
                VALUES ($1, $2, $3, $4, $5, '/portal/cobranca', '{owner,admin,finance}', $6)
                ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
                """);
            cmd.Parameters.Add(new() { Value = caller.Identity.Tenant.Value });
            cmd.Parameters.Add(new() { Value = esgotada ? "FRANQUIA_ESGOTADA" : "FRANQUIA_80" });
            cmd.Parameters.Add(new() { Value = esgotada && caller.Limits.HardQuota ? "critico" : "aviso" });
            cmd.Parameters.Add(new() { Value = esgotada ? $"Franquia de {what} esgotada" : $"80% da franquia de {what} usada" });
            cmd.Parameters.Add(new() { Value = body });
            cmd.Parameters.Add(new() { Value = $"quota:{metric}:{month:yyyyMM}:{(esgotada ? 100 : 80)}" });
            return await cmd.ExecuteNonQueryAsync(c).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
