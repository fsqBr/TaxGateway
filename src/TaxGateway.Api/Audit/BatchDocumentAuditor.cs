using System.Collections.Concurrent;
using System.Text.Json;

using TaxGateway.Api.Http;
using TaxGateway.Contracts;
using TaxGateway.Core.Engines;
using TaxGateway.Core.Params;
using TaxGateway.Core.Tenancy;
using TaxGateway.Infrastructure.Batches;
using TaxGateway.Infrastructure.Billing;
using TaxGateway.Infrastructure.Params;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Api.Audit;

/// <summary>
/// Cada XML do lote passa pelo mesmo AuditService da API (D-606): mesmas regras, mesma cobrança por documento,
/// mesmo replay por chave de acesso. Erro de um documento vira uma linha do relatório e não derruba o chunk.
/// D-R12: cada documento passa pela mesma regra de consumo da API (<see cref="PlanQuota"/>): conta com pagamento
/// pendente ou suspensa para de auditar, e franquia/teto valem documento a documento — um lote ou a captura de DF-e não
/// cobram além do que o cliente autorizou. O consumo é relido do ledger a cada 10 s e somado localmente entre leituras.
/// </summary>
public sealed class BatchDocumentAuditor(AuditService audit, TenantSettingsService settings, IParamSnapshotProvider snapshots, ITenantDb db, TimeProvider clock)
    : IBatchDocumentAuditor
{
    private static readonly TimeSpan UsageTtl = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<Guid, (QuotaUsage Usage, DateTimeOffset At, DateOnly Month)> _usage = new();

    public async Task<BatchDocResult> AuditAsync(TenantId tenant, byte[] xml, string direction, CancellationToken ct)
    {
        // Plano real do tenant: a cobrança de SCHEMA_ERROR e os limites seguem o contrato (D-703).
        var s = await settings.GetAsync(tenant, ct).ConfigureAwait(false);
        var limits = ParamSnapshotService.EffectiveLimits(snapshots.Current, s.PlanCode, s.LimitsOverrideJson);
        var now = clock.GetUtcNow();
        var month = TaxGateway.Api.Simulation.SimulationService.BillingMonth(now);
        if (!_usage.TryGetValue(tenant.Value, out var cached) || cached.Month != month || now - cached.At > UsageTtl)
        {
            cached = (await PlanQuota.LoadAsync(db, tenant, month, includeReserved: false, ct).ConfigureAwait(false), now, month);
            _usage[tenant.Value] = cached;
        }

        if (PlanQuota.Check(limits, s.Status, s.OverageCapCents, cached.Usage, PlanQuota.DocMetric) is { } denial)
        {
            return new BatchDocResult(null, "REJEITADO", 0, 0, $"{denial.Code}: {denial.Detail}");
        }

        var caller = new Caller(new ApiKeyIdentity(tenant, Guid.Empty, [Scopes.AuditoriaWrite], s.PlanCode, s.Status, s.LimitsOverrideJson, s.AutoProvisionCnpj), limits);
        try
        {
            var json = await audit.AuditAsync(caller, new AuditRequest(xml, direction, false, false), ct).ConfigureAwait(false);
            var r = JsonSerializer.Deserialize(json, AuditoriaJsonContext.Default.AuditoriaResponse)!;
            if (r.IsBillable)
            {
                _usage.AddOrUpdate(tenant.Value, cached, (_, c) => (c.Usage with { Docs = c.Usage.Docs + 1 }, c.At, c.Month));
            }

            return new BatchDocResult(r.ChaveAcesso, r.Status, r.Ocorrencias.Count(o => o.Natureza == "DETERMINISTICA"), r.ResumoFinanceiro.Diferenca, null);
        }
        catch (ApiProblemException ex)
        {
            return new BatchDocResult(null, "REJEITADO", 0, 0, $"{ex.Code}: {ex.Detail ?? ex.Title}");
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "P0001" && ex.MessageText.StartsWith("ERR_LIMITE_CNPJS:", StringComparison.Ordinal))
        {
            return new BatchDocResult(null, "REJEITADO", 0, 0, $"ERR_LIMITE_CNPJS: o plano permite {ex.MessageText["ERR_LIMITE_CNPJS:".Length..]} CNPJ(s).");
        }
        catch (EngineRejectedException ex)
        {
            return new BatchDocResult(null, "REJEITADO", 0, 0, $"ERR_ENGINE_REJECTED: {string.Join("; ", ex.Errors.Select(e => e.Codigo))}");
        }
    }
}
