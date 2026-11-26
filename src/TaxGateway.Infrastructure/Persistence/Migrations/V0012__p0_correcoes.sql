-- V0012: correções P0 da revisão multidisciplinar (docs/revisao-multidisciplinar.md; D-P03, D-P08).

-- ---------- Idempotency-Key com lease (D-P03) ----------
-- Antes: uma queda no meio do processamento deixava a chave "em processamento" por 24 h (409 para o cliente que
-- repetia). Agora a reserva vale por um lease curto; vencido, a mesma requisição (mesmo hash) retoma a chave.
ALTER TABLE idempotency_requests ADD COLUMN locked_until TIMESTAMPTZ;
UPDATE idempotency_requests SET locked_until = now() WHERE status_code IS NULL;

-- ---------- Lotes: claim seguro, cerca (fencing) e teto de tentativas (D-P08) ----------
ALTER TABLE batches DROP CONSTRAINT batches_status_check;
ALTER TABLE batches ADD CONSTRAINT batches_status_check
    CHECK (status IN ('QUEUED','RUNNING','COMPLETING','COMPLETED','FAILED','REJECTED'));

-- Claim: a subconsulta de travamento confere de novo status = 'QUEUED' (sob READ COMMITTED, um chunk reservado e
-- confirmado por outro worker entre o snapshot e o FOR UPDATE voltava a ser reservado). Devolve "attempts": é o
-- token de cerca — o worker só grava o resultado se o chunk ainda estiver RUNNING com a MESMA tentativa.
DROP FUNCTION app.claim_batch_chunks(INT, INT, INT);
CREATE FUNCTION app.claim_batch_chunks(p_limit INT, p_per_tenant INT, p_lease_seconds INT, p_max_attempts INT)
RETURNS TABLE (tenant_id UUID, batch_id UUID, chunk_no INT, storage_key TEXT, attempt INT)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
BEGIN
  -- Lease vencido: volta para a fila, salvo se já esgotou as tentativas (chunk "envenenado" não roda para sempre).
  UPDATE public.batch_chunks c SET status = 'QUEUED', lease_until = NULL
   WHERE c.status = 'RUNNING' AND c.lease_until < now() AND c.attempts < p_max_attempts;

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
     ORDER BY x.priority, x.rn, x.created_at          -- prioridade do plano primeiro (antes: rn primeiro, D-P08)
     LIMIT p_limit
  )
  UPDATE public.batch_chunks c
     SET status = 'RUNNING', attempts = c.attempts + 1, lease_until = now() + make_interval(secs => p_lease_seconds)
    FROM (SELECT b.batch_id, b.chunk_no FROM public.batch_chunks b JOIN picked p USING (batch_id, chunk_no)
           WHERE b.status = 'QUEUED'
           FOR UPDATE OF b SKIP LOCKED) s
   WHERE c.batch_id = s.batch_id AND c.chunk_no = s.chunk_no AND c.status = 'QUEUED'
  RETURNING c.tenant_id, c.batch_id, c.chunk_no, c.storage_key, c.attempts;
END $$;

-- Reconciliador: (1) chunks que esgotaram as tentativas viram FAILED e contam como documentos com falha no lote;
-- (2) devolve os lotes com todos os chunks terminados que ainda não concluíram (queda entre o último DONE e a
-- conclusão, ou lote só com chunks falhos). A conclusão em si é feita pelo worker com a transição atômica
-- RUNNING → COMPLETING (só um worker vence).
CREATE FUNCTION app.batches_to_finalize(p_max_attempts INT)
RETURNS TABLE (tenant_id UUID, batch_id UUID)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
BEGIN
  WITH dead AS (
    UPDATE public.batch_chunks c SET status = 'FAILED', lease_until = NULL
     WHERE c.status = 'RUNNING' AND c.lease_until < now() AND c.attempts >= p_max_attempts
    RETURNING c.batch_id, c.docs
  ), agg AS (SELECT d.batch_id, count(*) AS n, sum(d.docs) AS docs FROM dead d GROUP BY d.batch_id)
  UPDATE public.batches b
     SET status = 'RUNNING', done_chunks = b.done_chunks + a.n,
         processed_docs = b.processed_docs + a.docs, failed_docs = b.failed_docs + a.docs
    FROM agg a WHERE b.id = a.batch_id;

  RETURN QUERY
  SELECT b.tenant_id, b.id FROM public.batches b
   WHERE b.status IN ('QUEUED','RUNNING')
     AND b.total_chunks > 0
     AND NOT EXISTS (SELECT 1 FROM public.batch_chunks c WHERE c.batch_id = b.id AND c.status IN ('QUEUED','RUNNING'));
END $$;

GRANT SELECT, UPDATE ON batches TO tg_scheduler;
CREATE POLICY scheduler_finalize ON batches FOR ALL TO tg_scheduler USING (true) WITH CHECK (true);

REVOKE ALL ON FUNCTION app.claim_batch_chunks(INT, INT, INT, INT) FROM PUBLIC;
REVOKE ALL ON FUNCTION app.batches_to_finalize(INT) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.claim_batch_chunks(INT, INT, INT, INT) TO tg_worker;
GRANT EXECUTE ON FUNCTION app.batches_to_finalize(INT) TO tg_worker;
ALTER FUNCTION app.claim_batch_chunks(INT, INT, INT, INT) OWNER TO tg_scheduler;
ALTER FUNCTION app.batches_to_finalize(INT) OWNER TO tg_scheduler;

-- ---------- Faturamento: um fechamento por vez, estado antes de chamar o provedor (D-P07) ----------
-- CHARGING é gravado ANTES de chamar o provedor; quem não consegue a transição CLOSED → CHARGING não cobra.
ALTER TABLE billing_periods DROP CONSTRAINT billing_periods_status_check;
ALTER TABLE billing_periods ADD CONSTRAINT billing_periods_status_check
    CHECK (status IN ('OPEN','CLOSING','CLOSED','CHARGING','INVOICED'));
ALTER TABLE billing_periods ADD COLUMN charging_since TIMESTAMPTZ;

-- Chunks agora fecham por contagem OU bytes (D-P09): o número do 1º documento vai no chunk para o relatório.
ALTER TABLE batch_chunks ADD COLUMN first_doc INT NOT NULL DEFAULT 0;
UPDATE batch_chunks SET first_doc = (chunk_no - 1) * 500;   -- lotes anteriores: chunks fixos de 500

-- ---------- Revogações com o banco como fonte da verdade (D-P10) ----------
-- Antes: suspensão de tenant, cliente OAuth revogado e jti revogado viviam só em conjuntos do Valkey — quem escrevesse
-- no Valkey tirava uma suspensão com um SREM. Agora o banco é a fonte; o Valkey só acelera a propagação (pub/sub).
-- O estado efetivo em cada instância é banco ∪ Valkey: escrever no Valkey pode no máximo SUSPENDER (negação de
-- serviço), nunca liberar.
CREATE TABLE revoked_tokens (
    jti         TEXT PRIMARY KEY,
    expires_at  TIMESTAMPTZ NOT NULL,
    revoked_by  TEXT NOT NULL,
    revoked_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX ON revoked_tokens (expires_at);
GRANT SELECT, INSERT, UPDATE, DELETE ON revoked_tokens TO tg_admin;

CREATE FUNCTION app.revocation_state()
RETURNS TABLE (kind TEXT, value TEXT, expires_at TIMESTAMPTZ)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  SELECT 'tenant', t.id::text, NULL::timestamptz FROM public.tenants t WHERE t.status <> 'ACTIVE'
  UNION ALL
  SELECT 'client', a.client_id, NULL FROM public.oauth_applications a WHERE a.revoked_at IS NOT NULL
  UNION ALL
  SELECT 'jti', r.jti, r.expires_at FROM public.revoked_tokens r WHERE r.expires_at > now()
$$;
REVOKE ALL ON FUNCTION app.revocation_state() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.revocation_state() TO tg_app;
