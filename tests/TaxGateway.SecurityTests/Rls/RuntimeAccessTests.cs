using Npgsql;

using TaxGateway.Core.Tenancy;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.SecurityTests.Rls;

/// <summary>Componentes de runtime (TenantDb, ApiKeyLookup, SqlMigrator) contra o banco real.</summary>
[Collection(PostgresCollection.Name)]
public sealed class RuntimeAccessTests(PostgresFixture db)
{
    [Fact]
    public async Task TenantDb_sets_context_and_it_does_not_leak_to_the_pooled_connection()
    {
        // Pool de 1 conexão: a mesma conexão física é reutilizada nas três etapas.
        var cs = new NpgsqlConnectionStringBuilder(db.ConnectionFor("tg_app")) { Pooling = true, MaxPoolSize = 1, NoResetOnClose = true };
        await using var ds = NpgsqlDataSource.Create(cs.ConnectionString);
        var tenantDb = new TenantDb(ds);

        var seen = await tenantDb.InTenantAsync(new TenantId(PostgresFixture.TenantA), async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("SELECT array_agg(DISTINCT tenant_id) FROM tenant_cnpjs");
            return (Guid[])(await cmd.ExecuteScalarAsync(ct))!;
        });
        Assert.Equal([PostgresFixture.TenantA], seen);

        // Falha dentro da unidade de trabalho: ROLLBACK, e a conexão volta limpa ao pool.
        await Assert.ThrowsAsync<InvalidOperationException>(() => tenantDb.InTenantAsync<int>(new TenantId(PostgresFixture.TenantB),
            (_, _) => throw new InvalidOperationException("falha simulada")));

        await using var raw = await ds.OpenConnectionAsync();
        await using var check = new NpgsqlCommand("SELECT coalesce(current_setting('app.current_tenant_id', true), ''), (SELECT count(*) FROM tenant_cnpjs)", raw);
        await using var reader = await check.ExecuteReaderAsync();
        await reader.ReadAsync();
        Assert.Equal("", reader.GetString(0));
        Assert.Equal(0, reader.GetInt64(1));
    }

    [Fact]
    public async Task TenantScope_cannot_be_used_after_the_unit_of_work()
    {
        await using var ds = NpgsqlDataSource.Create(db.ConnectionFor("tg_app"));
        TenantScope? leaked = null;
        await new TenantDb(ds).InTenantAsync(new TenantId(PostgresFixture.TenantA), (s, _) => { leaked = s; return Task.FromResult(0); });
        Assert.Throws<ObjectDisposedException>(() => leaked!.CreateCommand("SELECT 1"));
    }

    [Fact]
    public void TenantId_rejects_empty_guid() =>
        Assert.Throws<ArgumentException>(() => new TenantId(Guid.Empty));

    [Fact]
    public async Task ApiKeyLookup_resolves_only_active_keys_and_only_tg_app_can_call_it()
    {
        await using var ds = NpgsqlDataSource.Create(db.ConnectionFor("tg_app"));
        var lookup = new ApiKeyLookup(ds);
        var hashA = System.Security.Cryptography.SHA256.HashData("key1"u8);   // seed: sha256('key' || n)

        var found = await lookup.ResolveAsync(hashA);
        Assert.NotNull(found);
        Assert.Equal(PostgresFixture.TenantA, found.Tenant.Value);
        Assert.Equal("STARTER", found.PlanCode);
        Assert.Equal("ACTIVE", found.TenantStatus);
        Assert.Null(await lookup.ResolveAsync(new byte[32]));

        // Revogada ou expirada: não resolve (alteração feita e desfeita como superusuário).
        await using (var su = new NpgsqlConnection(db.SuperuserConnection))
        {
            await su.OpenAsync();
            await using var tx = await su.BeginTransactionAsync();
            await using var revoke = new NpgsqlCommand("UPDATE api_keys SET expires_at = now() - interval '1 s' WHERE key_hash = $1", su, tx);
            revoke.Parameters.Add(new() { Value = hashA });
            await revoke.ExecuteNonQueryAsync();
            await using (var setRole = new NpgsqlCommand("SET LOCAL ROLE tg_app", su, tx))
            {
                await setRole.ExecuteNonQueryAsync();
            }

            await using var call = new NpgsqlCommand("SELECT count(*) FROM app.resolve_api_key($1)", su, tx);
            call.Parameters.Add(new() { Value = hashA });
            Assert.Equal(0L, await call.ExecuteScalarAsync());
        }

        // Fora da função, tg_app sem contexto não lê api_keys; tg_worker não pode chamar a função.
        await using (var cmd = ds.CreateCommand("SELECT count(*) FROM api_keys"))
        {
            Assert.Equal(0L, await cmd.ExecuteScalarAsync());
        }

        await using var worker = NpgsqlDataSource.Create(db.ConnectionFor("tg_worker"));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => new ApiKeyLookup(worker).ResolveAsync(hashA));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Migrator_is_idempotent_and_detects_tampered_migrations()
    {
        await using var owner = NpgsqlDataSource.Create(db.ConnectionFor("tg_owner"));
        var migrator = new SqlMigrator(owner);
        var embedded = SqlMigrator.LoadEmbedded();

        Assert.Empty(await migrator.MigrateAsync(embedded));

        var tampered = embedded.Select(m => m.Version == 1 ? SqlMigrator.Create(1, m.Name, m.Sql + "\n-- alterada") : m).ToList();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => migrator.MigrateAsync(tampered));
        Assert.Contains("Checksum divergente em V0001", ex.Message, StringComparison.Ordinal);

        var missing = embedded.Take(embedded.Count - 1).ToList();
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => migrator.MigrateAsync(missing));
        Assert.Contains("banco à frente do código", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// D-P27: migration presa atrás de um lock desiste em lock_timeout (sem enfileirar o tráfego da tabela atrás dela),
    /// tenta de novo e, persistindo, falha sem registrar nada.
    /// </summary>
    [Fact]
    public async Task Migration_gives_up_on_lock_timeout_instead_of_queueing_traffic()
    {
        await using var owner = NpgsqlDataSource.Create(db.ConnectionFor("tg_owner"));
        var embedded = SqlMigrator.LoadEmbedded();
        var next = embedded.Count + 1;
        var blocked = embedded.Append(SqlMigrator.Create(next, "teste_lock", "ALTER TABLE plans ADD COLUMN zz_lock_test INT;")).ToList();

        await using var holder = new NpgsqlConnection(db.SuperuserConnection);
        await holder.OpenAsync();
        await using var tx = await holder.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand("LOCK TABLE plans IN ACCESS SHARE MODE", holder, tx))
        {
            await lockCmd.ExecuteNonQueryAsync();
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            new SqlMigrator(owner, TimeSpan.FromMilliseconds(200), lockRetries: 2).MigrateAsync(blocked));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, ex.SqlState);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"demorou {sw.Elapsed}");
        await tx.RollbackAsync();

        await using var check = owner.CreateCommand($"SELECT count(*) FROM migrations.history WHERE version = {next}");
        Assert.Equal(0L, (long)(await check.ExecuteScalarAsync())!);
    }

    [Fact]
    public void Embedded_migrations_are_sequential_and_crlf_insensitive()
    {
        var embedded = SqlMigrator.LoadEmbedded();
        Assert.Equal(Enumerable.Range(1, embedded.Count), embedded.Select(m => m.Version));
        Assert.Equal(SqlMigrator.Create(1, "x", "a\nb").Checksum, SqlMigrator.Create(1, "x", "a\r\nb").Checksum);
    }
}
