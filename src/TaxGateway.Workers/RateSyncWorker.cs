using System.Text.Json;

using Npgsql;

using TaxGateway.Infrastructure.Params;

namespace TaxGateway.Workers;

/// <summary>
/// D-R30/D-R31: mantém o catálogo sincronizado com a base oficial da Receita, sem intervenção humana.
/// <list type="bullet">
/// <item>Bundle novo do motor (deploy do pipeline fiscal): anuncia a atualização às contas, com o resumo do relatório de
/// regressão (<c>fiscal-bundles/&lt;bundle&gt;.json</c>), e roda a sincronização completa (União, UFs e municípios).</item>
/// <item>Todo dia: confere União e UFs (a Receita pode publicar alíquota nova sem trocar o bundle de código).</item>
/// </list>
/// O que muda vira mudança de parâmetro aplicada; os avisos saem pelo NotificationMailer (só às contas afetadas, com a
/// data em que passa a valer, e de novo no dia em que começa a valer).
/// </summary>
public sealed partial class RateSyncWorker(NpgsqlDataSource workerDb, RateSyncService sync, IConfiguration config,
    Microsoft.Extensions.Options.IOptions<TaxGateway.Infrastructure.Engines.EngineOptions> engines, ILogger<RateSyncWorker> log) : BackgroundService
{
    private DateOnly _lastDaily;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(config.GetValue("Workers:RateSyncMinutes", 60)));
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

    /// <summary>Uma rodada: null quando não havia nada a fazer.</summary>
    public async Task<RateSyncResult?> RunOnceAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3));
        if (await sync.NeedsFullSyncAsync(ct).ConfigureAwait(false))
        {
            await AnnounceAsync(ct).ConfigureAwait(false);
            var full = await sync.RunOnceAsync(full: true, ct).ConfigureAwait(false);
            _lastDaily = today;
            LogSynced(log, full.Bundle, full.Probes, full.Changes.Count);
            return full;
        }

        if (_lastDaily == today)
        {
            return null;
        }

        _lastDaily = today;
        return await sync.RunOnceAsync(full: false, ct).ConfigureAwait(false);
    }

    /// <summary>Anuncia o bundle novo (uma vez; a primeira instalação não anuncia — não há "anterior").</summary>
    private async Task AnnounceAsync(CancellationToken ct)
    {
        var bundle = engines.Value.Bundle;
        string? previous;
        await using (var cmd = workerDb.CreateCommand("SELECT bundle FROM rfb_sync_state WHERE bundle <> $1 ORDER BY synced_at DESC LIMIT 1"))
        {
            cmd.Parameters.Add(new() { Value = bundle });
            previous = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }

        await using var announce = workerDb.CreateCommand("SELECT app.announce_engine_bundle($1, $2, $3)");
        announce.Parameters.Add(new() { Value = bundle });
        announce.Parameters.Add(new NpgsqlParameter { Value = (object?)previous ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        announce.Parameters.Add(new()
        {
            Value = previous is null
            ? $"O TaxGateway calcula e audita com a versão {bundle} da calculadora oficial da Receita Federal."
            : Summary(bundle, AppContext.BaseDirectory)
        });
        await announce.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resumo da mudança de cálculo para o aviso: vem do relatório de regressão que o pipeline fiscal grava em
    /// <c>fiscal-bundles/&lt;bundle&gt;.json</c> (casos do corpus, quantos mudaram, destaques).
    /// </summary>
    public static string Summary(string bundle, string baseDir)
    {
        const string Default = "A Receita Federal publicou uma nova versão da calculadora oficial e o TaxGateway já está usando. " +
            "Cálculos e auditorias novos seguem a versão nova; os anteriores continuam com a versão registrada em cada resultado.";
        var path = Path.Combine(baseDir, "fiscal-bundles", bundle + ".json");
        if (!File.Exists(path))
        {
            return Default;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var casos = root.TryGetProperty("casos", out var c) ? c.GetInt32() : 0;
            var diferentes = root.TryGetProperty("com_diferenca", out var d) ? d.GetInt32() : 0;
            var text = diferentes == 0
                ? $"Nos {casos} casos de referência do TaxGateway o cálculo não mudou."
                : $"Em {diferentes} de {casos} casos de referência o cálculo mudou.";
            if (root.TryGetProperty("destaques", out var h) && h.ValueKind == JsonValueKind.Array)
            {
                var items = h.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Take(5).ToList();
                if (items.Count > 0)
                {
                    text += " Destaques: " + string.Join("; ", items) + ".";
                }
            }

            return Default + " " + text;
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sincronização com a Receita (bundle {Bundle}): {Probes} consultas, {Changes} mudanças.")]
    private static partial void LogSynced(ILogger logger, string bundle, int probes, int changes);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha na sincronização de alíquotas com a Receita.")]
    private static partial void LogError(ILogger logger, Exception ex);
}
