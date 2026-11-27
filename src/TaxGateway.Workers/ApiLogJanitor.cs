using Npgsql;

namespace TaxGateway.Workers;

/// <summary>D-R27: apaga o registro de chamadas da API com mais de 30 dias (a cada hora).</summary>
public sealed partial class ApiLogJanitor(NpgsqlDataSource workerDb, ILogger<ApiLogJanitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogError(log, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using var cmd = workerDb.CreateCommand("SELECT app.purge_api_call_logs()");
        return (int)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha ao expurgar o registro de chamadas.")]
    private static partial void LogError(ILogger logger, Exception ex);
}
