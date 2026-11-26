using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

using TaxGateway.Core.Tenancy;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Infrastructure.Auth;

public sealed class ApiKeyOptions
{
    public const string Section = "Auth";

    /// <summary>
    /// Pepper do HMAC das API keys (base64, ≥ 32 bytes). Em produção vem do KMS/secret manager e nunca do banco:
    /// um vazamento só do banco não permite verificar chaves offline (SDD §3.2).
    /// </summary>
    public string ApiKeyPepper { get; set; } = "";

    /// <summary>"live" em produção, "test" nos demais ambientes.</summary>
    public string KeyEnvironment { get; set; } = "test";
}

/// <summary>Chave em texto (mostrada uma única vez), prefixo público e hash armazenado.</summary>
public sealed record GeneratedApiKey(string Plaintext, string Prefix, byte[] Hash);

/// <summary>Formato e hash das API keys: <c>tg_{env}_sk_&lt;prefix8&gt;_&lt;secret32&gt;</c> (SDD §3.2).</summary>
public sealed partial class ApiKeyHasher
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private readonly byte[] _pepper;
    private readonly string _env;

    public ApiKeyHasher(IOptions<ApiKeyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var pepper = options.Value.ApiKeyPepper;
        if (string.IsNullOrWhiteSpace(pepper) || !Base64.IsValid(pepper) || Convert.FromBase64String(pepper).Length < 32)
        {
            throw new InvalidOperationException("Auth:ApiKeyPepper ausente ou com menos de 32 bytes (base64).");
        }

        _pepper = Convert.FromBase64String(pepper);
        _env = options.Value.KeyEnvironment is "live" ? "live" : "test";
    }

    /// <summary>Rejeita sem tocar em cache ou banco qualquer valor fora do formato.</summary>
    public static bool HasValidFormat(string? key) => key is not null && KeyFormat().IsMatch(key);

    public byte[] Hash(string key) => HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes(key));

    public GeneratedApiKey Generate()
    {
        var prefix = RandomNumberGenerator.GetString(Alphabet, 8);
        var secret = RandomNumberGenerator.GetString(Alphabet, 32);   // ~190 bits
        var key = $"tg_{_env}_sk_{prefix}_{secret}";
        return new GeneratedApiKey(key, prefix, Hash(key));
    }

    [GeneratedRegex("^tg_(live|test)_sk_[A-Za-z0-9]{8}_[A-Za-z0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyFormat();
}

/// <summary>
/// Autenticação por API Key (SDD §3.2, §16.2 item 3): L1 em memória (30 s; negativo 10 s), L2 no Valkey
/// (<c>auth:apikey:{hash}</c>, 5 min) e, no miss, <c>app.resolve_api_key</c>. Revogação apaga o L2 e avisa as
/// instâncias por pub/sub para descartarem o L1 (D-210).
/// </summary>
public sealed partial class ApiKeyAuthenticator : IDisposable
{
    public const string RevocationChannel = "auth:revoked";
    private static readonly TimeSpan L1Ttl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan L1NegativeTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan L2Ttl = TimeSpan.FromMinutes(5);

    private readonly ApiKeyHasher _hasher;
    private readonly ApiKeyLookup _lookup;
    private readonly IConnectionMultiplexer _valkey;
    private readonly ILogger<ApiKeyAuthenticator> _log;
    private readonly MemoryCache _l1 = new(new MemoryCacheOptions { SizeLimit = 100_000 });

    public ApiKeyAuthenticator(ApiKeyHasher hasher, ApiKeyLookup lookup, IConnectionMultiplexer valkey, ILogger<ApiKeyAuthenticator> log)
    {
        _hasher = hasher;
        _lookup = lookup;
        _valkey = valkey;
        _log = log;
        try
        {
            valkey.GetSubscriber().Subscribe(RedisChannel.Literal(RevocationChannel), (_, hex) => _l1.Remove(hex.ToString()));
        }
        catch (RedisException ex)
        {
            LogSubscribeFailed(_log, ex);   // sem pub/sub, o L1 expira em 30 s
        }
    }

    public async Task<ApiKeyIdentity?> AuthenticateAsync(string? key, CancellationToken ct)
    {
        if (!ApiKeyHasher.HasValidFormat(key))
        {
            return null;
        }

        var hash = _hasher.Hash(key!);
        var hex = Convert.ToHexStringLower(hash);
        if (_l1.TryGetValue(hex, out CachedIdentity? cached))
        {
            return cached!.Identity;
        }

        var identity = await FromValkeyAsync(hex).ConfigureAwait(false);
        if (identity is null)
        {
            identity = await _lookup.ResolveAsync(hash, ct).ConfigureAwait(false);
            if (identity is not null)
            {
                await ToValkeyAsync(hex, identity).ConfigureAwait(false);
            }
        }

        _l1.Set(hex, new CachedIdentity(identity), new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = identity is null ? L1NegativeTtl : L1Ttl,
            Size = 1,
        });
        return identity;
    }

    /// <summary>Invalida a chave em todas as instâncias (chamado depois de gravar revoked_at).</summary>
    public async Task InvalidateAsync(byte[] keyHash)
    {
        var hex = Convert.ToHexStringLower(keyHash);
        _l1.Remove(hex);
        try
        {
            var db = _valkey.GetDatabase();
            await db.KeyDeleteAsync($"auth:apikey:{hex}").ConfigureAwait(false);
            await _valkey.GetSubscriber().PublishAsync(RedisChannel.Literal(RevocationChannel), hex).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            LogInvalidateFailed(_log, ex);   // pior caso: L2 expira em 5 min; resolve_api_key já filtra revogadas
        }
    }

    private async Task<ApiKeyIdentity?> FromValkeyAsync(string hex)
    {
        try
        {
            var value = await _valkey.GetDatabase().StringGetAsync($"auth:apikey:{hex}").ConfigureAwait(false);
            return value.IsNullOrEmpty ? null : CachedIdentityJson.Read(value!);
        }
        catch (RedisException ex)
        {
            LogValkeyUnavailable(_log, ex);
            return null;
        }
    }

    private async Task ToValkeyAsync(string hex, ApiKeyIdentity identity)
    {
        try
        {
            await _valkey.GetDatabase().StringSetAsync($"auth:apikey:{hex}", CachedIdentityJson.Write(identity), L2Ttl).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            LogValkeyUnavailable(_log, ex);
        }
    }

    public void Dispose() => _l1.Dispose();

    private sealed record CachedIdentity(ApiKeyIdentity? Identity);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Valkey indisponível no cache de autenticação; seguindo pelo banco.")]
    private static partial void LogValkeyUnavailable(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao assinar o canal de revogação de API keys.")]
    private static partial void LogSubscribeFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha ao propagar revogação de API key.")]
    private static partial void LogInvalidateFailed(ILogger logger, Exception ex);
}

/// <summary>JSON do cache L2 (sem reflexão).</summary>
internal static class CachedIdentityJson
{
    public static string Write(ApiKeyIdentity i)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("t", i.Tenant.Value);
            w.WriteString("k", i.KeyId);
            w.WriteStartArray("s");
            foreach (var s in i.Scopes)
            {
                w.WriteStringValue(s);
            }

            w.WriteEndArray();
            w.WriteString("p", i.PlanCode);
            w.WriteString("st", i.TenantStatus);
            w.WriteString("o", i.LimitsOverrideJson);
            w.WriteBoolean("a", i.AutoProvisionCnpj);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static ApiKeyIdentity Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new ApiKeyIdentity(
            new TenantId(r.GetProperty("t").GetGuid()), r.GetProperty("k").GetGuid(),
            r.GetProperty("s").EnumerateArray().Select(x => x.GetString()!).ToArray(),
            r.GetProperty("p").GetString()!, r.GetProperty("st").GetString()!,
            r.GetProperty("o").GetString()!, r.GetProperty("a").GetBoolean());
    }
}
