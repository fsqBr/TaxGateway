-- V0009: direitos do titular (SDD §4.3; LGPD art. 18; decisão D-H05).
-- O controlador (tenant) localiza os documentos de um CPF pelo HMAC (chave do tenant no cofre, fora do banco) e pede o
-- expurgo: o XML é apagado do S3 e o vínculo com o CPF some. O registro fiscal (valores, achados) fica, sem dado pessoal.

-- Expurgo: só estas duas colunas podem ser alteradas pela API, e só no próprio tenant (RLS tenant_isolation da V0003).
GRANT UPDATE (cpf_hmac, xml_storage_key) ON fiscal_documents TO tg_app;
CREATE INDEX fiscal_documents_cpf ON fiscal_documents (tenant_id, cpf_hmac) WHERE cpf_hmac IS NOT NULL;

-- Registro das solicitações (prestação de contas, LGPD art. 37). Guarda o HMAC, nunca o CPF.
CREATE TABLE titular_requests (
    id            UUID PRIMARY KEY,
    tenant_id     UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    cpf_hmac      BYTEA NOT NULL,
    action        TEXT NOT NULL CHECK (action IN ('LOCALIZAR','EXPURGAR')),
    documents     INT NOT NULL,
    xml_deleted   INT NOT NULL DEFAULT 0,
    requested_by  TEXT NOT NULL,                 -- 'api-key:<id>' ou 'oauth:<client_id>'
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX ON titular_requests (tenant_id, created_at DESC);
ALTER TABLE titular_requests ENABLE ROW LEVEL SECURITY;
ALTER TABLE titular_requests FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON titular_requests TO tg_app, tg_worker   -- convenção da V0003 (teste de cobertura de RLS)
    USING (tenant_id = (SELECT app.current_tenant()))
    WITH CHECK (tenant_id = (SELECT app.current_tenant()));
GRANT SELECT, INSERT ON titular_requests TO tg_app;
