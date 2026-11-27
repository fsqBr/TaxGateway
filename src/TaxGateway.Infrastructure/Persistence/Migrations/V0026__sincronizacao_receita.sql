-- V0026 — Sincronização automática com a Receita, linha do tempo fiscal e avisos do ciclo de vida (D-R30 a D-R33).

-- ============================================================ D-R30: sincronização com o motor oficial
-- Estado da última sincronização por bundle do motor (a sincronização completa roda quando o bundle muda).
CREATE TABLE rfb_sync_state (
    bundle        TEXT PRIMARY KEY,
    synced_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    probes        INT NOT NULL DEFAULT 0,
    changes       INT NOT NULL DEFAULT 0,
    change_id     UUID,
    announced_at  TIMESTAMPTZ              -- aviso "motor oficial atualizado" já publicado
);

-- Linha do tempo pública das atualizações fiscais (alíquotas e motor oficial), para o site, o portal e as Novidades.
CREATE TABLE fiscal_updates (
    id              UUID PRIMARY KEY,
    kind            TEXT NOT NULL CHECK (kind IN ('ALIQUOTA','MOTOR')),
    title           TEXT NOT NULL,
    body            TEXT NOT NULL,
    effective_from  DATE,
    change_id       UUID UNIQUE REFERENCES param_changes(id),
    bundle          TEXT UNIQUE,
    source          TEXT NOT NULL CHECK (source IN ('MOTOR_OFICIAL','CATALOGO')),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX fiscal_updates_created ON fiscal_updates (created_at DESC);
GRANT SELECT ON fiscal_updates TO tg_app, tg_admin;
GRANT SELECT, INSERT ON fiscal_updates TO tg_scheduler;
GRANT SELECT, INSERT, UPDATE ON rfb_sync_state TO tg_scheduler;
GRANT SELECT ON rfb_sync_state TO tg_worker, tg_admin;

-- O tg_scheduler aplica a sincronização (o worker não escreve no catálogo diretamente).
GRANT SELECT, INSERT ON rate_catalog TO tg_scheduler;
GRANT UPDATE (superseded_by, superseded_at) ON rate_catalog TO tg_scheduler;
GRANT SELECT, INSERT ON param_changes TO tg_scheduler;

-- Avisos novos do ciclo de vida (D-R32).
ALTER TABLE tenant_notifications DROP CONSTRAINT tenant_notifications_kind_check;
ALTER TABLE tenant_notifications ADD CONSTRAINT tenant_notifications_kind_check CHECK (kind IN (
    'FRANQUIA_80','FRANQUIA_ESGOTADA','WEBHOOK_PAUSADO','CERTIFICADO_VENCENDO','MUDANCA_FISCAL',
    'TETO_80','TETO_ATINGIDO','PAGAMENTO_PENDENTE','ACESSO_RESTRITO','CONTA_SUSPENSA','PAGAMENTO_REGULARIZADO',
    'EMPRESA_VERIFICADA','DISPUTA_CNPJ',
    'MOTOR_ATUALIZADO','ASSINATURA_ATIVA','PLANO_ALTERADO','ASSINATURA_CANCELADA','FATURA_EMITIDA','FATURA_VENCENDO',
    'PAGAMENTO_RECEBIDO','RENOVACAO_PROXIMA','RESUMO_MENSAL','SEGURANCA'));

SET ROLE tg_scheduler;

-- Aplica uma sincronização: registra a mudança como APLICADA pela Receita (sem revisão humana, D-R30), supera as linhas
-- que mudaram e insere as novas. Idempotente por id da mudança.
CREATE FUNCTION app.apply_rate_sync(p_change UUID, p_bundle TEXT, p_rows JSONB, p_supersede UUID[])
RETURNS INT
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
DECLARE n INT;
BEGIN
  IF EXISTS (SELECT 1 FROM public.param_changes WHERE id = p_change) THEN
    RETURN 0;
  END IF;
  INSERT INTO public.param_changes (id, domain, scope, payload, justification, proposed_by, approved_by, status, effective_at)
  VALUES (p_change, 'RATE_CATALOG', 'GLOBAL', jsonb_build_object('rows', p_rows, 'supersedes', to_jsonb(p_supersede), 'bundle', p_bundle),
          'Sincronização automática com a base oficial da calculadora da Receita Federal (bundle ' || p_bundle || ')',
          'sincronizacao-rfb', 'motor-oficial-rfb', 'APPLIED', now());
  UPDATE public.rate_catalog SET superseded_by = p_change, superseded_at = now()
   WHERE id = ANY(p_supersede) AND superseded_at IS NULL;
  INSERT INTO public.rate_catalog (id, tributo, tipo, esfera, ente_codigo, forma_aplicacao, aliquota, valid_from, valid_to,
                                   ato_legal, fonte, provisoria, change_id, produto)
  SELECT gen_random_uuid(), r->>'tributo', r->>'tipo', r->>'esfera', r->>'ente_codigo', r->>'forma_aplicacao',
         (r->>'aliquota')::numeric, (r->>'valid_from')::date, (r->>'valid_to')::date, r->>'ato_legal', r->>'fonte',
         coalesce((r->>'provisoria')::boolean, false), p_change, NULL
    FROM jsonb_array_elements(p_rows) r;
  GET DIAGNOSTICS n = ROW_COUNT;
  INSERT INTO public.rfb_sync_state (bundle, changes, change_id) VALUES (p_bundle, n, p_change)
  ON CONFLICT (bundle) DO UPDATE SET synced_at = now(), changes = public.rfb_sync_state.changes + n, change_id = p_change;
  RETURN n;
END $$;

CREATE FUNCTION app.mark_rate_sync(p_bundle TEXT, p_probes INT)
RETURNS VOID
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  INSERT INTO public.rfb_sync_state (bundle, probes) VALUES (p_bundle, p_probes)
  ON CONFLICT (bundle) DO UPDATE SET synced_at = now(), probes = p_probes
$$;

-- D-R31: motor oficial atualizado — uma entrada na linha do tempo e um aviso para todas as contas ativas, uma vez
-- por bundle (o resumo do que mudou no cálculo vem do relatório de regressão do pipeline fiscal).
CREATE FUNCTION app.announce_engine_bundle(p_bundle TEXT, p_previous TEXT, p_summary TEXT)
RETURNS INT
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
DECLARE n INT;
BEGIN
  INSERT INTO public.rfb_sync_state (bundle) VALUES (p_bundle) ON CONFLICT (bundle) DO NOTHING;
  IF (SELECT announced_at FROM public.rfb_sync_state WHERE bundle = p_bundle) IS NOT NULL THEN
    RETURN 0;
  END IF;
  INSERT INTO public.fiscal_updates (id, kind, title, body, effective_from, bundle, source)
  VALUES (gen_random_uuid(), 'MOTOR',
          CASE WHEN p_previous IS NULL THEN 'Motor oficial da Receita em uso (' ELSE 'Motor oficial da Receita atualizado (' END || p_bundle || ')',
          left(p_summary, 4000),
          (now() AT TIME ZONE 'America/Sao_Paulo')::date, p_bundle, 'MOTOR_OFICIAL')
  ON CONFLICT (bundle) DO NOTHING;
  IF p_previous IS NOT NULL THEN   -- primeira instalação não avisa ninguém
    INSERT INTO public.tenant_notifications (tenant_id, kind, severity, title, body, link, audience, dedupe_key)
    SELECT t.id, 'MOTOR_ATUALIZADO', 'info', 'Motor oficial da Receita atualizado',
           left('A calculadora oficial passou da versão ' || p_previous || ' para ' || p_bundle || '. ' || p_summary, 2000),
           '/portal/aliquotas', '{owner,admin,fiscal,developer}', 'motor:' || p_bundle
      FROM public.tenants t WHERE t.status = 'ACTIVE'
    ON CONFLICT (tenant_id, dedupe_key) DO NOTHING;
  END IF;
  GET DIAGNOSTICS n = ROW_COUNT;
  UPDATE public.rfb_sync_state SET announced_at = now() WHERE bundle = p_bundle;
  RETURN n;
END $$;

-- D-R30: no dia em que uma alíquota publicada ANTES passa a valer, um segundo aviso ("passa a valer hoje"), só para
-- quem é afetado (mesma regra do aviso de publicação). Idempotente pelo dedupe.
CREATE FUNCTION app.notify_rates_effective_today()
RETURNS INT
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
DECLARE
  hoje DATE := (now() AT TIME ZONE 'America/Sao_Paulo')::date;
  c RECORD;
  n INT := 0;
  k INT;
BEGIN
  FOR c IN
    SELECT x.change_id, bool_or(x.ente_codigo IS NULL) AS nacional,
           string_agg(format('%s%s: %s%%', x.tributo, CASE WHEN x.ente_codigo IS NULL THEN '' ELSE ' ' || x.ente_codigo END,
                             trim(to_char(x.aliquota, 'FM990D0099'))), '; ') AS resumo
      FROM public.rate_catalog x JOIN public.param_changes p ON p.id = x.change_id
     WHERE x.superseded_at IS NULL AND x.valid_from = hoje AND p.created_at::date < hoje
     GROUP BY x.change_id
  LOOP
    INSERT INTO public.tenant_notifications (tenant_id, kind, severity, title, body, link, audience, dedupe_key)
    SELECT t.id, 'MUDANCA_FISCAL', 'aviso', 'Alíquota nova passa a valer hoje',
           left('A partir de hoje (' || to_char(hoje, 'DD/MM/YYYY') || '): ' || c.resumo || '. Simulações e auditorias com fato gerador a partir de hoje já usam o novo valor.', 2000),
           '/portal/aliquotas', '{owner,admin,fiscal}', 'vigente:' || c.change_id::text || ':' || hoje::text
      FROM public.tenants t
     WHERE t.status = 'ACTIVE'
       AND (c.nacional OR EXISTS (
             SELECT 1 FROM public.rate_catalog x JOIN public.fiscal_documents f
                 ON f.tenant_id = t.id AND f.billing_month >= date_trunc('month', now() - interval '12 months')::date
                AND ((length(x.ente_codigo) = 7 AND f.municipio_incidencia::text = x.ente_codigo)
                  OR (length(x.ente_codigo) = 2 AND lpad((f.municipio_incidencia / 100000)::text, 2, '0') = x.ente_codigo))
              WHERE x.change_id = c.change_id))
    ON CONFLICT (tenant_id, dedupe_key) DO NOTHING;
    GET DIAGNOSTICS k = ROW_COUNT;
    n := n + k;
  END LOOP;
  RETURN n;
END $$;

CREATE OR REPLACE FUNCTION app.notify_param_changes()
RETURNS INT
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
DECLARE
  c RECORD;
  r RECORD;
  n INT := 0;
  resumo TEXT;
  nacional BOOLEAN;
BEGIN
  FOR c IN
    SELECT p.id FROM public.param_changes p
     WHERE p.status IN ('APPROVED','APPLIED') AND p.scope = 'GLOBAL' AND p.notified_at IS NULL
       AND p.created_at > now() - interval '30 days'
     ORDER BY p.created_at
     FOR UPDATE SKIP LOCKED
  LOOP
    SELECT string_agg(format('%s%s%s: %s%% a partir de %s (%s)', x.tributo,
                             CASE WHEN x.ente_codigo IS NULL THEN '' ELSE ' ' || coalesce(m.nome || '/' || u2.sigla, u.sigla, x.ente_codigo) END,
                             CASE WHEN x.produto IS NULL THEN '' ELSE ' ' || x.produto END,
                             trim(to_char(x.aliquota, 'FM990D0099')), to_char(x.valid_from, 'DD/MM/YYYY'), x.ato_legal), '; '),
           bool_or(x.ente_codigo IS NULL)
      INTO resumo, nacional
      FROM public.rate_catalog x
      LEFT JOIN public.municipios m ON length(x.ente_codigo) = 7 AND m.codigo::text = x.ente_codigo
      LEFT JOIN public.ufs u2 ON u2.codigo = m.uf_codigo
      LEFT JOIN public.ufs u ON length(x.ente_codigo) = 2 AND lpad(u.codigo::text, 2, '0') = x.ente_codigo
     WHERE x.change_id = c.id;
    IF resumo IS NOT NULL THEN
      INSERT INTO public.tenant_notifications (tenant_id, kind, severity, title, body, link, audience, dedupe_key)
      SELECT t.id, 'MUDANCA_FISCAL', 'info', 'Mudança de alíquota publicada',
             left('Parâmetro fiscal atualizado: ' || resumo || '. Simulações e auditorias com data a partir da vigência já usam o novo valor.', 2000),
             '/portal/aliquotas', '{owner,admin,fiscal}', 'param:' || c.id::text
        FROM public.tenants t
       WHERE t.status = 'ACTIVE'
         AND (nacional OR EXISTS (
               SELECT 1 FROM public.rate_catalog x JOIN public.fiscal_documents f
                   ON f.tenant_id = t.id AND f.billing_month >= date_trunc('month', now() - interval '12 months')::date
                  AND ((length(x.ente_codigo) = 7 AND f.municipio_incidencia::text = x.ente_codigo)
                    OR (length(x.ente_codigo) = 2 AND lpad((f.municipio_incidencia / 100000)::text, 2, '0') = x.ente_codigo))
                WHERE x.change_id = c.id))
      ON CONFLICT (tenant_id, dedupe_key) DO NOTHING;

      -- Retroativas: reprocessar a partir da vigência (até o mês corrente) para quem tem documento no ente.
      FOR r IN SELECT x.ente_codigo, min(x.valid_from) AS desde FROM public.rate_catalog x
                WHERE x.change_id = c.id AND x.valid_from < now()::date GROUP BY x.ente_codigo
      LOOP
        INSERT INTO public.reprocess_requests (id, tenant_id, change_id, from_month, to_month, municipio, uf_codigo)
        SELECT gen_random_uuid(), f.tenant_id, c.id, date_trunc('month', r.desde)::date, date_trunc('month', now())::date,
               CASE WHEN length(r.ente_codigo) = 7 THEN r.ente_codigo::int END,
               CASE WHEN length(r.ente_codigo) = 2 THEN r.ente_codigo::smallint END
          FROM public.fiscal_documents f JOIN public.tenants t ON t.id = f.tenant_id AND t.status = 'ACTIVE'
         WHERE f.billing_month >= date_trunc('month', r.desde)::date
           AND (r.ente_codigo IS NULL
             OR (length(r.ente_codigo) = 7 AND f.municipio_incidencia::text = r.ente_codigo)
             OR (length(r.ente_codigo) = 2 AND lpad((f.municipio_incidencia / 100000)::text, 2, '0') = r.ente_codigo))
         GROUP BY f.tenant_id
        ON CONFLICT (tenant_id, change_id) DO NOTHING;
      END LOOP;
    END IF;
    IF resumo IS NOT NULL THEN
      INSERT INTO public.fiscal_updates (id, kind, title, body, effective_from, change_id, source)
      SELECT gen_random_uuid(), 'ALIQUOTA',
             CASE WHEN p.proposed_by = 'sincronizacao-rfb' THEN 'Alíquota atualizada pela Receita Federal' ELSE 'Mudança de alíquota publicada' END,
             left(resumo, 4000), (SELECT min(x.valid_from) FROM public.rate_catalog x WHERE x.change_id = c.id), c.id,
             CASE WHEN p.proposed_by = 'sincronizacao-rfb' THEN 'MOTOR_OFICIAL' ELSE 'CATALOGO' END
        FROM public.param_changes p WHERE p.id = c.id
      ON CONFLICT (change_id) DO NOTHING;
    END IF;
    UPDATE public.param_changes SET notified_at = now() WHERE id = c.id;
    n := n + 1;
  END LOOP;
  RETURN n;
END $$;

RESET ROLE;
REVOKE ALL ON FUNCTION app.apply_rate_sync(UUID, TEXT, JSONB, UUID[]) FROM PUBLIC;
REVOKE ALL ON FUNCTION app.mark_rate_sync(TEXT, INT) FROM PUBLIC;
REVOKE ALL ON FUNCTION app.announce_engine_bundle(TEXT, TEXT, TEXT) FROM PUBLIC;
REVOKE ALL ON FUNCTION app.notify_rates_effective_today() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.apply_rate_sync(UUID, TEXT, JSONB, UUID[]) TO tg_worker;
GRANT EXECUTE ON FUNCTION app.mark_rate_sync(TEXT, INT) TO tg_worker;
GRANT EXECUTE ON FUNCTION app.announce_engine_bundle(TEXT, TEXT, TEXT) TO tg_worker;
GRANT EXECUTE ON FUNCTION app.notify_rates_effective_today() TO tg_worker;
