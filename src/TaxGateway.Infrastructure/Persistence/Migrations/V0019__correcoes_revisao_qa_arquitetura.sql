-- V0019: correções da revisão de QA e arquitetura de 26/11/2026 (D-R01 a D-R06).

-- D-R01: assinante da Stripe é cobrado pela ASSINATURA (mensalidade + medidores); o fechamento mensal só registra o
-- período para reconciliação, com o status SUBSCRIPTION, e nunca emite fatura avulsa para ele.
ALTER TABLE billing_periods DROP CONSTRAINT billing_periods_status_check;
ALTER TABLE billing_periods ADD CONSTRAINT billing_periods_status_check
    CHECK (status IN ('OPEN','CLOSING','CLOSED','CHARGING','INVOICED','SUBSCRIPTION'));

-- D-R02: eventos de assinatura fora de ordem — guarda o "created" do último evento aplicado; evento mais antigo é ignorado.
ALTER TABLE tenants ADD COLUMN subscription_event_at BIGINT;
GRANT UPDATE (subscription_event_at) ON tenants TO tg_billing;
GRANT SELECT (subscription_event_at) ON tenants TO tg_billing;

-- D-R05: consumo medido com estado pendente — o intervalo e o identificador são gravados ANTES de enviar à Stripe;
-- uma falha depois do envio reenvia o MESMO identificador (a Stripe deduplica), nunca um novo.
ALTER TABLE stripe_usage_reports
    ADD COLUMN pending_qty        BIGINT,
    ADD COLUMN pending_identifier TEXT,
    ADD COLUMN pending_at         TIMESTAMPTZ;

-- D-R03: tentativas de MFA por sessão (5 erros encerram a sessão; a senha volta a ser exigida).
ALTER TABLE user_sessions ADD COLUMN mfa_failures INT NOT NULL DEFAULT 0;

-- D-R04: quem criou cada operador — o aprovador de uma alíquota não pode ter sido criado pelo proponente (nem o contrário).
ALTER TABLE users ADD COLUMN operator_created_by TEXT;
