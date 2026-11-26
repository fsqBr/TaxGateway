-- V0008: OAuth 2.0 client credentials e OEM (SDD §3.1, §6.4; decisões D-801 a D-806).

ALTER TABLE oauth_applications
    ADD COLUMN name         TEXT NOT NULL DEFAULT 'cliente',
    ADD COLUMN secret_hash  BYTEA,                       -- Argon2id(segredo, salt); o segredo é exibido uma única vez
    ADD COLUMN secret_salt  BYTEA,
    ADD COLUMN scopes       TEXT[] NOT NULL DEFAULT '{}',
    ADD COLUMN revoked_at   TIMESTAMPTZ,
    ADD COLUMN created_at   TIMESTAMPTZ NOT NULL DEFAULT now();

-- Provisionamento pelo backoffice (tg_admin) e listagem pelo próprio tenant.
GRANT INSERT ON oauth_applications TO tg_admin;
GRANT UPDATE (revoked_at) ON oauth_applications TO tg_admin;
CREATE POLICY admin_provision ON oauth_applications FOR ALL TO tg_admin USING (true) WITH CHECK (true);
GRANT SELECT (client_id, tenant_id, revoked_at) ON oauth_applications TO tg_admin;

-- Suspensão de tenant pelo backoffice já é permitida (V0004: UPDATE status em tenants para tg_admin).

-- Autenticação do cliente OAuth antes de existir contexto de tenant (mesmo padrão de resolve_api_key, D-103).
GRANT SELECT ON oauth_applications TO tg_auth;
CREATE POLICY auth_lookup ON oauth_applications FOR SELECT TO tg_auth USING (true);

SET ROLE tg_auth;
CREATE FUNCTION app.resolve_oauth_client(p_client_id TEXT)
RETURNS TABLE (tenant_id UUID, secret_hash BYTEA, secret_salt BYTEA, scopes TEXT[], plan_code TEXT, tenant_status TEXT,
               limits_override JSONB, auto_provision_cnpj BOOLEAN)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  SELECT a.tenant_id, a.secret_hash, a.secret_salt, a.scopes, t.plan_code, t.status, t.limits_override, t.auto_provision_cnpj
    FROM public.oauth_applications a
    JOIN public.tenants t ON t.id = a.tenant_id
   WHERE a.client_id = p_client_id AND a.revoked_at IS NULL
$$;
REVOKE ALL ON FUNCTION app.resolve_oauth_client(TEXT) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.resolve_oauth_client(TEXT) TO tg_app;

-- Dados do tenant para quem já está autenticado por token (status, plano, overrides): leitura do próprio tenant via id.
CREATE FUNCTION app.tenant_profile(p_tenant UUID)
RETURNS TABLE (plan_code TEXT, tenant_status TEXT, limits_override JSONB, auto_provision_cnpj BOOLEAN)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  SELECT t.plan_code, t.status, t.limits_override, t.auto_provision_cnpj FROM public.tenants t WHERE t.id = p_tenant
$$;
REVOKE ALL ON FUNCTION app.tenant_profile(UUID) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.tenant_profile(UUID) TO tg_app;
RESET ROLE;

-- OEM (§6.4): o tenant já tem SELECT/INSERT/UPDATE em tenant_cnpjs sob RLS (V0003); nada a conceder aqui.
