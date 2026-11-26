using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;

using Npgsql;

using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

using TaxGateway.Core.Tenancy;
using TaxGateway.Identity;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Api.Http;

/// <summary>
/// Fluxo A (SDD §3.1; D-801): valida o JWT localmente (assinatura pelas chaves publicadas no JWKS, exp, typ),
/// consulta a blacklist em memória (jti/cliente/tenant) e lê o perfil do tenant (plano, overrides) de um cache de 30 s —
/// os limites não vão no token. Devolve a mesma identidade do fluxo de API key, então o resto do pipeline não muda.
/// </summary>
public sealed class BearerAuthenticator(NpgsqlDataSource appDb, RevocationService revocations, IMemoryCache cache)
{
    public const string ClientItem = "tg.oauth.client";
    private static readonly TimeSpan ProfileTtl = TimeSpan.FromSeconds(30);

    public async Task<ApiKeyIdentity?> AuthenticateAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var result = await http.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme).ConfigureAwait(false);
        if (!result.Succeeded || result.Principal is not { } p
            || !Guid.TryParse(p.GetClaim(TaxGatewayIdentityExtensions.TenantClaim), out var tid))
        {
            return null;
        }

        var tenant = new TenantId(tid);
        http.Items[ClientItem] = p.GetClaim(OpenIddictConstants.Claims.Subject);   // client_id, para trilhas de auditoria
        if (revocations.IsRevoked(p.GetClaim(OpenIddictConstants.Claims.JwtId), p.GetClaim(OpenIddictConstants.Claims.Subject), tenant))
        {
            return null;
        }

        var key = $"tprofile:{tid}";
        if (!cache.TryGetValue(key, out ApiKeyIdentity? profile))
        {
            await using var cmd = appDb.CreateCommand(
                "SELECT plan_code, tenant_status, limits_override::text, auto_provision_cnpj FROM app.tenant_profile($1)");
            cmd.Parameters.Add(new() { Value = tid });
            await using var r = await cmd.ExecuteReaderAsync(http.RequestAborted).ConfigureAwait(false);
            if (!await r.ReadAsync(http.RequestAborted).ConfigureAwait(false))
            {
                return null;
            }

            profile = new ApiKeyIdentity(tenant, Guid.Empty, [], r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3));
            cache.Set(key, profile, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ProfileTtl, Size = 1 });
        }

        return profile! with { Scopes = [.. p.GetScopes()] };
    }
}
