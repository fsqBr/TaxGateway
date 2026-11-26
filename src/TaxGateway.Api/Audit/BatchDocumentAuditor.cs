using System.Text.Json;

using TaxGateway.Api.Http;
using TaxGateway.Contracts;
using TaxGateway.Core.Engines;
using TaxGateway.Core.Params;
using TaxGateway.Core.Tenancy;
using TaxGateway.Infrastructure.Batches;
using TaxGateway.Infrastructure.Params;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Api.Audit;

/// <summary>
/// Cada XML do lote passa pelo mesmo AuditService da API (D-606): mesmas regras, mesma cobrança por documento,
/// mesmo replay por chave de acesso. Erro de um documento vira uma linha do relatório e não derruba o chunk.
/// </summary>
public sealed class BatchDocumentAuditor(AuditService audit, TenantSettingsService settings, IParamSnapshotProvider snapshots) : IBatchDocumentAuditor
{
    public async Task<BatchDocResult> AuditAsync(TenantId tenant, byte[] xml, string direction, CancellationToken ct)
    {
        // Plano real do tenant: a cobrança de SCHEMA_ERROR e os limites seguem o contrato (D-703).
        var s = await settings.GetAsync(tenant, ct).ConfigureAwait(false);
        var limits = ParamSnapshotService.EffectiveLimits(snapshots.Current, s.PlanCode, s.LimitsOverrideJson);
        var caller = new Caller(new ApiKeyIdentity(tenant, Guid.Empty, [Scopes.AuditoriaWrite], s.PlanCode, "ACTIVE", s.LimitsOverrideJson, s.AutoProvisionCnpj), limits);
        try
        {
            var json = await audit.AuditAsync(caller, new AuditRequest(xml, direction, false, false), ct).ConfigureAwait(false);
            var r = JsonSerializer.Deserialize(json, AuditoriaJsonContext.Default.AuditoriaResponse)!;
            return new BatchDocResult(r.ChaveAcesso, r.Status, r.Ocorrencias.Count(o => o.Natureza == "DETERMINISTICA"), r.ResumoFinanceiro.Diferenca, null);
        }
        catch (ApiProblemException ex)
        {
            return new BatchDocResult(null, "REJEITADO", 0, 0, $"{ex.Code}: {ex.Detail ?? ex.Title}");
        }
        catch (EngineRejectedException ex)
        {
            return new BatchDocResult(null, "REJEITADO", 0, 0, $"ERR_ENGINE_REJECTED: {string.Join("; ", ex.Errors.Select(e => e.Codigo))}");
        }
    }
}
