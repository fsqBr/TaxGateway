using Npgsql;

namespace TaxGateway.SecurityTests.Rls;

/// <summary>Fase 2: role tg_admin (D-203), histórico do catálogo (D-204), snapshot (D-205) e carga inicial (D-206).</summary>
[Collection(PostgresCollection.Name)]
public sealed class AdminAndParamsTests(PostgresFixture db)
{
    private async Task<NpgsqlConnection> OpenAsync(string role)
    {
        var c = new NpgsqlConnection(db.ConnectionFor(role));
        await c.OpenAsync();
        return c;
    }

    [Theory]
    [InlineData("SELECT count(*) FROM fiscal_documents")]
    [InlineData("SELECT count(*) FROM simulations")]
    [InlineData("SELECT count(*) FROM usage_ledger")]
    [InlineData("SELECT count(*) FROM api_keys")]
    [InlineData("SELECT count(*) FROM certificates")]
    [InlineData("SELECT count(*) FROM tenant_cnpjs")]
    [InlineData("UPDATE plans SET rpm = 1")]
    [InlineData("DELETE FROM rate_catalog")]
    [InlineData("UPDATE rate_catalog SET aliquota = 99")]
    [InlineData("DELETE FROM param_changes")]
    [InlineData("SELECT secret_hash FROM oauth_applications")]   // V0008: o backoffice não lê o hash do segredo
    [InlineData("SELECT scopes FROM oauth_applications")]
    [InlineData("DELETE FROM oauth_applications")]
    public async Task Admin_cannot_read_tenant_data_nor_edit_rates_in_place(string sql)
    {
        await using var c = await OpenAsync("tg_admin");
        await using var cmd = new NpgsqlCommand(sql, c);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    private static readonly string[] OverviewKeys = ["\"cnpjs\"", "\"api_keys\"", "\"consumo\"", "\"faturas\""];

    /// <summary>
    /// D-J07/V0018: o backoffice vê CNPJs, chaves, consumo e faturas de UM cliente pela função, com campos fixos e sem
    /// segredo — sem ganhar leitura das tabelas (o teste acima continua valendo).
    /// </summary>
    [Fact]
    public async Task Admin_sees_tenant_overview_only_through_the_function_without_secrets()
    {
        await using var c = await OpenAsync("tg_admin");
        await using var cmd = new NpgsqlCommand("SELECT app.admin_tenant_overview(@t)::text", c);
        cmd.Parameters.AddWithValue("t", PostgresFixture.TenantA);
        var json = (string)(await cmd.ExecuteScalarAsync())!;
        foreach (var key in OverviewKeys)
        {
            Assert.Contains(key, json, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("hash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pfx", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Seed_loads_2026_2027_rates_with_provisional_cbs_and_traceability()
    {
        await using var c = await OpenAsync("tg_admin");   // tg_app não lê param_changes
        await using var cmd = new NpgsqlCommand("""
            SELECT tributo, aliquota, valid_from, provisoria, fonte
              FROM rate_catalog r JOIN param_changes p ON p.id = r.change_id
             WHERE superseded_at IS NULL ORDER BY valid_from, tributo
            """, c);
        var rows = new List<(string, decimal, DateOnly, bool, string)>();
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                rows.Add((r.GetString(0), r.GetDecimal(1), r.GetFieldValue<DateOnly>(2), r.GetBoolean(3), r.GetString(4)));
            }
        }

        Assert.Contains(("CBS", 0.9m, new DateOnly(2026, 1, 1), false, "LEI_FEDERAL"), rows);
        Assert.Contains(("IBS_UF", 0.05m, new DateOnly(2027, 1, 1), false, "LEI_FEDERAL"), rows);
        Assert.Contains(("IBS_MUN", 0.05m, new DateOnly(2027, 1, 1), false, "LEI_FEDERAL"), rows);
        Assert.Contains(("CBS", 11.2m, new DateOnly(2027, 1, 1), true, "TCU"), rows);
    }

    [Fact]
    public async Task Rate_history_is_kept_and_overlap_only_blocks_active_rows()
    {
        await using var su = new NpgsqlConnection(db.SuperuserConnection);
        await su.OpenAsync();
        await using var tx = await su.BeginTransactionAsync();
        await Exec(su, tx, "SET LOCAL ROLE tg_admin");
        var change = Guid.CreateVersion7();
        await Exec(su, tx, $"""
            INSERT INTO param_changes (id, domain, scope, payload, justification, proposed_by, status, effective_at)
            VALUES ('{change}', 'RATE_CATALOG', 'GLOBAL', '{"{}"}', 'Senado fixou a CBS', 'fiscal-a', 'PROPOSED', now())
            """);

        // Sobrepor a linha ativa sem superá-la é recusado...
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(su, tx, InsertCbs(change)));
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, ex.SqlState);
    }

    [Fact]
    public async Task Superseding_keeps_old_row_and_accepts_new_one()
    {
        await using var su = new NpgsqlConnection(db.SuperuserConnection);
        await su.OpenAsync();
        await using var tx = await su.BeginTransactionAsync();
        await Exec(su, tx, "SET LOCAL ROLE tg_admin");
        var change = Guid.CreateVersion7();
        await Exec(su, tx, $"""
            INSERT INTO param_changes (id, domain, scope, payload, justification, proposed_by, status, effective_at)
            VALUES ('{change}', 'RATE_CATALOG', 'GLOBAL', '{"{}"}', 'Senado fixou a CBS', 'fiscal-a', 'PROPOSED', now())
            """);
        await Exec(su, tx, $"""
            UPDATE rate_catalog SET superseded_by = '{change}', superseded_at = now()
             WHERE tributo = 'CBS' AND valid_from = '2027-01-01' AND superseded_at IS NULL
            """);
        await Exec(su, tx, InsertCbs(change));
        await Exec(su, tx, $"UPDATE param_changes SET approved_by = 'fiscal-b', status = 'APPLIED' WHERE id = '{change}'");

        await using var count = new NpgsqlCommand("SELECT count(*) FILTER (WHERE superseded_at IS NULL), count(*) FROM rate_catalog WHERE tributo = 'CBS' AND valid_from = '2027-01-01'", su, tx);
        await using var r = await count.ExecuteReaderAsync();
        await r.ReadAsync();
        Assert.Equal(1, r.GetInt64(0));
        Assert.Equal(2, r.GetInt64(1));   // a provisória continua no histórico
        await r.DisposeAsync();
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Same_person_cannot_approve_own_change()
    {
        await using var su = new NpgsqlConnection(db.SuperuserConnection);
        await su.OpenAsync();
        await using var tx = await su.BeginTransactionAsync();
        await Exec(su, tx, "SET LOCAL ROLE tg_admin");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(su, tx, $"""
            INSERT INTO param_changes (id, domain, scope, payload, justification, proposed_by, approved_by, status, effective_at)
            VALUES ('{Guid.CreateVersion7()}', 'RATE_CATALOG', 'GLOBAL', '{"{}"}', 'x', 'fiscal-a', 'fiscal-a', 'APPLIED', now())
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Param_snapshot_registration_is_idempotent_and_validated()
    {
        await using var c = await OpenAsync("tg_app");
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray()));
        await using var cmd = new NpgsqlCommand("SELECT app.register_param_snapshot($1)", c);
        cmd.Parameters.Add(new() { Value = sha });
        var first = (long)(await cmd.ExecuteScalarAsync())!;
        var second = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(first, second);

        await using var bad = new NpgsqlCommand("SELECT app.register_param_snapshot('nao-e-sha')", c);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => bad.ExecuteScalarAsync());
        Assert.Equal("22023", ex.SqlState);

        await using var direct = new NpgsqlCommand($"INSERT INTO param_snapshots (content_sha256) VALUES ('{new string('a', 64)}')", c);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => direct.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    private static string InsertCbs(Guid change) => $"""
        INSERT INTO rate_catalog (id, tributo, tipo, esfera, aliquota, valid_from, valid_to, ato_legal, fonte, change_id)
        VALUES ('{Guid.CreateVersion7()}', 'CBS', 'REFERENCIA', 'UNIAO', 11.1, '2027-01-01', '2028-01-01', 'Resolução do Senado', 'SENADO', '{change}')
        """;

    [Theory]
    [InlineData("UPDATE oauth_applications SET revoked_at = NULL")]
    [InlineData("INSERT INTO oauth_applications (client_id, tenant_id) VALUES ('x', gen_random_uuid())")]
    [InlineData("SELECT app.resolve_oauth_client('x')")]
    public async Task Fase8_app_nao_altera_clientes_oauth_e_worker_nao_resolve_cliente(string sql)
    {
        // tg_app só lê clientes do próprio tenant (RLS) e chama a função; tg_worker nem isso (D-801).
        var role = sql.StartsWith("SELECT app.", StringComparison.Ordinal) ? "tg_worker" : "tg_app";
        await using var c = await OpenAsync(role);
        await using var cmd = new NpgsqlCommand(sql, c);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Theory]
    [InlineData("tg_app", "UPDATE dfe_sync_state SET ult_nsu = 0")]               // só o worker avança o NSU (D-903)
    [InlineData("tg_app", "UPDATE dfe_sync_state SET window_count = 0")]          // nem zera o teto de 20/h
    [InlineData("tg_app", "UPDATE dfe_documents SET status = 'AUDITADO'")]
    [InlineData("tg_app", "SELECT app.claim_due_dfe(now(), 1, 1)")]
    [InlineData("tg_admin", "SELECT count(*) FROM certificates")]                 // backoffice não vê o A1
    [InlineData("tg_worker", "DELETE FROM certificates")]
    [InlineData("tg_worker", "UPDATE certificates SET pfx_ciphertext = ''")]
    public async Task Fase9_privilegios_de_dfe_e_certificado(string role, string sql)
    {
        await using var c = await OpenAsync(role);
        await using var cmd = new NpgsqlCommand(sql, c);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Fase8_resolve_oauth_client_ignora_cliente_revogado()
    {
        var tenant = Guid.CreateVersion7();
        await using (var su = new NpgsqlConnection(db.SuperuserConnection))
        {
            await su.OpenAsync();
            await using var cmd = new NpgsqlCommand($"""
                INSERT INTO plans VALUES ('STARTER', 60, 10, 2, 100, 10485760, 'LOW', 1000, 1, 9900) ON CONFLICT (code) DO NOTHING;
                INSERT INTO tenants (id, name, root_cnpj, plan_code, kms_key_ref) VALUES ('{tenant}', 'oauth', '33000167000101', 'STARTER', 'k');
                INSERT INTO oauth_applications (client_id, tenant_id, secret_hash, secret_salt, scopes) VALUES
                  ('tgc_ativo', '{tenant}', '\x01', '\x02', '{"{usage:read}"}'),
                  ('tgc_revogado', '{tenant}', '\x01', '\x02', '{"{usage:read}"}');
                UPDATE oauth_applications SET revoked_at = now() WHERE client_id = 'tgc_revogado';
                """, su);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var app = await OpenAsync("tg_app");
        foreach (var (id, expected) in new[] { ("tgc_ativo", 1L), ("tgc_revogado", 0L), ("inexistente", 0L) })
        {
            await using var q = new NpgsqlCommand("SELECT count(*) FROM app.resolve_oauth_client($1)", app);
            q.Parameters.AddWithValue(id);
            Assert.Equal(expected, (long)(await q.ExecuteScalarAsync())!);
        }

        await using (var su = new NpgsqlConnection(db.SuperuserConnection))
        {
            await su.OpenAsync();
            await using var del = new NpgsqlCommand($"DELETE FROM tenants WHERE id = '{tenant}'", su);
            await del.ExecuteNonQueryAsync();
        }
    }

    private static async Task Exec(NpgsqlConnection c, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        await cmd.ExecuteNonQueryAsync();
    }
}
