using System.IO.Compression;

using TaxGateway.Core.Tenancy;

namespace TaxGateway.Infrastructure.Persistence;

public enum IdempotencyState { New, Replay, Mismatch, InProgress }

public sealed record IdempotencyResult(IdempotencyState State, int StatusCode = 0, byte[]? Body = null);

/// <summary>
/// <c>Idempotency-Key</c> (SDD §6, D-212): a primeira requisição reserva a chave (linha sem resposta); a
/// conclusão grava status e corpo comprimido por 24 h. Mesma chave + mesmo hash = replay; hash diferente = 422;
/// chave ainda em processamento = 409. Falha transitória (5xx) libera a chave para nova tentativa.
/// Chave expirada é apagada na próxima reserva (RLS: só as do próprio tenant).
/// </summary>
public sealed class IdempotencyStore(ITenantDb db)
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>
    /// Lease da reserva (D-P03): se o processo cair no meio, depois disso a MESMA requisição (mesmo hash) retoma a
    /// chave em vez de receber 409 por 24 h. Folga sobre o pior caso de uma auditoria/lote síncrono.
    /// </summary>
    public static readonly TimeSpan ProcessingLease = TimeSpan.FromMinutes(2);

    public Task<IdempotencyResult> BeginAsync(TenantId tenant, string key, byte[] requestHash, CancellationToken ct) =>
        db.InTenantAsync(tenant, async (s, c) =>
        {
            await using (var cleanup = s.CreateCommand("DELETE FROM idempotency_requests WHERE key = $1 AND expires_at < now()"))
            {
                cleanup.Parameters.Add(new() { Value = key });
                await cleanup.ExecuteNonQueryAsync(c).ConfigureAwait(false);
            }

            await using (var reserve = s.CreateCommand("""
                INSERT INTO idempotency_requests (tenant_id, key, request_hash, expires_at, locked_until)
                VALUES ($1, $2, $3, now() + $4, now() + $5) ON CONFLICT (tenant_id, key) DO NOTHING
                """))
            {
                reserve.Parameters.Add(new() { Value = tenant.Value });
                reserve.Parameters.Add(new() { Value = key });
                reserve.Parameters.Add(new() { Value = requestHash });
                reserve.Parameters.Add(new() { Value = Retention });
                reserve.Parameters.Add(new() { Value = ProcessingLease });
                if (await reserve.ExecuteNonQueryAsync(c).ConfigureAwait(false) == 1)
                {
                    return new IdempotencyResult(IdempotencyState.New);
                }
            }

            // Reserva órfã (o processo caiu antes de concluir) com o mesmo conteúdo: retoma.
            await using (var takeover = s.CreateCommand("""
                UPDATE idempotency_requests SET locked_until = now() + $3
                 WHERE key = $1 AND request_hash = $2 AND status_code IS NULL AND locked_until < now()
                """))
            {
                takeover.Parameters.Add(new() { Value = key });
                takeover.Parameters.Add(new() { Value = requestHash });
                takeover.Parameters.Add(new() { Value = ProcessingLease });
                if (await takeover.ExecuteNonQueryAsync(c).ConfigureAwait(false) == 1)
                {
                    return new IdempotencyResult(IdempotencyState.New);
                }
            }

            await using var read = s.CreateCommand("SELECT request_hash, status_code, response_body FROM idempotency_requests WHERE key = $1");
            read.Parameters.Add(new() { Value = key });
            await using var r = await read.ExecuteReaderAsync(c).ConfigureAwait(false);
            if (!await r.ReadAsync(c).ConfigureAwait(false))
            {
                return new IdempotencyResult(IdempotencyState.InProgress);   // corrida rara com a limpeza: tratar como em curso
            }

            if (!r.GetFieldValue<byte[]>(0).AsSpan().SequenceEqual(requestHash))
            {
                return new IdempotencyResult(IdempotencyState.Mismatch);
            }

            return r.IsDBNull(1)
                ? new IdempotencyResult(IdempotencyState.InProgress)
                : new IdempotencyResult(IdempotencyState.Replay, r.GetInt32(1), Decompress(r.GetFieldValue<byte[]>(2)));
        }, ct);

    public Task CompleteAsync(TenantId tenant, string key, int statusCode, byte[] body, CancellationToken ct) =>
        db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("UPDATE idempotency_requests SET status_code = $2, response_body = $3 WHERE key = $1");
            cmd.Parameters.Add(new() { Value = key });
            cmd.Parameters.Add(new() { Value = statusCode });
            cmd.Parameters.Add(new() { Value = Compress(body) });
            return await cmd.ExecuteNonQueryAsync(c).ConfigureAwait(false);
        }, ct);

    public Task ReleaseAsync(TenantId tenant, string key, CancellationToken ct) =>
        db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("DELETE FROM idempotency_requests WHERE key = $1 AND status_code IS NULL");
            cmd.Parameters.Add(new() { Value = key });
            return await cmd.ExecuteNonQueryAsync(c).ConfigureAwait(false);
        }, ct);

    private static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var br = new BrotliStream(ms, CompressionLevel.Fastest))
        {
            br.Write(data);
        }

        return ms.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var br = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        br.CopyTo(output);
        return output.ToArray();
    }
}
