-- V0017: consulta de documentos auditados e avisos ao cliente (D-J01, D-J03; revisão de produto L4/L9/L15, UX C1).

-- ---------- Histórico de documentos ----------
-- Listagem paginada por (criação, id) dentro do tenant e detalhe por id sem varrer todas as partições.
CREATE INDEX fiscal_documents_listing ON fiscal_documents (tenant_id, created_at DESC, id DESC);
CREATE INDEX fiscal_documents_by_id ON fiscal_documents (tenant_id, id);
CREATE INDEX dfe_documents_listing ON dfe_documents (tenant_id, received_at DESC);

-- ---------- Avisos ao cliente ----------
-- Um aviso por fato (dedupe_key): franquia em 80%/esgotada, endpoint de webhook pausado, certificado A1 vencendo,
-- mudança fiscal aprovada. Aparece no portal (sino) e vai por e-mail aos papéis de "audience".
CREATE TABLE tenant_notifications (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id   UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    kind        TEXT NOT NULL CHECK (kind IN ('FRANQUIA_80','FRANQUIA_ESGOTADA','WEBHOOK_PAUSADO','CERTIFICADO_VENCENDO','MUDANCA_FISCAL')),
    severity    TEXT NOT NULL CHECK (severity IN ('info','aviso','critico')),
    title       TEXT NOT NULL CHECK (length(title) <= 160),
    body        TEXT NOT NULL CHECK (length(body) <= 2000),
    link        TEXT CHECK (link IS NULL OR link ~ '^/[A-Za-z0-9/_?=&.-]*$'),   -- só caminho interno do portal
    audience    TEXT[] NOT NULL DEFAULT '{owner,admin}',
    dedupe_key  TEXT NOT NULL,
    email       BOOLEAN NOT NULL DEFAULT TRUE,
    emailed_at  TIMESTAMPTZ,
    read_at     TIMESTAMPTZ,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, dedupe_key)
);
CREATE INDEX tenant_notifications_listing ON tenant_notifications (tenant_id, created_at DESC);
CREATE INDEX tenant_notifications_email ON tenant_notifications (created_at) WHERE email AND emailed_at IS NULL;
ALTER TABLE tenant_notifications ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenant_notifications FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON tenant_notifications TO tg_app, tg_worker
    USING (tenant_id = (SELECT app.current_tenant()))
    WITH CHECK (tenant_id = (SELECT app.current_tenant()));
GRANT SELECT, INSERT, UPDATE (read_at) ON tenant_notifications TO tg_app;
GRANT SELECT, INSERT ON tenant_notifications TO tg_worker;

-- Mudança fiscal: avisada uma vez a todos os clientes ativos (o agendador faz o fan-out).
ALTER TABLE param_changes ADD COLUMN notified_at TIMESTAMPTZ;

GRANT SELECT, INSERT, UPDATE (emailed_at) ON tenant_notifications TO tg_scheduler;
CREATE POLICY scheduler_notifications ON tenant_notifications TO tg_scheduler USING (true) WITH CHECK (true);
GRANT SELECT ON param_changes, rate_catalog TO tg_scheduler;
GRANT UPDATE (notified_at) ON param_changes TO tg_scheduler;
GRANT SELECT (id, email, realm, status, email_verified_at) ON users TO tg_scheduler;
CREATE POLICY scheduler_notifications_read ON user_memberships FOR SELECT TO tg_scheduler USING (true);

SET ROLE tg_scheduler;

-- Fan-out das mudanças de alíquota GLOBAIS aprovadas (últimos 30 dias, ainda não avisadas). Retorna quantas mudanças.
CREATE FUNCTION app.notify_param_changes()
RETURNS INT
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
DECLARE
  c RECORD;
  n INT := 0;
  resumo TEXT;
BEGIN
  FOR c IN
    SELECT p.id, p.effective_at FROM public.param_changes p
     WHERE p.status IN ('APPROVED','APPLIED') AND p.scope = 'GLOBAL' AND p.notified_at IS NULL
       AND p.created_at > now() - interval '30 days'
     ORDER BY p.created_at
     FOR UPDATE SKIP LOCKED
  LOOP
    SELECT string_agg(format('%s%s: %s%% a partir de %s (%s)', r.tributo,
                             CASE WHEN r.ente_codigo IS NULL THEN '' ELSE ' ' || r.ente_codigo END,
                             trim(to_char(r.aliquota, 'FM990D0099')), to_char(r.valid_from, 'DD/MM/YYYY'), r.ato_legal), '; ')
      INTO resumo
      FROM public.rate_catalog r WHERE r.change_id = c.id;
    IF resumo IS NOT NULL THEN
      INSERT INTO public.tenant_notifications (tenant_id, kind, severity, title, body, link, audience, dedupe_key)
      SELECT t.id, 'MUDANCA_FISCAL', 'info', 'Mudança de alíquota aprovada',
             left('Parâmetro fiscal atualizado: ' || resumo || '. Simulações e auditorias com data a partir da vigência já usam o novo valor.', 2000),
             '/portal/avisos', '{owner,admin,fiscal}', 'param:' || c.id::text
        FROM public.tenants t WHERE t.status = 'ACTIVE'
      ON CONFLICT (tenant_id, dedupe_key) DO NOTHING;
    END IF;
    UPDATE public.param_changes SET notified_at = now() WHERE id = c.id;
    n := n + 1;
  END LOOP;
  RETURN n;
END $$;

-- Avisos ainda não enviados por e-mail (até 3 dias), com os destinatários verificados dos papéis do aviso.
CREATE FUNCTION app.pending_notification_emails(p_limit INT)
RETURNS TABLE (id UUID, tenant_name TEXT, title TEXT, body TEXT, link TEXT, recipients TEXT[])
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  SELECT n.id, t.name, n.title, n.body, n.link,
         COALESCE((SELECT array_agg(DISTINCT u.email)
                     FROM public.user_memberships m JOIN public.users u ON u.id = m.user_id
                    WHERE m.tenant_id = n.tenant_id AND m.role = ANY (n.audience)
                      AND u.realm = 'customer' AND u.status = 'ACTIVE' AND u.email_verified_at IS NOT NULL), '{}')
    FROM public.tenant_notifications n JOIN public.tenants t ON t.id = n.tenant_id
   WHERE n.email AND n.emailed_at IS NULL AND n.created_at > now() - interval '3 days'
   ORDER BY n.created_at
   LIMIT p_limit
$$;

CREATE FUNCTION app.mark_notifications_emailed(p_ids UUID[])
RETURNS VOID
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  UPDATE public.tenant_notifications SET emailed_at = now() WHERE id = ANY (p_ids) AND emailed_at IS NULL
$$;

RESET ROLE;
REVOKE ALL ON FUNCTION app.notify_param_changes() FROM PUBLIC;
REVOKE ALL ON FUNCTION app.pending_notification_emails(INT) FROM PUBLIC;
REVOKE ALL ON FUNCTION app.mark_notifications_emailed(UUID[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.notify_param_changes() TO tg_worker;
GRANT EXECUTE ON FUNCTION app.pending_notification_emails(INT) TO tg_worker;
GRANT EXECUTE ON FUNCTION app.mark_notifications_emailed(UUID[]) TO tg_worker;

-- ---------- Backoffice: detalhe do cliente (D-J07) ----------
-- Leitura só de colunas sem segredo (nada de hash de chave, certificado ou XML), para o operador ver CNPJs, chaves,
-- consumo e faturas do cliente que atende. Escrita continua pelas rotas e funções existentes.
GRANT SELECT (tenant_id, cnpj, is_active, created_at) ON tenant_cnpjs TO tg_admin;
GRANT SELECT (tenant_id, id, name, key_prefix, scopes, expires_at, revoked_at, last_used_at, created_at) ON api_keys TO tg_admin;
GRANT SELECT (tenant_id, billing_month, metric, quantity) ON usage_ledger TO tg_admin;
GRANT SELECT (tenant_id, id, number, status, amount_due_cents, amount_paid_cents, period_start, period_end, created_at) ON invoices TO tg_admin;
CREATE POLICY admin_read ON tenant_cnpjs FOR SELECT TO tg_admin USING (true);
CREATE POLICY admin_read ON api_keys     FOR SELECT TO tg_admin USING (true);
CREATE POLICY admin_read ON usage_ledger FOR SELECT TO tg_admin USING (true);
CREATE POLICY admin_read ON invoices     FOR SELECT TO tg_admin USING (true);
