-- V0004: Admin API e parametrização (SDD §15; decisões D-203 a D-206).

-- ---------- tg_admin: escrita de parâmetros globais e provisionamento de tenants ----------
-- O role é criado pelo bootstrap do cluster (infra/postgres/init). Sem BYPASSRLS: o acesso
-- entre tenants é a política nomeada admin_provision, só para INSERT/SELECT do provisionamento.
GRANT USAGE ON SCHEMA public, app TO tg_admin;

GRANT SELECT, INSERT                  ON param_changes TO tg_admin;
GRANT UPDATE (approved_by, status)    ON param_changes TO tg_admin;
GRANT SELECT, INSERT                  ON rate_catalog  TO tg_admin;
GRANT SELECT ON plans, engine_bundles, engine_routing, credit_rules, param_snapshots TO tg_admin;

GRANT SELECT, INSERT ON tenants      TO tg_admin;
GRANT UPDATE (status, plan_code, limits_override, auto_provision_cnpj) ON tenants TO tg_admin;
GRANT INSERT         ON api_keys     TO tg_admin;
GRANT INSERT         ON tenant_cnpjs TO tg_admin;

CREATE POLICY admin_provision ON tenants      FOR ALL    TO tg_admin USING (true) WITH CHECK (true);
CREATE POLICY admin_provision ON api_keys     FOR INSERT TO tg_admin WITH CHECK (true);
CREATE POLICY admin_provision ON tenant_cnpjs FOR INSERT TO tg_admin WITH CHECK (true);

-- ---------- Catálogo de alíquotas: histórico sem edição no lugar (D-204) ----------
-- Uma mudança aprovada que substitui linhas vigentes marca as antigas como superadas (quem e quando);
-- a linha antiga continua no banco para reprocessar o passado. A exclusão de sobreposição vale só
-- entre linhas ativas.
ALTER TABLE rate_catalog
    ADD COLUMN provisoria     BOOLEAN NOT NULL DEFAULT FALSE,   -- ex.: valor do TCU antes da resolução do Senado
    ADD COLUMN superseded_by  UUID REFERENCES param_changes(id),
    ADD COLUMN superseded_at  TIMESTAMPTZ,
    ADD CONSTRAINT rate_catalog_superseded_pair CHECK ((superseded_by IS NULL) = (superseded_at IS NULL));

DO $$
DECLARE c TEXT;
BEGIN
  SELECT conname INTO STRICT c FROM pg_constraint WHERE conrelid = 'public.rate_catalog'::regclass AND contype = 'x';
  EXECUTE format('ALTER TABLE public.rate_catalog DROP CONSTRAINT %I', c);
END $$;

ALTER TABLE rate_catalog ADD CONSTRAINT rate_catalog_no_overlap
    EXCLUDE USING gist (tributo WITH =, tipo WITH =, (COALESCE(ente_codigo, '')) WITH =,
                        daterange(valid_from, valid_to) WITH &&)
    WHERE (superseded_at IS NULL);

-- Fonte "LEI_FEDERAL" (ADCT/LC 214) para alíquotas fixadas em lei, como as de 2026–2028.
ALTER TABLE rate_catalog DROP CONSTRAINT rate_catalog_fonte_check;
ALTER TABLE rate_catalog ADD CONSTRAINT rate_catalog_fonte_check
    CHECK (fonte IN ('LEI_FEDERAL','SENADO','TCU','CG_IBS','LEI_ENTE','BUNDLE_RFB'));

GRANT UPDATE (superseded_by, superseded_at) ON rate_catalog TO tg_admin;

-- ---------- Snapshot de parâmetros (§15.1, itens 4 e 5; D-205) ----------
-- Cada instância calcula o SHA-256 do conteúdo carregado e registra (ou reencontra) o id.
-- SECURITY DEFINER restrito: só grava um hash; os roles de runtime não têm INSERT na tabela.
CREATE FUNCTION app.register_param_snapshot(p_sha TEXT) RETURNS BIGINT
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
DECLARE v BIGINT;
BEGIN
  IF p_sha IS NULL OR p_sha !~ '^[0-9a-f]{64}$' THEN
    RAISE EXCEPTION 'sha256 inválido' USING ERRCODE = '22023';
  END IF;
  INSERT INTO public.param_snapshots (content_sha256) VALUES (p_sha)
    ON CONFLICT (content_sha256) DO NOTHING RETURNING id INTO v;
  IF v IS NULL THEN
    SELECT id INTO STRICT v FROM public.param_snapshots WHERE content_sha256 = p_sha;
  END IF;
  RETURN v;
END $$;
REVOKE ALL ON FUNCTION app.register_param_snapshot(TEXT) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.register_param_snapshot(TEXT) TO tg_app, tg_worker, tg_admin;

-- ---------- resolve_api_key devolve também os overrides do tenant (D-210) ----------
-- Os limites efetivos (plano + limits_override) e o auto-provisionamento de CNPJ entram no cache de
-- autenticação: nenhuma ida extra ao banco no caminho da requisição. Dono continua tg_auth.
SET ROLE tg_auth;
DROP FUNCTION app.resolve_api_key(BYTEA);
CREATE FUNCTION app.resolve_api_key(p_hash BYTEA)
RETURNS TABLE (tenant_id UUID, key_id UUID, scopes TEXT[], plan_code TEXT, tenant_status TEXT,
               limits_override JSONB, auto_provision_cnpj BOOLEAN)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  SELECT k.tenant_id, k.id, k.scopes, t.plan_code, t.status, t.limits_override, t.auto_provision_cnpj
    FROM public.api_keys k
    JOIN public.tenants  t ON t.id = k.tenant_id
   WHERE k.key_hash = p_hash
     AND k.revoked_at IS NULL
     AND (k.expires_at IS NULL OR k.expires_at > now())
$$;
REVOKE ALL ON FUNCTION app.resolve_api_key(BYTEA) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.resolve_api_key(BYTEA) TO tg_app;
RESET ROLE;
