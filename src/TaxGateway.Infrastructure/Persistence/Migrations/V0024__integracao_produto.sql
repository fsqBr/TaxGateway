-- V0024 — Integração e produto (D-R27, D-R28).

-- D-R27: clientes OAuth criados pelo próprio cliente no portal (antes só pelo backoffice). O segredo é guardado só como
-- hash; a revogação vale na hora para os tokens já emitidos (lista de revogados no Valkey, como no backoffice).
GRANT INSERT ON oauth_applications TO tg_app;
-- Revogar é por função (só marca revoked_at = now() do próprio tenant): o app não pode des-revogar nem alterar clientes.
GRANT SELECT, UPDATE (revoked_at) ON oauth_applications TO tg_scheduler;
CREATE POLICY scheduler_revoke ON oauth_applications FOR ALL TO tg_scheduler USING (true) WITH CHECK (true);
SET ROLE tg_scheduler;
CREATE FUNCTION app.revoke_own_oauth_client(p_client TEXT)
RETURNS INT
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  WITH r AS (UPDATE public.oauth_applications SET revoked_at = now()
              WHERE client_id = p_client AND tenant_id = app.current_tenant() AND revoked_at IS NULL RETURNING 1)
  SELECT count(*)::int FROM r
$$;
RESET ROLE;
REVOKE ALL ON FUNCTION app.revoke_own_oauth_client(TEXT) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.revoke_own_oauth_client(TEXT) TO tg_app;

-- D-R27: registro das chamadas da API para quem integra (erros e escritas; leituras 2xx não são gravadas para não
-- dobrar a carga). 30 dias de retenção.
CREATE TABLE api_call_logs (
    id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id    UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    method       TEXT NOT NULL,
    route        TEXT NOT NULL,
    status       SMALLINT NOT NULL,
    duration_ms  INT NOT NULL,
    credential   TEXT NOT NULL,              -- "api-key:<prefixo>", "oauth:<client>" ou "pessoa:<nome>"
    error_code   TEXT,
    trace_id     TEXT
);
CREATE INDEX api_call_logs_tenant_at ON api_call_logs (tenant_id, at DESC);
ALTER TABLE api_call_logs ENABLE ROW LEVEL SECURITY;
ALTER TABLE api_call_logs FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON api_call_logs TO tg_app, tg_worker
    USING (tenant_id = (SELECT app.current_tenant()))
    WITH CHECK (tenant_id = (SELECT app.current_tenant()));
GRANT SELECT, INSERT ON api_call_logs TO tg_app;
CREATE POLICY scheduler_purge ON api_call_logs FOR ALL TO tg_scheduler USING (true) WITH CHECK (true);
GRANT SELECT, DELETE ON api_call_logs TO tg_scheduler;

SET ROLE tg_scheduler;
CREATE FUNCTION app.purge_api_call_logs()
RETURNS INT
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  WITH d AS (DELETE FROM public.api_call_logs WHERE at < now() - interval '30 days' RETURNING 1) SELECT count(*)::int FROM d
$$;
RESET ROLE;
REVOKE ALL ON FUNCTION app.purge_api_call_logs() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.purge_api_call_logs() TO tg_worker;
