-- V0003: privilégios e Row-Level Security (SDD §4.1, decisões D-103/D-107).
-- Regra: todo acesso é explícito. Nenhum role de runtime tem BYPASSRLS; acesso cross-tenant
-- existe só onde há política nomeada para aquele role (billing, relay do outbox, autenticação).

-- ---------- Schemas ----------
GRANT USAGE ON SCHEMA public TO tg_app, tg_worker, tg_billing, tg_relay, tg_auth;
GRANT USAGE ON SCHEMA app    TO tg_app, tg_worker, tg_billing, tg_relay;
GRANT USAGE, CREATE ON SCHEMA app TO tg_auth;   -- necessário para ser dono de app.resolve_api_key

-- ---------- Tabelas globais (somente leitura em runtime; escrita via fluxo de param_changes) ----------
GRANT SELECT ON plans, engine_bundles, engine_routing, rate_catalog, credit_rules, param_snapshots
  TO tg_app, tg_worker;
GRANT SELECT ON plans TO tg_billing;

-- ---------- API (tg_app) ----------
GRANT SELECT                         ON tenants                    TO tg_app;
GRANT SELECT                         ON oauth_applications         TO tg_app;
GRANT SELECT, INSERT, UPDATE         ON api_keys                   TO tg_app;
GRANT SELECT, INSERT, UPDATE         ON tenant_cnpjs               TO tg_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON tenant_audit_rule_settings TO tg_app;
GRANT SELECT, INSERT, UPDATE         ON document_registry          TO tg_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON idempotency_requests       TO tg_app;
GRANT SELECT, INSERT                 ON batches                    TO tg_app;
GRANT SELECT, INSERT                 ON fiscal_documents           TO tg_app;
GRANT SELECT, INSERT                 ON simulations                TO tg_app;
GRANT SELECT, INSERT                 ON usage_ledger               TO tg_app;   -- append-only
GRANT SELECT, INSERT                 ON cnpj_monthly_activity      TO tg_app;
GRANT SELECT                         ON billing_periods            TO tg_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON tenant_webhooks            TO tg_app;
GRANT INSERT                         ON outbox                     TO tg_app;
GRANT SELECT, UPDATE                 ON webhook_deliveries         TO tg_app;   -- endpoint de retry
GRANT SELECT, INSERT, DELETE         ON certificates               TO tg_app;
GRANT SELECT                         ON dfe_sync_state             TO tg_app;

-- ---------- Workers por tenant (tg_worker) ----------
GRANT SELECT                         ON tenants                    TO tg_worker;
GRANT SELECT, INSERT                 ON tenant_cnpjs               TO tg_worker;
GRANT SELECT                         ON tenant_audit_rule_settings TO tg_worker;
GRANT SELECT, INSERT, UPDATE         ON document_registry          TO tg_worker;
GRANT SELECT, UPDATE                 ON batches                    TO tg_worker;
GRANT SELECT, INSERT                 ON fiscal_documents           TO tg_worker;
GRANT SELECT, INSERT                 ON usage_ledger               TO tg_worker;   -- append-only
GRANT SELECT, INSERT                 ON cnpj_monthly_activity      TO tg_worker;
GRANT SELECT, UPDATE                 ON tenant_webhooks            TO tg_worker;   -- desativar endpoint
GRANT INSERT                         ON outbox                     TO tg_worker;
GRANT SELECT, INSERT, UPDATE         ON webhook_deliveries         TO tg_worker;
GRANT SELECT                         ON certificates               TO tg_worker;
GRANT SELECT, INSERT, UPDATE         ON dfe_sync_state             TO tg_worker;

-- ---------- Billing (tg_billing) ----------
GRANT SELECT                 ON tenants, usage_ledger, cnpj_monthly_activity TO tg_billing;
GRANT SELECT, INSERT, UPDATE ON billing_periods                              TO tg_billing;

-- ---------- Relay do outbox (tg_relay) ----------
GRANT SELECT                 ON outbox TO tg_relay;
GRANT UPDATE (published_at)  ON outbox TO tg_relay;

-- ---------- Autenticação (tg_auth, dono de app.resolve_api_key) ----------
GRANT SELECT ON api_keys, tenants TO tg_auth;

-- ---------- RLS: ENABLE + FORCE + isolamento por tenant em TODA tabela com tenant_id ----------
-- (SELECT app.current_tenant()) vira InitPlan: avaliado uma vez por consulta, não por linha (D-107).
DO $$
DECLARE t TEXT;
BEGIN
  FOREACH t IN ARRAY ARRAY['oauth_applications','api_keys','tenant_cnpjs','tenant_audit_rule_settings',
    'document_registry','idempotency_requests','batches','fiscal_documents','simulations','usage_ledger',
    'cnpj_monthly_activity','billing_periods','tenant_webhooks','outbox','webhook_deliveries',
    'certificates','dfe_sync_state']
  LOOP
    EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON public.%I TO tg_app, tg_worker
                    USING (tenant_id = (SELECT app.current_tenant()))
                    WITH CHECK (tenant_id = (SELECT app.current_tenant()))', t);
  END LOOP;
END $$;

ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenants FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_self ON tenants FOR SELECT TO tg_app, tg_worker
  USING (id = (SELECT app.current_tenant()));

-- Políticas cross-tenant nomeadas (as únicas do sistema)
CREATE POLICY billing_read ON tenants               FOR SELECT TO tg_billing USING (true);
CREATE POLICY billing_read ON usage_ledger          FOR SELECT TO tg_billing USING (true);
CREATE POLICY billing_read ON cnpj_monthly_activity FOR SELECT TO tg_billing USING (true);
CREATE POLICY billing_rw   ON billing_periods                  TO tg_billing USING (true) WITH CHECK (true);

CREATE POLICY relay_read   ON outbox FOR SELECT TO tg_relay USING (true);
CREATE POLICY relay_mark   ON outbox FOR UPDATE TO tg_relay USING (true) WITH CHECK (true);

CREATE POLICY auth_lookup  ON api_keys FOR SELECT TO tg_auth USING (true);
CREATE POLICY auth_lookup  ON tenants  FOR SELECT TO tg_auth USING (true);

-- ---------- Autenticação por API Key (antes de existir contexto de tenant) ----------
CREATE FUNCTION app.resolve_api_key(p_hash BYTEA)
RETURNS TABLE (tenant_id UUID, key_id UUID, scopes TEXT[], plan_code TEXT, tenant_status TEXT)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  SELECT k.tenant_id, k.id, k.scopes, t.plan_code, t.status
    FROM public.api_keys k
    JOIN public.tenants  t ON t.id = k.tenant_id
   WHERE k.key_hash = p_hash
     AND k.revoked_at IS NULL
     AND (k.expires_at IS NULL OR k.expires_at > now())
$$;
-- REVOKE/GRANT antes da troca de dono: depois dela o tg_owner não administra mais os privilégios
-- (o PostgreSQL reescreve o grantor para o novo dono).
REVOKE ALL ON FUNCTION app.resolve_api_key(BYTEA) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.resolve_api_key(BYTEA) TO tg_app;
ALTER FUNCTION app.resolve_api_key(BYTEA) OWNER TO tg_auth;

-- Saúde do particionamento (só contagem; ver V0002)
GRANT EXECUTE ON FUNCTION app.default_partition_rows() TO tg_app, tg_worker;
