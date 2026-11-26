using Npgsql;

using TaxGateway.Core.Tenancy;

namespace TaxGateway.Infrastructure.Persistence;

public sealed record ApiKeyIdentity(
    TenantId Tenant,
    Guid KeyId,
    IReadOnlyList<string> Scopes,
    string PlanCode,
    string TenantStatus,
    string LimitsOverrideJson,
    bool AutoProvisionCnpj);

/// <summary>
/// Resolve uma API Key ANTES de existir contexto de tenant, via <c>app.resolve_api_key</c>
/// (SECURITY DEFINER, dono <c>tg_auth</c>, SDD §3.2). O cache L1/Valkey fica na frente (ApiKeyAuthenticator).
/// </summary>
public sealed class ApiKeyLookup(NpgsqlDataSource appDataSource)
{
    public async Task<ApiKeyIdentity?> ResolveAsync(byte[] keyHash, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keyHash);
        await using var cmd = appDataSource.CreateCommand(
            "SELECT tenant_id, key_id, scopes, plan_code, tenant_status, limits_override::text, auto_provision_cnpj FROM app.resolve_api_key($1)");
        cmd.Parameters.Add(new() { Value = keyHash });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new ApiKeyIdentity(
            new TenantId(reader.GetGuid(0)), reader.GetGuid(1), reader.GetFieldValue<string[]>(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetBoolean(6));
    }
}
