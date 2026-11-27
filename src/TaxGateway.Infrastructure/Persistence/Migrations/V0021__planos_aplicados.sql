-- V0021 — Planos aplicados em todos os pontos (D-R12 a D-R15).

-- D-R13: a captura de DF-e só roda para conta em dia e plano com DF-e (um rebaixamento desliga a captura na próxima
-- rodada; o certificado continua guardado para quando o plano voltar a permitir).
SET ROLE tg_scheduler;   -- dono da função (SECURITY DEFINER)
CREATE OR REPLACE FUNCTION app.claim_due_dfe(p_now TIMESTAMPTZ, p_limit INT, p_lease_seconds INT)
RETURNS TABLE (tenant_id UUID, cnpj TEXT)
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  UPDATE public.dfe_sync_state d SET lease_until = p_now + make_interval(secs => p_lease_seconds)
    FROM (SELECT x.tenant_id, x.cnpj FROM public.dfe_sync_state x
            JOIN public.tenants t ON t.id = x.tenant_id AND t.status = 'ACTIVE'
            JOIN public.plans p ON p.code = t.plan_code AND p.dfe_enabled
           WHERE x.enabled AND x.next_allowed_at <= p_now AND (x.lease_until IS NULL OR x.lease_until <= p_now)
             AND EXISTS (SELECT 1 FROM public.certificates c WHERE c.tenant_id = x.tenant_id AND c.cnpj = x.cnpj AND c.not_after > p_now)
           ORDER BY x.next_allowed_at LIMIT p_limit FOR UPDATE OF x SKIP LOCKED) s
   WHERE d.tenant_id = s.tenant_id AND d.cnpj = s.cnpj
  RETURNING d.tenant_id, d.cnpj::text
$$;
RESET ROLE;

-- D-R14: CNPJs incluídos viram limite quando o plano não vende CNPJ adicional (Sandbox: 1). Nos planos que cobram o
-- CNPJ extra, ele é permitido e cobrado (o preço aparece antes de cadastrar). Vale para todo caminho de cadastro
-- (portal, API OEM, autoprovisionamento) porque é um gatilho na tabela.
GRANT SELECT ON tenant_cnpjs TO tg_scheduler;
CREATE POLICY scheduler_cnpj_limit ON tenant_cnpjs FOR SELECT TO tg_scheduler USING (true);

CREATE FUNCTION app.enforce_cnpj_limit() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
DECLARE lim INT; n INT;
BEGIN
  IF NOT NEW.is_active THEN
    RETURN NEW;
  END IF;
  SELECT p.included_cnpjs INTO lim FROM public.tenants t JOIN public.plans p ON p.code = t.plan_code
   WHERE t.id = NEW.tenant_id AND p.cnpj_price_cents = 0 AND p.included_cnpjs > 0;
  IF lim IS NULL THEN
    RETURN NEW;
  END IF;
  SELECT count(*) INTO n FROM public.tenant_cnpjs c WHERE c.tenant_id = NEW.tenant_id AND c.is_active AND c.cnpj <> NEW.cnpj;
  IF n >= lim THEN
    RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'ERR_LIMITE_CNPJS:' || lim;   -- o limite vai na mensagem (DETAIL é ocultado pelo Npgsql)
  END IF;
  RETURN NEW;
END $$;
ALTER FUNCTION app.enforce_cnpj_limit() OWNER TO tg_scheduler;
REVOKE ALL ON FUNCTION app.enforce_cnpj_limit() FROM PUBLIC;

CREATE TRIGGER tenant_cnpjs_plan_limit BEFORE INSERT OR UPDATE OF is_active ON tenant_cnpjs
  FOR EACH ROW EXECUTE FUNCTION app.enforce_cnpj_limit();

-- D-R15: excedente de simulações faturado pela Stripe (antes só documentos e CNPJs iam para a assinatura).
ALTER TABLE plans ADD COLUMN stripe_sim_price_id TEXT;
GRANT UPDATE (stripe_sim_price_id) ON plans TO tg_billing;
ALTER TABLE stripe_usage_reports DROP CONSTRAINT stripe_usage_reports_metric_check;
ALTER TABLE stripe_usage_reports ADD CONSTRAINT stripe_usage_reports_metric_check CHECK (metric IN ('documentos','simulacoes','cnpjs'));
-- Planos pagos ressincronizam com a Stripe para ganhar o preço medido de simulações.
UPDATE plans SET stripe_synced_at = NULL WHERE price_cents > 0;

-- Planos de contrato (legados): sem franquia nem preço de lista — a cobrança é a do contrato comercial; não aparecem
-- na vitrine nem na contratação online.
UPDATE plans SET is_public = FALSE, self_service = FALSE WHERE code IN ('STARTER','GROWTH','SCALE','OEM');
COMMENT ON TABLE plans IS 'Planos. STARTER/GROWTH/SCALE/OEM são de contrato (sem franquia nem preço de lista; cobrança pelo contrato, D-R15).';
