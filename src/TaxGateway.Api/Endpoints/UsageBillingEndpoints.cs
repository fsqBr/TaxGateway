using System.Globalization;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Caching.Memory;

using TaxGateway.Api.Http;
using TaxGateway.Api.Simulation;
using TaxGateway.Infrastructure.Billing;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Api.Endpoints;

/// <summary>Consumo do tenant (SDD §10.2) e operações administrativas de faturamento (§10.3; D-701 a D-705).</summary>
public static class UsageBillingEndpoints
{
    public const string UsageRead = "usage:read";

    public static RouteGroupBuilder MapUsage(this RouteGroupBuilder v1)
    {
        v1.MapGet("/usage", UsageAsync).WithMetadata(new RequiredScope(UsageRead));
        return v1;
    }

    public static RouteGroupBuilder MapBillingAdmin(this RouteGroupBuilder admin)
    {
        admin.MapPost("/billing/close", async (string mes, BillingService b, CancellationToken ct) =>
        {
            var month = Month(mes);
            var closed = await b.CloseMonthAsync(month, ct);
            var invoiced = await b.InvoiceAsync(month, ct);
            return Results.Ok(new JsonObject { ["competencia"] = mes, ["periodos_fechados"] = closed, ["faturados"] = invoiced });
        }).WithMetadata(new RequiredScope(AdminEndpoints.Commercial));

        admin.MapGet("/billing/reconcile", async (string mes, BillingService b, CancellationToken ct) =>
        {
            var lines = await b.ReconcileAsync(Month(mes), ct);
            return Results.Ok(new JsonObject
            {
                ["competencia"] = mes,
                ["tenants"] = lines.Count,
                ["divergencias"] = new JsonArray(lines.Where(l => !l.Ok).Select(l => (JsonNode)new JsonObject
                {
                    ["tenant_id"] = l.Tenant,
                    ["ledger_documentos"] = l.LedgerDocs,
                    ["periodo_documentos"] = l.PeriodDocs,
                    ["ledger_simulacoes"] = l.LedgerSims,
                    ["periodo_simulacoes"] = l.PeriodSims,
                    ["valor_periodo_centavos"] = l.PeriodAmountCents,
                    ["valor_provedor_centavos"] = l.ProviderAmountCents,
                }).ToArray()),
            });
        }).WithMetadata(new RequiredScope(AdminEndpoints.Commercial));

        admin.MapPost("/billing/adjustments", async (HttpContext http, BillingService b, CancellationToken ct) =>
        {
            var body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct);
            if (body?["tenant_id"]?.GetValue<string>() is not { } t || !Guid.TryParse(t, out var tenant)
                || body["competencia"]?.GetValue<string>() is not { } mes || body["valor_centavos"]?.GetValue<long>() is not { } delta
                || body["motivo"]?.GetValue<string>() is not { Length: > 0 } motivo)
            {
                return ApiProblem.Result(400, "ERR_VALIDATION", "tenant_id, competencia (AAAA-MM), valor_centavos e motivo são obrigatórios");
            }

            var (version, status) = await b.AdjustAsync(tenant, Month(mes), delta, motivo, AdminKeyFilter.Get(http).Name, ct);
            return Results.Ok(new JsonObject { ["versao"] = $"v{version}", ["status"] = status });
        }).WithMetadata(new RequiredScope(AdminEndpoints.Commercial));
        return admin;
    }

    /// <summary>Consumo da competência corrente (America/Sao_Paulo), com cache de 60 s (§10.2).</summary>
    private static async Task<IResult> UsageAsync(HttpContext http, ITenantDb db, IMemoryCache cache, TimeProvider clock, string? mes)
    {
        var tenant = http.GetCaller().Identity.Tenant;
        var month = mes is null ? SimulationService.BillingMonth(clock.GetUtcNow()) : Month(mes);
        var key = $"usage:{tenant}:{month:yyyy-MM}";
        if (!cache.TryGetValue(key, out JsonObject? usage))
        {
            usage = await db.InTenantAsync(tenant, async (s, ct) =>
            {
                await using var cmd = s.CreateCommand("""
                    SELECT (SELECT count(*) FROM usage_ledger WHERE billing_month = $1 AND metric = 'DOC_AUDIT'),
                           (SELECT count(*) FROM usage_ledger WHERE billing_month = $1 AND metric = 'SIMULATION'),
                           (SELECT count(*) FROM cnpj_monthly_activity WHERE billing_month = $1),
                           p.code, p.included_docs, p.included_cnpjs
                      FROM tenants t JOIN plans p ON p.code = t.plan_code
                    """);
                cmd.Parameters.Add(new() { Value = month });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                var docs = r.GetInt64(0);
                var included = r.GetInt64(4);
                return new JsonObject
                {
                    ["competencia"] = month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    ["plano"] = r.GetString(3),
                    ["documentos_auditados"] = docs,
                    ["simulacoes"] = r.GetInt64(1),
                    ["cnpjs_ativos"] = r.GetInt64(2),
                    ["franquia_documentos"] = included,
                    ["excedente_documentos"] = Math.Max(0, docs - included),
                    ["atualizado_em"] = clock.GetUtcNow(),
                };
            }, http.RequestAborted);
            cache.Set(key, usage, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60), Size = 1 });
        }

        return Results.Ok(usage);
    }

    /// <summary>Competência "AAAA-MM". Valor inválido é 400 (antes: exceção de parse → 500, D-P12).</summary>
    internal static DateOnly Month(string mes) =>
        mes is { Length: 7 } && DateOnly.TryParseExact(mes + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m) && m.Year >= 2000
            ? m
            : throw new ApiProblemException(400, "ERR_VALIDATION", "Competência inválida", "Use o formato AAAA-MM (ex.: 2026-11).");
}
