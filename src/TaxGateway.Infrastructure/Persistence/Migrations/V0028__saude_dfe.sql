-- V0028 — Saúde da captura de DF-e (D-R37): aviso automático quando a captura de um CNPJ para.

ALTER TABLE tenant_notifications DROP CONSTRAINT tenant_notifications_kind_check;
ALTER TABLE tenant_notifications ADD CONSTRAINT tenant_notifications_kind_check CHECK (kind IN (
    'FRANQUIA_80','FRANQUIA_ESGOTADA','WEBHOOK_PAUSADO','CERTIFICADO_VENCENDO','MUDANCA_FISCAL',
    'TETO_80','TETO_ATINGIDO','PAGAMENTO_PENDENTE','ACESSO_RESTRITO','CONTA_SUSPENSA','PAGAMENTO_REGULARIZADO',
    'EMPRESA_VERIFICADA','DISPUTA_CNPJ',
    'MOTOR_ATUALIZADO','ASSINATURA_ATIVA','PLANO_ALTERADO','ASSINATURA_CANCELADA','FATURA_EMITIDA','FATURA_VENCENDO',
    'PAGAMENTO_RECEBIDO','RENOVACAO_PROXIMA','RESUMO_MENSAL','SEGURANCA',
    'DFE_PARADO'));

GRANT SELECT (code, dfe_enabled) ON plans TO tg_scheduler;

SET ROLE tg_scheduler;

-- Captura ligada num plano com DF-e e conta ativa, mas: sem certificado válido, ou sem consulta à SEFAZ há mais de 6 h
-- quando já devia ter consultado (o normal é no máximo 1 h). Um aviso por CNPJ por dia, ao dono, admin e fiscal.
CREATE FUNCTION app.notify_dfe_stalled(p_now TIMESTAMPTZ DEFAULT NULL)   -- p_now: só para testes
RETURNS INT
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
DECLARE n INT;
        agora TIMESTAMPTZ := coalesce(p_now, now());
BEGIN
  INSERT INTO public.tenant_notifications (tenant_id, kind, severity, title, body, link, audience, dedupe_key)
  SELECT s.tenant_id, 'DFE_PARADO', 'aviso', 'Captura de notas parada: ' || s.cnpj,
         CASE WHEN NOT EXISTS (SELECT 1 FROM public.certificates c WHERE c.tenant_id = s.tenant_id AND c.cnpj = s.cnpj AND c.not_after > agora)
              THEN 'A captura de notas dos fornecedores do CNPJ ' || s.cnpj || ' parou porque não há certificado A1 válido (ausente ou vencido). Envie o certificado novo em DF-e.'
              ELSE 'Nenhuma consulta à SEFAZ para o CNPJ ' || s.cnpj || ' desde ' ||
                   coalesce(to_char(s.last_query_at AT TIME ZONE 'America/Sao_Paulo', 'DD/MM/YYYY HH24:MI'), 'a configuração') ||
                   '. Abra DF-e e use "Testar conexão"; se o teste passar e a captura continuar parada, fale com o suporte.'
         END,
         '/portal/dfe', '{owner,admin,fiscal}',
         'dfe-parado:' || s.cnpj || ':' || to_char(agora AT TIME ZONE 'America/Sao_Paulo', 'YYYY-MM-DD')
    FROM public.dfe_sync_state s
    JOIN public.tenants t ON t.id = s.tenant_id AND t.status = 'ACTIVE'
    JOIN public.plans p ON p.code = t.plan_code AND p.dfe_enabled
   WHERE s.enabled
     AND (NOT EXISTS (SELECT 1 FROM public.certificates c WHERE c.tenant_id = s.tenant_id AND c.cnpj = s.cnpj AND c.not_after > agora)
          OR (s.last_query_at < agora - interval '6 hours' AND s.next_allowed_at < agora - interval '1 hour'))
  ON CONFLICT (tenant_id, dedupe_key) DO NOTHING;
  GET DIAGNOSTICS n = ROW_COUNT;
  RETURN n;
END $$;

REVOKE ALL ON FUNCTION app.notify_dfe_stalled(TIMESTAMPTZ) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.notify_dfe_stalled(TIMESTAMPTZ) TO tg_worker;

RESET ROLE;
