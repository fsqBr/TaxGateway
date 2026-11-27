using System.Globalization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using StackExchange.Redis;

using TaxGateway.Infrastructure.Engines;

namespace TaxGateway.Infrastructure.Params;

/// <summary>Resultado de uma rodada de sincronização.</summary>
public sealed record RateSyncResult(string Bundle, bool Full, int Probes, int Rows, IReadOnlyList<string> Changes);

/// <summary>
/// D-R30: aplica no catálogo, sem intervenção, o que a base oficial da Receita publicou. Completa (com todos os
/// municípios) quando o bundle do motor muda; entre bundles, confere União e UFs uma vez por dia. Cada aplicação vira uma
/// mudança de parâmetro APLICADA (trilha de auditoria) e os avisos às contas afetadas saem pelo app.notify_param_changes.
/// </summary>
public sealed partial class RateSyncService(NpgsqlDataSource db, IOfficialRateSource source, ParamSnapshotService snapshots,
    IConnectionMultiplexer valkey, IOptions<EngineOptions> options, ILogger<RateSyncService> log)
{
    /// <summary>Paralelismo das consultas ao motor (o motor é local ao cluster).</summary>
    public int Concurrency { get; init; } = 8;

    /// <summary>Limite de municípios (testes); null = todos.</summary>
    public int? MunicipioLimit { get; init; }

    /// <summary>Prévia: calcula o que mudaria sem gravar nada (conferência e testes).</summary>
    public bool DryRun { get; init; }

    /// <summary>True quando este bundle ainda não teve a sincronização completa.</summary>
    public async Task<bool> NeedsFullSyncAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT probes > 0 FROM rfb_sync_state WHERE bundle = $1");
        cmd.Parameters.Add(new() { Value = options.Value.Bundle });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not true;
    }

    public async Task<RateSyncResult> RunOnceAsync(bool full, CancellationToken ct)
    {
        var bundle = options.Value.Bundle;
        var probes = 0;
        using var gate = new SemaphoreSlim(Concurrency);
        async Task<decimal?> Counted(Func<Task<decimal?>> call)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Interlocked.Increment(ref probes);
                return await call().ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        var (ufs, municipios) = await EntesAsync(full, ct).ConfigureAwait(false);
        var start = RateSyncPlanner.WindowStart;
        var end = RateSyncPlanner.WindowEnd;
        var uniao = await RateSyncPlanner.ProbeAsync(d => Counted(() => source.UniaoAsync(d, ct)), start, end).ConfigureAwait(false);

        var ufSeries = new Dictionary<string, IReadOnlyList<RateSegment>>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(ufs, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Concurrency }, async (uf, c) =>
        {
            var s = await RateSyncPlanner.ProbeAsync(d => Counted(() => source.UfAsync(uf, d, c)), start, end).ConfigureAwait(false);
            lock (ufSeries)
            {
                ufSeries[uf.ToString("00", CultureInfo.InvariantCulture)] = s;
            }
        }).ConfigureAwait(false);

        // Municípios (só na completa): sondagem anual — a alíquota municipal muda por exercício — e busca binária onde muda.
        var munSeries = new Dictionary<string, IReadOnlyList<RateSegment>>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(municipios, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Concurrency }, async (m, c) =>
        {
            var s = await RateSyncPlanner.ProbeAsync(d => Counted(() => source.MunicipioAsync(m, d, c)), start, end, stepMonths: 12).ConfigureAwait(false);
            lock (munSeries)
            {
                munSeries[m.ToString(CultureInfo.InvariantCulture)] = s;
            }
        }).ConfigureAwait(false);

        var catalog = snapshots.Current.Rates.All.Where(r => r.Produto is null).ToList();
        var plan = RateSyncPlanner.Plan(uniao, ufSeries, munSeries, catalog, bundle);
        if (!DryRun && (plan.Rows.Count > 0 || plan.Supersede.Count > 0))
        {
            await using (var cmd = db.CreateCommand("SELECT app.apply_rate_sync($1, $2, $3::jsonb, $4)"))
            {
                cmd.Parameters.Add(new() { Value = Guid.CreateVersion7() });
                cmd.Parameters.Add(new() { Value = bundle });
                cmd.Parameters.Add(new() { Value = RateSyncPlanner.ToJson(plan.Rows) });
                cmd.Parameters.Add(new() { Value = plan.Supersede.ToArray() });
                await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }

            var snapshot = await snapshots.ReloadAsync(ct).ConfigureAwait(false);
            try
            {
                await valkey.GetSubscriber().PublishAsync(RedisChannel.Literal(ParamSnapshotService.ChangedChannel), snapshot.ContentSha256).ConfigureAwait(false);
            }
            catch (RedisException)
            {
                // As demais instâncias pegam a mudança na verificação periódica.
            }

            foreach (var change in plan.Changes)
            {
                LogApplied(log, bundle, change);
            }
        }

        if (full && !DryRun)
        {
            await using var mark = db.CreateCommand("SELECT app.mark_rate_sync($1, $2)");
            mark.Parameters.Add(new() { Value = bundle });
            mark.Parameters.Add(new() { Value = Math.Max(1, probes) });
            await mark.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        TaxGatewayMetrics.RateSyncChanges = plan.Changes.Count;
        return new RateSyncResult(bundle, full, probes, plan.Rows.Count, plan.Changes);
    }

    private async Task<(List<int> Ufs, List<int> Municipios)> EntesAsync(bool full, CancellationToken ct)
    {
        var ufs = new List<int>();
        var municipios = new List<int>();
        await using (var cmd = db.CreateCommand("SELECT codigo FROM ufs ORDER BY codigo"))
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                ufs.Add(r.GetInt16(0));
            }
        }

        if (!full)
        {
            return (ufs, municipios);
        }

        await using (var cmd = db.CreateCommand("SELECT codigo FROM municipios ORDER BY codigo LIMIT $1"))
        {
            cmd.Parameters.Add(new() { Value = MunicipioLimit ?? int.MaxValue });
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                municipios.Add(r.GetInt32(0));
            }
        }

        return (ufs, municipios);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alíquota sincronizada com a Receita (bundle {Bundle}): {Mudanca}")]
    private static partial void LogApplied(ILogger logger, string bundle, string mudanca);
}
