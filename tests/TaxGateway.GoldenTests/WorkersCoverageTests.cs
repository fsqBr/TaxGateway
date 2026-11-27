using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Npgsql;

using TaxGateway.Core.Engines;
using TaxGateway.Core.Params;
using TaxGateway.Infrastructure;
using TaxGateway.Infrastructure.Engines;
using TaxGateway.Workers;

namespace TaxGateway.GoldenTests;

/// <summary>Workers novos (D-R19/D-R22/D-R27): expurgo do log de chamadas, conferência catálogo × motor e ciclos de fundo.</summary>
[Collection(GoldenCollection.Name)]
public sealed class WorkersCoverageTests(GoldenFixture env)
{
    /// <summary>Motor de mentira que devolve a alíquota pedida para a CBS (para provocar divergência com o catálogo).</summary>
    private sealed class FixedRateEngine(decimal cbs) : IRfbTaxEngineAdapter
    {
        public RfbEngine Engine => RfbEngine.Regular;

        public EngineCapability Capabilities => default;

        public Task<EngineCalculation> CalculateAsync(EngineOperation operation, CancellationToken ct) =>
            Task.FromResult(new EngineCalculation(
                [new EngineItemResult(1, "CALCULADO", "000", "000001", 1000m, new TaxLine(cbs, cbs * 10, null, null), new TaxLine(0.1m, 1, null, null),
                    new TaxLine(0m, 0, null, null), 1m, null, [], null)],
                new EngineTotals(1000m, cbs * 10, 1, 0, 1, 0), [], new EngineVersion("t", RfbEngine.Regular, "t", "t")));
    }

    private CatalogEngineCheck Check(IRfbTaxEngineAdapter engine, DateTimeOffset now) =>
        new(engine, env.Services.GetRequiredService<IParamSnapshotProvider>(), new FakeTimeProvider(now),
            env.Services.GetRequiredService<IOptions<EngineOptions>>(), NullLogger<CatalogEngineCheck>.Instance);

    [Fact]
    public async Task Conferencia_aponta_divergencia_e_nao_roda_antes_de_2026()
    {
        var divergent = await Check(new FixedRateEngine(5m), new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero)).RunOnceAsync(CancellationToken.None);
        Assert.Contains(divergent!, p => p.StartsWith("CBS", StringComparison.Ordinal));
        Assert.Equal(divergent!.Count, TaxGatewayMetrics.CatalogEngineDivergences);

        var ok = await Check(new FixedRateEngine(0.9m), new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero)).RunOnceAsync(CancellationToken.None);
        Assert.Empty(ok!);
        Assert.Null(await Check(new FixedRateEngine(0.9m), new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero)).RunOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Conferencia_em_segundo_plano_registra_falha_do_motor_e_para_no_stop()
    {
        var failing = new FailingEngine();
        using var check = Check(failing, new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        await check.StartAsync(CancellationToken.None);
        for (var i = 0; i < 50 && failing.Calls == 0; i++)
        {
            await Task.Delay(50);
        }

        await check.StopAsync(CancellationToken.None);
        Assert.True(failing.Calls >= 1);
    }

    private sealed class FailingEngine : IRfbTaxEngineAdapter
    {
        public int Calls;

        public RfbEngine Engine => RfbEngine.Regular;

        public EngineCapability Capabilities => default;

        public Task<EngineCalculation> CalculateAsync(EngineOperation operation, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            throw new EngineUnavailableException("motor fora do ar (teste)");
        }
    }

    [Fact]
    public async Task Expurgo_apaga_o_log_com_mais_de_30_dias()
    {
        var (tenant, _) = await env.CreateTenantWithIdAsync("Log antigo", GoldenFixture.NewCnpj());
        await env.Services.GetRequiredService<TaxGateway.Infrastructure.Persistence.ITenantDb>().InTenantAsync(new TaxGateway.Core.Tenancy.TenantId(tenant), async (sc, ct) =>
        {
            await using var ins = sc.CreateCommand("""
                INSERT INTO api_call_logs (tenant_id, at, method, route, status, duration_ms, credential)
                VALUES ($1, now() - interval '40 days', 'POST', '/v1/x', 400, 1, 'teste'), ($1, now(), 'POST', '/v1/y', 400, 1, 'teste')
                """);
            ins.Parameters.Add(new() { Value = tenant });
            return await ins.ExecuteNonQueryAsync(ct);
        }, CancellationToken.None);

        var janitor = env.WorkerServices.GetRequiredService<ApiLogJanitor>();
        Assert.True(await janitor.RunOnceAsync(CancellationToken.None) >= 1);
        Assert.Equal(0, await janitor.RunOnceAsync(CancellationToken.None));
    }

    /// <summary>Logger que conta os erros registrados (prova que o laço passou pelo tratamento e seguiu vivo).</summary>
    private sealed class CountingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public int Errors;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                Interlocked.Increment(ref Errors);
            }
        }
    }

    private static async Task RunLoopAsync(Microsoft.Extensions.Hosting.IHostedService worker, Func<int> errors)
    {
        await worker.StartAsync(CancellationToken.None);
        for (var i = 0; i < 100 && errors() == 0; i++)
        {
            await Task.Delay(100);
        }

        await worker.StopAsync(CancellationToken.None);
        Assert.True(errors() >= 1, "o laço não registrou a falha");
    }

    [Fact]
    public async Task Ciclos_de_fundo_sobrevivem_ao_banco_fora_do_ar()
    {
        await using var dead = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1");
        var janitorLog = new CountingLogger<ApiLogJanitor>();
        using var janitor = new ApiLogJanitor(dead, janitorLog);
        await RunLoopAsync(janitor, () => janitorLog.Errors);
        await Assert.ThrowsAnyAsync<Exception>(() => janitor.RunOnceAsync(CancellationToken.None));

        var reprocessLog = new CountingLogger<ReprocessWorker>();
        using var reprocess = new ReprocessWorker(dead, env.Services.GetRequiredService<TaxGateway.Infrastructure.Persistence.ITenantDb>(),
            env.WorkerServices.GetRequiredService<TaxGateway.Api.Audit.ReprocessService>(), reprocessLog);
        await RunLoopAsync(reprocess, () => reprocessLog.Errors);

        var mailerLog = new CountingLogger<NotificationMailer>();
        using var mailer = new NotificationMailer(dead, new TaxGateway.Identity.Accounts.CapturingEmailSender(),
            Options.Create(new TaxGateway.Identity.Accounts.AccountOptions()), mailerLog);
        await RunLoopAsync(mailer, () => mailerLog.Errors);

        var syncLog = new CountingLogger<RateSyncWorker>();
        var sync = new TaxGateway.Infrastructure.Params.RateSyncService(dead, new TaxGateway.Infrastructure.Params.EngineRateSource(env.WorkerServices.GetRequiredService<IHttpClientFactory>()),
            env.WorkerServices.GetRequiredService<TaxGateway.Infrastructure.Params.ParamSnapshotService>(), env.WorkerServices.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>(),
            env.WorkerServices.GetRequiredService<IOptions<EngineOptions>>(), NullLogger<TaxGateway.Infrastructure.Params.RateSyncService>.Instance);
        using var syncWorker = new RateSyncWorker(dead, sync, env.WorkerServices.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
            env.WorkerServices.GetRequiredService<IOptions<EngineOptions>>(), syncLog);
        await RunLoopAsync(syncWorker, () => syncLog.Errors);
    }
}
