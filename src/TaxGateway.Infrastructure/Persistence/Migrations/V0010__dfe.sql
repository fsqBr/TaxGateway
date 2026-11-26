-- V0010: captura automática de DF-e e custódia do certificado A1 (SDD §4.2, §6.5; decisões D-901 a D-907).

-- ---------- Certificado A1 ----------
-- pfx_ciphertext/password_ciphertext: AES-256-GCM (nonce || tag || cifra) com a DEK do certificado; dek_wrapped: DEK
-- embrulhada pela KEK do tenant no cofre. Um certificado ativo por CNPJ (o upload novo substitui o anterior).
ALTER TABLE certificates
    ADD COLUMN subject              TEXT,
    ADD COLUMN expiry_alerted_days  INT;          -- menor limiar já avisado (30, 15, 5); NULL = nenhum
CREATE UNIQUE INDEX certificates_one_per_cnpj ON certificates (tenant_id, cnpj);
DROP INDEX IF EXISTS certificates_tenant_id_cnpj_idx;
GRANT UPDATE (expiry_alerted_days) ON certificates TO tg_worker;

-- ---------- Estado da distribuição por CNPJ (NT 2014.002) ----------
ALTER TABLE dfe_sync_state
    ADD COLUMN enabled           BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN c_uf_autor        SMALLINT NOT NULL DEFAULT 35,   -- UF do interessado (cUFAutor)
    ADD COLUMN manifest_ciencia  BOOLEAN NOT NULL DEFAULT TRUE,  -- resNFe → Ciência da Operação (configurável)
    ADD COLUMN window_start      TIMESTAMPTZ,                    -- janela de 1 h para o teto de 20 consultas
    ADD COLUMN window_count      INT NOT NULL DEFAULT 0,
    ADD COLUMN last_query_at     TIMESTAMPTZ,
    ADD COLUMN last_656_at       TIMESTAMPTZ,
    ADD COLUMN lease_until       TIMESTAMPTZ;
GRANT INSERT, UPDATE (enabled, c_uf_autor, manifest_ciencia, next_allowed_at) ON dfe_sync_state TO tg_app;

-- ---------- Documentos recebidos pela distribuição ----------
CREATE TABLE dfe_documents (
    tenant_id      UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    cnpj           cnpj_t NOT NULL,
    nsu            BIGINT NOT NULL,
    schema         TEXT NOT NULL,                 -- resNFe_v1.01.xsd, procNFe_v4.00.xsd, resEvento_v1.01.xsd...
    kind           TEXT NOT NULL CHECK (kind IN ('RESUMO','COMPLETO','EVENTO','OUTRO')),
    access_key     access_key_t,
    status         TEXT NOT NULL CHECK (status IN ('RECEBIDO','CIENCIA_ENVIADA','CIENCIA_FALHOU','AUDITADO','REJEITADO','IGNORADO')),
    audit_status   TEXT,
    detail         TEXT,
    received_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, cnpj, nsu)
);
CREATE INDEX ON dfe_documents (tenant_id, access_key);
ALTER TABLE dfe_documents ENABLE ROW LEVEL SECURITY;
ALTER TABLE dfe_documents FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON dfe_documents TO tg_app, tg_worker
    USING (tenant_id = (SELECT app.current_tenant()))
    WITH CHECK (tenant_id = (SELECT app.current_tenant()));
GRANT SELECT ON dfe_documents TO tg_app;
GRANT SELECT, INSERT, UPDATE (status, audit_status, detail) ON dfe_documents TO tg_worker;

-- ---------- Agendador: CNPJs liberados para consulta ----------
-- O "agora" vem da aplicação (TimeProvider): o mesmo relógio decide o bloqueio de 1 h, o teto de 20/h e o lease.
GRANT SELECT, UPDATE ON dfe_sync_state TO tg_scheduler;
CREATE POLICY scheduler_claim ON dfe_sync_state FOR ALL TO tg_scheduler USING (true) WITH CHECK (true);
GRANT SELECT ON certificates TO tg_scheduler;
CREATE POLICY scheduler_claim ON certificates FOR SELECT TO tg_scheduler USING (true);

CREATE FUNCTION app.claim_due_dfe(p_now TIMESTAMPTZ, p_limit INT, p_lease_seconds INT)
RETURNS TABLE (tenant_id UUID, cnpj TEXT)
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  UPDATE public.dfe_sync_state d SET lease_until = p_now + make_interval(secs => p_lease_seconds)
    FROM (SELECT x.tenant_id, x.cnpj FROM public.dfe_sync_state x
           WHERE x.enabled AND x.next_allowed_at <= p_now AND (x.lease_until IS NULL OR x.lease_until <= p_now)
             AND EXISTS (SELECT 1 FROM public.certificates c WHERE c.tenant_id = x.tenant_id AND c.cnpj = x.cnpj AND c.not_after > p_now)
           ORDER BY x.next_allowed_at LIMIT p_limit FOR UPDATE SKIP LOCKED) s
   WHERE d.tenant_id = s.tenant_id AND d.cnpj = s.cnpj
  RETURNING d.tenant_id, d.cnpj::text
$$;

-- Certificados perto do vencimento (alertas 30/15/5 dias): varredura entre tenants só do que o alerta precisa.
CREATE FUNCTION app.certificates_expiring(p_now TIMESTAMPTZ)
RETURNS TABLE (tenant_id UUID, cnpj TEXT, not_after TIMESTAMPTZ, expiry_alerted_days INT)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  SELECT c.tenant_id, c.cnpj::text, c.not_after, c.expiry_alerted_days FROM public.certificates c
   WHERE c.not_after <= p_now + interval '30 days'
     AND (c.expiry_alerted_days IS NULL OR c.expiry_alerted_days > 5)
$$;

REVOKE ALL ON FUNCTION app.claim_due_dfe(TIMESTAMPTZ, INT, INT) FROM PUBLIC;
REVOKE ALL ON FUNCTION app.certificates_expiring(TIMESTAMPTZ) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.claim_due_dfe(TIMESTAMPTZ, INT, INT) TO tg_worker;
GRANT EXECUTE ON FUNCTION app.certificates_expiring(TIMESTAMPTZ) TO tg_worker;
ALTER FUNCTION app.claim_due_dfe(TIMESTAMPTZ, INT, INT) OWNER TO tg_scheduler;
ALTER FUNCTION app.certificates_expiring(TIMESTAMPTZ) OWNER TO tg_scheduler;
