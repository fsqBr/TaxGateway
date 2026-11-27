using TaxGateway.Api.Http;
using TaxGateway.Core.Params;
using TaxGateway.Core.Tenancy;
using TaxGateway.Infrastructure.Params;
using TaxGateway.Infrastructure.Persistence;
using TaxGateway.Infrastructure.Storage;

namespace TaxGateway.Api.Audit;

public sealed record ReprocessSummary(int Encontrados, int Reprocessados, int SemXml, int Falhas);

/// <summary>
/// Reprocessamento de auditorias (SDD §2.3 passo 7; D-408): reaudita, com o bundle e os parâmetros vigentes no
/// processo, os XMLs guardados do tenant num intervalo de competências. Cada reauditoria vira uma nova linha com
/// <c>reprocess_of</c>, sem cobrança. Roda como Job efêmero com a imagem da API apontando para o motor do bundle
/// desejado: <c>dotnet TaxGateway.Api.dll reprocess --tenant ID --from AAAA-MM --to AAAA-MM</c>.
/// Documentos em zero_retention não têm XML e são contados em <c>SemXml</c>.
/// </summary>
public sealed partial class ReprocessService(ITenantDb db, TenantSettingsService settings, AuditService audit,
    IParamSnapshotProvider snapshots, ILogger<ReprocessService> log, XmlVault? vault = null)
{
    /// <summary>
    /// Reaudita a auditoria mais recente de cada chave no período, sem cobrança (PlatformInitiated, D-R07). Com
    /// <paramref name="municipio"/> ou <paramref name="ufCodigo"/>, só os documentos daquele ente (D-R19).
    /// </summary>
    public async Task<ReprocessSummary> RunAsync(TenantId tenant, DateOnly fromMonth, DateOnly toMonth, CancellationToken ct,
        int? municipio = null, short? ufCodigo = null)
    {
        var docs = await db.InTenantAsync(tenant, async (s, c) =>
        {
            // Só a auditoria mais recente de cada chave (a que o registry aponta).
            await using var cmd = s.CreateCommand("""
                SELECT f.access_key, f.xml_storage_key, f.kind
                  FROM document_registry r
                  JOIN fiscal_documents f ON f.id = r.latest_document_id AND f.access_key = r.access_key
                 WHERE f.billing_month BETWEEN $1 AND $2
                   AND ($3::int IS NULL OR f.municipio_incidencia = $3)
                   AND ($4::smallint IS NULL OR f.municipio_incidencia / 100000 = $4)
                 ORDER BY f.billing_month, f.created_at
                """);
            cmd.Parameters.Add(new() { Value = fromMonth });
            cmd.Parameters.Add(new() { Value = toMonth });
            cmd.Parameters.Add(new() { Value = (object?)municipio ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
            cmd.Parameters.Add(new() { Value = (object?)ufCodigo ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Smallint });
            await using var r = await cmd.ExecuteReaderAsync(c).ConfigureAwait(false);
            var list = new List<(string Chave, string? Key, string Kind)>();
            while (await r.ReadAsync(c).ConfigureAwait(false))
            {
                list.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2)));
            }

            return list;
        }, ct).ConfigureAwait(false);

        var kek = (await settings.GetAsync(tenant, ct).ConfigureAwait(false)).KmsKeyRef;
        var limits = ParamSnapshotService.EffectiveLimits(snapshots.Current, "ENTERPRISE", "{}");
        var caller = new Caller(new ApiKeyIdentity(tenant, Guid.Empty, [Scopes.AuditoriaWrite], "ENTERPRISE", "ACTIVE", "{}", AutoProvisionCnpj: true), limits);
        int ok = 0, semXml = 0, falhas = 0;
        foreach (var (chave, key, kind) in docs)
        {
            if (key is null || vault is null)
            {
                semXml++;
                continue;
            }

            try
            {
                var xml = await vault.GetAsync(kek, key, ct).ConfigureAwait(false);
                await audit.AuditAsync(caller, new AuditRequest(xml, kind, Reprocess: true, SignatureOptional: false, PlatformInitiated: true), ct).ConfigureAwait(false);
                ok++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                falhas++;
                LogFailure(log, ex, chave);
            }
        }

        return new ReprocessSummary(docs.Count, ok, semXml, falhas);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha ao reprocessar a chave {Chave}.")]
    private static partial void LogFailure(ILogger logger, Exception ex, string chave);
}
