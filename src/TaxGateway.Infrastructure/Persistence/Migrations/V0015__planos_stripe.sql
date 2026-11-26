-- V0015: cadastro de planos pelo backoffice (com histórico) e cobrança pela Stripe no lugar do Asaas (D-C01 a D-C08).

-- ============================================================================================ planos editáveis
-- Regra D-204 mantida: o backoffice NÃO faz UPDATE direto em plans. Toda edição passa por app.save_plan, que grava a
-- versão anterior em plan_history (quem, quando, o quê) — a trilha de preços que a fatura de cada mês usou.
CREATE TABLE plan_history (
    id          BIGSERIAL PRIMARY KEY,
    code        TEXT NOT NULL,
    data        JSONB NOT NULL,          -- linha completa do plano ANTES da mudança (ou a nova, na criação)
    changed_by  TEXT NOT NULL,
    changed_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    action      TEXT NOT NULL CHECK (action IN ('create','update'))
);
CREATE INDEX ON plan_history (code, changed_at DESC);
GRANT SELECT ON plan_history TO tg_admin;

GRANT SELECT, INSERT, UPDATE ON plans TO tg_scheduler;
GRANT SELECT, INSERT ON plan_history TO tg_scheduler;
GRANT USAGE ON SEQUENCE plan_history_id_seq TO tg_scheduler;
SET ROLE tg_scheduler;
CREATE FUNCTION app.save_plan(p_code TEXT, p JSONB, p_operator TEXT)
RETURNS TEXT
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
DECLARE
  old JSONB;
BEGIN
  IF p_code !~ '^[A-Z][A-Z0-9_]{1,29}$' THEN
    RAISE EXCEPTION 'código de plano inválido' USING ERRCODE = '22023';
  END IF;
  SELECT to_jsonb(x) INTO old FROM public.plans x WHERE x.code = p_code;
  IF old IS NULL THEN
    INSERT INTO public.plans (code, rpm, burst, concurrency, batch_max_docs, batch_max_bytes, queue_priority, included_docs, price_cents)
    VALUES (p_code, 60, 10, 2, 100, 10485760, 'normal', 0, 0);
    INSERT INTO public.plan_history (code, data, changed_by, action) VALUES (p_code, p, p_operator, 'create');
  ELSE
    INSERT INTO public.plan_history (code, data, changed_by, action) VALUES (p_code, old, p_operator, 'update');
  END IF;

  UPDATE public.plans SET
    name                   = coalesce(p->>'name', name),
    description            = coalesce(p->>'description', description),
    features               = coalesce(p->'features', features),
    price_cents            = coalesce((p->>'price_cents')::bigint, price_cents),
    included_docs          = coalesce((p->>'included_docs')::bigint, included_docs),
    included_simulations   = coalesce((p->>'included_simulations')::bigint, included_simulations),
    included_cnpjs         = CASE WHEN p ? 'included_cnpjs' THEN (p->>'included_cnpjs')::int ELSE included_cnpjs END,
    max_users              = CASE WHEN p ? 'max_users' THEN (p->>'max_users')::int ELSE max_users END,
    overage_doc_millicents = coalesce((p->>'overage_doc_millicents')::bigint, overage_doc_millicents),
    overage_sim_millicents = coalesce((p->>'overage_sim_millicents')::bigint, overage_sim_millicents),
    cnpj_price_cents       = coalesce((p->>'cnpj_price_cents')::bigint, cnpj_price_cents),
    rpm                    = coalesce((p->>'rpm')::int, rpm),
    burst                  = coalesce((p->>'burst')::int, burst),
    concurrency            = coalesce((p->>'concurrency')::int, concurrency),
    batch_max_docs         = coalesce((p->>'batch_max_docs')::int, batch_max_docs),
    batch_max_bytes        = coalesce((p->>'batch_max_bytes')::bigint, batch_max_bytes),
    queue_priority         = coalesce(p->>'queue_priority', queue_priority),
    bill_schema_errors     = coalesce((p->>'bill_schema_errors')::boolean, bill_schema_errors),
    hard_quota             = coalesce((p->>'hard_quota')::boolean, hard_quota),
    dfe_enabled            = coalesce((p->>'dfe_enabled')::boolean, dfe_enabled),
    is_public              = coalesce((p->>'is_public')::boolean, is_public),
    self_service           = coalesce((p->>'self_service')::boolean, self_service),
    active                 = coalesce((p->>'active')::boolean, active),
    sort_order             = coalesce((p->>'sort_order')::int, sort_order),
    updated_at             = now(),
    updated_by             = p_operator
  WHERE code = p_code;
  RETURN p_code;
END $$;
RESET ROLE;
REVOKE ALL ON FUNCTION app.save_plan(TEXT, JSONB, TEXT) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.save_plan(TEXT, JSONB, TEXT) TO tg_admin;

-- Ids da Stripe nos planos são gravados pela sincronização (worker, tg_billing), não pelo operador.
GRANT SELECT ON plans TO tg_billing;
GRANT UPDATE (stripe_product_id, stripe_price_id, stripe_metered_price_id) ON plans TO tg_billing;
ALTER TABLE plans ADD COLUMN stripe_synced_at TIMESTAMPTZ;
ALTER TABLE plans ADD COLUMN stripe_cnpj_price_id TEXT;
GRANT UPDATE (stripe_synced_at, stripe_cnpj_price_id) ON plans TO tg_billing;

-- ============================================================================================ Stripe
-- Assinatura por tenant: a Stripe é a fonte da cobrança (mensalidade fixa + excedente medido por Billing Meters).
ALTER TABLE tenants ALTER COLUMN billing_provider SET DEFAULT 'STRIPE';
ALTER TABLE tenants
    ADD COLUMN stripe_subscription_id  TEXT,
    ADD COLUMN subscription_status     TEXT CHECK (subscription_status IN ('incomplete','incomplete_expired','trialing','active','past_due','canceled','unpaid','paused')),
    ADD COLUMN current_period_end      TIMESTAMPTZ;
GRANT UPDATE (plan_code, billing_customer_ref, billing_provider, stripe_subscription_id, subscription_status, current_period_end, status) ON tenants TO tg_billing;
CREATE POLICY billing_update ON tenants FOR UPDATE TO tg_billing USING (true) WITH CHECK (true);

-- Eventos recebidos da Stripe (webhook): a API só verifica a assinatura e grava; os workers processam (idempotente pelo id).
CREATE TABLE stripe_events (
    id            TEXT PRIMARY KEY,                 -- evt_...
    type          TEXT NOT NULL,
    payload       JSONB NOT NULL,
    received_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    processed_at  TIMESTAMPTZ,
    attempts      INT NOT NULL DEFAULT 0,
    last_error    TEXT
);
CREATE INDEX stripe_events_pending ON stripe_events (received_at) WHERE processed_at IS NULL;
GRANT INSERT, SELECT (id) ON stripe_events TO tg_app;   -- SELECT do id: exigido pelo ON CONFLICT (id)
GRANT SELECT, UPDATE ON stripe_events TO tg_billing;

-- Faturas da Stripe espelhadas para o portal ("Plano e cobrança").
CREATE TABLE invoices (
    id                  TEXT PRIMARY KEY,           -- in_...
    tenant_id           UUID NOT NULL REFERENCES tenants(id),
    number              TEXT,
    status              TEXT NOT NULL,              -- draft, open, paid, uncollectible, void
    currency            TEXT NOT NULL DEFAULT 'brl',
    amount_due_cents    BIGINT NOT NULL DEFAULT 0,
    amount_paid_cents   BIGINT NOT NULL DEFAULT 0,
    hosted_invoice_url  TEXT,
    invoice_pdf         TEXT,
    period_start        TIMESTAMPTZ,
    period_end          TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX ON invoices (tenant_id, created_at DESC);

-- Uso já reportado à Stripe por competência e métrica (o reporte é incremental e idempotente).
CREATE TABLE stripe_usage_reports (
    tenant_id      UUID NOT NULL REFERENCES tenants(id),
    billing_month  DATE NOT NULL,
    metric         TEXT NOT NULL CHECK (metric IN ('documentos','cnpjs')),
    reported_qty   BIGINT NOT NULL DEFAULT 0,
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, billing_month, metric)
);

DO $$
DECLARE t TEXT;
BEGIN
  FOREACH t IN ARRAY ARRAY['invoices','stripe_usage_reports'] LOOP
    EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON public.%I TO tg_app, tg_worker
                    USING (tenant_id = (SELECT app.current_tenant()))
                    WITH CHECK (tenant_id = (SELECT app.current_tenant()))', t);
    EXECUTE format('CREATE POLICY billing_all ON public.%I FOR ALL TO tg_billing USING (true) WITH CHECK (true)', t);
  END LOOP;
END $$;
GRANT SELECT ON invoices TO tg_app;
GRANT SELECT, INSERT, UPDATE ON invoices, stripe_usage_reports TO tg_billing;
GRANT SELECT ON usage_ledger, tenant_cnpjs, cnpj_monthly_activity TO tg_billing;

-- O portal cria o cliente na Stripe na primeira contratação; o papel da API só grava o id por esta função (e só uma vez).
GRANT UPDATE (billing_customer_ref, billing_provider) ON tenants TO tg_scheduler;
CREATE POLICY scheduler_billing_customer ON tenants FOR UPDATE TO tg_scheduler USING (true) WITH CHECK (true);
SET ROLE tg_scheduler;
CREATE FUNCTION app.set_billing_customer(p_tenant UUID, p_customer TEXT)
RETURNS TEXT
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  UPDATE public.tenants SET billing_customer_ref = coalesce(billing_customer_ref, p_customer), billing_provider = 'STRIPE'
   WHERE id = p_tenant
  RETURNING billing_customer_ref
$$;
RESET ROLE;
REVOKE ALL ON FUNCTION app.set_billing_customer(UUID, TEXT) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.set_billing_customer(UUID, TEXT) TO tg_app;
