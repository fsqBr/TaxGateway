using Microsoft.Extensions.Caching.Memory;

using TaxGateway.Api.Simulation;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Api.Http;

public sealed record QuotaMetric(string Metric);

/// <summary>
/// Franquia, teto de excedente e inadimplência nas rotas que consomem (simular, auditar, lotes).
/// <list type="bullet">
/// <item>D-R09: tenant RESTRICTED (pagamento pendente há 10 dias) não consome: 402 <c>ERR_PAGAMENTO_PENDENTE</c>.</item>
/// <item>D-C07: plano com franquia rígida (Sandbox) para ao esgotar: 402 <c>ERR_FRANQUIA_ESGOTADA</c>.</item>
/// <item>D-R08: plano pago só cobra excedente até o TETO que o cliente definiu (padrão 0 = para ao esgotar a franquia,
/// como o Sandbox). Atingido o teto: 402 <c>ERR_TETO_EXCEDENTE</c>, com o caminho para subir o teto ou trocar de plano.</item>
/// </list>
/// A contagem vem do ledger (mesma fonte da cobrança) com cache de 30 s por tenant: numa rajada podem passar alguns
/// itens além do limite; nunca bloqueia antes. Avisos em 80% e 100% da franquia e do teto (D-J03).
/// </summary>
public sealed class QuotaFilter(ITenantDb db, IMemoryCache cache, TimeProvider clock) : IEndpointFilter
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private sealed record Usage(long Docs, long Sims);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var metric = http.GetEndpoint()?.Metadata.GetMetadata<QuotaMetric>()?.Metric;
        if (metric is null)
        {
            return await next(context).ConfigureAwait(false);
        }

        var caller = http.GetCaller();
        if (caller.Identity.TenantStatus == "RESTRICTED")
        {
            return ApiProblem.Result(402, "ERR_PAGAMENTO_PENDENTE", "Pagamento pendente: consumo suspenso",
                "Consulta e exportação continuam liberadas. Regularize o pagamento em Plano e cobrança para voltar a simular e auditar.");
        }

        var l = caller.Limits;
        var isDoc = metric == "DOC_AUDIT";
        var included = isDoc ? l.IncludedDocs : l.IncludedSimulations;
        var unitMillicents = isDoc ? l.OverageDocMillicents : l.OverageSimMillicents;
        // Sem franquia definida e sem preço de excedente (planos legados/negociados): sem limite.
        if (included <= 0 && !l.HardQuota && unitMillicents <= 0)
        {
            return await next(context).ConfigureAwait(false);
        }

        var month = SimulationService.BillingMonth(clock.GetUtcNow());
        var key = $"quota:{caller.Identity.Tenant}:{month:yyyyMM}";
        if (!cache.TryGetValue(key, out Usage? usage) || usage is null)
        {
            usage = await db.InTenantAsync(caller.Identity.Tenant, async (s, ct) =>
            {
                await using var cmd = s.CreateCommand("""
                    SELECT count(*) FILTER (WHERE metric = 'DOC_AUDIT'), count(*) FILTER (WHERE metric = 'SIMULATION')
                      FROM usage_ledger WHERE billing_month = $1
                    """);
                cmd.Parameters.Add(new() { Value = month, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date });
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await r.ReadAsync(ct).ConfigureAwait(false);
                return new Usage(r.GetInt64(0), r.GetInt64(1));
            }, http.RequestAborted).ConfigureAwait(false);
            cache.Set(key, usage, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl, Size = 1 });
            await NotifyAsync(caller, month, usage, http.RequestAborted).ConfigureAwait(false);
        }

        var used = isDoc ? usage.Docs : usage.Sims;
        var what = isDoc ? "auditorias" : "simulações";
        if (l.HardQuota)
        {
            if (used >= included)
            {
                TaxGateway.Infrastructure.TaxGatewayMetrics.QuotaExhausted.Add(1, new KeyValuePair<string, object?>("metrica", metric));
                return ApiProblem.Result(402, "ERR_FRANQUIA_ESGOTADA", $"Franquia de {what} do plano esgotada neste mês",
                    $"O plano {l.PlanCode} inclui {included} {what} por mês, sem excedente. Troque de plano em Plano e cobrança para continuar.");
            }

            return await next(context).ConfigureAwait(false);
        }

        // Plano pago: dentro da franquia passa; fora dela, só até o teto de excedente.
        if (used < included || unitMillicents <= 0)
        {
            return await next(context).ConfigureAwait(false);
        }

        var cap = caller.Billing?.OverageCapCents ?? 0;
        if (OverageCents(l, usage) + (unitMillicents / 1000.0m) > cap)
        {
            TaxGateway.Infrastructure.TaxGatewayMetrics.QuotaExhausted.Add(1, new KeyValuePair<string, object?>("metrica", metric));
            return cap == 0
                ? ApiProblem.Result(402, "ERR_TETO_EXCEDENTE", $"Franquia de {what} esgotada e sem teto de excedente",
                    $"O plano {l.PlanCode} inclui {included} {what} por mês. Para continuar este mês, defina um teto de excedente (cobrança automática até o valor escolhido) ou troque de plano em Plano e cobrança.")
                : ApiProblem.Result(402, "ERR_TETO_EXCEDENTE", "Teto de excedente atingido",
                    $"O excedente do mês chegou ao teto de R$ {cap / 100.0m:0.00}. Aumente o teto ou troque de plano em Plano e cobrança.");
        }

        return await next(context).ConfigureAwait(false);
    }

    /// <summary>Excedente acumulado no mês, em centavos (mesma regra do fechamento: o que passa da franquia × preço).</summary>
    public static decimal OverageCents(TaxGateway.Core.Params.PlanLimits l, long docs, long sims)
    {
        ArgumentNullException.ThrowIfNull(l);
        return (Math.Max(0, docs - l.IncludedDocs) * l.OverageDocMillicents / 1000.0m)
               + (Math.Max(0, sims - l.IncludedSimulations) * l.OverageSimMillicents / 1000.0m);
    }

    private static decimal OverageCents(TaxGateway.Core.Params.PlanLimits l, Usage u) => OverageCents(l, u.Docs, u.Sims);

    /// <summary>Avisos de franquia (80% e 100%) e de teto (80% e 100%), uma vez por mês cada (dedupe no banco).</summary>
    private async Task NotifyAsync(Caller caller, DateOnly month, Usage usage, CancellationToken ct)
    {
        var l = caller.Limits;
        var cap = caller.Billing?.OverageCapCents ?? 0;
        var notices = new List<(string Kind, string Severity, string Title, string Body, string Dedupe)>();
        foreach (var (metric, used, included, what) in new[]
                 {
                     ("DOC_AUDIT", usage.Docs, l.IncludedDocs, "auditorias"),
                     ("SIMULATION", usage.Sims, l.IncludedSimulations, "simulações"),
                 })
        {
            if (included <= 0 || used * 5 < included * 4)
            {
                continue;
            }

            var esgotada = used >= included;
            var body = l.HardQuota
                ? $"O plano {l.PlanCode} inclui {included} {what} por mês, sem excedente. {(esgotada ? "Novas chamadas recebem 402 até o próximo mês." : "Ao chegar a 100%, novas chamadas recebem 402.")} Troque de plano em Plano e cobrança."
                : cap == 0
                    ? $"O plano {l.PlanCode} inclui {included} {what} por mês e o teto de excedente está em R$ 0,00: {(esgotada ? "novas chamadas estão sendo recusadas" : "ao chegar a 100%, novas chamadas serão recusadas")}. Defina um teto (cobrança automática até o valor escolhido) ou troque de plano."
                    : $"O plano {l.PlanCode} inclui {included} {what} por mês. {(esgotada ? "A partir de agora" : "Ao passar de 100%")}, o uso extra é cobrado na próxima fatura até o teto de R$ {cap / 100.0m:0.00}.";
            notices.Add((esgotada ? "FRANQUIA_ESGOTADA" : "FRANQUIA_80", esgotada && (l.HardQuota || cap == 0) ? "critico" : "aviso",
                esgotada ? $"Franquia de {what} esgotada" : $"80% da franquia de {what} usada", body,
                $"quota:{metric}:{month:yyyyMM}:{(esgotada ? 100 : 80)}"));
        }

        if (!l.HardQuota && cap > 0)
        {
            var spent = OverageCents(l, usage);
            if (spent * 5 >= cap * 4)
            {
                var atingido = spent >= cap;
                notices.Add((atingido ? "TETO_ATINGIDO" : "TETO_80", atingido ? "critico" : "aviso",
                    atingido ? "Teto de excedente atingido" : "80% do teto de excedente usado",
                    $"O excedente do mês está em R$ {spent / 100.0m:0.00} de um teto de R$ {cap / 100.0m:0.00}. "
                    + (atingido ? "Novas chamadas fora da franquia estão sendo recusadas." : "Ao chegar ao teto, novas chamadas fora da franquia serão recusadas.")
                    + " Ajuste o teto ou troque de plano em Plano e cobrança.",
                    $"teto:{month:yyyyMM}:{cap}:{(atingido ? 100 : 80)}"));
            }
        }

        if (notices.Count == 0)
        {
            return;
        }

        await db.InTenantAsync(caller.Identity.Tenant, async (s, c) =>
        {
            var n = 0;
            foreach (var x in notices)
            {
                await using var cmd = s.CreateCommand("""
                    INSERT INTO tenant_notifications (tenant_id, kind, severity, title, body, link, audience, dedupe_key)
                    VALUES ($1, $2, $3, $4, $5, '/portal/cobranca', '{owner,admin,finance}', $6)
                    ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
                    """);
                cmd.Parameters.Add(new() { Value = caller.Identity.Tenant.Value });
                cmd.Parameters.Add(new() { Value = x.Kind });
                cmd.Parameters.Add(new() { Value = x.Severity });
                cmd.Parameters.Add(new() { Value = x.Title });
                cmd.Parameters.Add(new() { Value = x.Body });
                cmd.Parameters.Add(new() { Value = x.Dedupe });
                n += await cmd.ExecuteNonQueryAsync(c).ConfigureAwait(false);
            }

            return n;
        }, ct).ConfigureAwait(false);
    }
}
