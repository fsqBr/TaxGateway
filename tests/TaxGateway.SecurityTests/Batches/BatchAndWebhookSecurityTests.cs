using System.IO.Compression;
using System.Net;
using System.Text;

using Microsoft.Extensions.Options;

using Npgsql;

using TaxGateway.Core.Tenancy;
using TaxGateway.Infrastructure.Batches;
using TaxGateway.Infrastructure.Webhooks;
using TaxGateway.SecurityTests.Rls;

namespace TaxGateway.SecurityTests.Batches;

/// <summary>Critérios de saída da Fase 6: zip bomb rejeitada, webhook para IP privado bloqueado (D-604, D-606).</summary>
public sealed class ZipDefenseTests
{
    // As defesas rodam antes de qualquer acesso a banco/S3: dependências nulas provam que nada foi gravado.
    private static readonly BatchIngestService Ingest = new(null!, null!, null!, Options.Create(new BatchOptions()));
    private static readonly TenantId Tenant = TenantId.New();

    private static MemoryStream Zip(Action<ZipArchive> build)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            build(z);
        }

        ms.Position = 0;
        return ms;
    }

    private static void Add(ZipArchive z, string name, byte[] content, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var s = z.CreateEntry(name, level).Open();
        s.Write(content);
    }

    private static async Task<string> RejectCode(Stream zip, long maxBytes = 15 * 1024 * 1024, int maxDocs = 500) =>
        (await Assert.ThrowsAsync<BatchRejectedException>(() => Ingest.IngestAsync(Tenant, zip, maxBytes, maxDocs, 2, "OUTBOUND", CancellationToken.None))).Code;

    [Fact]
    public async Task Zip_bomb_is_rejected_by_compression_ratio_and_real_size()
    {
        // 50 MB de zeros compactam para ~50 KB: razão ~1000:1 e acima de 5 MB descompactados.
        using var bomb = Zip(z => Add(z, "nota.xml", new byte[50 * 1024 * 1024]));
        Assert.Equal("ERR_BATCH_REJECTED_UNSAFE_ARCHIVE", await RejectCode(bomb));
    }

    [Fact]
    public async Task Nested_zip_non_xml_and_too_many_entries_are_rejected()
    {
        using var inner = Zip(z => Add(z, "a.xml", "<x/>"u8.ToArray()));
        using var nested = Zip(z => Add(z, "nota.xml", inner.ToArray(), CompressionLevel.NoCompression));
        Assert.Equal("ERR_BATCH_REJECTED_UNSAFE_ARCHIVE", await RejectCode(nested));

        using var exe = Zip(z => Add(z, "../../evil.sh", "#!/bin/sh"u8.ToArray()));
        Assert.Equal("ERR_BATCH_REJECTED_UNSAFE_ARCHIVE", await RejectCode(exe));

        using var many = Zip(z =>
        {
            for (var i = 0; i < 6; i++)
            {
                Add(z, $"n{i}.xml", "<x/>"u8.ToArray());
            }
        });
        Assert.Equal("ERR_BATCH_REJECTED_UNSAFE_ARCHIVE", await RejectCode(many, maxDocs: 5));
    }

    [Fact]
    public async Task Upload_above_plan_limit_and_invalid_zip_are_rejected()
    {
        using var big = Zip(z => Add(z, "n.xml", Encoding.UTF8.GetBytes(new string('a', 4096)), CompressionLevel.NoCompression));
        Assert.Equal("ERR_BATCH_TOO_LARGE", await RejectCode(big, maxBytes: 1024));
        using var notZip = new MemoryStream("isto não é zip"u8.ToArray());
        Assert.Equal("ERR_BATCH_REJECTED_UNSAFE_ARCHIVE", await RejectCode(notZip));
    }

    [Fact]
    public void Chunk_packing_round_trips()
    {
        var docs = new List<byte[]> { "<a/>"u8.ToArray(), Array.Empty<byte>(), "<b>ç</b>"u8.ToArray() };
        var back = BatchIngestService.Unpack(BatchIngestService.Pack(docs));
        Assert.Equal(docs.Count, back.Count);
        Assert.All(docs.Zip(back), p => Assert.Equal(p.First, p.Second));
    }
}

public sealed class SsrfGuardTests
{
    [Fact]
    public async Task Host_que_nao_resolve_e_recusado_e_nao_vira_erro_interno()
    {
        var ex = await Assert.ThrowsAsync<WebhookUrlRejectedException>(() => SsrfGuard.ResolveAllowedAsync("nao-existe.invalid", CancellationToken.None));
        Assert.Contains("DNS", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.10")]
    [InlineData("169.254.169.254")]   // metadados da nuvem
    [InlineData("64:ff9b::a9fe:a9fe")]   // NAT64 → 169.254.169.254 (D-P15)
    [InlineData("64:ff9b:1::a00:1")]
    [InlineData("2002:a9fe:a9fe::1")]    // 6to4
    [InlineData("2001:0:4136:e378::1")]  // Teredo
    [InlineData("100::1")]
    [InlineData("192.0.2.10")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.9")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:10.0.0.1")]   // IPv4 mapeado em IPv6
    public void Internal_addresses_are_blocked(string ip) => Assert.True(SsrfGuard.IsBlocked(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700:4700::1111")]
    public void Public_addresses_are_allowed(string ip) => Assert.False(SsrfGuard.IsBlocked(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("http://example.com/hook")]          // não https
    [InlineData("https://user:pass@example.com/")]   // credencial na URL
    [InlineData("https://example.com:22/")]          // porta não permitida
    [InlineData("https://10.0.0.5/hook")]            // IP privado literal
    [InlineData("https://[::1]/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    public void Unsafe_urls_are_rejected_at_registration(string url) =>
        Assert.Throws<WebhookUrlRejectedException>(() => SsrfGuard.ValidateUrl(new Uri(url), allowInsecure: false));

    [Fact]
    public async Task Hostname_resolving_to_loopback_is_blocked_at_connect_time()
    {
        // "localhost" passa na validação de texto, mas resolve para 127.0.0.1: o ConnectCallback barra (DNS rebinding).
        using var http = new HttpClient(SsrfGuard.CreateHandler(allowInsecure: false));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(new Uri("https://localhost:8443/")));
        Assert.IsType<WebhookUrlRejectedException>(ex.InnerException);
    }

    [Fact]
    public void Signature_verification_accepts_any_active_secret_and_rejects_old_timestamp()
    {
        var body = "{\"a\":1}"u8.ToArray();
        var now = DateTimeOffset.UtcNow;
        var t = now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        string Sig(string secret) => Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(t + ".").Concat(body).ToArray()));
        var header = $"t={t},v1={Sig("novo")},v1={Sig("antigo")}";
        Assert.True(WebhookSignature.Verify(header, body, "antigo", TimeSpan.FromMinutes(5), now));
        Assert.True(WebhookSignature.Verify(header, body, "novo", TimeSpan.FromMinutes(5), now));
        Assert.False(WebhookSignature.Verify(header, body, "outro", TimeSpan.FromMinutes(5), now));
        Assert.False(WebhookSignature.Verify(header, body, "novo", TimeSpan.FromMinutes(5), now.AddMinutes(10)));
    }
}

/// <summary>Fair-share no banco (D-602): K por tenant, rodízio, prioridade, lease vencido volta para a fila.</summary>
[Collection(PostgresCollection.Name)]
public sealed class FairShareTests(PostgresFixture db)
{
    [Fact]
    public async Task Claim_respects_per_tenant_limit_round_robin_and_lease_expiry()
    {
        await using var su = new NpgsqlConnection(db.SuperuserConnection);
        await su.OpenAsync();
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        await using (var setup = new NpgsqlCommand($"""
            UPDATE batch_chunks SET status = 'DONE' WHERE status <> 'DONE';   -- a semente da suíte de RLS sai da fila durante o teste
            INSERT INTO batches (id, tenant_id, status, storage_key) VALUES ('{a}', '{PostgresFixture.TenantA}', 'QUEUED', 'k'), ('{b}', '{PostgresFixture.TenantB}', 'QUEUED', 'k');
            INSERT INTO batch_chunks (tenant_id, batch_id, chunk_no, priority, storage_key, docs)
              SELECT '{PostgresFixture.TenantA}', '{a}', n, 2, 'a' || n, 500 FROM generate_series(1, 100) n;
            INSERT INTO batch_chunks (tenant_id, batch_id, chunk_no, priority, storage_key, docs)
              SELECT '{PostgresFixture.TenantB}', '{b}', n, 2, 'b' || n, 500 FROM generate_series(1, 3) n;
            """, su))
        {
            await setup.ExecuteNonQueryAsync();
        }

        await using var worker = NpgsqlDataSource.Create(db.ConnectionFor("tg_worker"));
        async Task<List<(Guid Tenant, int Chunk)>> Claim(int limit, int k, int lease = 600)
        {
            await using var cmd = worker.CreateCommand("SELECT tenant_id, chunk_no FROM app.claim_batch_chunks($1, $2, $3, 3)");
            cmd.Parameters.Add(new() { Value = limit });
            cmd.Parameters.Add(new() { Value = k });
            cmd.Parameters.Add(new() { Value = lease });
            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<(Guid, int)>();
            while (await r.ReadAsync())
            {
                list.Add((r.GetGuid(0), r.GetInt32(1)));
            }

            return list;
        }

        // Um lote de 100 chunks não monopoliza: com K=2, cada tenant recebe 2.
        var first = await Claim(10, 2);
        Assert.Equal(2, first.Count(x => x.Tenant == PostgresFixture.TenantA));
        Assert.Equal(2, first.Count(x => x.Tenant == PostgresFixture.TenantB));
        Assert.Empty(await Claim(10, 2));   // os dois tenants estão no limite

        // Concluir um chunk de A libera exatamente uma vaga para A.
        await using (var done = new NpgsqlCommand($"UPDATE batch_chunks SET status = 'DONE' WHERE batch_id = '{a}' AND chunk_no = {first.First(x => x.Tenant == PostgresFixture.TenantA).Chunk}", su))
        {
            await done.ExecuteNonQueryAsync();
        }

        var next = await Claim(10, 2);
        Assert.Equal([PostgresFixture.TenantA], next.Select(x => x.Tenant));

        // Lease vencido (worker morreu): o chunk volta para a fila.
        await using (var expire = new NpgsqlCommand("UPDATE batch_chunks SET lease_until = now() - interval '1 s' WHERE status = 'RUNNING'", su))
        {
            await expire.ExecuteNonQueryAsync();
        }

        Assert.Equal(4, (await Claim(10, 2)).Count);

        // tg_app não pode reservar chunks (só os workers).
        await using var app = NpgsqlDataSource.Create(db.ConnectionFor("tg_app"));
        await using var denied = app.CreateCommand("SELECT * FROM app.claim_batch_chunks(1, 1, 1, 3)");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => denied.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);

        await using var cleanup = new NpgsqlCommand($"DELETE FROM batch_chunks WHERE batch_id IN ('{a}', '{b}'); DELETE FROM batches WHERE id IN ('{a}', '{b}'); UPDATE batch_chunks SET status = 'QUEUED', lease_until = NULL;", su);
        await cleanup.ExecuteNonQueryAsync();
    }
    /// <summary>D-P08: tentativa como cerca, teto de tentativas (chunk envenenado vira FAILED e conta como falha) e
    /// reconciliador que devolve o lote para conclusão.</summary>
    [Fact]
    public async Task Poison_chunk_fails_after_max_attempts_and_batch_is_returned_for_finalization()
    {
        await using var su = new NpgsqlConnection(db.SuperuserConnection);
        await su.OpenAsync();
        var a = Guid.CreateVersion7();
        await using (var setup = new NpgsqlCommand($"""
            UPDATE batch_chunks SET status = 'DONE' WHERE status <> 'DONE';
            INSERT INTO batches (id, tenant_id, status, storage_key, total_docs, total_chunks) VALUES ('{a}', '{PostgresFixture.TenantA}', 'QUEUED', 'k', 7, 2);
            INSERT INTO batch_chunks (tenant_id, batch_id, chunk_no, priority, storage_key, docs, first_doc)
              VALUES ('{PostgresFixture.TenantA}', '{a}', 1, 2, 'p1', 4, 0), ('{PostgresFixture.TenantA}', '{a}', 2, 2, 'p2', 3, 4);
            """, su))
        {
            await setup.ExecuteNonQueryAsync();
        }

        await using var worker = NpgsqlDataSource.Create(db.ConnectionFor("tg_worker"));
        async Task<List<(int Chunk, int Attempt)>> Claim()
        {
            await using var cmd = worker.CreateCommand("SELECT chunk_no, attempt FROM app.claim_batch_chunks(10, 5, 600, 2)");
            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<(int, int)>();
            while (await r.ReadAsync())
            {
                list.Add((r.GetInt32(0), r.GetInt32(1)));
            }

            return list;
        }

        async Task Expire()
        {
            await using var e = new NpgsqlCommand($"UPDATE batch_chunks SET lease_until = now() - interval '1 s' WHERE batch_id = '{a}' AND status = 'RUNNING'", su);
            await e.ExecuteNonQueryAsync();
        }

        Assert.Equal([(1, 1), (2, 1)], (await Claim()).OrderBy(x => x.Chunk));
        // O chunk 2 termina com a tentativa 1; o chunk 1 trava (worker morre) duas vezes.
        await using (var done = new NpgsqlCommand($"UPDATE batch_chunks SET status = 'DONE' WHERE batch_id = '{a}' AND chunk_no = 2 AND attempts = 1", su))
        {
            Assert.Equal(1, await done.ExecuteNonQueryAsync());
        }

        await Expire();
        Assert.Equal([(1, 2)], await Claim());     // a tentativa sobe: é a cerca para o worker antigo
        await using (var stale = new NpgsqlCommand($"UPDATE batch_chunks SET status = 'DONE' WHERE batch_id = '{a}' AND chunk_no = 1 AND status = 'RUNNING' AND attempts = 1", su))
        {
            Assert.Equal(0, await stale.ExecuteNonQueryAsync());   // o worker da tentativa 1 não grava mais
        }

        await Expire();
        Assert.Empty(await Claim());               // esgotou as 2 tentativas: não volta à fila

        await using var fin = worker.CreateCommand("SELECT batch_id FROM app.batches_to_finalize(2)");
        Assert.Equal(a, (Guid)(await fin.ExecuteScalarAsync())!);
        await using (var check = new NpgsqlCommand($"SELECT c.status, b.failed_docs, b.done_chunks FROM batch_chunks c JOIN batches b ON b.id = c.batch_id WHERE c.batch_id = '{a}' AND c.chunk_no = 1", su))
        await using (var r = await check.ExecuteReaderAsync())
        {
            await r.ReadAsync();
            Assert.Equal("FAILED", r.GetString(0));
            Assert.Equal(4, r.GetInt32(1));        // os 4 documentos do chunk envenenado contam como falha
            Assert.Equal(1, r.GetInt32(2));
        }

        await using var cleanup = new NpgsqlCommand($"DELETE FROM batch_chunks WHERE batch_id = '{a}'; DELETE FROM batches WHERE id = '{a}'; UPDATE batch_chunks SET status = 'QUEUED', lease_until = NULL;", su);
        await cleanup.ExecuteNonQueryAsync();
    }
}
