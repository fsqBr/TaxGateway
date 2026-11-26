using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using TaxGateway.Core.Tenancy;
using TaxGateway.Infrastructure.Persistence;
using TaxGateway.Infrastructure.Storage;

namespace TaxGateway.Infrastructure.Batches;

public sealed class BatchOptions
{
    public const string Section = "Batches";
    public int ChunkSize { get; set; } = 500;
    public long MaxEntryBytes { get; set; } = 5 * 1024 * 1024;
    public int MaxCompressionRatio { get; set; } = 100;
    public int TotalExpansionFactor { get; set; } = 10;
    public long ModeOneMaxBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>K do fair-share: chunks em voo por tenant (§6.3, parâmetro do §15.2).</summary>
    public int PerTenantInFlight { get; set; } = 2;
    public int WorkerSlots { get; set; } = 4;
    public int LeaseSeconds { get; set; } = 600;
    public int DocParallelism { get; set; } = 4;

    /// <summary>Estimativa de vazão para estimated_completion_seconds (documentos/s por slot), recalibrada no gate.</summary>
    public double DocsPerSecondPerSlot { get; set; } = 20;
}

public sealed class BatchRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record BatchCreated(Guid Id, int Documents, int Chunks);

/// <summary>Resultado de um documento do lote (uma linha do relatório).</summary>
public sealed record BatchDocResult(string? ChaveAcesso, string Status, int Deterministicas, decimal Diferenca, string? Erro);

/// <summary>Audita um XML dentro do lote, com as mesmas regras da API (implementado na API, usado pelos workers).</summary>
public interface IBatchDocumentAuditor
{
    Task<BatchDocResult> AuditAsync(TenantId tenant, byte[] xml, string direction, CancellationToken ct);
}

/// <summary>
/// Ingestão do lote (SDD §6.3; D-606): o upload vai para um arquivo temporário (nada inteiro em memória), as defesas
/// do ZIP valem durante a leitura (só .xml, sem ZIP aninhado, ≤ 5 MB descompactados por entrada contados de verdade,
/// razão ≤ 100:1, total ≤ 10× o limite do plano, entradas ≤ limite do plano; nomes ignorados, nada vai para disco
/// com o nome da entrada) e os XMLs são agrupados em chunks de 500, cifrados com a KEK do tenant.
/// </summary>
public sealed class BatchIngestService(ITenantDb db, XmlVault vault, TenantSettingsService settings, IOptions<BatchOptions> options)
{
    public async Task<BatchCreated> IngestAsync(TenantId tenant, Stream upload, long maxBytes, int maxDocs, short priority, string direction, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(upload);
        var o = options.Value;
        var temp = Path.Combine(Path.GetTempPath(), $"tg-batch-{Guid.NewGuid():N}.zip");
        await using var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await upload.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new BatchRejectedException("BATCH_TOO_LARGE", $"Lote acima do limite do plano ({maxBytes} bytes).");
            }

            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        file.Position = 0;
        var docs = new List<byte[]>();
        long expanded = 0;
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            throw Unsafe("Arquivo não é um ZIP válido.");
        }

        using (zip)
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                {
                    continue;   // diretório
                }

                if (!entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    throw Unsafe($"Entrada não permitida (só .xml): {Truncate(entry.FullName)}.");
                }

                if (docs.Count >= maxDocs)
                {
                    throw Unsafe($"Mais de {maxDocs} documentos (limite do plano).");
                }

                if (entry.Length > o.MaxEntryBytes || (entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > o.MaxCompressionRatio))
                {
                    throw Unsafe($"Entrada {Truncate(entry.FullName)} acima do limite de tamanho ou de compressão.");
                }

                // Os tamanhos do diretório central podem mentir: conta os bytes reais na descompressão.
                await using var es = await entry.OpenAsync(ct).ConfigureAwait(false);
                using var ms = new MemoryStream();
                var limit = o.MaxEntryBytes;
                while ((read = await es.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    if (ms.Length + read > limit)
                    {
                        throw Unsafe($"Entrada {Truncate(entry.FullName)} descompacta acima de {limit} bytes.");
                    }

                    ms.Write(buffer, 0, read);
                }

                if (entry.CompressedLength > 0 && ms.Length / Math.Max(1, entry.CompressedLength) > o.MaxCompressionRatio)
                {
                    throw Unsafe($"Entrada {Truncate(entry.FullName)} com razão de compressão acima de {o.MaxCompressionRatio}:1.");
                }

                expanded += ms.Length;
                if (expanded > maxBytes * o.TotalExpansionFactor)
                {
                    throw Unsafe("Total descompactado acima de 10× o limite do plano.");
                }

                var bytes = ms.ToArray();
                if (bytes.AsSpan().StartsWith("PK\u0003\u0004"u8))
                {
                    throw Unsafe("ZIP aninhado não é permitido.");
                }

                docs.Add(bytes);
            }
        }

        if (docs.Count == 0)
        {
            throw new BatchRejectedException("BATCH_EMPTY", "O ZIP não tem nenhum .xml.");
        }

        var id = Guid.CreateVersion7();
        var kek = (await settings.GetAsync(tenant, ct).ConfigureAwait(false)).KmsKeyRef;
        var prefix = $"t/{tenant}/batches/{id}/";
        var chunks = docs.Chunk(o.ChunkSize).ToList();
        for (var i = 0; i < chunks.Count; i++)
        {
            await vault.PutBlobAsync(kek, $"{prefix}chunk-{i + 1:D5}.bin.enc", Pack(chunks[i]), 7, ct).ConfigureAwait(false);
        }

        await db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var batch = s.CreateBatch();
            var b = new NpgsqlBatchCommand("""
                INSERT INTO batches (id, tenant_id, status, storage_key, total_docs, total_chunks, direction, priority)
                VALUES ($1, $2, 'QUEUED', $3, $4, $5, $6, $7)
                """);
            b.Parameters.Add(new() { Value = id });
            b.Parameters.Add(new() { Value = tenant.Value });
            b.Parameters.Add(new() { Value = prefix });
            b.Parameters.Add(new() { Value = docs.Count });
            b.Parameters.Add(new() { Value = chunks.Count });
            b.Parameters.Add(new() { Value = direction });
            b.Parameters.Add(new() { Value = priority });
            batch.BatchCommands.Add(b);
            var ch = new NpgsqlBatchCommand("""
                INSERT INTO batch_chunks (tenant_id, batch_id, chunk_no, priority, storage_key, docs)
                SELECT $1, $2, n, $3, $4 || 'chunk-' || lpad(n::text, 5, '0') || '.bin.enc', d
                  FROM unnest($5::int[], $6::int[]) AS u(n, d)
                """);
            ch.Parameters.Add(new() { Value = tenant.Value });
            ch.Parameters.Add(new() { Value = id });
            ch.Parameters.Add(new() { Value = priority });
            ch.Parameters.Add(new() { Value = prefix });
            ch.Parameters.Add(new() { Value = Enumerable.Range(1, chunks.Count).ToArray() });
            ch.Parameters.Add(new() { Value = chunks.Select(x => x.Length).ToArray() });
            batch.BatchCommands.Add(ch);
            return await batch.ExecuteNonQueryAsync(c).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return new BatchCreated(id, docs.Count, chunks.Count);
    }

    /// <summary>Formato do chunk: [u32 tamanho][bytes] repetido. Simples, sem nomes de arquivo.</summary>
    public static byte[] Pack(IReadOnlyList<byte[]> docs)
    {
        ArgumentNullException.ThrowIfNull(docs);
        var outBuf = new byte[docs.Sum(d => d.Length + 4)];
        var pos = 0;
        foreach (var d in docs)
        {
            BinaryPrimitives.WriteUInt32BigEndian(outBuf.AsSpan(pos), (uint)d.Length);
            d.CopyTo(outBuf, pos + 4);
            pos += d.Length + 4;
        }

        return outBuf;
    }

    public static List<byte[]> Unpack(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var list = new List<byte[]>();
        var pos = 0;
        while (pos < data.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            list.Add(data.AsSpan(pos + 4, len).ToArray());
            pos += len + 4;
        }

        return list;
    }

    private static BatchRejectedException Unsafe(string msg) => new("BATCH_REJECTED_UNSAFE_ARCHIVE", msg);

    private static string Truncate(string s) => s.Length > 80 ? s[..80] + "…" : s;
}

/// <summary>
/// Processamento dos chunks (SDD §6.3; D-602): reserva pela função de fair-share (K por tenant, rodízio entre
/// tenants, prioridade do plano), audita cada XML com as regras da API, grava o resultado do chunk e, no último
/// chunk, monta o relatório CSV e publica <c>batch.audit.completed</c> pelo outbox.
/// </summary>
public sealed partial class BatchProcessor(
    NpgsqlDataSource workerDb, ITenantDb db, XmlVault vault, TenantSettingsService settings, IBatchDocumentAuditor auditor,
    IOptions<BatchOptions> options, ILogger<BatchProcessor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await RunOnceAsync(stoppingToken).ConfigureAwait(false) == 0)
                {
                    await Task.Delay(500, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogError(log, ex);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    public sealed record Claimed(Guid Tenant, Guid Batch, int Chunk, string Key);

    public async Task<List<Claimed>> ClaimAsync(int limit, CancellationToken ct)
    {
        var o = options.Value;
        await using var cmd = workerDb.CreateCommand("SELECT tenant_id, batch_id, chunk_no, storage_key FROM app.claim_batch_chunks($1, $2, $3)");
        cmd.Parameters.Add(new() { Value = limit });
        cmd.Parameters.Add(new() { Value = o.PerTenantInFlight });
        cmd.Parameters.Add(new() { Value = o.LeaseSeconds });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<Claimed>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new Claimed(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3)));
        }

        return list;
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var claimed = await ClaimAsync(options.Value.WorkerSlots, ct).ConfigureAwait(false);
        await Parallel.ForEachAsync(claimed, new ParallelOptions { MaxDegreeOfParallelism = options.Value.WorkerSlots, CancellationToken = ct },
            async (c, token) => await ProcessChunkAsync(c, token).ConfigureAwait(false)).ConfigureAwait(false);
        return claimed.Count;
    }

    private async Task ProcessChunkAsync(Claimed c, CancellationToken ct)
    {
        var tenant = new TenantId(c.Tenant);
        var kek = (await settings.GetAsync(tenant, ct).ConfigureAwait(false)).KmsKeyRef;
        var direction = await db.InTenantAsync(tenant, async (s, t) =>
        {
            await using var cmd = s.CreateCommand("SELECT direction FROM batches WHERE id = $1");
            cmd.Parameters.Add(new() { Value = c.Batch });
            return (string)(await cmd.ExecuteScalarAsync(t).ConfigureAwait(false))!;
        }, ct).ConfigureAwait(false);

        var docs = BatchIngestService.Unpack(await vault.GetAsync(kek, c.Key, ct).ConfigureAwait(false));
        var results = new BatchDocResult[docs.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, docs.Count), new ParallelOptions { MaxDegreeOfParallelism = options.Value.DocParallelism, CancellationToken = ct },
            async (i, t) => results[i] = await auditor.AuditAsync(tenant, docs[i], direction, t).ConfigureAwait(false)).ConfigureAwait(false);

        var start = (c.Chunk - 1) * options.Value.ChunkSize;
        var csv = new StringBuilder();
        for (var i = 0; i < results.Length; i++)
        {
            var r = results[i];
            csv.Append(CultureInfo.InvariantCulture, $"{start + i + 1},{r.ChaveAcesso},{r.Status},{r.Deterministicas},{r.Diferenca:0.00},{Csv(r.Erro)}\n");
        }

        var prefix = c.Key[..(c.Key.LastIndexOf('/') + 1)];
        await vault.PutBlobAsync(kek, $"{prefix}result-{c.Chunk:D5}.csv.enc", Encoding.UTF8.GetBytes(csv.ToString()), 30, ct).ConfigureAwait(false);
        var failed = results.Count(r => r.Erro is not null);
        var conforme = results.Count(r => r.Status == "CONFORME");

        var (done, totalChunks) = await db.InTenantAsync(tenant, async (s, t) =>
        {
            await using (var upd = s.CreateCommand("UPDATE batch_chunks SET status = 'DONE', lease_until = NULL WHERE batch_id = $1 AND chunk_no = $2"))
            {
                upd.Parameters.Add(new() { Value = c.Batch });
                upd.Parameters.Add(new() { Value = c.Chunk });
                await upd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
            }

            await using var b = s.CreateCommand("""
                UPDATE batches SET status = 'RUNNING', processed_docs = processed_docs + $2, failed_docs = failed_docs + $3,
                       conforme_docs = conforme_docs + $4, done_chunks = done_chunks + 1
                 WHERE id = $1 RETURNING done_chunks, total_chunks
                """);
            b.Parameters.Add(new() { Value = c.Batch });
            b.Parameters.Add(new() { Value = results.Length });
            b.Parameters.Add(new() { Value = failed });
            b.Parameters.Add(new() { Value = conforme });
            await using var rd = await b.ExecuteReaderAsync(t).ConfigureAwait(false);
            await rd.ReadAsync(t).ConfigureAwait(false);
            return (rd.GetInt32(0), rd.GetInt32(1));
        }, ct).ConfigureAwait(false);

        await vault.DeleteAsync(c.Key, ct).ConfigureAwait(false);   // entrada temporária (vale também em zero_retention)
        if (done == totalChunks)
        {
            await CompleteAsync(tenant, kek, c.Batch, prefix, totalChunks, ct).ConfigureAwait(false);
        }
    }

    private async Task CompleteAsync(TenantId tenant, string kek, Guid batchId, string prefix, int totalChunks, CancellationToken ct)
    {
        var report = new StringBuilder("documento,chave_acesso,status,ocorrencias_deterministicas,diferenca,erro\n");
        for (var i = 1; i <= totalChunks; i++)
        {
            report.Append(Encoding.UTF8.GetString(await vault.GetAsync(kek, $"{prefix}result-{i:D5}.csv.enc", ct).ConfigureAwait(false)));
        }

        var reportKey = $"{prefix}relatorio.csv.enc";
        await vault.PutBlobAsync(kek, reportKey, Encoding.UTF8.GetBytes(report.ToString()), 30, ct).ConfigureAwait(false);
        await db.InTenantAsync(tenant, async (s, t) =>
        {
            await using var batch = s.CreateBatch();
            var b = new NpgsqlBatchCommand("UPDATE batches SET status = 'COMPLETED', report_key = $2, completed_at = now() WHERE id = $1");
            b.Parameters.Add(new() { Value = batchId });
            b.Parameters.Add(new() { Value = reportKey });
            batch.BatchCommands.Add(b);
            var ev = new NpgsqlBatchCommand("""
                INSERT INTO outbox (tenant_id, topic, payload)
                SELECT tenant_id, 'batch.audit.completed', jsonb_build_object('batch_id', id, 'total', total_docs, 'conformes', conforme_docs, 'falhas', failed_docs)
                  FROM batches WHERE id = $1
                """);
            ev.Parameters.Add(new() { Value = batchId });
            batch.BatchCommands.Add(ev);
            return await batch.ExecuteNonQueryAsync(t).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private static string Csv(string? s) => s is null ? "" : "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal).Replace('\n', ' ') + "\"";

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha no processador de lotes.")]
    private static partial void LogError(ILogger logger, Exception ex);
}
