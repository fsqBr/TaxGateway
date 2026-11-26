using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

using Konscious.Security.Cryptography;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

using Npgsql;

using OpenIddict.Abstractions;
using OpenIddict.Server;

using StackExchange.Redis;

using TaxGateway.Core.Tenancy;

using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TaxGateway.Identity;

/// <summary>
/// Segredo do cliente OAuth em Argon2id (SDD §3.1; D-802). Parâmetros do mínimo recomendado pelo OWASP
/// (19 MiB, 2 iterações, 1 via): custa ~dezenas de ms, aceitável no /oauth/token (uma vez a cada 15 min por cliente).
/// </summary>
public static class ClientSecretHasher
{
    public static (byte[] Hash, byte[] Salt) Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return (Compute(secret, salt), salt);
    }

    public static bool Verify(string secret, byte[] hash, byte[] salt) =>
        CryptographicOperations.FixedTimeEquals(Compute(secret, salt), hash);

    private static byte[] Compute(string secret, byte[] salt)
    {
        using var a = new Argon2id(Encoding.UTF8.GetBytes(secret)) { Salt = salt, MemorySize = 19456, Iterations = 2, DegreeOfParallelism = 1 };
        return a.GetBytes(32);
    }

    public static string NewSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(30)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

public sealed record OAuthClient(Guid Tenant, byte[] Hash, byte[] Salt, IReadOnlyList<string> Scopes, string PlanCode, string Status, string LimitsOverride, bool AutoProvision);

/// <summary>Busca do cliente antes de existir contexto de tenant (função SECURITY DEFINER do tg_auth, como a API key).</summary>
public sealed class OAuthClientStore(NpgsqlDataSource appDb)
{
    public async Task<OAuthClient?> FindAsync(string clientId, CancellationToken ct)
    {
        await using var cmd = appDb.CreateCommand(
            "SELECT tenant_id, secret_hash, secret_salt, scopes, plan_code, tenant_status, limits_override::text, auto_provision_cnpj FROM app.resolve_oauth_client($1)");
        cmd.Parameters.Add(new() { Value = clientId });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false) || r.IsDBNull(1))
        {
            return null;
        }

        return new OAuthClient(r.GetGuid(0), r.GetFieldValue<byte[]>(1), r.GetFieldValue<byte[]>(2), r.GetFieldValue<string[]>(3),
            r.GetString(4), r.GetString(5), r.GetString(6), r.GetBoolean(7));
    }
}

/// <summary>
/// Revogação (SDD §3.1; D-803): por jti, por cliente e por tenant suspenso. Estado no Valkey; cada instância mantém
/// conjuntos locais atualizados por pub/sub e por releitura a cada 2 s. A checagem por requisição é só em memória
/// (sem ida ao Valkey), e a suspensão vale em todas as instâncias em menos de 5 s (meta do §12, Fase 8).
/// </summary>
public sealed partial class RevocationService(IConnectionMultiplexer valkey, ILogger<RevocationService> log) : BackgroundService
{
    public const string Channel = "auth:revocations";
    private const string Tenants = "revoked:tenants";
    private const string Clients = "revoked:clients";
    private const string Jtis = "revoked:jtis";
    private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(2);

    private volatile HashSet<string> _tenants = [];
    private volatile HashSet<string> _clients = [];
    private readonly ConcurrentDictionary<string, DateTimeOffset> _jtis = new(StringComparer.Ordinal);

    public bool IsTenantSuspended(TenantId tenant) => _tenants.Contains(tenant.ToString());

    public bool IsRevoked(string? jti, string? clientId, TenantId tenant) =>
        _tenants.Contains(tenant.ToString())
        || (clientId is not null && _clients.Contains(clientId))
        || (jti is not null && _jtis.TryGetValue(jti, out var exp) && exp > DateTimeOffset.UtcNow);

    public Task SuspendTenantAsync(TenantId tenant) => ChangeAsync(db => db.SetAddAsync(Tenants, tenant.ToString()), $"t+{tenant}");

    public Task ReactivateTenantAsync(TenantId tenant) => ChangeAsync(db => db.SetRemoveAsync(Tenants, tenant.ToString()), $"t-{tenant}");

    public Task RevokeClientAsync(string clientId) => ChangeAsync(db => db.SetAddAsync(Clients, clientId), $"c+{clientId}");

    public Task RevokeJtiAsync(string jti, DateTimeOffset expiresAt) =>
        ChangeAsync(db => db.HashSetAsync(Jtis, jti, expiresAt.ToUnixTimeSeconds()), $"j+{jti}|{expiresAt.ToUnixTimeSeconds()}");

    private async Task ChangeAsync(Func<IDatabase, Task> write, string message)
    {
        await write(valkey.GetDatabase()).ConfigureAwait(false);
        Apply(message);   // efeito imediato nesta instância
        await valkey.GetSubscriber().PublishAsync(RedisChannel.Literal(Channel), message).ConfigureAwait(false);
    }

    private void Apply(string m)
    {
        var (op, value) = (m[..2], m[2..]);
        switch (op)
        {
            case "t+": _tenants = [.. _tenants, value]; break;
            case "t-": _tenants = [.. _tenants.Where(x => x != value)]; break;
            case "c+": _clients = [.. _clients, value]; break;
            case "j+":
                var parts = value.Split('|');
                _jtis[parts[0]] = DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
                break;
        }
    }

    public async Task LoadAsync()
    {
        var db = valkey.GetDatabase();
        _tenants = [.. (await db.SetMembersAsync(Tenants).ConfigureAwait(false)).Select(x => x.ToString())];
        _clients = [.. (await db.SetMembersAsync(Clients).ConfigureAwait(false)).Select(x => x.ToString())];
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var e in await db.HashGetAllAsync(Jtis).ConfigureAwait(false))
        {
            if ((long)e.Value > now)
            {
                _jtis[e.Name.ToString()] = DateTimeOffset.FromUnixTimeSeconds((long)e.Value);
            }
            else
            {
                await db.HashDeleteAsync(Jtis, e.Name).ConfigureAwait(false);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await valkey.GetSubscriber().SubscribeAsync(RedisChannel.Literal(Channel), (_, m) => Apply(m.ToString())).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            LogError(log, ex);
        }

        using var timer = new PeriodicTimer(Refresh);
        do
        {
            try
            {
                await LoadAsync().ConfigureAwait(false);
            }
            catch (RedisException ex)
            {
                LogError(log, ex);   // mantém o último estado conhecido
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Revogações: Valkey indisponível, mantendo o último estado.")]
    private static partial void LogError(ILogger logger, Exception ex);
}

public sealed class IdentityOptions
{
    public const string Section = "Identity";

    /// <summary>Chaves RSA de assinatura (PEM). A primeira assina; as demais só validam (rotação com sobreposição, D-804).</summary>
    public List<SigningKey> SigningKeys { get; set; } = [];

    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>Só dev/teste: aceita /oauth/token sem TLS.</summary>
    public bool AllowHttp { get; set; }

    /// <summary>
    /// Só dev: sem <see cref="SigningKeys"/>, usa chave efêmera do processo. Fora disso a API não sobe sem chave
    /// configurada — com chave efêmera, cada pod assinaria com uma chave diferente e os tokens falhariam entre pods.
    /// </summary>
    public bool AllowEphemeralKeys { get; set; }
}

public sealed class SigningKey
{
    public string Kid { get; set; } = "";
    public string Pem { get; set; } = "";
}

public static class TaxGatewayIdentityExtensions
{
    public const string TenantClaim = "tid";
    public const string PlanClaim = "plan";

    /// <summary>
    /// OpenIddict 7 em modo degradado (D-801): só client_credentials, sem banco próprio do OpenIddict — os clientes
    /// estão em oauth_applications. JWT assinado (RS256, typ at+jwt), sem cifrar (validável pelo JWKS), 15 min.
    /// </summary>
    public static IServiceCollection AddTaxGatewayIdentity(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var options = config.GetSection(IdentityOptions.Section).Get<IdentityOptions>() ?? new IdentityOptions();
        services.AddSingleton<OAuthClientStore>();
        services.AddSingleton<RevocationService>();
        services.AddHostedService(sp => sp.GetRequiredService<RevocationService>());

        services.AddOpenIddict()
            .AddServer(o =>
            {
                o.SetTokenEndpointUris("/oauth/token");
                o.SetJsonWebKeySetEndpointUris("/.well-known/jwks.json");
                o.SetConfigurationEndpointUris("/.well-known/openid-configuration");
                o.AllowClientCredentialsFlow();
                o.EnableDegradedMode();
                o.DisableAccessTokenEncryption();
                o.SetAccessTokenLifetime(TimeSpan.FromMinutes(options.AccessTokenMinutes));
                o.RegisterScopes("simulacao:write", "auditoria:write", "keys:manage", "webhooks:manage", "usage:read", "cnpjs:manage", "titulares:manage", "dfe:manage");
                if (options.SigningKeys.Count == 0)
                {
                    if (!options.AllowEphemeralKeys)
                    {
                        throw new InvalidOperationException("Identity:SigningKeys não configurada (D-804). Em dev, use Identity:AllowEphemeralKeys=true.");
                    }

                    o.AddEphemeralSigningKey();
                }

                foreach (var k in options.SigningKeys)
                {
                    var rsa = RSA.Create();
                    rsa.ImportFromPem(k.Pem);
                    o.AddSigningKey(new RsaSecurityKey(rsa) { KeyId = k.Kid });
                }

                o.AddEphemeralEncryptionKey();
                var aspnet = o.UseAspNetCore();
                if (options.AllowHttp)
                {
                    aspnet.DisableTransportSecurityRequirement();
                }

                o.AddEventHandler<ValidateTokenRequestContext>(b => b.UseScopedHandler<ValidateClientHandler>());
                o.AddEventHandler<HandleTokenRequestContext>(b => b.UseScopedHandler<IssueTokenHandler>());
            })
            .AddValidation(o =>
            {
                o.UseLocalServer();
                o.UseAspNetCore();
            });
        services.AddAuthentication();
        return services;
    }

    /// <summary>Valida client_id/segredo (Argon2id), tenant ativo e escopos pedidos ⊆ escopos do cliente.</summary>
    public sealed class ValidateClientHandler(OAuthClientStore store, RevocationService revocations) : IOpenIddictServerHandler<ValidateTokenRequestContext>
    {
        public async ValueTask HandleAsync(ValidateTokenRequestContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!context.Request.IsClientCredentialsGrantType())
            {
                context.Reject(Errors.UnsupportedGrantType, "Só client_credentials.");
                return;
            }

            var client = context.ClientId is null ? null : await store.FindAsync(context.ClientId, CancellationToken.None).ConfigureAwait(false);
            if (client is null || context.Request.ClientSecret is null || !ClientSecretHasher.Verify(context.Request.ClientSecret, client.Hash, client.Salt))
            {
                context.Reject(Errors.InvalidClient, "Credenciais inválidas.");
                return;
            }

            if (client.Status != "ACTIVE" || revocations.IsRevoked(null, context.ClientId, new TenantId(client.Tenant)))
            {
                context.Reject(Errors.InvalidClient, "Cliente revogado ou tenant suspenso.");
                return;
            }

            var requested = context.Request.GetScopes();
            if (requested.Any(s => !client.Scopes.Contains(s, StringComparer.Ordinal)))
            {
                context.Reject(Errors.InvalidScope, "Escopo não concedido a este cliente.");
                return;
            }

            context.Transaction.Properties["tg.client"] = client;
        }
    }

    /// <summary>Emite o token: sub = client_id, tid, plan e escopos (limites não vão no token: mudam na hora, §3.1).</summary>
    public sealed class IssueTokenHandler : IOpenIddictServerHandler<HandleTokenRequestContext>
    {
        public ValueTask HandleAsync(HandleTokenRequestContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var client = (OAuthClient)context.Transaction.Properties["tg.client"]!;
            var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
            identity.SetClaim(Claims.Subject, context.ClientId);
            identity.SetClaim(TenantClaim, client.Tenant.ToString());
            identity.SetClaim(PlanClaim, client.PlanCode);
            var requested = context.Request.GetScopes();
            identity.SetScopes(requested.Length > 0 ? requested : [.. client.Scopes]);
            identity.SetDestinations(_ => [Destinations.AccessToken]);
            context.SignIn(new ClaimsPrincipal(identity));
            return ValueTask.CompletedTask;
        }
    }
}
