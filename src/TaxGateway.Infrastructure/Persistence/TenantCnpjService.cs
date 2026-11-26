using System.Collections.Frozen;

using Microsoft.Extensions.Caching.Memory;

using TaxGateway.Core.Tenancy;

namespace TaxGateway.Infrastructure.Persistence;

/// <summary>
/// CNPJs ativos do tenant (SDD §3.3) em cache L1 de 30 s: só o primeiro acesso do tenant vai ao banco.
/// Com <c>auto_provision_cnpj</c>, um CNPJ novo é registrado na hora.
/// </summary>
public sealed class TenantCnpjService(ITenantDb db, IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <returns>true se o CNPJ está (ou passou a estar) ativo para o tenant.</returns>
    public async Task<bool> EnsureAllowedAsync(TenantId tenant, string cnpj, bool autoProvision, CancellationToken ct)
    {
        var key = $"cnpjs:{tenant}";
        if (!cache.TryGetValue(key, out FrozenSet<string>? active))
        {
            active = await db.InTenantAsync(tenant, async (s, c) =>
            {
                await using var cmd = s.CreateCommand("SELECT cnpj FROM tenant_cnpjs WHERE is_active");
                await using var r = await cmd.ExecuteReaderAsync(c).ConfigureAwait(false);
                var set = new HashSet<string>(StringComparer.Ordinal);
                while (await r.ReadAsync(c).ConfigureAwait(false))
                {
                    set.Add(r.GetString(0));
                }

                return set.ToFrozenSet(StringComparer.Ordinal);
            }, ct).ConfigureAwait(false);
            cache.Set(key, active, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl, Size = active.Count + 1 });
        }

        if (active!.Contains(cnpj))
        {
            return true;
        }

        if (!autoProvision)
        {
            return false;
        }

        // Um CNPJ desativado pelo tenant (DELETE /v1/oem/cnpjs) NÃO volta pelo auto-provisionamento: só um POST
        // explícito o reativa (D-806). ON CONFLICT DO NOTHING devolve 0 linhas nesse caso.
        var inserted = await db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("INSERT INTO tenant_cnpjs (tenant_id, cnpj) VALUES ($1, $2) ON CONFLICT DO NOTHING");
            cmd.Parameters.Add(new() { Value = tenant.Value });
            cmd.Parameters.Add(new() { Value = cnpj });
            return await cmd.ExecuteNonQueryAsync(c).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        cache.Remove(key);
        return inserted == 1;
    }

    /// <summary>Cadastra ou reativa (POST /v1/oem/cnpjs). true se passou a ativo agora; false se já estava ativo.</summary>
    public async Task<bool> ActivateAsync(TenantId tenant, string cnpj, CancellationToken ct)
    {
        var n = await db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("""
                INSERT INTO tenant_cnpjs (tenant_id, cnpj) VALUES ($1, $2)
                ON CONFLICT (tenant_id, cnpj) DO UPDATE SET is_active = TRUE, deactivated_from = NULL WHERE NOT tenant_cnpjs.is_active
                """);
            cmd.Parameters.Add(new() { Value = tenant.Value });
            cmd.Parameters.Add(new() { Value = cnpj });
            return await cmd.ExecuteNonQueryAsync(c).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        Invalidate(tenant);
        return n == 1;
    }

    /// <summary>
    /// Desativa (DELETE /v1/oem/cnpjs/{cnpj}): chamadas com o CNPJ passam a ser recusadas; a competência corrente já
    /// cobrada continua e a cobrança acaba na seguinte (<c>deactivated_from</c>, SDD §6.4). false se não estava ativo.
    /// </summary>
    public async Task<bool> DeactivateAsync(TenantId tenant, string cnpj, DateOnly nextMonth, CancellationToken ct)
    {
        var n = await db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand(
                "UPDATE tenant_cnpjs SET is_active = FALSE, deactivated_from = $2 WHERE cnpj = $1 AND is_active");
            cmd.Parameters.Add(new() { Value = cnpj });
            cmd.Parameters.Add(new() { Value = nextMonth });
            return await cmd.ExecuteNonQueryAsync(c).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        Invalidate(tenant);
        return n == 1;
    }

    public void Invalidate(TenantId tenant) => cache.Remove($"cnpjs:{tenant}");
}
