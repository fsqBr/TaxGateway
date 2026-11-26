using Npgsql;

namespace TaxGateway.SecurityTests.Rls;

/// <summary>
/// Critério de saída da Fase 1 (SDD §12): isolamento provado em TODAS as tabelas com tenant_id,
/// para leitura e escrita, nos dois roles de runtime por tenant.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RlsIsolationTests(PostgresFixture db)
{
    private static readonly string[] RuntimeTenantRoles = ["tg_app", "tg_worker"];
    private static readonly string[] AllRuntimeRoles = ["tg_app", "tg_worker", "tg_billing", "tg_relay", "tg_auth", "tg_admin", "tg_scheduler"];

    public static TheoryData<string, string> TableRoleMatrix()
    {
        var data = new TheoryData<string, string>();
        foreach (var table in Seed.TenantTables.Keys.Append("tenants"))
        {
            foreach (var role in RuntimeTenantRoles)
            {
                data.Add(table, role);
            }
        }

        return data;
    }

    [Fact]
    public async Task Every_table_with_tenant_id_is_covered_and_has_forced_rls_and_isolation_policy()
    {
        await using var conn = await OpenAsync(db.SuperuserConnection);
        var rows = await QueryAsync(conn, """
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity,
                   EXISTS (SELECT 1 FROM pg_policies p
                            WHERE p.schemaname = 'public' AND p.tablename = c.relname AND p.policyname = 'tenant_isolation'
                              AND p.roles @> ARRAY['tg_app','tg_worker']::name[]
                              AND p.cmd = 'ALL' AND p.qual IS NOT NULL AND p.with_check IS NOT NULL)
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public'
             WHERE c.relkind IN ('r','p') AND NOT c.relispartition
               AND EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped)
            """, r => (Table: r.GetString(0), Enabled: r.GetBoolean(1), Forced: r.GetBoolean(2), Policy: r.GetBoolean(3)));

        Assert.Equal(Seed.TenantTables.Keys.Order(), rows.Select(r => r.Table).Order());
        Assert.All(rows, r =>
        {
            Assert.True(r.Enabled, $"{r.Table}: RLS não habilitado");
            Assert.True(r.Forced, $"{r.Table}: RLS sem FORCE");
            Assert.True(r.Policy, $"{r.Table}: sem política tenant_isolation (USING + WITH CHECK) para tg_app/tg_worker");
        });

        var tenants = await QueryAsync(conn, "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid = 'public.tenants'::regclass", r => r.GetBoolean(0));
        Assert.True(tenants.Single(), "tenants: RLS sem ENABLE/FORCE");
    }

    [Fact]
    public async Task No_partition_grants_privileges_to_runtime_roles()
    {
        await using var conn = await OpenAsync(db.SuperuserConnection);
        var leaks = await QueryAsync(conn, """
            SELECT c.relname || ' -> ' || r.rolname
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public'
             CROSS JOIN pg_roles r
             WHERE c.relispartition
               AND r.rolname = ANY(@roles)
               AND has_table_privilege(r.oid, c.oid, 'SELECT,INSERT,UPDATE,DELETE')
            """, r => r.GetString(0), ("roles", AllRuntimeRoles));
        Assert.Empty(leaks);

        var partitions = await QueryAsync(conn, "SELECT count(*) FROM pg_class WHERE relispartition AND relnamespace = 'public'::regnamespace", r => r.GetInt64(0));
        Assert.True(partitions.Single() >= 3 * 6, "esperadas partições mensais + DEFAULT nas 3 tabelas particionadas");
    }

    [Fact]
    public async Task No_runtime_role_bypasses_rls()
    {
        await using var conn = await OpenAsync(db.SuperuserConnection);
        var bypass = await QueryAsync(conn, "SELECT rolname FROM pg_roles WHERE rolname = ANY(@roles) AND (rolbypassrls OR rolsuper)",
            r => r.GetString(0), ("roles", AllRuntimeRoles));
        Assert.Empty(bypass);
    }

    [Theory]
    [MemberData(nameof(TableRoleMatrix))]
    public async Task Read_sees_only_own_tenant(string table, string role)
    {
        var column = table == "tenants" ? "id" : "tenant_id";
        var result = await AsTenantAsync(role, PostgresFixture.TenantA, async (conn, tx) =>
        {
            if (!await HasPrivilegeAsync(conn, tx, table, "SELECT"))
            {
                return (Own: -1L, Other: -1L);
            }

            var own = await ScalarAsync<long>(conn, tx, $"SELECT count(*) FROM {table} WHERE {column} = @a");
            var all = await ScalarAsync<long>(conn, tx, $"SELECT count(*) FROM {table}");
            return (Own: own, Other: all - own);
        });

        if (result.Own == -1)
        {
            return; // sem GRANT de SELECT: isolado pelo privilégio
        }

        Assert.True(result.Own >= 1, $"{role} não enxerga a própria linha em {table}");
        Assert.Equal(0, result.Other);
    }

    [Theory]
    [MemberData(nameof(TableRoleMatrix))]
    public async Task Insert_with_other_tenant_id_is_rejected(string table, string role)
    {
        if (table == "tenants")
        {
            return; // nenhum role de runtime tem INSERT em tenants (coberto pelo teste de privilégios)
        }

        // Copia a linha do tenant B (como superusuário) e tenta inseri-la no contexto do tenant A.
        string json;
        await using (var su = await OpenAsync(db.SuperuserConnection))
        {
            json = (await QueryAsync(su, $"SELECT to_jsonb(x)::text FROM {table} x WHERE tenant_id = @b LIMIT 1",
                r => r.GetString(0), ("b", PostgresFixture.TenantB))).Single();
        }

        var error = await CaptureAsync(() => AsTenantAsync(role, PostgresFixture.TenantA, (conn, tx) =>
            ExecAsync(conn, tx, $"INSERT INTO {table} OVERRIDING SYSTEM VALUE SELECT * FROM jsonb_populate_record(NULL::{table}, @j::jsonb)",
                ("j", json))));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error);   // RLS (WITH CHECK) ou falta de GRANT: ambos 42501
    }

    [Theory]
    [MemberData(nameof(TableRoleMatrix))]
    public async Task Update_and_delete_cannot_reach_other_tenant(string table, string role)
    {
        var column = table == "tenants" ? "id" : "tenant_id";
        foreach (var (op, sql) in new[]
                 {
                     ("UPDATE", $"UPDATE {table} SET {column} = {column} WHERE {column} = @b"),
                     ("DELETE", $"DELETE FROM {table} WHERE {column} = @b"),
                 })
        {
            var affected = -1;
            var error = await CaptureAsync(async () => affected = await AsTenantAsync(role, PostgresFixture.TenantA,
                (conn, tx) => ExecAsync(conn, tx, sql, ("b", PostgresFixture.TenantB))));
            Assert.True(error == PostgresErrorCodes.InsufficientPrivilege || (error is null && affected == 0),
                $"{role} {op} em {table} atingiu {affected} linha(s) do outro tenant (erro: {error})");
        }
    }

    [Theory]
    [MemberData(nameof(TableRoleMatrix))]
    public async Task Update_cannot_move_own_row_to_other_tenant(string table, string role)
    {
        if (table == "tenants")
        {
            return;
        }

        var affected = -1;
        var error = await CaptureAsync(async () => affected = await AsTenantAsync(role, PostgresFixture.TenantA,
            (conn, tx) => ExecAsync(conn, tx, $"UPDATE {table} SET tenant_id = @b WHERE tenant_id = @a", ("b", PostgresFixture.TenantB))));
        Assert.True(error == PostgresErrorCodes.InsufficientPrivilege,
            $"{role} moveu {affected} linha(s) de {table} para outro tenant (erro: {error})");
    }

    [Theory]
    [InlineData("tg_app")]
    [InlineData("tg_worker")]
    public async Task Without_tenant_context_queries_return_zero_rows_without_error(string role)
    {
        await using var conn = await OpenAsync(db.ConnectionFor(role));
        await using var tx = await conn.BeginTransactionAsync();
        Assert.Equal(0, await ScalarAsync<long>(conn, tx, "SELECT count(*) FROM tenant_cnpjs"));
        await ExecAsync(conn, tx, "SELECT set_config('app.current_tenant_id', '', true)");
        Assert.Equal(0, await ScalarAsync<long>(conn, tx, "SELECT count(*) FROM tenant_cnpjs"));
        Assert.Equal(0, await ScalarAsync<long>(conn, tx, "SELECT count(*) FROM tenants"));
    }

    // ---------- Roles cross-tenant nomeados ----------

    [Fact]
    public async Task Billing_reads_usage_of_all_tenants_but_nothing_else()
    {
        await using var conn = await OpenAsync(db.ConnectionFor("tg_billing"));
        await using var tx = await conn.BeginTransactionAsync();
        Assert.Equal(2, await ScalarAsync<long>(conn, tx, "SELECT count(DISTINCT tenant_id) FROM usage_ledger"));
        Assert.Equal(2, await ScalarAsync<long>(conn, tx, "SELECT count(DISTINCT tenant_id) FROM cnpj_monthly_activity"));
        await tx.RollbackAsync();

        foreach (var table in new[] { "fiscal_documents", "simulations", "api_keys", "outbox", "certificates" })
        {
            var error = await CaptureAsync(async () =>
            {
                await using var c = await OpenAsync(db.ConnectionFor("tg_billing"));
                await ScalarAsync<long>(c, null, $"SELECT count(*) FROM {table}");
            });
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error);
        }

        var ledgerWrite = await CaptureAsync(async () =>
        {
            await using var c = await OpenAsync(db.ConnectionFor("tg_billing"));
            await ExecAsync(c, null, "DELETE FROM usage_ledger");
        });
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ledgerWrite);
    }

    [Fact]
    public async Task Relay_reads_all_outbox_and_can_only_mark_published()
    {
        await using var conn = await OpenAsync(db.ConnectionFor("tg_relay"));
        await using var tx = await conn.BeginTransactionAsync();
        Assert.Equal(2, await ScalarAsync<long>(conn, tx, "SELECT count(DISTINCT tenant_id) FROM outbox"));
        Assert.Equal(2, await ExecAsync(conn, tx, "UPDATE outbox SET published_at = now()"));
        await tx.RollbackAsync();

        var changePayload = await CaptureAsync(async () =>
        {
            await using var c = await OpenAsync(db.ConnectionFor("tg_relay"));
            await ExecAsync(c, null, "UPDATE outbox SET payload = '{}'");
        });
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, changePayload);

        var readOther = await CaptureAsync(async () =>
        {
            await using var c = await OpenAsync(db.ConnectionFor("tg_relay"));
            await ScalarAsync<long>(c, null, "SELECT count(*) FROM fiscal_documents");
        });
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, readOther);
    }

    [Fact]
    public async Task Usage_ledger_is_append_only_for_runtime_roles()
    {
        foreach (var role in RuntimeTenantRoles)
        {
            foreach (var sql in new[] { "UPDATE usage_ledger SET quantity = 2", "DELETE FROM usage_ledger" })
            {
                var error = await CaptureAsync(() => AsTenantAsync(role, PostgresFixture.TenantA, (c, t) => ExecAsync(c, t, sql)));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error);
            }
        }
    }

    // ---------- Partição DEFAULT ----------

    [Fact]
    public async Task Insert_outside_partition_window_goes_to_default_and_is_reported()
    {
        var reported = await AsTenantAsync("tg_app", PostgresFixture.TenantA, async (conn, tx) =>
        {
            await ExecAsync(conn, tx, """
                INSERT INTO simulations (id, billing_month, tenant_id, cnpj_subject, engine, engine_bundle, param_snapshot_id, request, result)
                VALUES (gen_random_uuid(), '2099-01-01', @a, 'AB12CD34EF5601', 'regular', 'x', 1, '{}', '{}')
                """);
            var rows = await QueryAsync(conn, "SELECT parent_table, row_count FROM app.default_partition_rows()",
                r => (Table: r.GetString(0), Rows: r.GetInt64(1)), tx: tx);
            await tx.RollbackAsync();
            return rows;
        }, commit: false);

        Assert.Equal(1, reported.Single(r => r.Table == "simulations").Rows);
        Assert.All(reported.Where(r => r.Table != "simulations"), r => Assert.Equal(0, r.Rows));
    }

    // ---------- Infra de teste ----------

    private async Task<T> AsTenantAsync<T>(string role, Guid tenant, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, bool commit = false)
    {
        await using var conn = await OpenAsync(db.ConnectionFor(role));
        await using var tx = await conn.BeginTransactionAsync();
        await ExecAsync(conn, tx, "SELECT set_config('app.current_tenant_id', @ctx::text, true)", ("ctx", tenant));
        var result = await work(conn, tx);
        if (commit)
        {
            await tx.CommitAsync();
        }

        return result; // sem commit: DisposeAsync faz ROLLBACK e o seed não muda entre testes
    }

    private static async Task<string?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    private static async Task<bool> HasPrivilegeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string table, string privilege) =>
        await ScalarAsync<bool>(conn, tx, $"SELECT has_table_privilege(current_user, 'public.{table}', '{privilege}')");

    private static async Task<NpgsqlConnection> OpenAsync(string cs)
    {
        var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        return conn;
    }

    private static NpgsqlCommand Command(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, (string Name, object Value)[] extra)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        if (sql.Contains("@a", StringComparison.Ordinal))
        {
            cmd.Parameters.AddWithValue("a", PostgresFixture.TenantA);
        }

        foreach (var (name, value) in extra)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return cmd;
    }

    private static async Task<int> ExecAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, params (string Name, object Value)[] p)
    {
        await using var cmd = Command(conn, tx, sql, p);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, params (string Name, object Value)[] p)
    {
        await using var cmd = Command(conn, tx, sql, p);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<List<T>> QueryAsync<T>(NpgsqlConnection conn, string sql, Func<NpgsqlDataReader, T> map,
        (string Name, object Value)? p = null, NpgsqlTransaction? tx = null)
    {
        await using var cmd = Command(conn, tx, sql, p is null ? [] : [p.Value]);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<T>();
        while (await reader.ReadAsync())
        {
            list.Add(map(reader));
        }

        return list;
    }
}
