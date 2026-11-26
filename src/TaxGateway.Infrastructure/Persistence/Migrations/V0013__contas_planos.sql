-- V0013: contas de usuário com MFA, sessões, convites e papéis (D-U01 a D-U08); planos comerciais editáveis (D-C01).
-- Pedido do usuário: cadastro simples para acessar o sistema sem colar API key no navegador, MFA obrigatório, planos
-- com valores no backoffice. As API keys continuam sendo o acesso de MÁQUINA (ERP → API); pessoas entram por conta.

-- ============================================================================================ planos comerciais
ALTER TABLE plans
    ADD COLUMN name                   TEXT    NOT NULL DEFAULT '',
    ADD COLUMN description            TEXT    NOT NULL DEFAULT '',
    ADD COLUMN features               JSONB   NOT NULL DEFAULT '[]',     -- lista de textos para a página de planos
    ADD COLUMN included_simulations   BIGINT  NOT NULL DEFAULT 0,
    ADD COLUMN max_users              INT,                                -- NULL = ilimitado
    ADD COLUMN hard_quota             BOOLEAN NOT NULL DEFAULT FALSE,     -- TRUE = sem excedente: bloqueia na franquia (sandbox)
    ADD COLUMN dfe_enabled            BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN is_public              BOOLEAN NOT NULL DEFAULT FALSE,     -- aparece na página de planos
    ADD COLUMN self_service           BOOLEAN NOT NULL DEFAULT FALSE,     -- contratável sem falar com vendas
    ADD COLUMN sort_order             INT     NOT NULL DEFAULT 100,
    ADD COLUMN active                 BOOLEAN NOT NULL DEFAULT TRUE,      -- FALSE = não aceita novos clientes (legado)
    ADD COLUMN stripe_product_id      TEXT,
    ADD COLUMN stripe_price_id        TEXT,                               -- preço fixo mensal (recorrente)
    ADD COLUMN stripe_metered_price_id TEXT,                              -- excedente de documentos (Billing Meter)
    ADD COLUMN updated_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    ADD COLUMN updated_by             TEXT;

-- Planos legados (STARTER/GROWTH/SCALE) continuam valendo para quem já os tem, mas saem da vitrine.
UPDATE plans SET name = initcap(lower(code)), active = FALSE, sort_order = 900 WHERE code IN ('STARTER','GROWTH','SCALE');
UPDATE plans SET name = 'OEM (contrato)', sort_order = 800 WHERE code = 'OEM';

-- Planos de mercado (valores sugeridos — editáveis no backoffice; D-C01). Moeda: centavos de real; millicents = 1/1000 de centavo.
INSERT INTO plans (code, name, description, features, rpm, burst, concurrency, batch_max_docs, batch_max_bytes, queue_priority,
                   included_docs, included_simulations, included_cnpjs, max_users, price_cents, overage_doc_millicents,
                   overage_sim_millicents, cnpj_price_cents, bill_schema_errors, hard_quota, dfe_enabled, is_public, self_service, sort_order) VALUES
('SANDBOX', 'Sandbox', 'Para testar a integração com o motor oficial, sem custo.',
 '["Motor oficial da Receita (mesmo de produção)","200 auditorias e 2.000 simulações por mês","1 CNPJ","2 usuários","Webhooks e lotes pequenos","Suporte pela central de ajuda"]',
  60,   5,  2,    100,    10485760, 'low',        200,    2000,  1,    2,      0,     0,    0,    0, TRUE,  TRUE,  FALSE, TRUE, TRUE, 10),
('ESSENCIAL', 'Essencial', 'Para pequenas empresas e o Simples Nacional conferirem as notas do mês.',
 '["2.000 documentos auditados por mês","20.000 simulações","3 CNPJs","3 usuários","Auditoria de saída e de entrada","Lotes até 2.000 notas","R$ 0,08 por documento excedente"]',
 300,  20,  5,   2000,    52428800, 'normal',    2000,   20000,  3,    3,  14900,  8000,  400, 2900, TRUE, FALSE, FALSE, TRUE, TRUE, 20),
('PROFISSIONAL', 'Profissional', 'Para empresas do regime regular com volume e captura automática de notas.',
 '["15.000 documentos auditados por mês","150.000 simulações","10 CNPJs","10 usuários","Captura de DF-e com certificado A1","Lotes até 25.000 notas","R$ 0,05 por documento excedente"]',
 1200,  60, 15,  25000,   524288000, 'normal',  15000,  150000, 10,   10,  59000,  5000,  200, 1900, TRUE, FALSE, TRUE,  TRUE, TRUE, 30),
('ESCRITORIO', 'Escritório contábil', 'Para escritórios que conferem as notas de muitos clientes.',
 '["30.000 documentos auditados por mês","50 CNPJs de clientes","Usuários ilimitados","Captura de DF-e","Relatórios por cliente","R$ 0,04 por documento excedente","R$ 12 por CNPJ extra"]',
 1200,  60, 15,  25000,   524288000, 'normal',  30000,  100000, 50, NULL,  99000,  4000,  200, 1200, TRUE, FALSE, TRUE,  TRUE, TRUE, 40),
('PARCEIRO_OEM', 'Software house (OEM)', 'Para ERPs e software houses que atendem muitos CNPJs com uma integração.',
 '["100.000 documentos por mês","CNPJs ilimitados (R$ 4 por CNPJ ativo)","Uso separado por CNPJ","Prioridade máxima na fila","OAuth 2.0 e API keys por sistema","R$ 0,02 por documento excedente"]',
 3000, 100, 30, 100000,  2147483648, 'realtime', 100000, 1000000, 0, NULL, 249000,  2000,   50,  400, FALSE, FALSE, TRUE, TRUE, FALSE, 50),
('ENTERPRISE', 'Enterprise', 'Para grandes empresas: contrato, SLA, SSO e suporte dedicado.',
 '["Volume sob contrato","SLA de 99,9%","SSO corporativo","Regras de auditoria configuráveis","Suporte dedicado"]',
 3000, 100, 30, 100000,  2147483648, 'realtime',      0,       0, NULL, NULL,  0,     0,    0,    0, TRUE, FALSE, TRUE,  TRUE, FALSE, 60)
ON CONFLICT (code) DO UPDATE SET
    name = EXCLUDED.name, description = EXCLUDED.description, features = EXCLUDED.features, is_public = EXCLUDED.is_public,
    self_service = EXCLUDED.self_service, sort_order = EXCLUDED.sort_order;

GRANT SELECT ON plans TO tg_app;
-- Edição de planos só pela função com histórico (V0014, D-C02); o backoffice não faz UPDATE direto (D-204).

-- ============================================================================================ contas e sessões
-- Tabelas globais de identidade (sem tenant_id): acessadas pelo papel da API (tg_app) com SQL parametrizado; os vínculos
-- com tenants (user_memberships) têm RLS como toda tabela com tenant_id.
CREATE TABLE users (
    id                 UUID PRIMARY KEY,
    realm              TEXT NOT NULL CHECK (realm IN ('customer','operator')),
    email              TEXT NOT NULL,
    name               TEXT NOT NULL,
    password_hash      BYTEA NOT NULL,              -- Argon2id
    password_salt      BYTEA NOT NULL,
    email_verified_at  TIMESTAMPTZ,
    totp_secret_enc    BYTEA,                       -- AES-GCM com chave derivada do pepper (nunca em claro no banco)
    totp_enabled_at    TIMESTAMPTZ,
    totp_last_step     BIGINT NOT NULL DEFAULT 0,   -- anti-replay do código TOTP
    operator_scopes    TEXT[] NOT NULL DEFAULT '{}',
    failed_logins      INT NOT NULL DEFAULT 0,
    locked_until       TIMESTAMPTZ,
    status             TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','DISABLED')),
    created_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_login_at      TIMESTAMPTZ,
    password_changed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK (email = lower(email))
);
CREATE UNIQUE INDEX users_realm_email ON users (realm, email);

CREATE TABLE user_memberships (
    user_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    tenant_id   UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    role        TEXT NOT NULL CHECK (role IN ('owner','admin','fiscal','developer','finance','viewer')),
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, tenant_id)
);
CREATE INDEX ON user_memberships (tenant_id);

CREATE TABLE user_sessions (
    id              UUID PRIMARY KEY,
    user_id         UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash      BYTEA NOT NULL UNIQUE,          -- SHA-256 do valor do cookie (o valor nunca é gravado)
    mfa_passed      BOOLEAN NOT NULL DEFAULT FALSE,
    active_tenant   UUID REFERENCES tenants(id) ON DELETE SET NULL,
    ip              TEXT,
    user_agent      TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_seen_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at      TIMESTAMPTZ NOT NULL,           -- absoluto (12 h)
    revoked_at      TIMESTAMPTZ
);
CREATE INDEX ON user_sessions (user_id);
CREATE INDEX ON user_sessions (expires_at);

CREATE TABLE user_recovery_codes (
    user_id    UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    code_hash  BYTEA NOT NULL,
    used_at    TIMESTAMPTZ,
    PRIMARY KEY (user_id, code_hash)
);

-- Links de uso único: confirmar e-mail, redefinir senha, definir senha de operador. Só o hash do token fica no banco.
CREATE TABLE user_tokens (
    token_hash  BYTEA PRIMARY KEY,
    purpose     TEXT NOT NULL CHECK (purpose IN ('verify_email','reset_password','operator_setup')),
    user_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    expires_at  TIMESTAMPTZ NOT NULL,
    used_at     TIMESTAMPTZ,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX ON user_tokens (expires_at);

-- Convites para um tenant (D-U05). Têm tenant_id, então RLS como toda tabela de tenant; o aceite (antes de haver
-- contexto de tenant) passa pela função app.invite_by_token.
CREATE TABLE user_invites (
    token_hash  BYTEA PRIMARY KEY,
    tenant_id   UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    email       TEXT NOT NULL CHECK (email = lower(email)),
    role        TEXT NOT NULL CHECK (role IN ('admin','fiscal','developer','finance','viewer')),
    invited_by  UUID NOT NULL,
    expires_at  TIMESTAMPTZ NOT NULL,
    used_at     TIMESTAMPTZ,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX ON user_invites (tenant_id);

-- Trilha de auditoria de segurança e de ações administrativas (append-only para a aplicação; D-U07).
CREATE TABLE audit_log (
    id          BIGSERIAL PRIMARY KEY,
    at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    actor_type  TEXT NOT NULL CHECK (actor_type IN ('user','operator','api_key','oauth','system','anonymous')),
    actor_id    TEXT,
    action      TEXT NOT NULL,
    tenant_id   UUID,
    target      TEXT,
    ip          TEXT,
    trace_id    TEXT,
    detail      JSONB NOT NULL DEFAULT '{}'
);
CREATE INDEX ON audit_log (tenant_id, at DESC);
CREATE INDEX ON audit_log (actor_id, at DESC);

GRANT SELECT, INSERT, UPDATE         ON users, user_sessions TO tg_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON user_recovery_codes, user_tokens TO tg_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON user_memberships, user_invites TO tg_app;
GRANT SELECT, INSERT ON audit_log TO tg_app, tg_worker;
GRANT SELECT, INSERT ON audit_log TO tg_admin;
GRANT USAGE ON SEQUENCE audit_log_id_seq TO tg_app, tg_admin, tg_worker;
GRANT SELECT ON users, user_memberships, user_sessions TO tg_admin;
GRANT UPDATE (status, operator_scopes, locked_until, failed_logins) ON users TO tg_admin;
GRANT INSERT ON users, user_tokens TO tg_admin;

-- RLS em tudo que tem tenant_id (regra do SDD §13, verificada por RlsIsolationTests).
DO $$
DECLARE t TEXT;
BEGIN
  FOREACH t IN ARRAY ARRAY['user_memberships','user_invites','audit_log'] LOOP
    EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON public.%I TO tg_app, tg_worker
                    USING (tenant_id = (SELECT app.current_tenant()))
                    WITH CHECK (tenant_id = (SELECT app.current_tenant()))', t);
  END LOOP;
END $$;
CREATE POLICY admin_read ON user_memberships FOR SELECT TO tg_admin USING (true);
-- Eventos sem tenant (login, MFA, senha) entram na trilha sem contexto; nenhum papel da aplicação lê eventos de outro tenant.
CREATE POLICY global_events ON audit_log FOR INSERT TO tg_app, tg_worker WITH CHECK (tenant_id IS NULL);
CREATE POLICY global_events_read ON audit_log FOR SELECT TO tg_app USING (tenant_id IS NULL AND actor_type = 'user' AND actor_id = current_setting('app.current_user_id', true));
CREATE POLICY admin_all ON audit_log FOR ALL TO tg_admin USING (true) WITH CHECK (true);

-- Vínculos de um usuário em TODOS os tenants (login, troca de empresa): antes de existir contexto de tenant.
CREATE POLICY auth_lookup ON user_memberships FOR SELECT TO tg_auth USING (true);
GRANT SELECT ON user_memberships TO tg_auth;
SET ROLE tg_auth;
CREATE FUNCTION app.user_memberships(p_user UUID)
RETURNS TABLE (tenant_id UUID, role TEXT, tenant_name TEXT, root_cnpj TEXT, plan_code TEXT, tenant_status TEXT)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  SELECT m.tenant_id, m.role, t.name, t.root_cnpj, t.plan_code, t.status
    FROM public.user_memberships m JOIN public.tenants t ON t.id = m.tenant_id
   WHERE m.user_id = p_user
   ORDER BY m.created_at
$$;
RESET ROLE;
REVOKE ALL ON FUNCTION app.user_memberships(UUID) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.user_memberships(UUID) TO tg_app;

-- Aceite de convite: localiza o convite pelo hash do token antes de existir contexto de tenant.
CREATE POLICY auth_lookup ON user_invites FOR SELECT TO tg_auth USING (true);
GRANT SELECT ON user_invites TO tg_auth;
SET ROLE tg_auth;
CREATE FUNCTION app.invite_by_token(p_hash BYTEA)
RETURNS TABLE (tenant_id UUID, email TEXT, role TEXT, expires_at TIMESTAMPTZ, used_at TIMESTAMPTZ, tenant_name TEXT)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  SELECT i.tenant_id, i.email, i.role, i.expires_at, i.used_at, t.name
    FROM public.user_invites i JOIN public.tenants t ON t.id = i.tenant_id
   WHERE i.token_hash = p_hash
$$;
RESET ROLE;
REVOKE ALL ON FUNCTION app.invite_by_token(BYTEA) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.invite_by_token(BYTEA) TO tg_app;

-- Cadastro self-service (D-U02): cria tenant no plano SANDBOX + vínculo de dono, sem dar ao papel da API permissão
-- geral de criar tenants. Só o plano sandbox; troca de plano passa pela cobrança (Stripe) ou pelo backoffice.
-- Dono: tg_scheduler (NOLOGIN, só age por funções SECURITY DEFINER; o dono das migrações pode assumi-lo). Recebe só
-- INSERT nas três tabelas, com políticas restritas a INSERT.
GRANT SELECT, INSERT ON tenants, tenant_cnpjs, user_memberships TO tg_scheduler;
CREATE POLICY scheduler_signup ON tenants FOR INSERT TO tg_scheduler WITH CHECK (plan_code = 'SANDBOX');
CREATE POLICY scheduler_signup_read ON tenants FOR SELECT TO tg_scheduler USING (true);
CREATE POLICY scheduler_signup ON tenant_cnpjs FOR INSERT TO tg_scheduler WITH CHECK (true);
CREATE POLICY scheduler_signup ON user_memberships FOR INSERT TO tg_scheduler WITH CHECK (role = 'owner');
SET ROLE tg_scheduler;
CREATE FUNCTION app.signup_tenant(p_tenant UUID, p_name TEXT, p_root_cnpj TEXT, p_owner UUID)
RETURNS BOOLEAN
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
BEGIN
  IF EXISTS (SELECT 1 FROM public.tenants WHERE root_cnpj = p_root_cnpj) THEN
    RETURN FALSE;   -- empresa já cadastrada: o dono convida em vez de criar outro tenant
  END IF;
  INSERT INTO public.tenants (id, name, root_cnpj, plan_code, auto_provision_cnpj, kms_key_ref, billing_provider)
  VALUES (p_tenant, p_name, p_root_cnpj, 'SANDBOX', FALSE, 'tenant/' || p_tenant::text, 'STRIPE');
  INSERT INTO public.tenant_cnpjs (tenant_id, cnpj) VALUES (p_tenant, p_root_cnpj);
  INSERT INTO public.user_memberships (user_id, tenant_id, role) VALUES (p_owner, p_tenant, 'owner');
  RETURN TRUE;
END $$;
RESET ROLE;
REVOKE ALL ON FUNCTION app.signup_tenant(UUID, TEXT, TEXT, UUID) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.signup_tenant(UUID, TEXT, TEXT, UUID) TO tg_app;
