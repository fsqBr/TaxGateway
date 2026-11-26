using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Caching.Memory;

using NpgsqlTypes;

using TaxGateway.Core.Tenancy;

namespace TaxGateway.Infrastructure.Persistence;

/// <summary>Auditoria a gravar (uma linha em fiscal_documents).</summary>
public sealed record AuditRecord(
    Guid Id,
    DateOnly BillingMonth,
    string Kind,                 // OUTBOUND | INBOUND
    string Model,                // NFE55 | NFCE65
    string AccessKey,
    string CnpjSubject,
    string? CnpjCounterparty,
    bool ConsumidorFinal,
    byte[]? CpfHmac,
    string Engine,
    string EngineBundle,
    string RulesVersion,
    long ParamSnapshotId,
    string Status,
    string DeclaredJson,
    string RecalculatedJson,     // inclui a resposta da API (para replay)
    decimal DifferenceTotal,
    string FindingsJson,
    string? XmlStorageKey,
    byte[] XmlSha256,
    bool IsBillable,
    bool Reprocess,
    decimal CreditPotential = 0,
    string? CreditStatus = null);

public sealed record StoredAudit(Guid Id, DateOnly BillingMonth, string Status, string ResponseJson, bool IsBillable);

public enum SaveResult { Saved, Replay }

/// <summary>
/// Gravação atômica da auditoria (SDD §10.2; D-310): numa única transação com o contexto do tenant —
/// document_registry (deduplicação global por chave, entre meses) → fiscal_documents → usage_ledger (se
/// cobrável) → cnpj_monthly_activity → outbox. Conflito no registry = replay: nada mais é gravado nem cobrado.
/// </summary>
public sealed class AuditRepository(ITenantDb db)
{
    public static byte[] IdempotencyHash(string kind, string accessKey) =>
        SHA256.HashData(Encoding.ASCII.GetBytes($"{kind}:{accessKey}"));

    public Task<StoredAudit?> FindLatestAsync(TenantId tenant, string kind, string accessKey, CancellationToken ct) =>
        db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("""
                SELECT f.id, f.billing_month, f.audit_status, f.recalculated->>'resposta', f.is_billable
                  FROM document_registry r
                  JOIN fiscal_documents f ON f.tenant_id = r.tenant_id AND f.id = r.latest_document_id AND f.access_key = r.access_key
                 WHERE r.idempotency_hash = $1
                """);
            cmd.Parameters.Add(new() { Value = IdempotencyHash(kind, accessKey) });
            await using var r = await cmd.ExecuteReaderAsync(c).ConfigureAwait(false);
            return await r.ReadAsync(c).ConfigureAwait(false)
                ? new StoredAudit(r.GetGuid(0), r.GetFieldValue<DateOnly>(1), r.GetString(2), r.GetString(3), r.GetBoolean(4))
                : null;
        }, ct);

    public Task<SaveResult> SaveAsync(TenantId tenant, AuditRecord a, CancellationToken ct) =>
        db.InTenantAsync(tenant, async (s, c) =>
        {
            var hash = IdempotencyHash(a.Kind, a.AccessKey);
            await using (var reg = s.CreateCommand("""
                INSERT INTO document_registry (tenant_id, idempotency_hash, kind, access_key, first_document_id, first_billing_month, latest_document_id)
                VALUES ($1, $2, $3, $4, $5, $6, $5)
                ON CONFLICT (tenant_id, idempotency_hash) DO NOTHING
                RETURNING first_document_id
                """))
            {
                reg.Parameters.Add(new() { Value = tenant.Value });
                reg.Parameters.Add(new() { Value = hash });
                reg.Parameters.Add(new() { Value = a.Kind });
                reg.Parameters.Add(new() { Value = a.AccessKey });
                reg.Parameters.Add(new() { Value = a.Id });
                reg.Parameters.Add(new() { Value = a.BillingMonth });
                var inserted = await reg.ExecuteScalarAsync(c).ConfigureAwait(false) is not null;
                if (!inserted && !a.Reprocess)
                {
                    return SaveResult.Replay;   // outra requisição gravou a mesma chave antes: replay, sem cobrança
                }

                if (!inserted)
                {
                    await using var upd = s.CreateCommand("UPDATE document_registry SET latest_document_id = $2 WHERE idempotency_hash = $1");
                    upd.Parameters.Add(new() { Value = hash });
                    upd.Parameters.Add(new() { Value = a.Id });
                    await upd.ExecuteNonQueryAsync(c).ConfigureAwait(false);
                }
            }

            var batch = s.CreateBatch();
            await using (batch.ConfigureAwait(false))
            {
                var doc = new Npgsql.NpgsqlBatchCommand("""
                    INSERT INTO fiscal_documents (id, billing_month, tenant_id, kind, model, access_key, cnpj_subject, cnpj_counterparty,
                        is_consumidor_final, cpf_hmac, engine, engine_bundle, rules_version, param_snapshot_id, audit_status,
                        declared, recalculated, difference_total, findings, xml_storage_key, xml_sha256, is_billable, reprocess_of,
                        credit_potential, credit_status)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21, $22,
                        CASE WHEN $23 THEN (SELECT first_document_id FROM document_registry WHERE idempotency_hash = $24) END, $25, $26)
                    """);
                static object N(object? v) => v ?? DBNull.Value;
                doc.Parameters.Add(new() { Value = a.Id });
                doc.Parameters.Add(new() { Value = a.BillingMonth });
                doc.Parameters.Add(new() { Value = tenant.Value });
                doc.Parameters.Add(new() { Value = a.Kind });
                doc.Parameters.Add(new() { Value = a.Model });
                doc.Parameters.Add(new() { Value = a.AccessKey });
                doc.Parameters.Add(new() { Value = a.CnpjSubject });
                doc.Parameters.Add(new() { Value = N(a.CnpjCounterparty), NpgsqlDbType = NpgsqlDbType.Text });
                doc.Parameters.Add(new() { Value = a.ConsumidorFinal });
                doc.Parameters.Add(new() { Value = N(a.CpfHmac), NpgsqlDbType = NpgsqlDbType.Bytea });
                doc.Parameters.Add(new() { Value = a.Engine });
                doc.Parameters.Add(new() { Value = a.EngineBundle });
                doc.Parameters.Add(new() { Value = a.RulesVersion });
                doc.Parameters.Add(new() { Value = a.ParamSnapshotId });
                doc.Parameters.Add(new() { Value = a.Status });
                doc.Parameters.Add(new() { Value = a.DeclaredJson, NpgsqlDbType = NpgsqlDbType.Jsonb });
                doc.Parameters.Add(new() { Value = a.RecalculatedJson, NpgsqlDbType = NpgsqlDbType.Jsonb });
                doc.Parameters.Add(new() { Value = a.DifferenceTotal });
                doc.Parameters.Add(new() { Value = a.FindingsJson, NpgsqlDbType = NpgsqlDbType.Jsonb });
                doc.Parameters.Add(new() { Value = N(a.XmlStorageKey), NpgsqlDbType = NpgsqlDbType.Text });
                doc.Parameters.Add(new() { Value = a.XmlSha256 });
                doc.Parameters.Add(new() { Value = a.IsBillable });
                doc.Parameters.Add(new() { Value = a.Reprocess });
                doc.Parameters.Add(new() { Value = hash });
                doc.Parameters.Add(new() { Value = a.CreditPotential });
                doc.Parameters.Add(new() { Value = N(a.CreditStatus), NpgsqlDbType = NpgsqlDbType.Text });
                batch.BatchCommands.Add(doc);

                if (a.IsBillable)
                {
                    var ledger = new Npgsql.NpgsqlBatchCommand("""
                        INSERT INTO usage_ledger (billing_month, tenant_id, metric, cnpj_subject, source_id) VALUES ($1, $2, 'DOC_AUDIT', $3, $4)
                        """);
                    ledger.Parameters.Add(new() { Value = a.BillingMonth });
                    ledger.Parameters.Add(new() { Value = tenant.Value });
                    ledger.Parameters.Add(new() { Value = a.CnpjSubject });
                    ledger.Parameters.Add(new() { Value = a.Id });
                    batch.BatchCommands.Add(ledger);

                    var act = new Npgsql.NpgsqlBatchCommand("""
                        INSERT INTO cnpj_monthly_activity (tenant_id, billing_month, cnpj) VALUES ($1, $2, $3) ON CONFLICT DO NOTHING
                        """);
                    act.Parameters.Add(new() { Value = tenant.Value });
                    act.Parameters.Add(new() { Value = a.BillingMonth });
                    act.Parameters.Add(new() { Value = a.CnpjSubject });
                    batch.BatchCommands.Add(act);
                }

                var outbox = new Npgsql.NpgsqlBatchCommand("""
                    INSERT INTO outbox (tenant_id, topic, payload)
                    VALUES ($1, 'document.audited', jsonb_build_object('audit_id', $2::text, 'chave_acesso', $3::text, 'status', $4::text, 'reprocess', $5::boolean))
                    """);
                outbox.Parameters.Add(new() { Value = tenant.Value });
                outbox.Parameters.Add(new() { Value = a.Id.ToString() });
                outbox.Parameters.Add(new() { Value = a.AccessKey });
                outbox.Parameters.Add(new() { Value = a.Status });
                outbox.Parameters.Add(new() { Value = a.Reprocess });
                batch.BatchCommands.Add(outbox);

                await batch.ExecuteNonQueryAsync(c).ConfigureAwait(false);
            }

            return SaveResult.Saved;
        }, ct);
}

public sealed record TenantSettings(int? XmlRetentionDays, string KmsKeyRef, bool AutoProvisionCnpj = false, string PlanCode = "STARTER", string LimitsOverrideJson = "{}");

/// <summary>Retenção do XML e referência da KEK do tenant (cache L1 de 30 s).</summary>
public sealed class TenantSettingsService(ITenantDb db, IMemoryCache cache)
{
    public async Task<TenantSettings> GetAsync(TenantId tenant, CancellationToken ct)
    {
        var key = $"tenant-settings:{tenant}";
        if (cache.TryGetValue(key, out TenantSettings? cached))
        {
            return cached!;
        }

        var settings = await db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("SELECT xml_retention_days, kms_key_ref, auto_provision_cnpj, plan_code, limits_override::text FROM tenants");
            await using var r = await cmd.ExecuteReaderAsync(c).ConfigureAwait(false);
            if (!await r.ReadAsync(c).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Tenant não encontrado no contexto.");
            }

            return new TenantSettings(r.IsDBNull(0) ? null : r.GetInt32(0), r.GetString(1), r.GetBoolean(2), r.GetString(3), r.GetString(4));
        }, ct).ConfigureAwait(false);
        cache.Set(key, settings, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30), Size = 1 });
        return settings;
    }
}
