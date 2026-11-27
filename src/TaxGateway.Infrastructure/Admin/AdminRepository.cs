using System.Text.Json;

using Npgsql;

using NpgsqlTypes;

using TaxGateway.Core.Tenancy;

namespace TaxGateway.Infrastructure.Admin;

/// <summary>Fonte de dados do role <c>tg_admin</c> (Admin API). Separada da do <c>tg_app</c>.</summary>
public sealed class AdminDataSource(NpgsqlDataSource dataSource)
{
    public NpgsqlDataSource DataSource { get; } = dataSource;
}

/// <summary>Novo tenant. <c>XmlRetentionDays</c> null = zero_retention (padrão, D-308). A KEK do tenant é "tenant/{id}".</summary>
public sealed record NewTenant(string Name, string RootCnpj, string PlanCode, bool AutoProvisionCnpj, IReadOnlyList<string> Cnpjs, int? XmlRetentionDays = null);

public sealed record RateChangeRow(
    string Tributo, string Tipo, string Esfera, string? EnteCodigo, string? FormaAplicacao, decimal Aliquota,
    DateOnly ValidFrom, DateOnly? ValidTo, string AtoLegal, string Fonte, bool Provisoria);

public sealed record ParamChange(
    Guid Id, string Domain, string Status, string ProposedBy, string? ApprovedBy, string Justification,
    DateTimeOffset EffectiveAt, DateTimeOffset CreatedAt, string PayloadJson);

public sealed class ParamChangeConflictException(string message) : Exception(message);

/// <summary>
/// Escritas administrativas (SDD §15): provisionamento de tenant e o fluxo de duas pessoas do catálogo de
/// alíquotas (proposta → aprovação por outra pessoa → aplicação). Nada é editado no lugar: linhas substituídas
/// ficam marcadas como superadas (D-204).
/// </summary>
public sealed class AdminRepository(AdminDataSource admin)
{
    private NpgsqlDataSource Db => admin.DataSource;

    public async Task<TenantId> CreateTenantAsync(NewTenant t, string keyName, string keyPrefix, byte[] keyHash, string[] scopes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(t);
        var tenant = TenantId.New();
        await using var conn = await Db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var batch = new NpgsqlBatch(conn, tx);

        var ins = new NpgsqlBatchCommand("""
            INSERT INTO tenants (id, name, root_cnpj, plan_code, auto_provision_cnpj, kms_key_ref, xml_retention_days,
                                 cnpj_verified_at, cnpj_verification_method, cnpj_verified_by)   -- D-R10: contrato conferido pelo comercial
            VALUES ($1, $2, $3, $4, $5, $6, $7, now(), 'MANUAL', 'backoffice')
            """);
        ins.Parameters.Add(new() { Value = tenant.Value });
        ins.Parameters.Add(new() { Value = t.Name });
        ins.Parameters.Add(new() { Value = t.RootCnpj });
        ins.Parameters.Add(new() { Value = t.PlanCode });
        ins.Parameters.Add(new() { Value = t.AutoProvisionCnpj });
        ins.Parameters.Add(new() { Value = $"tenant/{tenant}" });
        ins.Parameters.Add(new() { Value = (object?)t.XmlRetentionDays ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        batch.BatchCommands.Add(ins);

        var cnpjs = new NpgsqlBatchCommand("INSERT INTO tenant_cnpjs (tenant_id, cnpj) SELECT $1, unnest($2::text[]) ON CONFLICT DO NOTHING");
        cnpjs.Parameters.Add(new() { Value = tenant.Value });
        cnpjs.Parameters.Add(new() { Value = t.Cnpjs.Append(t.RootCnpj).Distinct().ToArray() });
        batch.BatchCommands.Add(cnpjs);

        var key = new NpgsqlBatchCommand("""
            INSERT INTO api_keys (id, tenant_id, name, key_prefix, key_hash, scopes) VALUES ($1, $2, $3, $4, $5, $6)
            """);
        key.Parameters.Add(new() { Value = Guid.CreateVersion7() });
        key.Parameters.Add(new() { Value = tenant.Value });
        key.Parameters.Add(new() { Value = keyName });
        key.Parameters.Add(new() { Value = keyPrefix });
        key.Parameters.Add(new() { Value = keyHash });
        key.Parameters.Add(new() { Value = scopes });
        batch.BatchCommands.Add(key);

        await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return tenant;
    }

    public async Task<Guid> ProposeRateChangeAsync(string proposer, string justification, DateTimeOffset effectiveAt,
        IReadOnlyList<RateChangeRow> rows, IReadOnlyList<Guid> supersedes, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(new RatePayload([.. rows], [.. supersedes]), AdminJsonContext.Default.RatePayload);
        await using var cmd = Db.CreateCommand("""
            INSERT INTO param_changes (id, domain, scope, payload, justification, proposed_by, status, effective_at)
            VALUES ($1, 'RATE_CATALOG', 'GLOBAL', $2, $3, $4, 'PROPOSED', $5)
            """);
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(new() { Value = payload, NpgsqlDbType = NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new() { Value = justification });
        cmd.Parameters.Add(new() { Value = proposer });
        cmd.Parameters.Add(new() { Value = effectiveAt.UtcDateTime });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>Aprova e aplica numa transação. Aprovador = proponente é recusado (aqui e por CHECK no banco).</summary>
    public async Task ApproveRateChangeAsync(Guid changeId, string approver, CancellationToken ct)
    {
        await using var conn = await Db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        string proposer, status, domain, payloadJson;
        await using (var read = new NpgsqlCommand(
            "SELECT proposed_by, status, domain, payload::text FROM param_changes WHERE id = $1 FOR UPDATE", conn, tx))
        {
            read.Parameters.Add(new() { Value = changeId });
            await using var r = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new KeyNotFoundException($"Mudança {changeId} não encontrada.");
            }

            (proposer, status, domain, payloadJson) = (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3));
        }

        if (domain != "RATE_CATALOG" || status != "PROPOSED")
        {
            throw new ParamChangeConflictException($"Mudança {changeId} está {status} ({domain}); só PROPOSED de RATE_CATALOG pode ser aprovada.");
        }

        if (string.Equals(proposer, approver, StringComparison.Ordinal))
        {
            throw new ParamChangeConflictException("Domínio fiscal exige dois aprovadores diferentes (SDD §15.1).");
        }

        // D-R04: duas pessoas DE VERDADE — quem aprova não pode ser um operador criado por quem propôs (nem o contrário).
        await using (var puppet = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM users WHERE realm = 'operator'
                             AND ((email = $1 AND operator_created_by = $2) OR (email = $2 AND operator_created_by = $1)))
            """, conn, tx))
        {
            puppet.Parameters.Add(new() { Value = approver });
            puppet.Parameters.Add(new() { Value = proposer });
            if (await puppet.ExecuteScalarAsync(ct).ConfigureAwait(false) is true)
            {
                throw new ParamChangeConflictException("Quem aprova não pode ter criado (nem ter sido criado por) quem propôs a mudança (D-R04).");
            }
        }

        var payload = JsonSerializer.Deserialize(payloadJson, AdminJsonContext.Default.RatePayload)!;
        await using var batch = new NpgsqlBatch(conn, tx);
        if (payload.Supersedes.Count > 0)
        {
            var sup = new NpgsqlBatchCommand("""
                UPDATE rate_catalog SET superseded_by = $1, superseded_at = now()
                 WHERE id = ANY($2) AND superseded_at IS NULL
                """);
            sup.Parameters.Add(new() { Value = changeId });
            sup.Parameters.Add(new() { Value = payload.Supersedes.ToArray() });
            batch.BatchCommands.Add(sup);
        }

        foreach (var row in payload.Rows)
        {
            var ins = new NpgsqlBatchCommand("""
                INSERT INTO rate_catalog (id, tributo, tipo, esfera, ente_codigo, forma_aplicacao, aliquota, valid_from, valid_to,
                                          ato_legal, fonte, provisoria, change_id)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
                """);
            ins.Parameters.Add(new() { Value = Guid.CreateVersion7() });
            ins.Parameters.Add(new() { Value = row.Tributo });
            ins.Parameters.Add(new() { Value = row.Tipo });
            ins.Parameters.Add(new() { Value = row.Esfera });
            ins.Parameters.Add(new() { Value = (object?)row.EnteCodigo ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            ins.Parameters.Add(new() { Value = (object?)row.FormaAplicacao ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            ins.Parameters.Add(new() { Value = row.Aliquota });
            ins.Parameters.Add(new() { Value = row.ValidFrom });
            ins.Parameters.Add(new() { Value = (object?)row.ValidTo ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
            ins.Parameters.Add(new() { Value = row.AtoLegal });
            ins.Parameters.Add(new() { Value = row.Fonte });
            ins.Parameters.Add(new() { Value = row.Provisoria });
            ins.Parameters.Add(new() { Value = changeId });
            batch.BatchCommands.Add(ins);
        }

        var done = new NpgsqlBatchCommand("UPDATE param_changes SET approved_by = $2, status = 'APPLIED' WHERE id = $1");
        done.Parameters.Add(new() { Value = changeId });
        done.Parameters.Add(new() { Value = approver });
        batch.BatchCommands.Add(done);

        try
        {
            await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.ExclusionViolation or PostgresErrorCodes.CheckViolation)
        {
            throw new ParamChangeConflictException($"A mudança conflita com o catálogo vigente: {ex.MessageText}");
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ParamChange>> ListChangesAsync(string? status, CancellationToken ct)
    {
        await using var cmd = Db.CreateCommand("""
            SELECT id, domain, status, proposed_by, approved_by, justification, effective_at, created_at, payload::text
              FROM param_changes WHERE ($1::text IS NULL OR status = $1) ORDER BY created_at DESC LIMIT 200
            """);
        cmd.Parameters.Add(new() { Value = (object?)status ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<ParamChange>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ParamChange(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.GetString(5), r.GetFieldValue<DateTimeOffset>(6), r.GetFieldValue<DateTimeOffset>(7), r.GetString(8)));
        }

        return list;
    }

    /// <summary>Linhas ativas do catálogo (para o operador escolher o que superar).</summary>
    public async Task<string> ListActiveRatesJsonAsync(CancellationToken ct)
    {
        await using var cmd = Db.CreateCommand("""
            SELECT coalesce(json_agg(json_build_object(
                     'id', id, 'tributo', tributo, 'tipo', tipo, 'esfera', esfera, 'ente_codigo', ente_codigo,
                     'forma_aplicacao', forma_aplicacao, 'aliquota', aliquota, 'valid_from', valid_from, 'valid_to', valid_to,
                     'ato_legal', ato_legal, 'fonte', fonte, 'provisoria', provisoria, 'change_id', change_id)
                   ORDER BY tributo, ente_codigo NULLS FIRST, valid_from), '[]')::text
              FROM rate_catalog WHERE superseded_at IS NULL
            """);
        return (string)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <summary>Cliente OAuth (Fase 8, D-802): só o hash Argon2id do segredo é gravado. false se o tenant não existe.</summary>
    public async Task<bool> CreateOAuthClientAsync(Guid tenant, string clientId, string name, byte[] hash, byte[] salt, string[] scopes, CancellationToken ct)
    {
        await using var cmd = Db.CreateCommand("""
            INSERT INTO oauth_applications (client_id, tenant_id, name, secret_hash, secret_salt, scopes)
            SELECT $1, t.id, $3, $4, $5, $6 FROM tenants t WHERE t.id = $2
            """);
        cmd.Parameters.Add(new() { Value = clientId });
        cmd.Parameters.Add(new() { Value = tenant });
        cmd.Parameters.Add(new() { Value = name });
        cmd.Parameters.Add(new() { Value = hash });
        cmd.Parameters.Add(new() { Value = salt });
        cmd.Parameters.Add(new() { Value = scopes });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>jti revogado no banco (fonte da verdade, D-P10); o Valkey só propaga mais rápido.</summary>
    public async Task RevokeTokenAsync(string jti, DateTimeOffset expiresAt, string operatorName, CancellationToken ct)
    {
        await using var cmd = Db.CreateCommand("""
            INSERT INTO revoked_tokens (jti, expires_at, revoked_by) VALUES ($1, $2, $3)
            ON CONFLICT (jti) DO UPDATE SET expires_at = greatest(revoked_tokens.expires_at, EXCLUDED.expires_at)
            """);
        cmd.Parameters.Add(new() { Value = jti });
        cmd.Parameters.Add(new() { Value = expiresAt });
        cmd.Parameters.Add(new() { Value = operatorName });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> RevokeOAuthClientAsync(string clientId, CancellationToken ct)
    {
        await using var cmd = Db.CreateCommand("UPDATE oauth_applications SET revoked_at = now() WHERE client_id = $1 AND revoked_at IS NULL");
        cmd.Parameters.Add(new() { Value = clientId });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>ACTIVE ↔ SUSPENDED. false se o tenant não existe ou está CLOSED.</summary>
    public async Task<bool> SetTenantStatusAsync(Guid tenant, string status, CancellationToken ct)
    {
        await using var cmd = Db.CreateCommand("UPDATE tenants SET status = $2 WHERE id = $1 AND status <> 'CLOSED'");
        cmd.Parameters.Add(new() { Value = tenant });
        cmd.Parameters.Add(new() { Value = status });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }
}

public sealed record RatePayload(List<RateChangeRow> Rows, List<Guid> Supersedes);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.SnakeCaseLower)]
[System.Text.Json.Serialization.JsonSerializable(typeof(RatePayload))]
internal sealed partial class AdminJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
