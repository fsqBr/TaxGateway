-- V0007: faturamento (SDD §10; decisões D-701 a D-705).

-- Preço do excedente e cobrança de SCHEMA_ERROR por plano (§10.1, §15.2 "Comercial"). Valores definidos pelo
-- comercial (zerados na carga inicial, como o preço base).
ALTER TABLE plans
    ADD COLUMN overage_doc_millicents  BIGINT  NOT NULL DEFAULT 0 CHECK (overage_doc_millicents >= 0),   -- por documento acima da franquia (1/1000 de centavo)
    ADD COLUMN overage_sim_millicents  BIGINT  NOT NULL DEFAULT 0 CHECK (overage_sim_millicents >= 0),   -- por simulação
    ADD COLUMN cnpj_price_cents        BIGINT  NOT NULL DEFAULT 0 CHECK (cnpj_price_cents >= 0),         -- por CNPJ ativo acima do incluído (OEM)
    ADD COLUMN bill_schema_errors      BOOLEAN NOT NULL DEFAULT TRUE;

-- Notas de ajuste: nunca editar a v1 já faturada; cada ajuste é uma versão nova (v2, v3...) com chave idempotente.
CREATE TABLE billing_adjustments (
    tenant_id                 UUID NOT NULL REFERENCES tenants(id),
    billing_month             month_t NOT NULL,
    version                   INT NOT NULL CHECK (version >= 2),
    amount_delta_cents        BIGINT NOT NULL,             -- positivo = débito; negativo = crédito
    reason                    TEXT NOT NULL,
    created_by                TEXT NOT NULL,
    provider_ref              TEXT,
    provider_idempotency_key  TEXT NOT NULL UNIQUE,         -- tenant:month:v{n}
    status                    TEXT NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING','INVOICED')),
    created_at                TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, billing_month, version),
    FOREIGN KEY (tenant_id, billing_month) REFERENCES billing_periods (tenant_id, billing_month)
);
CREATE INDEX ON billing_adjustments (tenant_id);

ALTER TABLE billing_adjustments ENABLE ROW LEVEL SECURITY;
ALTER TABLE billing_adjustments FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON billing_adjustments TO tg_app, tg_worker
    USING (tenant_id = (SELECT app.current_tenant())) WITH CHECK (tenant_id = (SELECT app.current_tenant()));
CREATE POLICY billing_rw ON billing_adjustments TO tg_billing USING (true) WITH CHECK (true);
GRANT SELECT ON billing_adjustments TO tg_app;
GRANT SELECT, INSERT, UPDATE ON billing_adjustments TO tg_billing;

-- O fechamento precisa do plano (preços) e do cliente no provedor.
GRANT SELECT (id, plan_code, limits_override, billing_provider, billing_customer_ref, status) ON tenants TO tg_billing;
