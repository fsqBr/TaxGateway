-- V0016: entrega de webhooks justa entre clientes e com disjuntor por endpoint (D-P21; revisão de arquitetura 14).
-- Antes: a reserva ordenava só por next_attempt_at — um lote de 25 mil documentos de um cliente com endpoint lento
-- ocupava os slots por horas e atrasava os webhooks de todos.

ALTER TABLE tenant_webhooks
    ADD COLUMN consecutive_failures INT NOT NULL DEFAULT 0,
    ADD COLUMN paused_until         TIMESTAMPTZ;

GRANT SELECT ON tenant_webhooks TO tg_scheduler;
CREATE POLICY scheduler_claim ON tenant_webhooks FOR SELECT TO tg_scheduler USING (true);

DROP FUNCTION app.claim_due_webhook_deliveries(INT, INT);
CREATE FUNCTION app.claim_due_webhook_deliveries(p_limit INT, p_lease_seconds INT, p_per_tenant INT)
RETURNS TABLE (tenant_id UUID, delivery_id UUID)
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  WITH due AS (
    SELECT d.id, d.tenant_id,
           row_number() OVER (PARTITION BY d.tenant_id ORDER BY d.next_attempt_at) AS rn,
           d.next_attempt_at
      FROM public.webhook_deliveries d
      JOIN public.tenant_webhooks w ON w.id = d.webhook_id
     WHERE d.status IN ('PENDING','RETRYING') AND d.next_attempt_at <= now()
       AND (w.paused_until IS NULL OR w.paused_until <= now())      -- disjuntor do endpoint aberto: espera
  ), picked AS (
    SELECT id FROM due WHERE rn <= p_per_tenant ORDER BY rn, next_attempt_at LIMIT p_limit   -- rodízio entre clientes
  )
  UPDATE public.webhook_deliveries d SET next_attempt_at = now() + make_interval(secs => p_lease_seconds)
    FROM (SELECT x.id FROM public.webhook_deliveries x JOIN picked p USING (id)
           WHERE x.status IN ('PENDING','RETRYING') AND x.next_attempt_at <= now()
           FOR UPDATE OF x SKIP LOCKED) s
   WHERE d.id = s.id
  RETURNING d.tenant_id, d.id
$$;
REVOKE ALL ON FUNCTION app.claim_due_webhook_deliveries(INT, INT, INT) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.claim_due_webhook_deliveries(INT, INT, INT) TO tg_worker;
ALTER FUNCTION app.claim_due_webhook_deliveries(INT, INT, INT) OWNER TO tg_scheduler;
