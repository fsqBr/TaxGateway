using Npgsql;

namespace TaxGateway.SecurityTests.Rls;

/// <summary>
/// Uma linha por tabela com tenant_id, para cada tenant. O teste de cobertura exige que TODA tabela com
/// tenant_id esteja aqui: tabela nova sem seed quebra o build (SDD §13).
/// Parâmetros: @t = tenant_id, @n = número do tenant (gera CNPJ/chave únicos).
/// </summary>
internal static class Seed
{
    private const string Cnpj = "'AB12CD34EF56' || lpad(@n::text, 2, '0')";
    private const string AccessKey = "'352611AB12CD34EF56' || lpad(@n::text, 26, '0')";

    public static readonly IReadOnlyDictionary<string, string> TenantTables = new Dictionary<string, string>
    {
        ["oauth_applications"] = "INSERT INTO oauth_applications VALUES ('client-' || @n, @t)",
        ["api_keys"] = """
            INSERT INTO api_keys (id, tenant_id, name, key_prefix, key_hash, scopes)
            VALUES (md5(@t::text || 'key')::uuid, @t, 'k', 'tg' || @n, sha256(('key' || @n)::bytea), '{calculo}')
            """,
        ["tenant_cnpjs"] = $"INSERT INTO tenant_cnpjs (tenant_id, cnpj) VALUES (@t, {Cnpj})",
        ["tenant_audit_rule_settings"] = "INSERT INTO tenant_audit_rule_settings (tenant_id, rule_id) VALUES (@t, 'R001')",
        ["document_registry"] = $"""
            INSERT INTO document_registry VALUES (@t, sha256(('reg' || @n)::bytea), 'OUTBOUND', {AccessKey},
              md5(@t::text || 'doc')::uuid, '2026-11-01', md5(@t::text || 'doc')::uuid, now())
            """,
        ["idempotency_requests"] = "INSERT INTO idempotency_requests (tenant_id, key, request_hash, expires_at) VALUES (@t, 'idem', '\\x00', now() + interval '1 day')",
        ["batches"] = "INSERT INTO batches (id, tenant_id, status, storage_key) VALUES (md5(@t::text || 'batch')::uuid, @t, 'QUEUED', 's3://x')",
        ["batch_chunks"] = "INSERT INTO batch_chunks (tenant_id, batch_id, chunk_no, priority, storage_key, docs) VALUES (@t, md5(@t::text || 'batch')::uuid, 1, 2, 's3://c', 10)",
        ["fiscal_documents"] = $$"""
            INSERT INTO fiscal_documents (id, billing_month, tenant_id, kind, model, access_key, cnpj_subject, engine,
              engine_bundle, rules_version, param_snapshot_id, audit_status, declared, recalculated, xml_sha256, is_billable)
            VALUES (md5(@t::text || 'doc')::uuid, '2026-11-01', @t, 'OUTBOUND', 'NFE55', {{AccessKey}}, {{Cnpj}}, 'regular',
              '2026.11.2', 'r1', 1, 'CONFORME', '{}', '{}', '\x00', true)
            """,
        ["simulations"] = $$"""
            INSERT INTO simulations (id, billing_month, tenant_id, cnpj_subject, engine, engine_bundle, param_snapshot_id, request, result)
            VALUES (md5(@t::text || 'sim')::uuid, '2026-11-01', @t, {{Cnpj}}, 'regular', '2026.11.2', 1, '{}', '{}')
            """,
        ["usage_ledger"] = $"""
            INSERT INTO usage_ledger (billing_month, tenant_id, metric, cnpj_subject, source_id)
            VALUES ('2026-11-01', @t, 'DOC_AUDIT', {Cnpj}, md5(@t::text || 'doc')::uuid)
            """,
        ["cnpj_monthly_activity"] = $"INSERT INTO cnpj_monthly_activity (tenant_id, billing_month, cnpj) VALUES (@t, '2026-11-01', {Cnpj})",
        ["billing_periods"] = "INSERT INTO billing_periods (tenant_id, billing_month, status) VALUES (@t, '2026-11-01', 'OPEN')",
        ["billing_adjustments"] = "INSERT INTO billing_adjustments (tenant_id, billing_month, version, amount_delta_cents, reason, created_by, provider_idempotency_key) VALUES (@t, '2026-11-01', 2, 100, 'seed', 'seed', @t::text || ':2026-11:v2')",
        ["tenant_webhooks"] = """
            INSERT INTO tenant_webhooks (id, tenant_id, target_url, secrets_ciphertext, dek_wrapped, subscribed_events)
            VALUES (md5(@t::text || 'wh')::uuid, @t, 'https://example.test/hook', '\x00', '\x00', '{audit.completed}')
            """,
        ["outbox"] = "INSERT INTO outbox (tenant_id, topic, payload) VALUES (@t, 'audit.completed', '{}')",
        ["webhook_deliveries"] = """
            INSERT INTO webhook_deliveries (id, tenant_id, webhook_id, event, payload, status)
            VALUES (md5(@t::text || 'wd')::uuid, @t, md5(@t::text || 'wh')::uuid, 'audit.completed', '{}', 'PENDING')
            """,
        ["certificates"] = $"""
            INSERT INTO certificates (id, tenant_id, cnpj, pfx_ciphertext, password_ciphertext, dek_wrapped, thumbprint, not_after)
            VALUES (md5(@t::text || 'cert')::uuid, @t, {Cnpj}, '\x00', '\x00', '\x00', 'thumb', now() + interval '1 year')
            """,
        ["user_memberships"] = """
            INSERT INTO users (id, realm, email, name, password_hash, password_salt)
            VALUES (md5(@t::text || 'user')::uuid, 'customer', 'u' || @n || '@seed.test', 'u', '\x00', '\x00');
            INSERT INTO user_memberships (user_id, tenant_id, role) VALUES (md5(@t::text || 'user')::uuid, @t, 'owner')
            """,
        ["user_invites"] = "INSERT INTO user_invites (token_hash, tenant_id, email, role, invited_by, expires_at) VALUES (sha256(('inv' || @n)::bytea), @t, 'c' || @n || '@seed.test', 'viewer', md5(@t::text || 'user')::uuid, now() + interval '1 day')",
        ["audit_log"] = "INSERT INTO audit_log (actor_type, actor_id, action, tenant_id) VALUES ('system', 'seed', 'seed', @t)",
        ["invoices"] = "INSERT INTO invoices (id, tenant_id, status) VALUES ('in_seed_' || @n, @t, 'paid')",
        ["stripe_usage_reports"] = "INSERT INTO stripe_usage_reports (tenant_id, billing_month, metric, reported_qty) VALUES (@t, '2026-11-01', 'documentos', 1)",
        ["dfe_sync_state"] = $"INSERT INTO dfe_sync_state (tenant_id, cnpj) VALUES (@t, {Cnpj})",
        ["dfe_documents"] = $"INSERT INTO dfe_documents (tenant_id, cnpj, nsu, schema, kind, status) VALUES (@t, {Cnpj}, 1, 'resNFe_v1.01.xsd', 'RESUMO', 'RECEBIDO')",
        ["cnpj_verification_codes"] = "INSERT INTO cnpj_verification_codes (tenant_id, code_hash, email, expires_at) VALUES (@t, '\\x01', 'seed@seed.test', now() + interval '1 hour')",
        ["cnpj_disputes"] = """
            INSERT INTO users (id, realm, email, name, password_hash, password_salt)
            VALUES (md5(@t::text || 'disp')::uuid, 'customer', 'd' || @n || '@seed.test', 'seed', '\x01', '\x01') ON CONFLICT DO NOTHING;
            INSERT INTO cnpj_disputes (id, cnpj_root, tenant_id, claimant_user, method, certificate)
            VALUES (md5(@t::text || 'dispute')::uuid, '00000000000000', @t, md5(@t::text || 'disp')::uuid, 'CERTIFICADO', 'seed')
            """,
        ["tenant_notifications"] = "INSERT INTO tenant_notifications (tenant_id, kind, severity, title, body, dedupe_key) VALUES (@t, 'FRANQUIA_80', 'aviso', 'seed', 'seed', 'seed-' || @n)",
        ["titular_requests"] = "INSERT INTO titular_requests (id, tenant_id, cpf_hmac, action, documents, requested_by) VALUES (md5(@t::text || 'tit')::uuid, @t, '\\x01', 'LOCALIZAR', 0, 'seed')",
    };

    public static async Task InsertGlobalAsync(NpgsqlConnection superuser)
    {
        // Os planos vêm da carga inicial (V0005); aqui só garantimos o STARTER usado pelos tenants de teste.
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO plans VALUES ('STARTER', 60, 10, 2, 100, 10485760, 'LOW', 1000, 1, 9900) ON CONFLICT (code) DO NOTHING;
            """, superuser);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task InsertTenantAsync(NpgsqlConnection superuser, Guid tenant, int n)
    {
        await ExecAsync(superuser, $"""
            INSERT INTO tenants (id, name, root_cnpj, plan_code, kms_key_ref)
            VALUES (@t, 'Tenant ' || @n, {Cnpj}, 'STARTER', 'kms://dev/' || @n)
            """, tenant, n);

        foreach (var sql in TenantTables.Values)
        {
            await ExecAsync(superuser, sql, tenant, n);
        }
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, Guid tenant, int n)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("n", n);
        await cmd.ExecuteNonQueryAsync();
    }
}
