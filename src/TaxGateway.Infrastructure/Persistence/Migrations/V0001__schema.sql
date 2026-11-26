-- V0001: schema base (SDD §9). Executada por tg_owner. Extensões e roles vêm do bootstrap (infra/postgres/init).
-- A partir da Fase 1 estas migrations são a fonte de verdade do modelo de dados (D-101); o SQL do §9 do SDD é ilustrativo.

CREATE SCHEMA app;

-- Contexto de tenant seguro ('' ou ausente => NULL => zero linhas, sem erro de cast)
CREATE FUNCTION app.current_tenant() RETURNS uuid
LANGUAGE sql STABLE PARALLEL SAFE AS
$$ SELECT NULLIF(current_setting('app.current_tenant_id', true), '')::uuid $$;

-- Domínios
CREATE DOMAIN cnpj_t       AS CHAR(14) CHECK (VALUE ~ '^[A-Z0-9]{12}[0-9]{2}$');
CREATE DOMAIN access_key_t AS CHAR(44) CHECK (VALUE ~ '^[0-9]{6}[A-Z0-9]{12}[0-9]{26}$');
CREATE DOMAIN month_t      AS DATE     CHECK (EXTRACT(DAY FROM VALUE) = 1);

-- ===================== Globais (sem tenant_id) =====================
CREATE TABLE plans (
    code             TEXT PRIMARY KEY,           -- STARTER, GROWTH, SCALE, OEM, ENTERPRISE
    rpm              INT NOT NULL,
    burst            INT NOT NULL,
    concurrency      INT NOT NULL,
    batch_max_docs   INT NOT NULL,
    batch_max_bytes  BIGINT NOT NULL,
    queue_priority   TEXT NOT NULL,
    included_docs    BIGINT NOT NULL,
    included_cnpjs   INT,
    price_cents      BIGINT NOT NULL
);

CREATE TABLE engine_bundles (
    bundle       TEXT PRIMARY KEY,
    manifest     JSONB NOT NULL,
    db_sha256    TEXT NOT NULL,
    approved_by  TEXT NOT NULL,
    deployed_at  TIMESTAMPTZ,
    retired_at   TIMESTAMPTZ
);

CREATE TABLE engine_routing (                    -- §8.1
    crt               TEXT NOT NULL,
    tp_regime_ibscbs  TEXT NOT NULL DEFAULT '-',  -- '-' = ausente
    engines           TEXT[] NOT NULL DEFAULT '{}',
    simples_mode      TEXT CHECK (simples_mode IN ('SN_PLENO','SN_SUBLIMITE','SOMENTE_RECEITA')),
    note              TEXT,
    PRIMARY KEY (crt, tp_regime_ibscbs)
);

CREATE TABLE param_changes (                     -- §15: trilha de toda mudança de parâmetro
    id             UUID PRIMARY KEY,
    domain         TEXT NOT NULL,
    scope          TEXT NOT NULL,                -- GLOBAL | PLAN:<code> | TENANT:<id> | CNPJ:<cnpj>
    payload        JSONB NOT NULL,
    justification  TEXT NOT NULL,
    proposed_by    TEXT NOT NULL,
    approved_by    TEXT,
    status         TEXT NOT NULL CHECK (status IN ('PROPOSED','APPROVED','REJECTED','APPLIED')),
    effective_at   TIMESTAMPTZ NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK (approved_by IS NULL OR approved_by <> proposed_by)
);

CREATE TABLE rate_catalog (                      -- §8.5
    id               UUID PRIMARY KEY,
    tributo          TEXT NOT NULL CHECK (tributo IN ('CBS','IBS_UF','IBS_MUN','IS')),
    tipo             TEXT NOT NULL CHECK (tipo IN ('REFERENCIA','PROPRIA_ENTE')),
    esfera           TEXT NOT NULL CHECK (esfera IN ('UNIAO','UF','MUNICIPIO')),
    ente_codigo      TEXT CHECK (ente_codigo ~ '^([0-9]{2}|[0-9]{7})$'),
    forma_aplicacao  TEXT CHECK (forma_aplicacao IN ('SUBSTITUICAO','ACRESCIMO','DECRESCIMO')),
    aliquota         NUMERIC(9,4) NOT NULL CHECK (aliquota >= 0),
    valid_from       DATE NOT NULL,
    valid_to         DATE,
    ato_legal        TEXT NOT NULL,
    fonte            TEXT NOT NULL CHECK (fonte IN ('SENADO','TCU','CG_IBS','LEI_ENTE','BUNDLE_RFB')),
    change_id        UUID NOT NULL REFERENCES param_changes(id),
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK ((tipo = 'REFERENCIA') = (ente_codigo IS NULL)),
    CHECK ((tipo = 'PROPRIA_ENTE') = (forma_aplicacao IS NOT NULL)),
    CHECK (valid_to IS NULL OR valid_to > valid_from),
    EXCLUDE USING gist (tributo WITH =, tipo WITH =, (COALESCE(ente_codigo, '')) WITH =,
                        daterange(valid_from, valid_to) WITH &&)
);

CREATE TABLE param_snapshots (
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    content_sha256  TEXT NOT NULL UNIQUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE credit_rules (
    valid_from      DATE PRIMARY KEY,
    default_status  TEXT NOT NULL CHECK (default_status IN ('APROPRIAVEL_POR_DESTAQUE','PENDENTE_EXTINCAO')),
    ato_legal       TEXT NOT NULL
);

-- ===================== Tenancy =====================
CREATE TABLE tenants (
    id                    UUID PRIMARY KEY,      -- UUIDv7 gerado na aplicação
    name                  TEXT NOT NULL,
    root_cnpj             cnpj_t NOT NULL UNIQUE,
    plan_code             TEXT NOT NULL REFERENCES plans(code),
    limits_override       JSONB NOT NULL DEFAULT '{}',
    auto_provision_cnpj   BOOLEAN NOT NULL DEFAULT FALSE,
    xml_retention_days    INT CHECK (xml_retention_days > 0),   -- NULL = zero_retention
    kms_key_ref           TEXT NOT NULL,
    billing_provider      TEXT NOT NULL DEFAULT 'ASAAS' CHECK (billing_provider IN ('ASAAS','STRIPE')),
    billing_customer_ref  TEXT,
    status                TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','SUSPENDED','CLOSED')),
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE oauth_applications (
    client_id  TEXT PRIMARY KEY,
    tenant_id  UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE
);
CREATE INDEX ON oauth_applications (tenant_id);

CREATE TABLE api_keys (
    id            UUID PRIMARY KEY,
    tenant_id     UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    name          TEXT NOT NULL,
    key_prefix    TEXT NOT NULL,
    key_hash      BYTEA NOT NULL UNIQUE CHECK (octet_length(key_hash) = 32),  -- HMAC-SHA256(pepper_kms, key)
    scopes        TEXT[] NOT NULL,
    expires_at    TIMESTAMPTZ,
    revoked_at    TIMESTAMPTZ,
    last_used_at  TIMESTAMPTZ,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX ON api_keys (tenant_id);

CREATE TABLE tenant_cnpjs (
    tenant_id         UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    cnpj              cnpj_t NOT NULL,
    regime_hint       TEXT CHECK (regime_hint IN ('REGIME_REGULAR','SIMPLES_NACIONAL')),
    is_active         BOOLEAN NOT NULL DEFAULT TRUE,
    deactivated_from  month_t,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, cnpj)
);

CREATE TABLE tenant_audit_rule_settings (
    tenant_id  UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    rule_id    TEXT NOT NULL,
    enabled    BOOLEAN NOT NULL DEFAULT TRUE,
    severity   TEXT CHECK (severity IN ('INFO','WARNING','ERROR')),
    params     JSONB NOT NULL DEFAULT '{}',
    PRIMARY KEY (tenant_id, rule_id)
);

-- ===================== Documentos e simulações =====================
-- Deduplicação GLOBAL (não particionada): idempotência entre meses
CREATE TABLE document_registry (
    tenant_id            UUID NOT NULL REFERENCES tenants(id),
    idempotency_hash     BYTEA NOT NULL,          -- SHA256(kind || ':' || access_key)
    kind                 TEXT NOT NULL CHECK (kind IN ('OUTBOUND','INBOUND')),
    access_key           access_key_t NOT NULL,
    first_document_id    UUID NOT NULL,
    first_billing_month  month_t NOT NULL,
    latest_document_id   UUID NOT NULL,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, idempotency_hash)
);

CREATE TABLE idempotency_requests (              -- Idempotency-Key HTTP (24 h)
    tenant_id      UUID NOT NULL REFERENCES tenants(id),
    key            TEXT NOT NULL,
    request_hash   BYTEA NOT NULL,
    status_code    INT,
    response_body  BYTEA,                         -- comprimido
    expires_at     TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (tenant_id, key)
);
CREATE INDEX ON idempotency_requests (expires_at);

CREATE TABLE batches (
    id              UUID PRIMARY KEY,
    tenant_id       UUID NOT NULL REFERENCES tenants(id),
    status          TEXT NOT NULL CHECK (status IN ('QUEUED','RUNNING','COMPLETED','FAILED','REJECTED')),
    storage_key     TEXT NOT NULL,
    total_docs      INT,
    processed_docs  INT NOT NULL DEFAULT 0,
    failed_docs     INT NOT NULL DEFAULT 0,
    report_key      TEXT,
    rejection_code  TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at    TIMESTAMPTZ
);
CREATE INDEX ON batches (tenant_id, created_at DESC);

-- Auditorias (particionada por mês; partições criadas em V0003)
CREATE TABLE fiscal_documents (
    id                   UUID NOT NULL,           -- UUIDv7
    billing_month        DATE NOT NULL CHECK (EXTRACT(DAY FROM billing_month) = 1),  -- D-106: pg_partman não aceita domínio
    tenant_id            UUID NOT NULL REFERENCES tenants(id),
    batch_id             UUID,
    kind                 TEXT NOT NULL CHECK (kind IN ('OUTBOUND','INBOUND')),
    model                TEXT NOT NULL CHECK (model IN ('NFE55','NFCE65','NFSE')),
    access_key           access_key_t,
    cnpj_subject         cnpj_t NOT NULL,
    cnpj_counterparty    cnpj_t,
    is_consumidor_final  BOOLEAN NOT NULL DEFAULT FALSE,
    cpf_hmac             BYTEA,
    engine               TEXT NOT NULL,
    engine_bundle        TEXT NOT NULL,
    rules_version        TEXT NOT NULL,
    param_snapshot_id    BIGINT NOT NULL,         -- §15: parâmetros vigentes no cálculo (D-108)
    audit_status         TEXT NOT NULL CHECK (audit_status IN
                           ('CONFORME','DIVERGENTE','INDICIO','SCHEMA_ERROR','ASSINATURA_INVALIDA','INDETERMINADO')),
    declared             JSONB NOT NULL,
    recalculated         JSONB NOT NULL,
    difference_total     NUMERIC(15,2) NOT NULL DEFAULT 0,
    credit_potential     NUMERIC(15,2) NOT NULL DEFAULT 0,
    credit_status        TEXT,
    findings             JSONB NOT NULL DEFAULT '[]',
    xml_storage_key      TEXT,                    -- NULL em zero_retention
    xml_sha256           BYTEA NOT NULL,
    is_billable          BOOLEAN NOT NULL,
    reprocess_of         UUID,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (billing_month, id)
) PARTITION BY RANGE (billing_month);
CREATE INDEX ON fiscal_documents (tenant_id, billing_month, created_at DESC);
CREATE INDEX ON fiscal_documents (tenant_id, access_key);
CREATE INDEX ON fiscal_documents (tenant_id, batch_id) WHERE batch_id IS NOT NULL;

CREATE TABLE simulations (                       -- retenção de 13 meses (V0003)
    id                 UUID NOT NULL,
    billing_month      DATE NOT NULL CHECK (EXTRACT(DAY FROM billing_month) = 1),  -- D-106: pg_partman não aceita domínio
    tenant_id          UUID NOT NULL REFERENCES tenants(id),
    cnpj_subject       cnpj_t NOT NULL,
    engine             TEXT NOT NULL,
    engine_bundle      TEXT NOT NULL,
    param_snapshot_id  BIGINT NOT NULL,
    request            JSONB NOT NULL,
    result             JSONB NOT NULL,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (billing_month, id)
) PARTITION BY RANGE (billing_month);
CREATE INDEX ON simulations (tenant_id, billing_month, created_at DESC);

-- ===================== Billing =====================
CREATE TABLE usage_ledger (                      -- append-only (sem UPDATE/DELETE em runtime)
    id             BIGINT GENERATED ALWAYS AS IDENTITY,
    billing_month  DATE NOT NULL CHECK (EXTRACT(DAY FROM billing_month) = 1),  -- D-106: pg_partman não aceita domínio
    tenant_id      UUID NOT NULL REFERENCES tenants(id),
    metric         TEXT NOT NULL CHECK (metric IN ('DOC_AUDIT','SIMULATION')),
    cnpj_subject   cnpj_t NOT NULL,
    source_id      UUID NOT NULL,
    quantity       INT NOT NULL DEFAULT 1 CHECK (quantity > 0),
    occurred_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (billing_month, id),
    UNIQUE (billing_month, metric, source_id)
) PARTITION BY RANGE (billing_month);
CREATE INDEX ON usage_ledger (tenant_id, billing_month);

CREATE TABLE cnpj_monthly_activity (
    tenant_id      UUID NOT NULL REFERENCES tenants(id),
    billing_month  month_t NOT NULL,
    cnpj           cnpj_t NOT NULL,
    first_seen_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, billing_month, cnpj)
);

CREATE TABLE billing_periods (
    tenant_id                 UUID NOT NULL REFERENCES tenants(id),
    billing_month             month_t NOT NULL,
    status                    TEXT NOT NULL CHECK (status IN ('OPEN','CLOSING','CLOSED','INVOICED')),
    docs_count                BIGINT,
    simulations_count         BIGINT,
    active_cnpjs              INT,
    amount_cents              BIGINT,
    provider_ref              TEXT,
    provider_idempotency_key  TEXT UNIQUE,         -- tenant:month:v{n}
    closed_at                 TIMESTAMPTZ,
    PRIMARY KEY (tenant_id, billing_month)
);

-- ===================== Webhooks e outbox =====================
CREATE TABLE tenant_webhooks (
    id                  UUID PRIMARY KEY,
    tenant_id           UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    target_url          TEXT NOT NULL CHECK (target_url LIKE 'https://%'),
    secrets_ciphertext  BYTEA NOT NULL,
    dek_wrapped         BYTEA NOT NULL,
    subscribed_events   TEXT[] NOT NULL,
    is_active           BOOLEAN NOT NULL DEFAULT TRUE,
    disabled_reason     TEXT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX ON tenant_webhooks (tenant_id);

CREATE TABLE outbox (
    id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id     UUID NOT NULL REFERENCES tenants(id),
    topic         TEXT NOT NULL,
    payload       JSONB NOT NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    published_at  TIMESTAMPTZ
);
CREATE INDEX outbox_pending ON outbox (id) WHERE published_at IS NULL;

CREATE TABLE webhook_deliveries (
    id                UUID PRIMARY KEY,
    tenant_id         UUID NOT NULL REFERENCES tenants(id),
    webhook_id        UUID NOT NULL REFERENCES tenant_webhooks(id) ON DELETE CASCADE,
    event             TEXT NOT NULL,
    payload           JSONB NOT NULL,
    status            TEXT NOT NULL CHECK (status IN ('PENDING','DELIVERED','RETRYING','DEAD')),
    attempts          INT NOT NULL DEFAULT 0,
    next_attempt_at   TIMESTAMPTZ,
    last_status_code  INT,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX ON webhook_deliveries (tenant_id, created_at DESC);

-- ===================== DF-e / Certificados (Fase 9) =====================
CREATE TABLE certificates (
    id                   UUID PRIMARY KEY,
    tenant_id            UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    cnpj                 cnpj_t NOT NULL,
    pfx_ciphertext       BYTEA NOT NULL,
    password_ciphertext  BYTEA NOT NULL,
    dek_wrapped          BYTEA NOT NULL,
    thumbprint           TEXT NOT NULL,
    not_after            TIMESTAMPTZ NOT NULL,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX ON certificates (tenant_id, cnpj);

CREATE TABLE dfe_sync_state (
    tenant_id        UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    cnpj             cnpj_t NOT NULL,
    ult_nsu          BIGINT NOT NULL DEFAULT 0,
    max_nsu          BIGINT,
    next_allowed_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_cstat       TEXT,
    PRIMARY KEY (tenant_id, cnpj)
);
