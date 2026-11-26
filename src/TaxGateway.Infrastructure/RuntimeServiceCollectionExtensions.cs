using System.Threading.RateLimiting;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

using Npgsql;

using Polly;

using StackExchange.Redis;

using TaxGateway.Core.Engines;
using TaxGateway.Core.Params;
using TaxGateway.Infrastructure.Admin;
using TaxGateway.Infrastructure.Auth;
using TaxGateway.Infrastructure.Engines;
using TaxGateway.Infrastructure.Params;
using TaxGateway.Infrastructure.Persistence;
using TaxGateway.Infrastructure.RateLimiting;

namespace TaxGateway.Infrastructure;

public static class RuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Serviços de runtime da API (Fase 2): Valkey, autenticação, rate limit, parâmetros, motor regular e
    /// persistência assíncrona. Exige <c>ConnectionStrings:Valkey</c> e as seções Engines/Auth/RateLimiting.
    /// </summary>
    /// <summary>Chave do Valkey de cache (D-P26).</summary>
    public const string ValkeyCacheKey = "cache";

    public static IServiceCollection AddTaxGatewayRuntime(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        services.AddOptions<EngineOptions>().Bind(config.GetSection(EngineOptions.Section)).Validate(o => o.Bundle.Length > 0, "Engines:Bundle obrigatório");
        services.AddOptions<ApiKeyOptions>().Bind(config.GetSection(ApiKeyOptions.Section));
        services.AddOptions<RateLimitOptions>().Bind(config.GetSection(RateLimitOptions.Section));

        var valkey = config.GetConnectionString("Valkey") ?? throw new InvalidOperationException("ConnectionStrings:Valkey não configurada.");
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var o = ConfigurationOptions.Parse(valkey);
            o.AbortOnConnectFail = false;   // sobe sem Valkey; rate limit e caches degradam para o modo local
            return ConnectionMultiplexer.Connect(o);
        });

        // D-P26 (revisão de arquitetura 21): o cache de simulação (alta cardinalidade, descartável) pode ir para outro
        // Valkey (ConnectionStrings:ValkeyCache, com maxmemory-policy allkeys-lru), longe do estado de segurança e do
        // rate limit (que ficam no principal, com noeviction/volatile-lru). Sem a string, usa o principal.
        var valkeyCache = config.GetConnectionString("ValkeyCache");
        services.AddKeyedSingleton<IConnectionMultiplexer>(ValkeyCacheKey, (sp, _) =>
        {
            if (string.IsNullOrEmpty(valkeyCache))
            {
                return sp.GetRequiredService<IConnectionMultiplexer>();
            }

            var o = ConfigurationOptions.Parse(valkeyCache);
            o.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(o);
        });

        // D-P19: cache em memória com teto (contagem de entradas; cada uso define Size = 1). Sem teto, tráfego de alta
        // cardinalidade enchia o pod até o OOMKill.
        services.AddMemoryCache(o => o.SizeLimit = 200_000);
        services.AddSingleton<ApiKeyHasher>();
        services.AddSingleton<CacheSeal>();
        services.AddSingleton<ApiKeyAuthenticator>();
        services.AddSingleton<TenantRateLimiter>();
        services.AddSingleton<IdempotencyStore>();
        services.AddSingleton<TenantCnpjService>();

        services.AddSingleton<ParamSnapshotService>();
        services.AddSingleton<IParamSnapshotProvider>(sp => sp.GetRequiredService<ParamSnapshotService>());
        services.AddHostedService(sp => sp.GetRequiredService<ParamSnapshotService>());

        services.AddSingleton<SimulationWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<SimulationWriter>());

        AddRegularEngine(services);
        return services;
    }

    /// <summary>Fonte de dados do <c>tg_admin</c> para a Admin API (opcional: sem connection string, a Admin API fica desligada).</summary>
    public static IServiceCollection AddTaxGatewayAdmin(this IServiceCollection services, string adminConnectionString)
    {
        services.AddSingleton(_ => new AdminDataSource(NpgsqlDataSource.Create(DbPools.Normalize(adminConnectionString, DbPools.Auxiliary))));
        services.AddSingleton<AdminRepository>();
        return services;
    }

    private static void AddRegularEngine(IServiceCollection services)
    {
        // Bulkhead + resiliência do caminho síncrono (SDD §8.4; D-209), um pipeline por motor (D-501).
        AddEngineClient(services, RegularEngineAdapter.SyncClient, o => (o.Regular.BaseUrl, o.Regular.SyncTimeoutMs, o.Regular.SyncMaxConcurrency, o.Regular.SyncMaxQueue));
        AddEngineClient(services, SimplesEngineAdapter.SyncClient, o => (o.Simples.BaseUrl, o.Simples.SyncTimeoutMs, o.Regular.SyncMaxConcurrency, o.Regular.SyncMaxQueue));
        services.AddSingleton<SimplesEngineAdapter>();
        services.AddSingleton<ISimplesEngine>(sp => sp.GetRequiredService<SimplesEngineAdapter>());

        services.AddHttpClient(RfbReferenceData.Client, (sp, c) =>
        {
            c.BaseAddress = sp.GetRequiredService<IOptions<EngineOptions>>().Value.Regular.BaseUrl;
            c.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddSingleton<RegularEngineAdapter>();
        services.AddSingleton<IRfbTaxEngineAdapter>(sp => sp.GetRequiredService<RegularEngineAdapter>());
        services.AddSingleton<RfbReferenceData>();
        services.AddSingleton<IRfbReferenceData>(sp => sp.GetRequiredService<RfbReferenceData>());
        services.AddHostedService(sp => sp.GetRequiredService<RfbReferenceData>());
    }

    private static void AddEngineClient(IServiceCollection services, string name, Func<EngineOptions, (Uri Url, int TimeoutMs, int MaxConcurrency, int MaxQueue)> pick)
    {
        services.AddHttpClient(name, (sp, c) =>
            {
                c.BaseAddress = pick(sp.GetRequiredService<IOptions<EngineOptions>>().Value).Url;
                c.Timeout = Timeout.InfiniteTimeSpan;   // os timeouts vêm do pipeline abaixo
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 256,
                EnableMultipleHttp2Connections = true,
            })
            .AddResilienceHandler(name, (b, ctx) =>
            {
                var o = pick(ctx.ServiceProvider.GetRequiredService<IOptions<EngineOptions>>().Value);
                // D-Q08: timeouts e esperas de rede são tempo real, nunca o relógio de negócio (TimeProvider do DI pode ser
                // controlado em teste; com ele, a espera entre tentativas nunca terminava e a requisição pendurava).
                b.TimeProvider = TimeProvider.System;
                // D-P20 (revisão de arquitetura 11): o teto total é a estratégia MAIS EXTERNA — inclui a espera na fila (antes a
                // fila de até 256 ficava fora do teto, ~18 s); fila curta (descarte de carga em 503 em vez de enfileirar); e
                // SEM nova tentativa em timeout: com o motor saturado, o cálculo que estourou continua rodando na JVM e repetir
                // triplicava a carga justamente no pico. Só falha de conexão e 502/503/504 (reinício do sidecar) repetem.
                b.AddTimeout(TimeSpan.FromMilliseconds(o.TimeoutMs * 2));   // teto total: fila + tentativas
                b.AddConcurrencyLimiter(new ConcurrencyLimiterOptions
                {
                    PermitLimit = o.MaxConcurrency,
                    QueueLimit = Math.Min(o.MaxQueue, o.MaxConcurrency),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                });
                b.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 2,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = TimeSpan.FromMilliseconds(50),
                    ShouldHandle = args => ValueTask.FromResult(args.Outcome switch
                    {
                        { Exception: HttpRequestException } => true,
                        { Result.StatusCode: System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.GatewayTimeout } => true,
                        _ => false,
                    }),
                });
                b.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.3,
                    MinimumThroughput = 20,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    BreakDuration = TimeSpan.FromSeconds(15),
                });
                b.AddTimeout(TimeSpan.FromMilliseconds(o.TimeoutMs));   // por tentativa
            });
    }
}
