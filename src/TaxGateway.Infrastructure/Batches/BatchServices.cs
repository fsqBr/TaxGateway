using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
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

    /// <summary>Teto de bytes por chunk (D-P09): o chunk fecha no que vier primeiro, contagem ou bytes. Limita a memória
    /// da ingestão na API e do processamento nos workers (antes: 500 × 5 MB = 2,5 GB num chunk).</summary>
    public long ChunkMaxBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>Tentativas por chunk antes de virar FAILED (chunk que derruba o worker não roda para sempre).</summary>
    public int MaxChunkAttempts { get; set; } = 3;

    /// <summary>Renovação do lease durante o processamento (heartbeat), em segundos.</summary>
    public int HeartbeatSeconds { get; set; } = 60;
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

/// <summary>Upload já gravado em arquivo temporário (apagado ao descartar), com o SHA-256 do conteúdo para a idempotência.</summary>
public sealed class StagedUpload : IAsyncDisposable
{
    internal StagedUpload(FileStream file, byte[] sha256, long length)
    {
        File = file;
        Sha256 = sha256;
        Length = length;
    }

    internal FileStream File { get; }

    public byte[] Sha256 { get; }

    public long Length { get; }

    public ValueTask DisposeAsync() => File.DisposeAsync();
}

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
    /// <summary>
    /// Grava o upload num arquivo temporário (nada inteiro em memória) calculando o SHA-256 no caminho — o hash entra
    /// na Idempotency-Key antes de qualquer efeito (D-P03).
    /// </summary>
    public static async Task<StagedUpload> StageAsync(Stream upload, long maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(upload);
        var temp = Path.Combine(Path.GetTempPath(), $"tg-batch-{Guid.NewGuid():N}.zip");
        var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await upload.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw new BatchRejectedException("ERR_BATCH_TOO_LARGE", $"Lote acima do limite do plano ({maxBytes} bytes).");
                }

                sha.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            file.Position = 0;
            return new StagedUpload(file, sha.GetHashAndReset(), total);
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<BatchCreated> IngestAsync(TenantId tenant, Stream upload, long maxBytes, int maxDocs, short priority, string direction, CancellationToken ct)
    {
        await using var staged = await StageAsync(upload, maxBytes, ct).ConfigureAwait(false);
        return await IngestAsync(tenant, staged, maxBytes, maxDocs, priority, direction, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lê o ZIP em streaming (D-P09): cada chunk é cifrado e enviado assim que fecha (500 documentos ou
    /// <see cref="BatchOptions.ChunkMaxBytes"/>), então a memória fica limitada a um chunk, não ao lote inteiro.
    /// Se o lote for recusado no meio, os chunks já enviados são apagados.
    /// </summary>
    public async Task<BatchCreated> IngestAsync(TenantId tenant, StagedUpload staged, long maxBytes, int maxDocs, short priority, string direction, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(staged);
        var o = options.Value;
        var file = staged.File;
        file.Position = 0;
        var buffer = new byte[81920];
        int read;
        var id = Guid.CreateVersion7();
        string? kek = null;   // buscada no 1º envio: as defesas do ZIP rodam antes de qualquer acesso a banco/KMS/S3
        var prefix = $"t/{tenant}/batches/{id}/";
        var current = new List<byte[]>();
        long currentBytes = 0;
        var chunkSizes = new List<int>();
        var uploaded = new List<string>();
        var totalDocs = 0;
        long expanded = 0;

        async Task FlushAsync()
        {
            if (current.Count == 0)
            {
                return;
            }

            var key = $"{prefix}chunk-{chunkSizes.Count + 1:D5}.bin.enc";
            kek ??= (await settings.GetAsync(tenant, ct).ConfigureAwait(false)).KmsKeyRef;
            await vault.PutBlobAsync(kek, key, Pack(current), 7, ct).ConfigureAwait(false);
            uploaded.Add(key);
            chunkSizes.Add(current.Count);
            current.Clear();
            currentBytes = 0;
        }

        try
        {
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

                    if (totalDocs >= maxDocs)
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
                        throw Unsafe($"Total descompactado acima de {o.TotalExpansionFactor}× o limite do plano.");
                    }

                    var bytes = ms.ToArray();
                    if (bytes.AsSpan().StartsWith("PK\u0003\u0004"u8))
                    {
                        throw Unsafe("ZIP aninhado não é permitido.");
                    }

                    if (current.Count >= o.ChunkSize || (current.Count > 0 && currentBytes + bytes.Length > o.ChunkMaxBytes))
                    {
                        await FlushAsync().ConfigureAwait(false);
                    }

                    current.Add(bytes);
                    currentBytes += bytes.Length;
                    totalDocs++;
                }
            }

            if (totalDocs == 0)
            {
                throw new BatchRejectedException("ERR_BATCH_EMPTY", "O ZIP não tem nenhum .xml.");
            }

            await FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            foreach (var key in uploaded)
            {
                await vault.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
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
            b.Parameters.Add(new() { Value = totalDocs });
            b.Parameters.Add(new() { Value = chunkSizes.Count });
            b.Parameters.Add(new() { Value = direction });
            b.Parameters.Add(new() { Value = priority });
            batch.BatchCommands.Add(b);
            var ch = new NpgsqlBatchCommand("""
                INSERT INTO batch_chunks (tenant_id, batch_id, chunk_no, priority, storage_key, docs, first_doc)
                SELECT $1, $2, n, $3, $4 || 'chunk-' || lpad(n::text, 5, '0') || '.bin.enc', d, f
                  FROM unnest($5::int[], $6::int[], $7::int[]) AS u(n, d, f)
                """);
            ch.Parameters.Add(new() { Value = tenant.Value });
            ch.Parameters.Add(new() { Value = id });
            ch.Parameters.Add(new() { Value = priority });
            ch.Parameters.Add(new() { Value = prefix });
            ch.Parameters.Add(new() { Value = Enumerable.Range(1, chunkSizes.Count).ToArray() });
            ch.Parameters.Add(new() { Value = chunkSizes.ToArray() });
            var offsets = new int[chunkSizes.Count];
            for (var i = 1; i < offsets.Length; i++)
            {
                offsets[i] = offsets[i - 1] + chunkSizes[i - 1];
            }

            ch.Parameters.Add(new() { Value = offsets });   // 1º documento do chunk (numeração do relatório com chunks de tamanho variável)
            batch.BatchCommands.Add(ch);
            return await batch.ExecuteNonQueryAsync(c).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return new BatchCreated(id, totalDocs, chunkSizes.Count);
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

    private static BatchRejectedException Unsafe(string msg) => new("ERR_BATCH_REJECTED_UNSAFE_ARCHIVE", msg);

    private static string Truncate(string s) => s.Length > 80 ? s[..80] + "…" : s;
}

/// <summary>
/// Processamento dos chunks (SDD §6.3; D-602, D-P08): reserva pela função de fair-share (K por tenant, rodízio entre
/// tenants, prioridade do plano), audita cada XML com as regras da API e grava o resultado do chunk.
/// Correção P0: o resultado só vale se o chunk ainda estiver RUNNING com a MESMA tentativa (cerca/fencing — um worker
/// lento cujo lease venceu não grava por cima do que assumiu o chunk); o lease é renovado durante o processamento;
/// a conclusão do lote é uma transição atômica RUNNING → COMPLETING (só um worker vence) e um reconciliador conclui
/// lotes que ficaram com todos os chunks terminados (queda entre o último DONE e a conclusão, ou chunks que
/// esgotaram as tentativas e viraram FAILED). Os slots trabalham em fluxo contínuo: um chunk lento não segura os outros.
/// </summary>
public sealed partial class BatchProcessor(
    NpgsqlDataSource workerDb, ITenantDb db, XmlVault vault, TenantSettingsService settings, IBatchDocumentAuditor auditor,
    IOptions<BatchOptions> options, ILogger<BatchProcessor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var slots = options.Value.WorkerSlots;
        var running = new List<Task>();
        var lastFinalize = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                running.RemoveAll(t => t.IsCompleted);
                if (DateTimeOffset.UtcNow - lastFinalize > TimeSpan.FromSeconds(30))
                {
                    await FinalizeStuckAsync(stoppingToken).ConfigureAwait(false);
                    lastFinalize = DateTimeOffset.UtcNow;
                }

                var free = slots - running.Count;
                var claimed = free > 0 ? await ClaimAsync(free, stoppingToken).ConfigureAwait(false) : [];
                foreach (var c in claimed)
                {
                    running.Add(Task.Run(() => SafeProcessAsync(c, stoppingToken), stoppingToken));
                }

                if (claimed.Count == 0)
                {
                    var wait = Task.Delay(500, stoppingToken);
                    await (running.Count == 0 ? wait : Task.WhenAny(running.Append(wait))).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogError(log, ex);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }

        await Task.WhenAll(running).ConfigureAwait(false);
    }

    public sealed record Claimed(Guid Tenant, Guid Batch, int Chunk, string Key, int Attempt);

    public async Task<List<Claimed>> ClaimAsync(int limit, CancellationToken ct)
    {
        var o = options.Value;
        await using var cmd = workerDb.CreateCommand("SELECT tenant_id, batch_id, chunk_no, storage_key, attempt FROM app.claim_batch_chunks($1, $2, $3, $4)");
        cmd.Parameters.Add(new() { Value = limit });
        cmd.Parameters.Add(new() { Value = o.PerTenantInFlight });
        cmd.Parameters.Add(new() { Value = o.LeaseSeconds });
        cmd.Parameters.Add(new() { Value = o.MaxChunkAttempts });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<Claimed>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new Claimed(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetInt32(4)));
        }

        return list;
    }

    /// <summary>Uma rodada síncrona (testes): reserva até WorkerSlots chunks, processa e conclui o que estiver pronto.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var claimed = await ClaimAsync(options.Value.WorkerSlots, ct).ConfigureAwait(false);
        await Task.WhenAll(claimed.Select(c => SafeProcessAsync(c, ct))).ConfigureAwait(false);
        await FinalizeStuckAsync(ct).ConfigureAwait(false);
        return claimed.Count;
    }

    private async Task SafeProcessAsync(Claimed c, CancellationToken ct)
    {
        try
        {
            await ProcessChunkAsync(c, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // O lease vence e o chunk volta à fila (até MaxChunkAttempts); os outros slots seguem.
            LogChunkError(log, ex, c.Batch, c.Chunk, c.Attempt);
            TaxGatewayMetrics.BatchChunks.Add(1, new KeyValuePair<string, object?>("resultado", "retry"));
        }
    }

    /// <summary>Chunks que esgotaram as tentativas viram FAILED; lotes com tudo terminado são concluídos.</summary>
    public async Task<int> FinalizeStuckAsync(CancellationToken ct)
    {
        var pending = new List<(Guid Tenant, Guid Batch)>();
        await using (var cmd = workerDb.CreateCommand("SELECT tenant_id, batch_id FROM app.batches_to_finalize($1)"))
        {
            cmd.Parameters.Add(new() { Value = options.Value.MaxChunkAttempts });
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                pending.Add((r.GetGuid(0), r.GetGuid(1)));
            }
        }

        foreach (var (t, b) in pending)
        {
            var tenant = new TenantId(t);
            var kek = (await settings.GetAsync(tenant, ct).ConfigureAwait(false)).KmsKeyRef;
            await TryCompleteAsync(tenant, kek, b, ct).ConfigureAwait(false);
        }

        return pending.Count;
    }

    private async Task ProcessChunkAsync(Claimed c, CancellationToken ct)
    {
        var tenant = new TenantId(c.Tenant);
        var kek = (await settings.GetAsync(tenant, ct).ConfigureAwait(false)).KmsKeyRef;
        var (direction, firstDoc) = await db.InTenantAsync(tenant, async (s, t) =>
        {
            await using var cmd = s.CreateCommand("""
                SELECT b.direction, c.first_doc FROM batches b JOIN batch_chunks c ON c.batch_id = b.id
                 WHERE b.id = $1 AND c.chunk_no = $2
                """);
            cmd.Parameters.Add(new() { Value = c.Batch });
            cmd.Parameters.Add(new() { Value = c.Chunk });
            await using var r = await cmd.ExecuteReaderAsync(t).ConfigureAwait(false);
            await r.ReadAsync(t).ConfigureAwait(false);
            return (r.GetString(0), r.GetInt32(1));
        }, ct).ConfigureAwait(false);

        // Heartbeat: renova o lease enquanto o chunk roda; se a cerca não bater mais (outro worker assumiu), cancela.
        using var lost = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.HeartbeatSeconds)));
        var hb = Task.Run(async () =>
        {
            try
            {
                while (await heartbeat.WaitForNextTickAsync(lost.Token).ConfigureAwait(false))
                {
                    if (!await RenewLeaseAsync(tenant, c, lost.Token).ConfigureAwait(false))
                    {
                        await lost.CancelAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // fim normal
            }
        }, CancellationToken.None);

        try
        {
            var docs = BatchIngestService.Unpack(await vault.GetAsync(kek, c.Key, lost.Token).ConfigureAwait(false));
            var results = new BatchDocResult[docs.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, docs.Count), new ParallelOptions { MaxDegreeOfParallelism = options.Value.DocParallelism, CancellationToken = lost.Token },
                async (i, t) => results[i] = await AuditWithRetryAsync(tenant, docs[i], direction, t).ConfigureAwait(false)).ConfigureAwait(false);

            var csv = new StringBuilder();
            for (var i = 0; i < results.Length; i++)
            {
                var r = results[i];
                csv.Append(CultureInfo.InvariantCulture, $"{firstDoc + i + 1},{r.ChaveAcesso},{r.Status},{r.Deterministicas},{r.Diferenca:0.00},{Csv(r.Erro)}\n");
            }

            var prefix = c.Key[..(c.Key.LastIndexOf('/') + 1)];
            await vault.PutBlobAsync(kek, $"{prefix}result-{c.Chunk:D5}.csv.enc", Encoding.UTF8.GetBytes(csv.ToString()), 30, lost.Token).ConfigureAwait(false);
            var failed = results.Count(r => r.Erro is not null);
            var conforme = results.Count(r => r.Status == "CONFORME");

            var applied = await db.InTenantAsync(tenant, async (s, t) =>
            {
                // Cerca: só conta se ESTA tentativa ainda é a dona do chunk.
                await using (var upd = s.CreateCommand("""
                    UPDATE batch_chunks SET status = 'DONE', lease_until = NULL
                     WHERE batch_id = $1 AND chunk_no = $2 AND status = 'RUNNING' AND attempts = $3
                    """))
                {
                    upd.Parameters.Add(new() { Value = c.Batch });
                    upd.Parameters.Add(new() { Value = c.Chunk });
                    upd.Parameters.Add(new() { Value = c.Attempt });
                    if (await upd.ExecuteNonQueryAsync(t).ConfigureAwait(false) == 0)
                    {
                        return false;
                    }
                }

                await using var b = s.CreateCommand("""
                    UPDATE batches SET status = 'RUNNING', processed_docs = processed_docs + $2, failed_docs = failed_docs + $3,
                           conforme_docs = conforme_docs + $4, done_chunks = done_chunks + 1
                     WHERE id = $1 AND status IN ('QUEUED','RUNNING')
                    """);
                b.Parameters.Add(new() { Value = c.Batch });
                b.Parameters.Add(new() { Value = results.Length });
                b.Parameters.Add(new() { Value = failed });
                b.Parameters.Add(new() { Value = conforme });
                await b.ExecuteNonQueryAsync(t).ConfigureAwait(false);
                return true;
            }, ct).ConfigureAwait(false);

            if (!applied)
            {
                LogFenced(log, c.Batch, c.Chunk, c.Attempt);
                TaxGatewayMetrics.BatchChunks.Add(1, new KeyValuePair<string, object?>("resultado", "fenced"));
                return;
            }

            await vault.DeleteAsync(c.Key, ct).ConfigureAwait(false);   // entrada temporária (vale também em zero_retention)
            TaxGatewayMetrics.BatchChunks.Add(1, new KeyValuePair<string, object?>("resultado", "done"));
            await TryCompleteAsync(tenant, kek, c.Batch, ct).ConfigureAwait(false);
        }
        finally
        {
            await lost.CancelAsync().ConfigureAwait(false);
            await hb.ConfigureAwait(false);
        }
    }

    private async Task<BatchDocResult> AuditWithRetryAsync(TenantId tenant, byte[] xml, string direction, CancellationToken ct)
    {
        // Falha transitória (motor reiniciando, rede) não derruba o chunk: tenta de novo com espera crescente e, no fim,
        // registra o documento como erro no relatório em vez de cancelar os outros documentos do chunk.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await auditor.AuditAsync(tenant, xml, direction, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt >= 3)
                {
                    LogDocError(log, ex);
                    return new BatchDocResult(null, "ERRO", 0, 0, "Falha transitória ao auditar este documento; reenvie-o.");
                }

                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct).ConfigureAwait(false);
            }
        }
    }

    private Task<bool> RenewLeaseAsync(TenantId tenant, Claimed c, CancellationToken ct) =>
        db.InTenantAsync(tenant, async (s, t) =>
        {
            await using var cmd = s.CreateCommand("""
                UPDATE batch_chunks SET lease_until = now() + make_interval(secs => $4)
                 WHERE batch_id = $1 AND chunk_no = $2 AND status = 'RUNNING' AND attempts = $3
                """);
            cmd.Parameters.Add(new() { Value = c.Batch });
            cmd.Parameters.Add(new() { Value = c.Chunk });
            cmd.Parameters.Add(new() { Value = c.Attempt });
            cmd.Parameters.Add(new() { Value = options.Value.LeaseSeconds });
            return await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false) == 1;
        }, ct);

    /// <summary>Conclui o lote se todos os chunks terminaram; a transição RUNNING → COMPLETING garante um só vencedor.</summary>
    private async Task TryCompleteAsync(TenantId tenant, string kek, Guid batchId, CancellationToken ct)
    {
        var info = await db.InTenantAsync(tenant, async (s, t) =>
        {
            await using var cmd = s.CreateCommand("""
                UPDATE batches b SET status = 'COMPLETING'
                 WHERE b.id = $1 AND b.status IN ('QUEUED','RUNNING')
                   AND NOT EXISTS (SELECT 1 FROM batch_chunks c WHERE c.batch_id = b.id AND c.status IN ('QUEUED','RUNNING'))
                RETURNING b.storage_key, b.total_chunks
                """);
            cmd.Parameters.Add(new() { Value = batchId });
            await using var r = await cmd.ExecuteReaderAsync(t).ConfigureAwait(false);
            return await r.ReadAsync(t).ConfigureAwait(false) ? (r.GetString(0), r.GetInt32(1)) : ((string, int)?)null;
        }, ct).ConfigureAwait(false);
        if (info is not { } i)
        {
            return;
        }

        try
        {
            await CompleteAsync(tenant, kek, batchId, i.Item1, i.Item2, ct).ConfigureAwait(false);
        }
        catch
        {
            // Volta para RUNNING: o reconciliador tenta de novo.
            await db.InTenantAsync(tenant, async (s, t) =>
            {
                await using var back = s.CreateCommand("UPDATE batches SET status = 'RUNNING' WHERE id = $1 AND status = 'COMPLETING'");
                back.Parameters.Add(new() { Value = batchId });
                return await back.ExecuteNonQueryAsync(t).ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task CompleteAsync(TenantId tenant, string kek, Guid batchId, string prefix, int totalChunks, CancellationToken ct)
    {
        var failedChunks = await db.InTenantAsync(tenant, async (s, t) =>
        {
            await using var cmd = s.CreateCommand("SELECT chunk_no, first_doc, docs FROM batch_chunks WHERE batch_id = $1 AND status = 'FAILED'");
            cmd.Parameters.Add(new() { Value = batchId });
            await using var r = await cmd.ExecuteReaderAsync(t).ConfigureAwait(false);
            var map = new Dictionary<int, (int First, int Docs)>();
            while (await r.ReadAsync(t).ConfigureAwait(false))
            {
                map[r.GetInt32(0)] = (r.GetInt32(1), r.GetInt32(2));
            }

            return map;
        }, ct).ConfigureAwait(false);

        var report = new StringBuilder("documento,chave_acesso,status,ocorrencias_deterministicas,diferenca,erro\n");
        for (var i = 1; i <= totalChunks; i++)
        {
            if (failedChunks.TryGetValue(i, out var f))
            {
                for (var d = 0; d < f.Docs; d++)
                {
                    report.Append(CultureInfo.InvariantCulture, $"{f.First + d + 1},,ERRO,0,0.00,\"Parte do lote não pôde ser processada após {options.Value.MaxChunkAttempts} tentativas; reenvie estes documentos.\"\n");
                }

                continue;
            }

            report.Append(Encoding.UTF8.GetString(await vault.GetAsync(kek, $"{prefix}result-{i:D5}.csv.enc", ct).ConfigureAwait(false)));
        }

        var reportKey = $"{prefix}relatorio.csv.enc";
        await vault.PutBlobAsync(kek, reportKey, Encoding.UTF8.GetBytes(report.ToString()), 30, ct).ConfigureAwait(false);
        await db.InTenantAsync(tenant, async (s, t) =>
        {
            await using var batch = s.CreateBatch();
            var b = new NpgsqlBatchCommand("UPDATE batches SET status = 'COMPLETED', report_key = $2, completed_at = now() WHERE id = $1 AND status = 'COMPLETING'");
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chunk {Chunk} do lote {Batch} (tentativa {Attempt}) falhou; volta à fila até o teto de tentativas.")]
    private static partial void LogChunkError(ILogger logger, Exception ex, Guid batch, int chunk, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chunk {Chunk} do lote {Batch}: tentativa {Attempt} perdeu a posse (lease vencido); resultado descartado.")]
    private static partial void LogFenced(ILogger logger, Guid batch, int chunk, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Documento do lote falhou após 3 tentativas.")]
    private static partial void LogDocError(ILogger logger, Exception ex);
}
