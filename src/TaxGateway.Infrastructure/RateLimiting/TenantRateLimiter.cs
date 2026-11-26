using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

using TaxGateway.Core.Params;
using TaxGateway.Core.Tenancy;

namespace TaxGateway.Infrastructure.RateLimiting;

public sealed class RateLimitOptions
{
    public const string Section = "RateLimiting";

    /// <summary>Acima disso o Valkey é considerado lento e a requisição usa o limitador local (D-211).</summary>
    public int ValkeyTimeoutMs { get; set; } = 50;

    /// <summary>Depois de uma falha do Valkey, quanto tempo seguir direto no modo local.</summary>
    public int LocalModeSeconds { get; set; } = 5;

    /// <summary>Número de réplicas da API: no modo local cada instância aplica limite/N (SDD §5.2).</summary>
    public int InstanceCountHint { get; set; } = 1;
}

public enum RateLimitReason { None, Concurrency, Rate }

/// <summary>Resultado da tentativa. Dispose libera o lease de concorrência (sempre em <c>finally</c>).</summary>
public sealed class RateLimitLease : IAsyncDisposable
{
    private readonly Func<ValueTask>? _release;
    private int _released;

    internal RateLimitLease(bool acquired, RateLimitReason reason, PlanLimits limits, int tokensLeft, int concurrencyLeft,
        TimeSpan retryAfter, bool local, Func<ValueTask>? release)
    {
        Acquired = acquired;
        Reason = reason;
        Limits = limits;
        TokensLeft = tokensLeft;
        ConcurrencyLeft = concurrencyLeft;
        RetryAfter = retryAfter;
        Local = local;
        _release = release;
    }

    public bool Acquired { get; }
    public RateLimitReason Reason { get; }
    public PlanLimits Limits { get; }
    public int TokensLeft { get; }
    public int ConcurrencyLeft { get; }
    public TimeSpan RetryAfter { get; }
    public bool Local { get; }

    public ValueTask DisposeAsync() =>
        _release is not null && Interlocked.Exchange(ref _released, 1) == 0 ? _release() : ValueTask.CompletedTask;
}

/// <summary>
/// Token bucket + leases de concorrência num único script Lua atômico (SDD §5.2, §16.2 item 8): uma ida ao Valkey.
/// O relógio é o do Valkey (TIME), igual para todas as instâncias. Lease com TTL: uma requisição que morreu não
/// prende o slot além do TTL. Valkey fora ou lento: limitador local com limite/N, métrica e log (nunca rejeita tudo).
/// </summary>
public sealed partial class TenantRateLimiter
{
    // KEYS: 1 = bucket (hash tk/ts), 2 = leases (zset id -> expira_em_ms)
    // ARGV: rpm, burst, concorrência, ttl do lease (ms), id do lease
    // Retorno: {permitido, motivo(0 ok,1 conc,2 taxa), tokens restantes, concorrência restante, retry_ms}
    private const string Script = """
        local tb, cc = KEYS[1], KEYS[2]
        local rpm, burst, conc = tonumber(ARGV[1]), tonumber(ARGV[2]), tonumber(ARGV[3])
        local ttl, id = tonumber(ARGV[4]), ARGV[5]
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        redis.call('ZREMRANGEBYSCORE', cc, '-inf', now)
        local inuse = redis.call('ZCARD', cc)
        local s = redis.call('HMGET', tb, 'tk', 'ts')
        local tokens, ts = tonumber(s[1]), tonumber(s[2])
        if tokens == nil or ts == nil then tokens = burst; ts = now end
        local rate = rpm / 60000.0
        tokens = math.min(burst, tokens + math.max(0, now - ts) * rate)
        if inuse >= conc then
          return {0, 1, math.floor(tokens), 0, 1000}
        end
        if tokens < 1 then
          return {0, 2, 0, conc - inuse, math.ceil((1 - tokens) / rate)}
        end
        tokens = tokens - 1
        redis.call('HSET', tb, 'tk', tostring(tokens), 'ts', now)
        redis.call('PEXPIRE', tb, math.ceil(burst / rate) + 1000)
        redis.call('ZADD', cc, now + ttl, id)
        redis.call('PEXPIRE', cc, ttl + 1000)
        return {1, 0, math.floor(tokens), conc - inuse - 1, 0}
        """;

    private static readonly Meter Meter = new("TaxGateway");
    private static readonly Counter<long> Rejected = Meter.CreateCounter<long>("taxgateway_ratelimit_rejected_total");
    private static readonly Counter<long> Fallback = Meter.CreateCounter<long>("taxgateway_ratelimit_local_mode_total");

    private readonly IConnectionMultiplexer _valkey;
    private readonly RateLimitOptions _options;
    private readonly ILogger<TenantRateLimiter> _log;
    private readonly LocalLimiter _local = new();
    private long _localUntilTicks;

    public TenantRateLimiter(IConnectionMultiplexer valkey, IOptions<RateLimitOptions> options, ILogger<TenantRateLimiter> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        _valkey = valkey;
        _options = options.Value;
        _log = log;
    }

    public async Task<RateLimitLease> AcquireAsync(TenantId tenant, PlanLimits limits, TimeSpan leaseTtl)
    {
        ArgumentNullException.ThrowIfNull(limits);
        RateLimitLease lease;
        if (Stopwatch.GetTimestamp() < Interlocked.Read(ref _localUntilTicks))
        {
            lease = _local.Acquire(tenant, limits, Math.Max(1, _options.InstanceCountHint));
        }
        else
        {
            try
            {
                lease = await AcquireValkeyAsync(tenant, limits, leaseTtl).WaitAsync(TimeSpan.FromMilliseconds(_options.ValkeyTimeoutMs)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                Interlocked.Exchange(ref _localUntilTicks, Stopwatch.GetTimestamp() + (_options.LocalModeSeconds * Stopwatch.Frequency));
                Fallback.Add(1);
                LogLocalMode(_log, ex);
                lease = _local.Acquire(tenant, limits, Math.Max(1, _options.InstanceCountHint));
            }
        }

        if (!lease.Acquired)
        {
            Rejected.Add(1, new KeyValuePair<string, object?>("plan", limits.PlanCode),
                new KeyValuePair<string, object?>("reason", lease.Reason == RateLimitReason.Rate ? "rpm" : "conc"));
        }

        return lease;
    }

    private async Task<RateLimitLease> AcquireValkeyAsync(TenantId tenant, PlanLimits limits, TimeSpan leaseTtl)
    {
        var id = Guid.CreateVersion7().ToString("N");
        var bucket = $"rl:{{{tenant}}}:tb";   // hash tag: as duas chaves no mesmo slot do cluster
        var leases = $"rl:{{{tenant}}}:cc";
        var db = _valkey.GetDatabase();
        var r = (RedisResult[])(await db.ScriptEvaluateAsync(Script, [bucket, leases],
            [limits.Rpm, limits.Burst, limits.Concurrency, (long)leaseTtl.TotalMilliseconds, id]).ConfigureAwait(false))!;

        var acquired = (int)r[0] == 1;
        var reason = (int)r[1] switch { 1 => RateLimitReason.Concurrency, 2 => RateLimitReason.Rate, _ => RateLimitReason.None };
        Func<ValueTask>? release = acquired
            ? async () =>
            {
                try
                {
                    await db.SortedSetRemoveAsync(leases, id).ConfigureAwait(false);
                }
                catch (RedisException)
                {
                    // O TTL do lease libera o slot sozinho.
                }
            }
        : null;
        return new RateLimitLease(acquired, reason, limits, (int)r[2], (int)r[3], TimeSpan.FromMilliseconds((long)r[4]), false, release);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Valkey indisponível ou lento: limitador local (limite/N) por alguns segundos.")]
    private static partial void LogLocalMode(ILogger logger, Exception ex);

    /// <summary>Modo degradado: token bucket e concorrência em memória, por instância.</summary>
    private sealed class LocalLimiter
    {
        private readonly ConcurrentDictionary<TenantId, State> _states = new();

        public RateLimitLease Acquire(TenantId tenant, PlanLimits limits, int instances)
        {
            var rpm = Math.Max(1.0, (double)limits.Rpm / instances);
            var burst = Math.Max(1.0, (double)limits.Burst / instances);
            var conc = Math.Max(1, limits.Concurrency / instances);
            var s = _states.GetOrAdd(tenant, _ => new State { Tokens = burst, Ts = Stopwatch.GetTimestamp() });
            lock (s)
            {
                var now = Stopwatch.GetTimestamp();
                s.Tokens = Math.Min(burst, s.Tokens + (Stopwatch.GetElapsedTime(s.Ts, now).TotalMilliseconds * rpm / 60000.0));
                s.Ts = now;
                if (s.InUse >= conc)
                {
                    return new RateLimitLease(false, RateLimitReason.Concurrency, limits, (int)s.Tokens, 0, TimeSpan.FromSeconds(1), true, null);
                }

                if (s.Tokens < 1)
                {
                    var wait = TimeSpan.FromMilliseconds(Math.Ceiling((1 - s.Tokens) * 60000.0 / rpm));
                    return new RateLimitLease(false, RateLimitReason.Rate, limits, 0, conc - s.InUse, wait, true, null);
                }

                s.Tokens -= 1;
                s.InUse++;
                return new RateLimitLease(true, RateLimitReason.None, limits, (int)s.Tokens, conc - s.InUse, TimeSpan.Zero, true,
                    () =>
                    {
                        lock (s)
                        {
                            s.InUse--;
                        }

                        return ValueTask.CompletedTask;
                    });
            }
        }

        private sealed class State
        {
            public double Tokens;
            public long Ts;
            public int InUse;
        }
    }
}

/// <summary>
/// Headers do draft-ietf-httpapi-ratelimit-headers (SDD §5.3), isolados aqui para acompanhar o draft até virar RFC.
/// </summary>
public static class RateLimitHeaderWriter
{
    public static void Write(IDictionary<string, Microsoft.Extensions.Primitives.StringValues> headers, RateLimitLease lease)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(lease);
        var l = lease.Limits;
        headers["RateLimit-Policy"] = $"\"rpm\";q={l.Rpm};w=60, \"conc\";q={l.Concurrency};qu=\"concurrent-requests\"";
        headers["RateLimit"] = $"\"rpm\";a={Math.Max(0, lease.TokensLeft)};w=60, \"conc\";a={Math.Max(0, lease.ConcurrencyLeft)}";
        if (!lease.Acquired)
        {
            headers["Retry-After"] = Math.Max(1, (int)Math.Ceiling(lease.RetryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
