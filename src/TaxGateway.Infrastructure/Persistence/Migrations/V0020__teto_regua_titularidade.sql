-- V0020: teto de excedente, régua de inadimplência e verificação de titularidade do CNPJ (D-R08 a D-R10).

-- ---------- D-R08: teto de excedente por cliente (padrão 0 = bloqueia ao esgotar a franquia) ----------
ALTER TABLE tenants ADD COLUMN overage_cap_cents BIGINT NOT NULL DEFAULT 0 CHECK (overage_cap_cents BETWEEN 0 AND 100000000);

-- ---------- D-R09: régua de inadimplência ----------
-- RESTRICTED = consulta e exportação liberadas; consumo (simular, auditar, lotes, DF-e) bloqueado até pagar.
ALTER TABLE tenants DROP CONSTRAINT tenants_status_check;
ALTER TABLE tenants ADD CONSTRAINT tenants_status_check CHECK (status IN ('ACTIVE','RESTRICTED','SUSPENDED','CLOSED'));
ALTER TABLE tenants
    ADD COLUMN payment_failed_at TIMESTAMPTZ,
    ADD COLUMN dunning_stage     INT NOT NULL DEFAULT 0,   -- último passo da régua já aplicado (0, 3, 7, 10, 15)
    ADD COLUMN suspended_reason  TEXT;                     -- INADIMPLENCIA (reativa sozinho ao pagar) ou manual
GRANT UPDATE (payment_failed_at, dunning_stage, suspended_reason) ON tenants TO tg_billing;

-- Suspensão de verdade só SUSPENDED/CLOSED; RESTRICTED continua autenticando (para consultar e pagar).
CREATE OR REPLACE FUNCTION app.revocation_state()
RETURNS TABLE (kind TEXT, value TEXT, expires_at TIMESTAMPTZ)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  SELECT 'tenant', t.id::text, NULL::timestamptz FROM public.tenants t WHERE t.status IN ('SUSPENDED','CLOSED')
  UNION ALL
  SELECT 'client', a.client_id, NULL FROM public.oauth_applications a WHERE a.revoked_at IS NOT NULL
  UNION ALL
  SELECT 'jti', r.jti, r.expires_at FROM public.revoked_tokens r WHERE r.expires_at > now()
$$;

-- ---------- D-R10: titularidade do CNPJ ----------
ALTER TABLE tenants
    ADD COLUMN cnpj_verified_at         TIMESTAMPTZ,
    ADD COLUMN cnpj_verification_method TEXT CHECK (cnpj_verification_method IN ('CERTIFICADO','EMAIL_RECEITA','MANUAL')),
    ADD COLUMN cnpj_verified_by         TEXT;

-- Códigos enviados ao e-mail do cadastro da Receita (hash do código, nunca o código).
CREATE TABLE cnpj_verification_codes (
    tenant_id   UUID PRIMARY KEY REFERENCES tenants(id) ON DELETE CASCADE,
    code_hash   BYTEA NOT NULL,
    email       TEXT NOT NULL,
    attempts    INT NOT NULL DEFAULT 0,
    expires_at  TIMESTAMPTZ NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
ALTER TABLE cnpj_verification_codes ENABLE ROW LEVEL SECURITY;
ALTER TABLE cnpj_verification_codes FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON cnpj_verification_codes TO tg_app, tg_worker
    USING (tenant_id = (SELECT app.current_tenant()))
    WITH CHECK (tenant_id = (SELECT app.current_tenant()));
GRANT SELECT, INSERT, UPDATE, DELETE ON cnpj_verification_codes TO tg_app;

-- Disputa: alguém prova (por certificado) que é o titular de um CNPJ já cadastrado por outra conta, não verificada.
CREATE TABLE cnpj_disputes (
    id              UUID PRIMARY KEY,
    cnpj_root       TEXT NOT NULL,
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    claimant_user   UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    method          TEXT NOT NULL CHECK (method IN ('CERTIFICADO')),
    certificate     TEXT NOT NULL,                     -- titular e thumbprint do certificado apresentado (sem a chave)
    status          TEXT NOT NULL DEFAULT 'ABERTA' CHECK (status IN ('ABERTA','TRANSFERIDA','REJEITADA')),
    resolved_by     TEXT,
    resolved_at     TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX cnpj_disputes_open ON cnpj_disputes (tenant_id, claimant_user) WHERE status = 'ABERTA';
ALTER TABLE cnpj_disputes ENABLE ROW LEVEL SECURITY;
ALTER TABLE cnpj_disputes FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON cnpj_disputes TO tg_app, tg_worker
    USING (tenant_id = (SELECT app.current_tenant()))
    WITH CHECK (tenant_id = (SELECT app.current_tenant()));
CREATE POLICY admin_disputes ON cnpj_disputes FOR SELECT TO tg_admin USING (true);
GRANT SELECT ON cnpj_disputes TO tg_admin;

-- Avisos novos.
ALTER TABLE tenant_notifications DROP CONSTRAINT tenant_notifications_kind_check;
ALTER TABLE tenant_notifications ADD CONSTRAINT tenant_notifications_kind_check CHECK (kind IN (
    'FRANQUIA_80','FRANQUIA_ESGOTADA','WEBHOOK_PAUSADO','CERTIFICADO_VENCENDO','MUDANCA_FISCAL',
    'TETO_80','TETO_ATINGIDO','PAGAMENTO_PENDENTE','ACESSO_RESTRITO','CONTA_SUSPENSA','PAGAMENTO_REGULARIZADO',
    'EMPRESA_VERIFICADA','DISPUTA_CNPJ'));
CREATE POLICY billing_notifications ON tenant_notifications FOR INSERT TO tg_billing WITH CHECK (true);
GRANT INSERT ON tenant_notifications TO tg_billing;
GRANT SELECT (tenant_id, dedupe_key) ON tenant_notifications TO tg_billing;
CREATE POLICY billing_notifications_read ON tenant_notifications FOR SELECT TO tg_billing USING (true);

-- Funções do agendador (tg_scheduler: só age por funções SECURITY DEFINER).
GRANT UPDATE (overage_cap_cents, cnpj_verified_at, cnpj_verification_method, cnpj_verified_by) ON tenants TO tg_scheduler;
GRANT SELECT, INSERT, DELETE ON user_memberships TO tg_scheduler;
CREATE POLICY scheduler_transfer ON user_memberships FOR DELETE TO tg_scheduler USING (true);
CREATE POLICY scheduler_transfer_insert ON user_memberships FOR INSERT TO tg_scheduler WITH CHECK (true);
GRANT SELECT, INSERT, UPDATE ON cnpj_disputes TO tg_scheduler;
CREATE POLICY scheduler_disputes ON cnpj_disputes TO tg_scheduler USING (true) WITH CHECK (true);
GRANT INSERT ON tenant_notifications TO tg_scheduler;

SET ROLE tg_scheduler;

-- Teto definido pelo próprio cliente (rota com billing:manage) ou pelo backoffice.
CREATE FUNCTION app.set_overage_cap(p_tenant UUID, p_cents BIGINT)
RETURNS VOID
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  UPDATE public.tenants SET overage_cap_cents = p_cents WHERE id = p_tenant
$$;

CREATE FUNCTION app.mark_cnpj_verified(p_tenant UUID, p_method TEXT, p_by TEXT)
RETURNS VOID
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
BEGIN
  UPDATE public.tenants SET cnpj_verified_at = now(), cnpj_verification_method = p_method, cnpj_verified_by = p_by
   WHERE id = p_tenant AND cnpj_verified_at IS NULL;
  IF FOUND THEN   -- só na primeira verificação: aviso para quem administra a conta
    INSERT INTO public.tenant_notifications (tenant_id, kind, severity, title, body, link, audience, dedupe_key)
    VALUES (p_tenant, 'EMPRESA_VERIFICADA', 'info', 'Empresa verificada',
            'A titularidade do CNPJ foi confirmada. Plano pago e captura de notas (DF-e) estão liberados.',
            '/portal/cobranca', '{owner,admin}', 'verificada')
    ON CONFLICT (tenant_id, dedupe_key) DO NOTHING;
  END IF;
END $$;

-- Abre a disputa (chamada pela rota de conta, depois de conferir o certificado na aplicação). p_cnpj_root = raiz de 8
-- caracteres (o certificado pode ser de filial). Sem linha = CNPJ sem cadastro; verified = a empresa já é verificada (nada é aberto).
CREATE FUNCTION app.open_cnpj_dispute(p_id UUID, p_cnpj_root TEXT, p_user UUID, p_certificate TEXT)
RETURNS TABLE (tenant_id UUID, verified BOOLEAN)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
#variable_conflict use_column
DECLARE t RECORD;
BEGIN
  SELECT x.id, x.cnpj_verified_at IS NOT NULL AS v INTO t FROM public.tenants x WHERE left(x.root_cnpj, 8) = p_cnpj_root;
  IF NOT FOUND THEN
    RETURN;
  END IF;
  IF NOT t.v THEN
    INSERT INTO public.cnpj_disputes (id, cnpj_root, tenant_id, claimant_user, method, certificate)
    VALUES (p_id, p_cnpj_root, t.id, p_user, 'CERTIFICADO', p_certificate)
    ON CONFLICT DO NOTHING;
    INSERT INTO public.tenant_notifications (tenant_id, kind, severity, title, body, link, audience, dedupe_key)
    VALUES (t.id, 'DISPUTA_CNPJ', 'critico', 'Titularidade do CNPJ contestada',
            'Uma pessoa apresentou o certificado digital (e-CNPJ) desta empresa e pediu a titularidade da conta. Verifique a '
            || 'empresa em Plano e cobrança para manter a conta; sem verificação, o time do TaxGateway pode transferi-la ao titular.',
            '/portal/cobranca', '{owner,admin}', 'disputa:' || p_id::text)
    ON CONFLICT (tenant_id, dedupe_key) DO NOTHING;
  END IF;
  RETURN QUERY SELECT t.id, t.v;
END $$;

-- Resolve a disputa (backoffice, admin:security): TRANSFERIR = o requerente vira dono, os membros anteriores saem e a
-- empresa fica verificada por certificado; REJEITAR = só fecha.
CREATE FUNCTION app.resolve_cnpj_dispute(p_id UUID, p_transfer BOOLEAN, p_operator TEXT)
RETURNS UUID
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
#variable_conflict use_column
DECLARE d RECORD;
BEGIN
  SELECT * INTO d FROM public.cnpj_disputes WHERE id = p_id AND status = 'ABERTA' FOR UPDATE;
  IF NOT FOUND THEN
    RETURN NULL;
  END IF;
  IF p_transfer THEN
    DELETE FROM public.user_memberships WHERE tenant_id = d.tenant_id;
    INSERT INTO public.user_memberships (user_id, tenant_id, role) VALUES (d.claimant_user, d.tenant_id, 'owner');
    UPDATE public.tenants SET cnpj_verified_at = now(), cnpj_verification_method = 'CERTIFICADO', cnpj_verified_by = 'disputa:' || p_operator
     WHERE id = d.tenant_id;
    UPDATE public.cnpj_disputes SET status = 'REJEITADA', resolved_by = p_operator, resolved_at = now()
     WHERE tenant_id = d.tenant_id AND status = 'ABERTA' AND id <> p_id;
  END IF;
  UPDATE public.cnpj_disputes SET status = CASE WHEN p_transfer THEN 'TRANSFERIDA' ELSE 'REJEITADA' END,
         resolved_by = p_operator, resolved_at = now() WHERE id = p_id;
  RETURN d.tenant_id;
END $$;

RESET ROLE;
REVOKE ALL ON FUNCTION app.set_overage_cap(UUID, BIGINT) FROM PUBLIC;
REVOKE ALL ON FUNCTION app.mark_cnpj_verified(UUID, TEXT, TEXT) FROM PUBLIC;
REVOKE ALL ON FUNCTION app.open_cnpj_dispute(UUID, TEXT, UUID, TEXT) FROM PUBLIC;
REVOKE ALL ON FUNCTION app.resolve_cnpj_dispute(UUID, BOOLEAN, TEXT) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.set_overage_cap(UUID, BIGINT) TO tg_app, tg_admin;
GRANT EXECUTE ON FUNCTION app.mark_cnpj_verified(UUID, TEXT, TEXT) TO tg_app, tg_admin;
GRANT EXECUTE ON FUNCTION app.open_cnpj_dispute(UUID, TEXT, UUID, TEXT) TO tg_app;
GRANT EXECUTE ON FUNCTION app.resolve_cnpj_dispute(UUID, BOOLEAN, TEXT) TO tg_admin;

-- Perfil do tenant com o teto e a verificação (para o filtro de franquia e as rotas que exigem empresa verificada).
SET ROLE tg_auth;
DROP FUNCTION app.tenant_profile(UUID);
CREATE FUNCTION app.tenant_profile(p_tenant UUID)
RETURNS TABLE (plan_code TEXT, tenant_status TEXT, limits_override JSONB, auto_provision_cnpj BOOLEAN, overage_cap_cents BIGINT, cnpj_verified BOOLEAN)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  SELECT t.plan_code, t.status, t.limits_override, t.auto_provision_cnpj, t.overage_cap_cents, t.cnpj_verified_at IS NOT NULL
    FROM public.tenants t WHERE t.id = p_tenant
$$;
RESET ROLE;
REVOKE ALL ON FUNCTION app.tenant_profile(UUID) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.tenant_profile(UUID) TO tg_app;
