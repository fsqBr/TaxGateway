-- V0006: lotes, fair-share e entrega de webhooks (SDD §6.3, §7; decisões D-601 a D-606).
-- O agendador (tg_scheduler, NOLOGIN, sem BYPASSRLS) é dono de funções SECURITY DEFINER que fazem as varreduras
-- entre tenants por política nomeada; os workers só recebem (tenant_id, id) e processam no contexto do tenant.

-- ---------- Lotes ----------
ALTER TABLE batches
    ADD COLUMN direction     TEXT NOT NULL DEFAULT 'OUTBOUND' CHECK (direction IN ('OUTBOUND','INBOUND')),
    ADD COLUMN priority      SMALLINT NOT NULL DEFAULT 2 CHECK (priority BETWEEN 0 AND 3),   -- 0 realtime .. 3 low
    ADD COLUMN total_chunks  INT,
    ADD COLUMN done_chunks   INT NOT NULL DEFAULT 0,
    ADD COLUMN conforme_docs INT NOT NULL DEFAULT 0;

CREATE TABLE batch_chunks (
    tenant_id    UUID NOT NULL REFERENCES tenants(id),
    batch_id     UUID NOT NULL REFERENCES batches(id) ON DELETE CASCADE,
    chunk_no     INT NOT NULL,
    priority     SMALLINT NOT NULL,
    storage_key  TEXT NOT NULL,
    docs         INT NOT NULL CHECK (docs > 0),
    status       TEXT NOT NULL DEFAULT 'QUEUED' CHECK (status IN ('QUEUED','RUNNING','DONE','FAILED')),
    attempts     INT NOT NULL DEFAULT 0,
    lease_until  TIMESTAMPTZ,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (batch_id, chunk_no)
);
CREATE INDEX batch_chunks_queue ON batch_chunks (priority, created_at) WHERE status = 'QUEUED';
CREATE INDEX ON batch_chunks (tenant_id, status);

ALTER TABLE batch_chunks ENABLE ROW LEVEL SECURITY;
ALTER TABLE batch_chunks FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON batch_chunks TO tg_app, tg_worker
    USING (tenant_id = (SELECT app.current_tenant())) WITH CHECK (tenant_id = (SELECT app.current_tenant()));
GRANT SELECT, INSERT          ON batch_chunks TO tg_app;
GRANT SELECT, UPDATE          ON batch_chunks TO tg_worker;
GRANT INSERT                  ON batches      TO tg_app;   -- já tinha SELECT/INSERT; idempotente
GRANT UPDATE                  ON batches      TO tg_worker;

-- ---------- Agendador (tg_scheduler) ----------
GRANT USAGE ON SCHEMA public TO tg_scheduler;
GRANT USAGE, CREATE ON SCHEMA app TO tg_scheduler;
GRANT SELECT, UPDATE ON batch_chunks, webhook_deliveries TO tg_scheduler;
CREATE POLICY scheduler_claim ON batch_chunks       FOR ALL TO tg_scheduler USING (true) WITH CHECK (true);
CREATE POLICY scheduler_claim ON webhook_deliveries FOR ALL TO tg_scheduler USING (true) WITH CHECK (true);

-- Fair-share (§6.3): no máximo K chunks em voo por tenant; entre tenants, rodízio (o 1º chunk de cada tenant
-- vem antes do 2º de qualquer outro), respeitando a prioridade do plano. Lease vencido volta para a fila.
CREATE FUNCTION app.claim_batch_chunks(p_limit INT, p_per_tenant INT, p_lease_seconds INT)
RETURNS TABLE (tenant_id UUID, batch_id UUID, chunk_no INT, storage_key TEXT)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
BEGIN
  UPDATE public.batch_chunks c SET status = 'QUEUED', lease_until = NULL
   WHERE c.status = 'RUNNING' AND c.lease_until < now();

  RETURN QUERY
  WITH running AS (
    SELECT c.tenant_id, count(*) AS n FROM public.batch_chunks c WHERE c.status = 'RUNNING' GROUP BY c.tenant_id
  ), candidates AS (
    SELECT c.tenant_id, c.batch_id, c.chunk_no, c.priority, c.created_at,
           row_number() OVER (PARTITION BY c.tenant_id ORDER BY c.priority, c.created_at, c.chunk_no) AS rn,
           coalesce(r.n, 0) AS inflight
      FROM public.batch_chunks c LEFT JOIN running r ON r.tenant_id = c.tenant_id
     WHERE c.status = 'QUEUED'
  ), picked AS (
    SELECT x.batch_id, x.chunk_no FROM candidates x
     WHERE x.rn <= p_per_tenant - x.inflight
     ORDER BY x.rn, x.priority, x.created_at
     LIMIT p_limit
  )
  UPDATE public.batch_chunks c
     SET status = 'RUNNING', attempts = c.attempts + 1, lease_until = now() + make_interval(secs => p_lease_seconds)
    FROM (SELECT b.batch_id, b.chunk_no FROM public.batch_chunks b JOIN picked p USING (batch_id, chunk_no)
           FOR UPDATE OF b SKIP LOCKED) s
   WHERE c.batch_id = s.batch_id AND c.chunk_no = s.chunk_no
  RETURNING c.tenant_id, c.batch_id, c.chunk_no, c.storage_key;
END $$;

-- Webhooks vencidos (retry): reserva com lease curto para não haver entrega dupla entre réplicas.
CREATE FUNCTION app.claim_due_webhook_deliveries(p_limit INT, p_lease_seconds INT)
RETURNS TABLE (tenant_id UUID, delivery_id UUID)
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  UPDATE public.webhook_deliveries d SET next_attempt_at = now() + make_interval(secs => p_lease_seconds)
    FROM (SELECT x.id FROM public.webhook_deliveries x
           WHERE x.status IN ('PENDING','RETRYING') AND x.next_attempt_at <= now()
           ORDER BY x.next_attempt_at LIMIT p_limit FOR UPDATE SKIP LOCKED) s
   WHERE d.id = s.id
  RETURNING d.tenant_id, d.id
$$;

REVOKE ALL ON FUNCTION app.claim_batch_chunks(INT, INT, INT) FROM PUBLIC;
REVOKE ALL ON FUNCTION app.claim_due_webhook_deliveries(INT, INT) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.claim_batch_chunks(INT, INT, INT) TO tg_worker;
GRANT EXECUTE ON FUNCTION app.claim_due_webhook_deliveries(INT, INT) TO tg_worker;
ALTER FUNCTION app.claim_batch_chunks(INT, INT, INT) OWNER TO tg_scheduler;
ALTER FUNCTION app.claim_due_webhook_deliveries(INT, INT) OWNER TO tg_scheduler;

CREATE INDEX webhook_deliveries_due ON webhook_deliveries (next_attempt_at) WHERE status IN ('PENDING','RETRYING');
