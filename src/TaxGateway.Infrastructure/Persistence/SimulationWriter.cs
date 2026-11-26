using System.Diagnostics.Metrics;
using System.Threading.Channels;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using NpgsqlTypes;

using TaxGateway.Core.Tenancy;

namespace TaxGateway.Infrastructure.Persistence;

/// <summary>Uma simulação concluída, a gravar com o ledger (cobrança) e a atividade do CNPJ.</summary>
public sealed record SimulationRecord(
    TenantId Tenant,
    Guid Id,
    DateOnly BillingMonth,
    string CnpjSubject,
    string Engine,
    string EngineBundle,
    long ParamSnapshotId,
    string RequestJson,
    string ResultJson,
    DateTimeOffset CreatedAt);

/// <summary>
/// Persistência fora do caminho crítico (SDD §16.2 item 6): as simulações entram num Channel e são gravadas
/// em lote a cada 50 ms ou 1.000 linhas. Trade-off documentado: a queda do pod perde até ~50 ms de eventos
/// (subcobrança, a favor do cliente). <c>COPY FROM</c> não é permitido em tabela com RLS, então cada lote
/// é um INSERT ... SELECT unnest(...) por tenant, numa transação com o contexto do tenant (D-213).
/// </summary>
public sealed partial class SimulationWriter(ITenantDb db, ILogger<SimulationWriter> log) : BackgroundService
{
    private const int MaxBatch = 1000;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(50);
    private static readonly Meter Meter = new("TaxGateway");
    private static readonly Counter<long> Dropped = Meter.CreateCounter<long>("taxgateway_simulation_persist_dropped_total");

    private readonly Channel<SimulationRecord> _channel = Channel.CreateBounded<SimulationRecord>(
        new BoundedChannelOptions(100_000) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>D-P19: teto também em BYTES na fila (128 MB) — 100 mil registros de 500 itens não cabem num pod de 1 GiB.</summary>
    public const long MaxQueuedBytes = 128L * 1024 * 1024;
    private long _queuedBytes;

    private static long SizeOf(SimulationRecord r) => (r.RequestJson.Length + r.ResultJson.Length) * 2L + 256;

    /// <summary>Nunca bloqueia a requisição. Fila cheia (contagem ou bytes): descarta, conta e registra (alerta na métrica).</summary>
    public void Enqueue(SimulationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var size = SizeOf(record);
        if (Interlocked.Add(ref _queuedBytes, size) > MaxQueuedBytes || !_channel.Writer.TryWrite(record))
        {
            Interlocked.Add(ref _queuedBytes, -size);
            Dropped.Add(1);
            LogDropped(log, record.Id);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var buffer = new List<SimulationRecord>(MaxBatch);
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                using var window = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                window.CancelAfter(FlushInterval);
                try
                {
                    while (buffer.Count < MaxBatch && await reader.WaitToReadAsync(window.Token).ConfigureAwait(false))
                    {
                        while (buffer.Count < MaxBatch && reader.TryRead(out var item))
                        {
                            Interlocked.Add(ref _queuedBytes, -SizeOf(item));
                            buffer.Add(item);
                        }
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // fim da janela de 50 ms
                }

                await FlushAsync(buffer, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // desligamento: drena o que sobrou abaixo
        }

        _channel.Writer.TryComplete();
        while (reader.TryRead(out var item))
        {
            Interlocked.Add(ref _queuedBytes, -SizeOf(item));
            buffer.Add(item);
            if (buffer.Count == MaxBatch)
            {
                await FlushAsync(buffer, CancellationToken.None).ConfigureAwait(false);
            }
        }

        await FlushAsync(buffer, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Grava e esvazia o buffer (exposto para testes).</summary>
    public async Task FlushAsync(List<SimulationRecord> buffer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        foreach (var group in buffer.GroupBy(r => r.Tenant))
        {
            var rows = group.ToArray();
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await db.InTenantAsync(group.Key, (s, c) => InsertAsync(s, rows, c), ct).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (ex is NpgsqlException or TimeoutException && attempt < 3)
                {
                    LogRetry(log, ex, attempt);
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
                {
                    Dropped.Add(rows.Length);
                    LogGaveUp(log, ex, rows.Length);
                    break;
                }
            }
        }

        buffer.Clear();
    }

    private static async Task<int> InsertAsync(TenantScope s, SimulationRecord[] rows, CancellationToken ct)
    {
        var tenant = s.Tenant.Value;
        var batch = s.CreateBatch();
        await using (batch.ConfigureAwait(false))
        {
            var sim = new NpgsqlBatchCommand("""
                INSERT INTO simulations (id, billing_month, tenant_id, cnpj_subject, engine, engine_bundle, param_snapshot_id, request, result, created_at)
                SELECT u.id, u.m, $3, u.cnpj, u.engine, u.bundle, u.snap, u.req, u.res, u.at
                  FROM unnest($1::uuid[], $2::date[], $4::text[], $5::text[], $6::text[], $7::bigint[], $8::jsonb[], $9::jsonb[], $10::timestamptz[])
                       AS u(id, m, cnpj, engine, bundle, snap, req, res, at)
                """);
            sim.Parameters.Add(new() { Value = rows.Select(r => r.Id).ToArray() });
            sim.Parameters.Add(new() { Value = rows.Select(r => r.BillingMonth).ToArray() });
            sim.Parameters.Add(new() { Value = tenant });
            sim.Parameters.Add(new() { Value = rows.Select(r => r.CnpjSubject).ToArray() });
            sim.Parameters.Add(new() { Value = rows.Select(r => r.Engine).ToArray() });
            sim.Parameters.Add(new() { Value = rows.Select(r => r.EngineBundle).ToArray() });
            sim.Parameters.Add(new() { Value = rows.Select(r => r.ParamSnapshotId).ToArray() });
            sim.Parameters.Add(new() { Value = rows.Select(r => r.RequestJson).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
            sim.Parameters.Add(new() { Value = rows.Select(r => r.ResultJson).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
            sim.Parameters.Add(new() { Value = rows.Select(r => r.CreatedAt.UtcDateTime).ToArray() });
            batch.BatchCommands.Add(sim);

            // Cobrança: uma linha por simulação; a UNIQUE (billing_month, metric, source_id) impede cobrança dupla.
            var ledger = new NpgsqlBatchCommand("""
                INSERT INTO usage_ledger (billing_month, tenant_id, metric, cnpj_subject, source_id, occurred_at)
                SELECT u.m, $3, 'SIMULATION', u.cnpj, u.id, u.at
                  FROM unnest($1::uuid[], $2::date[], $4::text[], $5::timestamptz[]) AS u(id, m, cnpj, at)
                ON CONFLICT (billing_month, metric, source_id) DO NOTHING
                """);
            ledger.Parameters.Add(new() { Value = rows.Select(r => r.Id).ToArray() });
            ledger.Parameters.Add(new() { Value = rows.Select(r => r.BillingMonth).ToArray() });
            ledger.Parameters.Add(new() { Value = tenant });
            ledger.Parameters.Add(new() { Value = rows.Select(r => r.CnpjSubject).ToArray() });
            ledger.Parameters.Add(new() { Value = rows.Select(r => r.CreatedAt.UtcDateTime).ToArray() });
            batch.BatchCommands.Add(ledger);

            var activity = new NpgsqlBatchCommand("""
                INSERT INTO cnpj_monthly_activity (tenant_id, billing_month, cnpj)
                SELECT DISTINCT $1::uuid, u.m, u.cnpj FROM unnest($2::date[], $3::text[]) AS u(m, cnpj)
                ON CONFLICT DO NOTHING
                """);
            activity.Parameters.Add(new() { Value = tenant });
            activity.Parameters.Add(new() { Value = rows.Select(r => r.BillingMonth).ToArray() });
            activity.Parameters.Add(new() { Value = rows.Select(r => r.CnpjSubject).ToArray() });
            batch.BatchCommands.Add(activity);

            return await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Fila de persistência cheia: simulação {SimulationId} não será gravada.")]
    private static partial void LogDropped(ILogger logger, Guid simulationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao gravar lote de simulações (tentativa {Attempt}).")]
    private static partial void LogRetry(ILogger logger, Exception ex, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Lote de {Count} simulações descartado após 3 tentativas.")]
    private static partial void LogGaveUp(ILogger logger, Exception ex, int count);
}
