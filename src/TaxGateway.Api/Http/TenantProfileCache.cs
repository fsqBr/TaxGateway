using Microsoft.Extensions.Caching.Memory;

using Npgsql;

using StackExchange.Redis;

using TaxGateway.Core.Tenancy;

namespace TaxGateway.Api.Http;

public sealed record TenantProfile(string PlanCode, string Status, string LimitsOverrideJson, bool AutoProvisionCnpj);

/// <summary>
/// Perfil do tenant (plano, situação, overrides) para TODAS as credenciais — API key, OAuth e sessão (D-C08). Antes, o
/// plano ia junto no cache da API key (até 5 min): depois de contratar ou trocar de plano, o ERP continuava nos limites
/// antigos. Agora o perfil tem cache próprio de 30 s e é invalidado na hora pelo canal <see cref="Channel"/>, publicado
/// por quem muda plano ou situação (checkout/assinatura da Stripe, backoffice).
/// </summary>
public sealed class TenantProfileCache
{
    public const string Channel = "tenant:changed";
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private readonly NpgsqlDataSource _appDb;
    private readonly IMemoryCache _cache;

    public TenantProfileCache(NpgsqlDataSource appDb, IMemoryCache cache, IConnectionMultiplexer valkey)
    {
        _appDb = appDb;
        _cache = cache;
        try
        {
            valkey.GetSubscriber().Subscribe(RedisChannel.Literal(Channel), (_, tenant) => _cache.Remove(Key(tenant.ToString())));
        }
        catch (RedisException)
        {
            // sem pub/sub, vale o TTL de 30 s
        }
    }

    private static string Key(string tenant) => $"tenantprofile:{tenant}";

    public async Task<TenantProfile?> GetAsync(TenantId tenant, CancellationToken ct)
    {
        var key = Key(tenant.ToString());
        if (_cache.TryGetValue(key, out TenantProfile? p))
        {
            return p;
        }

        await using var cmd = _appDb.CreateCommand("SELECT plan_code, tenant_status, limits_override::text, auto_provision_cnpj FROM app.tenant_profile($1)");
        cmd.Parameters.Add(new() { Value = tenant.Value });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        p = new TenantProfile(r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3));
        _cache.Set(key, p, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl, Size = 1 });
        return p;
    }

    /// <summary>Avisa todas as instâncias que o perfil do tenant mudou.</summary>
    public static async Task PublishChangedAsync(IConnectionMultiplexer valkey, Guid tenant)
    {
        try
        {
            await valkey.GetSubscriber().PublishAsync(RedisChannel.Literal(Channel), tenant.ToString()).ConfigureAwait(false);
        }
        catch (RedisException)
        {
            // TTL de 30 s cobre
        }
    }
}
