using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using TaxGateway.Core.Tenancy;
using TaxGateway.Infrastructure.Batches;
using TaxGateway.Infrastructure.Persistence;
using TaxGateway.Infrastructure.Storage;
using TaxGateway.Infrastructure.Xml;

namespace TaxGateway.Infrastructure.Dfe;

public sealed class CertificateRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record CertificateInfo(string Cnpj, string Thumbprint, string Subject, DateTimeOffset NotAfter);

/// <summary>
/// Custódia do A1 (SDD §4.2; D-901): PFX e senha cifrados com AES-256-GCM por uma DEK própria do certificado; a DEK vai
/// embrulhada pela KEK do tenant no cofre. Decifrado só em memória, com chave efêmera (não vai para o key store do SO).
/// </summary>
public sealed class CertificateStore(ITenantDb db, TenantSettingsService settings, IKeyVault vault, TimeProvider clock)
{
    public async Task<CertificateInfo> StoreAsync(TenantId tenant, string cnpj, byte[] pfx, string password, CancellationToken ct)
    {
        X509Certificate2 cert;
        try
        {
            cert = X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException)
        {
            throw new CertificateRejectedException("ERR_CERT_INVALID", "PFX ilegível ou senha incorreta.");
        }

        using (cert)
        {
            if (!cert.HasPrivateKey || cert.GetRSAPrivateKey() is null)
            {
                throw new CertificateRejectedException("ERR_CERT_INVALID", "O PFX não contém chave privada RSA.");
            }

            // e-CNPJ da matriz atende as filiais: a raiz (8 primeiras posições) precisa ser a mesma (D-901).
            var certCnpj = NfeSignatureVerifier.CnpjFromCertificate(cert);
            if (certCnpj is null || !string.Equals(certCnpj[..8], cnpj[..8], StringComparison.Ordinal))
            {
                throw new CertificateRejectedException("ERR_CERT_CNPJ_MISMATCH", $"O certificado é do CNPJ {certCnpj ?? "desconhecido"}, raiz diferente de {cnpj}.");
            }

            var notAfter = new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            if (notAfter <= clock.GetUtcNow())
            {
                throw new CertificateRejectedException("ERR_CERT_EXPIRED", $"Certificado vencido em {notAfter:yyyy-MM-dd}.");
            }

            var kek = (await settings.GetAsync(tenant, ct).ConfigureAwait(false)).KmsKeyRef;
            var aad = Aad(tenant, cnpj);
            var dek = RandomNumberGenerator.GetBytes(32);
            byte[] pfxCt, pwdCt, wrapped;
            try
            {
                pfxCt = Seal(dek, pfx, aad);
                pwdCt = Seal(dek, Encoding.UTF8.GetBytes(password), aad);
                wrapped = await vault.WrapAsync(kek, dek, aad, ct).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
            }

            var info = new CertificateInfo(cnpj, cert.Thumbprint, cert.Subject, notAfter);
            await db.InTenantAsync(tenant, async (s, c) =>
            {
                await using var batch = s.CreateBatch();
                var del = new NpgsqlBatchCommand("DELETE FROM certificates WHERE cnpj = $1");
                del.Parameters.Add(new() { Value = cnpj });
                batch.BatchCommands.Add(del);
                var ins = new NpgsqlBatchCommand("""
                    INSERT INTO certificates (id, tenant_id, cnpj, pfx_ciphertext, password_ciphertext, dek_wrapped, thumbprint, not_after, subject)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
                    """);
                ins.Parameters.Add(new() { Value = Guid.CreateVersion7() });
                ins.Parameters.Add(new() { Value = tenant.Value });
                ins.Parameters.Add(new() { Value = cnpj });
                ins.Parameters.Add(new() { Value = pfxCt });
                ins.Parameters.Add(new() { Value = pwdCt });
                ins.Parameters.Add(new() { Value = wrapped });
                ins.Parameters.Add(new() { Value = info.Thumbprint });
                ins.Parameters.Add(new() { Value = notAfter });
                ins.Parameters.Add(new() { Value = info.Subject });
                batch.BatchCommands.Add(ins);
                return await batch.ExecuteNonQueryAsync(c).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            return info;
        }
    }

    public Task<CertificateInfo?> GetInfoAsync(TenantId tenant, string cnpj, CancellationToken ct) =>
        db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("SELECT thumbprint, coalesce(subject, ''), not_after FROM certificates WHERE cnpj = $1");
            cmd.Parameters.Add(new() { Value = cnpj });
            await using var r = await cmd.ExecuteReaderAsync(c).ConfigureAwait(false);
            return await r.ReadAsync(c).ConfigureAwait(false)
                ? new CertificateInfo(cnpj, r.GetString(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2))
                : null;
        }, ct);

    public Task<bool> DeleteAsync(TenantId tenant, string cnpj, CancellationToken ct) =>
        db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("DELETE FROM certificates WHERE cnpj = $1");
            cmd.Parameters.Add(new() { Value = cnpj });
            return await cmd.ExecuteNonQueryAsync(c).ConfigureAwait(false) > 0;
        }, ct);

    /// <summary>Certificado em memória para uma chamada mTLS/assinatura. Quem chama descarta.</summary>
    public async Task<X509Certificate2?> LoadAsync(TenantId tenant, string cnpj, CancellationToken ct)
    {
        var row = await db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("SELECT pfx_ciphertext, password_ciphertext, dek_wrapped FROM certificates WHERE cnpj = $1");
            cmd.Parameters.Add(new() { Value = cnpj });
            await using var r = await cmd.ExecuteReaderAsync(c).ConfigureAwait(false);
            return await r.ReadAsync(c).ConfigureAwait(false)
                ? (Pfx: r.GetFieldValue<byte[]>(0), Pwd: r.GetFieldValue<byte[]>(1), Dek: r.GetFieldValue<byte[]>(2))
                : default((byte[] Pfx, byte[] Pwd, byte[] Dek)?);
        }, ct).ConfigureAwait(false);
        if (row is not { } x)
        {
            return null;
        }

        var aad = Aad(tenant, cnpj);
        var kek = (await settings.GetAsync(tenant, ct).ConfigureAwait(false)).KmsKeyRef;
        var dek = await vault.UnwrapAsync(kek, x.Dek, aad, ct).ConfigureAwait(false);
        try
        {
            var pfx = Open(dek, x.Pfx, aad);
            var pwd = Encoding.UTF8.GetString(Open(dek, x.Pwd, aad));
            // Linux (produção): chave só em memória. Windows (dev): o SChannel não usa chave efêmera em TLS de cliente,
            // então a chave vai para um contêiner temporário apagado no Dispose (D-901).
            return X509CertificateLoader.LoadPkcs12(pfx, pwd, OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    private static byte[] Aad(TenantId tenant, string cnpj) => Encoding.UTF8.GetBytes($"{tenant}|{cnpj}|a1");

    private static byte[] Seal(byte[] key, byte[] plain, byte[] aad)
    {
        var output = new byte[12 + 16 + plain.Length];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(output.AsSpan(0, 12), plain, output.AsSpan(28), output.AsSpan(12, 16), aad);
        return output;
    }

    private static byte[] Open(byte[] key, byte[] sealedData, byte[] aad)
    {
        var plain = new byte[sealedData.Length - 28];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(sealedData.AsSpan(0, 12), sealedData.AsSpan(28), sealedData.AsSpan(12, 16), plain, aad);
        return plain;
    }
}

/// <summary>
/// Distribuição DF-e por CNPJ (SDD §6.5; NT 2014.002; D-902 a D-905). Regras aplicadas ANTES de cada consulta, para a
/// SEFAZ nunca precisar responder 656:
/// <list type="bullet">
/// <item>após 137 ("nenhum documento") ou ultNSU = maxNSU, a próxima consulta do CNPJ só depois de 1 hora;</item>
/// <item>a consulta sempre parte do ultNSU devolvido pela última resposta;</item>
/// <item>no máximo 20 consultas por CNPJ por janela de 1 hora (vale para todas, inclusive a drenagem de pendências);</item>
/// <item>após um 656, bloqueio de 1 hora; erro de rede ou cStat inesperado também bloqueia por 1 hora (conservador).</item>
/// </list>
/// O "agora" é o TimeProvider; o agendamento usa <c>app.claim_due_dfe</c> com lease (uma réplica por CNPJ).
/// </summary>
public sealed partial class DfeSyncService(NpgsqlDataSource workerDb, ITenantDb db, CertificateStore certs, ISefazDfeClient sefaz,
    IBatchDocumentAuditor auditor, TimeProvider clock, ILogger<DfeSyncService> log)
{
    public const int MaxQueriesPerHour = 20;
    public static readonly TimeSpan Block = TimeSpan.FromHours(1);
    private const int LeaseSeconds = 600;

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var claimed = new List<(TenantId Tenant, string Cnpj)>();
        await using (var cmd = workerDb.CreateCommand("SELECT tenant_id, cnpj FROM app.claim_due_dfe($1, $2, $3)"))
        {
            cmd.Parameters.Add(new() { Value = clock.GetUtcNow() });
            cmd.Parameters.Add(new() { Value = 20 });
            cmd.Parameters.Add(new() { Value = LeaseSeconds });
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                claimed.Add((new TenantId(r.GetGuid(0)), r.GetString(1)));
            }
        }

        foreach (var (tenant, cnpj) in claimed)
        {
            await SyncOneAsync(tenant, cnpj, ct).ConfigureAwait(false);
        }

        return claimed.Count;
    }

    private sealed class State
    {
        public long UltNsu;
        public long? MaxNsu;
        public DateTimeOffset? WindowStart;
        public int WindowCount;
        public int CUf;
        public bool Ciencia;
        public DateTimeOffset NextAllowed;
        public string? LastCStat;
        public DateTimeOffset? LastQuery;
        public DateTimeOffset? Last656;
    }

    private sealed record Processed(DfeDoc Doc, string Kind, string? Chave, string Status, string? AuditStatus, string? Detail, JsonObject? Event);

    public async Task SyncOneAsync(TenantId tenant, string cnpj, CancellationToken ct)
    {
        var st = await db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("""
                SELECT ult_nsu, max_nsu, window_start, window_count, c_uf_autor, manifest_ciencia, next_allowed_at, last_cstat, last_query_at, last_656_at
                  FROM dfe_sync_state WHERE cnpj = $1
                """);
            cmd.Parameters.Add(new() { Value = cnpj });
            await using var r = await cmd.ExecuteReaderAsync(c).ConfigureAwait(false);
            await r.ReadAsync(c).ConfigureAwait(false);
            return new State
            {
                UltNsu = r.GetInt64(0),
                MaxNsu = r.IsDBNull(1) ? null : r.GetInt64(1),
                WindowStart = r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2),
                WindowCount = r.GetInt32(3),
                CUf = r.GetInt16(4),
                Ciencia = r.GetBoolean(5),
                NextAllowed = r.GetFieldValue<DateTimeOffset>(6),
                LastCStat = r.IsDBNull(7) ? null : r.GetString(7),
                LastQuery = r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
                Last656 = r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9),
            };
        }, ct).ConfigureAwait(false);

        using var cert = await certs.LoadAsync(tenant, cnpj, ct).ConfigureAwait(false);
        if (cert is null)
        {
            st.NextAllowed = clock.GetUtcNow() + Block;
            await SaveAsync(tenant, cnpj, st, [], ct).ConfigureAwait(false);
            return;
        }

        while (true)
        {
            var now = clock.GetUtcNow();
            if (st.WindowStart is null || now - st.WindowStart >= Block)
            {
                st.WindowStart = now;
                st.WindowCount = 0;
            }

            if (st.WindowCount >= MaxQueriesPerHour)
            {
                st.NextAllowed = st.WindowStart.Value + Block;
                break;
            }

            st.WindowCount++;
            st.LastQuery = now;
            DistResult r;
            try
            {
                r = await sefaz.DistNsuAsync(cert, st.CUf, cnpj, st.UltNsu, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or System.Xml.XmlException)
            {
                LogQueryFailed(log, cnpj, ex);
                st.LastCStat = "ERRO";
                st.NextAllowed = now + Block;
                break;
            }

            st.LastCStat = r.CStat;
            if (r.CStat == "138")
            {
                var processed = await ProcessAsync(tenant, cnpj, cert, st.Ciencia, r.Docs, ct).ConfigureAwait(false);
                st.UltNsu = r.UltNsu;
                st.MaxNsu = r.MaxNsu;
                var caughtUp = r.UltNsu >= r.MaxNsu;
                st.NextAllowed = caughtUp ? now + Block : now;
                await SaveAsync(tenant, cnpj, st, processed, ct).ConfigureAwait(false);
                if (caughtUp)
                {
                    break;
                }

                continue;
            }

            if (r.UltNsu > 0)
            {
                st.UltNsu = r.UltNsu;   // 137/656 devolvem o ultNSU a usar na próxima consulta
            }

            if (r.MaxNsu > 0)
            {
                st.MaxNsu = r.MaxNsu;
            }

            if (r.CStat == "656")
            {
                st.Last656 = now;
                LogConsumoIndevido(log, cnpj, r.XMotivo);
            }

            st.NextAllowed = now + Block;   // 137, 656 e qualquer outro: 1 h
            break;
        }

        await SaveAsync(tenant, cnpj, st, [], ct).ConfigureAwait(false);
    }

    private async Task<List<Processed>> ProcessAsync(TenantId tenant, string cnpj, X509Certificate2 cert, bool ciencia, IReadOnlyList<DfeDoc> docs, CancellationToken ct)
    {
        // Documentos já gravados (reconsulta após falha entre consultar e gravar) não são reprocessados.
        var nsus = docs.Select(d => d.Nsu).ToArray();
        var seen = await db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("SELECT nsu FROM dfe_documents WHERE cnpj = $1 AND nsu = ANY($2)");
            cmd.Parameters.Add(new() { Value = cnpj });
            cmd.Parameters.Add(new() { Value = nsus });
            await using var r = await cmd.ExecuteReaderAsync(c).ConfigureAwait(false);
            var set = new HashSet<long>();
            while (await r.ReadAsync(c).ConfigureAwait(false))
            {
                set.Add(r.GetInt64(0));
            }

            return set;
        }, ct).ConfigureAwait(false);

        var result = new List<Processed>();
        foreach (var d in docs.Where(d => !seen.Contains(d.Nsu)))
        {
            string? chave = null;
            try
            {
                chave = DfeProtocol.AccessKey(d.Xml);
            }
            catch (System.Xml.XmlException)
            {
                // documento malformado: registrado como OUTRO/IGNORADO abaixo
            }

            if (d.Schema.StartsWith("resNFe", StringComparison.Ordinal))
            {
                if (!ciencia || chave is null)
                {
                    result.Add(new Processed(d, "RESUMO", chave, "RECEBIDO", null, null, null));
                    continue;
                }

                try
                {
                    var ev = await sefaz.CienciaAsync(cert, cnpj, chave, clock.GetUtcNow(), ct).ConfigureAwait(false);
                    var ok = ev.CStat is "135" or "136" or "573";   // 573 = já registrada (reenvio)
                    result.Add(new Processed(d, "RESUMO", chave, ok ? "CIENCIA_ENVIADA" : "CIENCIA_FALHOU", null, $"{ev.CStat} {ev.XMotivo}", null));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or System.Xml.XmlException)
                {
                    result.Add(new Processed(d, "RESUMO", chave, "CIENCIA_FALHOU", null, ex.GetType().Name, null));
                }
            }
            else if (d.Schema.StartsWith("procNFe", StringComparison.Ordinal))
            {
                var a = await auditor.AuditAsync(tenant, d.Xml, "INBOUND", ct).ConfigureAwait(false);
                var status = a.Erro is null ? "AUDITADO" : "REJEITADO";
                var ev = new JsonObject
                {
                    ["cnpj"] = cnpj,
                    ["nsu"] = d.Nsu,
                    ["chave_acesso"] = a.ChaveAcesso ?? chave,
                    ["status_auditoria"] = a.Status,
                    ["ocorrencias_deterministicas"] = a.Deterministicas,
                    ["diferenca"] = a.Diferenca,
                    ["erro"] = a.Erro,
                };
                result.Add(new Processed(d, "COMPLETO", chave, status, a.Status, a.Erro, ev));
            }
            else
            {
                result.Add(new Processed(d, d.Schema.StartsWith("resEvento", StringComparison.Ordinal) || d.Schema.StartsWith("procEvento", StringComparison.Ordinal)
                    ? "EVENTO" : "OUTRO", chave, "IGNORADO", null, null, null));
            }
        }

        return result;
    }

    private Task<int> SaveAsync(TenantId tenant, string cnpj, State st, List<Processed> processed, CancellationToken ct) =>
        db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var batch = s.CreateBatch();
            foreach (var p in processed)
            {
                var ins = new NpgsqlBatchCommand("""
                    INSERT INTO dfe_documents (tenant_id, cnpj, nsu, schema, kind, access_key, status, audit_status, detail, received_at)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) ON CONFLICT DO NOTHING
                    """);
                ins.Parameters.Add(new() { Value = tenant.Value });
                ins.Parameters.Add(new() { Value = cnpj });
                ins.Parameters.Add(new() { Value = p.Doc.Nsu });
                ins.Parameters.Add(new() { Value = p.Doc.Schema });
                ins.Parameters.Add(new() { Value = p.Kind });
                ins.Parameters.Add(new() { Value = (object?)p.Chave ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
                ins.Parameters.Add(new() { Value = p.Status });
                ins.Parameters.Add(new() { Value = (object?)p.AuditStatus ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
                ins.Parameters.Add(new() { Value = (object?)p.Detail ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
                ins.Parameters.Add(new() { Value = clock.GetUtcNow() });
                batch.BatchCommands.Add(ins);
                if (p.Event is not null)
                {
                    var ev = new NpgsqlBatchCommand("INSERT INTO outbox (tenant_id, topic, payload) VALUES ($1, 'dfe.document.audited', $2::jsonb)");
                    ev.Parameters.Add(new() { Value = tenant.Value });
                    ev.Parameters.Add(new() { Value = p.Event.ToJsonString() });
                    batch.BatchCommands.Add(ev);
                }
            }

            var upd = new NpgsqlBatchCommand("""
                UPDATE dfe_sync_state SET ult_nsu = $2, max_nsu = $3, window_start = $4, window_count = $5, next_allowed_at = $6,
                       last_cstat = $7, last_query_at = $8, last_656_at = $9, lease_until = NULL
                 WHERE cnpj = $1
                """);
            upd.Parameters.Add(new() { Value = cnpj });
            upd.Parameters.Add(new() { Value = st.UltNsu });
            upd.Parameters.Add(new() { Value = (object?)st.MaxNsu ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
            upd.Parameters.Add(new() { Value = (object?)st.WindowStart ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
            upd.Parameters.Add(new() { Value = st.WindowCount });
            upd.Parameters.Add(new() { Value = st.NextAllowed });
            upd.Parameters.Add(new() { Value = (object?)st.LastCStat ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            upd.Parameters.Add(new() { Value = (object?)st.LastQuery ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
            upd.Parameters.Add(new() { Value = (object?)st.Last656 ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
            batch.BatchCommands.Add(upd);
            return await batch.ExecuteNonQueryAsync(c).ConfigureAwait(false);
        }, ct);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DF-e: consulta do CNPJ {Cnpj} falhou; nova tentativa em 1 h.")]
    private static partial void LogQueryFailed(ILogger logger, string cnpj, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "DF-e: SEFAZ respondeu 656 (consumo indevido) para {Cnpj}: {Motivo}. Bloqueado por 1 h.")]
    private static partial void LogConsumoIndevido(ILogger logger, string cnpj, string motivo);
}

/// <summary>Alertas de vencimento do A1 a 30, 15 e 5 dias (SDD §4.2; D-906): um webhook por limiar.</summary>
public sealed class CertificateExpiryService(NpgsqlDataSource workerDb, ITenantDb db, TimeProvider clock)
{
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = new List<(TenantId Tenant, string Cnpj, DateTimeOffset NotAfter, int? Alerted)>();
        await using (var cmd = workerDb.CreateCommand("SELECT tenant_id, cnpj, not_after, expiry_alerted_days FROM app.certificates_expiring($1)"))
        {
            cmd.Parameters.Add(new() { Value = now });
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                due.Add((new TenantId(r.GetGuid(0)), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.IsDBNull(3) ? null : r.GetInt32(3)));
            }
        }

        var sent = 0;
        foreach (var (tenant, cnpj, notAfter, alerted) in due)
        {
            var days = (int)Math.Ceiling((notAfter - now).TotalDays);
            var threshold = days <= 5 ? 5 : days <= 15 ? 15 : 30;
            if (alerted is not null && alerted <= threshold)
            {
                continue;
            }

            var payload = new JsonObject { ["cnpj"] = cnpj, ["vence_em"] = notAfter, ["dias_restantes"] = Math.Max(days, 0), ["limiar_dias"] = threshold };
            sent += await db.InTenantAsync(tenant, async (s, c) =>
            {
                await using var batch = s.CreateBatch();
                var upd = new NpgsqlBatchCommand("UPDATE certificates SET expiry_alerted_days = $2 WHERE cnpj = $1");
                upd.Parameters.Add(new() { Value = cnpj });
                upd.Parameters.Add(new() { Value = threshold });
                batch.BatchCommands.Add(upd);
                var ev = new NpgsqlBatchCommand("INSERT INTO outbox (tenant_id, topic, payload) VALUES ($1, 'certificate.expiring', $2::jsonb)");
                ev.Parameters.Add(new() { Value = tenant.Value });
                ev.Parameters.Add(new() { Value = payload.ToJsonString() });
                batch.BatchCommands.Add(ev);
                await batch.ExecuteNonQueryAsync(c).ConfigureAwait(false);
                return 1;
            }, ct).ConfigureAwait(false);
        }

        return sent;
    }
}

/// <summary>Laço dos workers: distribuição a cada <see cref="DfeOptions.PollSeconds"/> e vencimentos a cada hora.</summary>
public sealed partial class DfeWorker(DfeSyncService sync, CertificateExpiryService expiry, IOptions<DfeOptions> options, ILogger<DfeWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastExpiry = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.Value.PollSeconds)));
        do
        {
            try
            {
                await sync.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                if (DateTimeOffset.UtcNow - lastExpiry > TimeSpan.FromHours(1))
                {
                    await expiry.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                    lastExpiry = DateTimeOffset.UtcNow;
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                LogFailed(log, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "DF-e: ciclo falhou; nova tentativa no próximo intervalo.")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
