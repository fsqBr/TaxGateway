-- V0027 — Avisos do ciclo de vida do cliente (D-R32) e preferências de e-mail por pessoa (D-R33).

-- ============================================================ D-R32: fatura vencendo
-- Vencimento da fatura (due_date da Stripe; cobrança automática no cartão usa a próxima tentativa).
ALTER TABLE invoices ADD COLUMN due_at TIMESTAMPTZ;
CREATE INDEX invoices_open_due ON invoices (due_at) WHERE status = 'open';
GRANT SELECT (id, tenant_id, number, status, amount_due_cents, hosted_invoice_url, due_at) ON invoices TO tg_scheduler;

-- Resumo do mês: contagens por situação da auditoria (sem conteúdo do documento).
GRANT SELECT (tenant_id, billing_month, kind, audit_status, credit_potential) ON fiscal_documents TO tg_scheduler;

-- ============================================================ D-R33: preferências de e-mail
-- Avisos informativos que a pessoa não quer receber por e-mail (continuam no portal). Os críticos (cobrança, acesso,
-- segurança, franquia esgotada, mudança fiscal que afeta a empresa) não podem ser desligados: a API só aceita os
-- tipos da lista D-R33.
ALTER TABLE users ADD COLUMN notice_email_opt_out TEXT[] NOT NULL DEFAULT '{}'
    CHECK (notice_email_opt_out <@ ARRAY['RESUMO_MENSAL','MOTOR_ATUALIZADO','RENOVACAO_PROXIMA','FATURA_EMITIDA','PAGAMENTO_RECEBIDO','FRANQUIA_80','TETO_80']::text[]);
GRANT SELECT (notice_email_opt_out) ON users TO tg_scheduler;

SET ROLE tg_scheduler;

-- Destinatários do e-mail de cada aviso: as pessoas da conta no papel do aviso, menos quem desligou aquele tipo.
CREATE OR REPLACE FUNCTION app.pending_notification_emails(p_limit INT)
RETURNS TABLE (id UUID, tenant_name TEXT, title TEXT, body TEXT, link TEXT, recipients TEXT[])
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  SELECT n.id, t.name, n.title, n.body, n.link,
         COALESCE((SELECT array_agg(DISTINCT u.email)
                     FROM public.user_memberships m JOIN public.users u ON u.id = m.user_id
                    WHERE m.tenant_id = n.tenant_id AND m.role = ANY (n.audience)
                      AND u.realm = 'customer' AND u.status = 'ACTIVE' AND u.email_verified_at IS NOT NULL
                      AND NOT (n.kind = ANY (u.notice_email_opt_out))), '{}')
    FROM public.tenant_notifications n JOIN public.tenants t ON t.id = n.tenant_id
   WHERE n.email AND n.emailed_at IS NULL AND n.created_at > now() - interval '3 days'
   ORDER BY n.created_at
   LIMIT p_limit
$$;

-- Fatura em aberto que vence nos próximos 3 dias: um aviso por fatura (financeiro e dono).
CREATE FUNCTION app.notify_invoices_due()
RETURNS INT
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
DECLARE n INT;
BEGIN
  INSERT INTO public.tenant_notifications (tenant_id, kind, severity, title, body, link, audience, dedupe_key)
  SELECT i.tenant_id, 'FATURA_VENCENDO', 'aviso', 'Fatura vence em breve',
         'A fatura ' || coalesce(i.number, i.id) || ' de R$ ' || replace(to_char(i.amount_due_cents / 100.0, 'FM999999990.00'), '.', ',') ||
         ' vence em ' || to_char(i.due_at AT TIME ZONE 'America/Sao_Paulo', 'DD/MM/YYYY') ||
         '. Pague pelo link da fatura em Cobrança para evitar restrição de acesso.',
         '/portal/cobranca', '{owner,admin,finance}', 'vencendo:' || i.id
    FROM public.invoices i JOIN public.tenants t ON t.id = i.tenant_id AND t.status <> 'CLOSED'
   WHERE i.status = 'open' AND i.amount_due_cents > 0
     AND i.due_at BETWEEN now() AND now() + interval '3 days'
  ON CONFLICT (tenant_id, dedupe_key) DO NOTHING;
  GET DIAGNOSTICS n = ROW_COUNT;
  RETURN n;
END $$;

-- Resumo do mês anterior, nos primeiros dias do mês (Brasília): documentos auditados, divergências e crédito em risco.
-- Só para contas que auditaram algo no mês; um por conta e mês.
CREATE FUNCTION app.notify_monthly_summary(p_today DATE DEFAULT NULL)   -- p_today: só para testes
RETURNS INT
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
DECLARE n INT;
        hoje DATE := coalesce(p_today, (now() AT TIME ZONE 'America/Sao_Paulo')::date);
        mes DATE := (date_trunc('month', hoje) - interval '1 month')::date;
BEGIN
  IF extract(day FROM hoje) > 3 THEN
    RETURN 0;
  END IF;
  INSERT INTO public.tenant_notifications (tenant_id, kind, severity, title, body, link, audience, dedupe_key)
  SELECT s.tenant_id, 'RESUMO_MENSAL', 'info', 'Resumo de ' || to_char(mes, 'MM/YYYY'),
         s.total || CASE WHEN s.total = 1 THEN ' documento auditado' ELSE ' documentos auditados' END ||
         ', ' || s.problemas || ' com divergência ou indício' ||
         CASE WHEN s.risco > 0 THEN ' e R$ ' || replace(to_char(s.risco, 'FM999999999990.00'), '.', ',') || ' de crédito em risco nas entradas' ELSE '' END ||
         '. Veja a apuração do mês e os fornecedores que mais colocam o crédito em risco.',
         '/portal/apuracao?competencia=' || to_char(mes, 'YYYY-MM'), '{owner,admin,fiscal}', 'resumo:' || to_char(mes, 'YYYY-MM')
    FROM (SELECT f.tenant_id, count(*) AS total,
                 count(*) FILTER (WHERE f.audit_status IN ('DIVERGENTE','INDICIO')) AS problemas,
                 coalesce(sum(f.credit_potential) FILTER (WHERE f.kind = 'INBOUND' AND f.audit_status IN ('DIVERGENTE','INDICIO')), 0) AS risco
            FROM public.fiscal_documents f
           WHERE f.billing_month = mes
           GROUP BY f.tenant_id) s
    JOIN public.tenants t ON t.id = s.tenant_id AND t.status = 'ACTIVE'
  ON CONFLICT (tenant_id, dedupe_key) DO NOTHING;
  GET DIAGNOSTICS n = ROW_COUNT;
  RETURN n;
END $$;

RESET ROLE;
REVOKE ALL ON FUNCTION app.notify_invoices_due() FROM PUBLIC;
REVOKE ALL ON FUNCTION app.notify_monthly_summary(DATE) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.notify_invoices_due() TO tg_worker;
GRANT EXECUTE ON FUNCTION app.notify_monthly_summary(DATE) TO tg_worker;
