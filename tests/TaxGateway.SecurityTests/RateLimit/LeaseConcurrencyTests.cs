using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

using TaxGateway.Core.Params;
using TaxGateway.Core.Tenancy;
using TaxGateway.Infrastructure.RateLimiting;

using Testcontainers.Redis;

namespace TaxGateway.SecurityTests.RateLimit;

/// <summary>Valkey real (mesma imagem do compose) para o script Lua de token bucket + leases.</summary>
public sealed class ValkeyFixture : IAsyncLifetime
{
    private RedisContainer? _container;

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new RedisBuilder("valkey/valkey:8.1.12").Build();
        await _container.StartAsync();
        Connection = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        Connection?.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>
/// Critério de saída da Fase 2: nenhum lease fica preso. Concorrência pesada, exceções no meio da requisição e
/// requisições que "morrem" sem liberar o slot.
/// </summary>
public sealed class LeaseConcurrencyTests(ValkeyFixture valkey) : IClassFixture<ValkeyFixture>
{
    private TenantRateLimiter Limiter(int timeoutMs = 2000) => new(valkey.Connection,
        Options.Create(new RateLimitOptions { ValkeyTimeoutMs = timeoutMs }), NullLogger<TenantRateLimiter>.Instance);

    private long Leases(TenantId t) => valkey.Connection.GetDatabase().SortedSetLength($"rl:{{{t}}}:cc");

    [Fact]
    public async Task Concurrency_limit_is_never_exceeded_and_no_lease_remains_after_errors()
    {
        var tenant = TenantId.New();
        var limits = new PlanLimits("TEST", Rpm: 1_000_000, Burst: 100_000, Concurrency: 5);
        var limiter = Limiter();
        var inFlight = 0;
        var maxSeen = 0;
        var acquired = 0;
        var rejected = 0;

        await Parallel.ForAsync(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 64 }, async (i, ct) =>
        {
            var lease = await limiter.AcquireAsync(tenant, limits, TimeSpan.FromSeconds(10));
            try
            {
                if (!lease.Acquired)
                {
                    Interlocked.Increment(ref rejected);
                    return;
                }

                Interlocked.Increment(ref acquired);
                var now = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref maxSeen, now);
                await Task.Delay(Random.Shared.Next(1, 8), ct);
                Interlocked.Decrement(ref inFlight);
                if (i % 7 == 0)
                {
                    throw new InvalidOperationException("falha simulada no handler");
                }
            }
            catch (InvalidOperationException)
            {
                // o handler falhou; o finally/await using precisa liberar o lease mesmo assim
            }
            finally
            {
                await lease.DisposeAsync();
            }
        });

        Assert.True(maxSeen <= 5, $"concorrência observada {maxSeen} > limite 5");
        Assert.True(acquired > 0 && rejected > 0, $"esperado misto de aceites ({acquired}) e rejeições ({rejected})");
        Assert.Equal(0, Leases(tenant));
    }

    [Fact]
    public async Task Abandoned_lease_expires_by_ttl_and_frees_the_slot()
    {
        var tenant = TenantId.New();
        var limits = new PlanLimits("TEST", 1000, 1000, Concurrency: 1);
        var limiter = Limiter();

        var dead = await limiter.AcquireAsync(tenant, limits, TimeSpan.FromMilliseconds(300));   // nunca liberado (pod caiu)
        Assert.True(dead.Acquired);
        var blocked = await limiter.AcquireAsync(tenant, limits, TimeSpan.FromSeconds(5));
        Assert.False(blocked.Acquired);
        Assert.Equal(RateLimitReason.Concurrency, blocked.Reason);

        await Task.Delay(400);
        await using var next = await limiter.AcquireAsync(tenant, limits, TimeSpan.FromSeconds(5));
        Assert.True(next.Acquired);
    }

    [Fact]
    public async Task Token_bucket_allows_burst_then_rejects_with_retry_after()
    {
        var tenant = TenantId.New();
        var limits = new PlanLimits("TEST", Rpm: 60, Burst: 3, Concurrency: 100);
        var limiter = Limiter();
        for (var i = 0; i < 3; i++)
        {
            await using var ok = await limiter.AcquireAsync(tenant, limits, TimeSpan.FromSeconds(5));
            Assert.True(ok.Acquired);
        }

        await using var no = await limiter.AcquireAsync(tenant, limits, TimeSpan.FromSeconds(5));
        Assert.False(no.Acquired);
        Assert.Equal(RateLimitReason.Rate, no.Reason);
        Assert.InRange(no.RetryAfter, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1.1));   // 1 ficha/s
        Assert.Equal(0, Leases(tenant));
    }

    [Fact]
    public async Task Tenants_do_not_share_buckets()
    {
        var limits = new PlanLimits("TEST", 60, Burst: 1, Concurrency: 10);
        var limiter = Limiter();
        await using var a = await limiter.AcquireAsync(TenantId.New(), limits, TimeSpan.FromSeconds(5));
        await using var b = await limiter.AcquireAsync(TenantId.New(), limits, TimeSpan.FromSeconds(5));
        Assert.True(a.Acquired && b.Acquired);
    }

    [Fact]
    public async Task Valkey_unavailable_falls_back_to_local_limiter_instead_of_rejecting()
    {
        var down = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { "127.0.0.1:1" },
            AbortOnConnectFail = false,
            ConnectTimeout = 100,
        });
        var limiter = new TenantRateLimiter(down, Options.Create(new RateLimitOptions { ValkeyTimeoutMs = 50, InstanceCountHint = 2 }),
            NullLogger<TenantRateLimiter>.Instance);
        var tenant = TenantId.New();
        var limits = new PlanLimits("TEST", 600, Burst: 4, Concurrency: 10);   // local: burst 4/2 = 2

        await using var l1 = await limiter.AcquireAsync(tenant, limits, TimeSpan.FromSeconds(5));
        await using var l2 = await limiter.AcquireAsync(tenant, limits, TimeSpan.FromSeconds(5));
        await using var l3 = await limiter.AcquireAsync(tenant, limits, TimeSpan.FromSeconds(5));
        Assert.True(l1.Acquired && l1.Local);
        Assert.True(l2.Acquired);
        Assert.False(l3.Acquired);   // limite/N no modo local
        down.Dispose();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
