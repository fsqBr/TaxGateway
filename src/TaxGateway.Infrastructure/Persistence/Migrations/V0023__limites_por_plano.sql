-- V0023 — Limites de requisição por plano revisados e vitrine coerente com o que o código aplica (D-R23, D-R24).

-- Sub-limite por CNPJ atendido (OEM): um cliente final não esgota o limite dos outros (X-On-Behalf-Of-CNPJ).
ALTER TABLE plans ADD COLUMN per_cnpj_rpm INT CHECK (per_cnpj_rpm IS NULL OR per_cnpj_rpm > 0);

-- O backoffice grava o sub-limite por CNPJ como os demais campos do plano (0 ou null = sem sub-limite).
SET ROLE tg_scheduler;
CREATE OR REPLACE FUNCTION app.save_plan(p_code TEXT, p JSONB, p_operator TEXT)
RETURNS TEXT
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $$
DECLARE
  old JSONB;
BEGIN
  IF p_code !~ '^[A-Z][A-Z0-9_]{1,29}$' THEN
    RAISE EXCEPTION 'código de plano inválido' USING ERRCODE = '22023';
  END IF;
  SELECT to_jsonb(x) INTO old FROM public.plans x WHERE x.code = p_code;
  IF old IS NULL THEN
    INSERT INTO public.plans (code, rpm, burst, concurrency, batch_max_docs, batch_max_bytes, queue_priority, included_docs, price_cents)
    VALUES (p_code, 60, 10, 2, 100, 10485760, 'normal', 0, 0);
    INSERT INTO public.plan_history (code, data, changed_by, action) VALUES (p_code, p, p_operator, 'create');
  ELSE
    INSERT INTO public.plan_history (code, data, changed_by, action) VALUES (p_code, old, p_operator, 'update');
  END IF;

  UPDATE public.plans SET
    name                   = coalesce(p->>'name', name),
    description            = coalesce(p->>'description', description),
    features               = coalesce(p->'features', features),
    price_cents            = coalesce((p->>'price_cents')::bigint, price_cents),
    included_docs          = coalesce((p->>'included_docs')::bigint, included_docs),
    included_simulations   = coalesce((p->>'included_simulations')::bigint, included_simulations),
    included_cnpjs         = CASE WHEN p ? 'included_cnpjs' THEN (p->>'included_cnpjs')::int ELSE included_cnpjs END,
    max_users              = CASE WHEN p ? 'max_users' THEN (p->>'max_users')::int ELSE max_users END,
    overage_doc_millicents = coalesce((p->>'overage_doc_millicents')::bigint, overage_doc_millicents),
    overage_sim_millicents = coalesce((p->>'overage_sim_millicents')::bigint, overage_sim_millicents),
    cnpj_price_cents       = coalesce((p->>'cnpj_price_cents')::bigint, cnpj_price_cents),
    rpm                    = coalesce((p->>'rpm')::int, rpm),
    burst                  = coalesce((p->>'burst')::int, burst),
    concurrency            = coalesce((p->>'concurrency')::int, concurrency),
    batch_max_docs         = coalesce((p->>'batch_max_docs')::int, batch_max_docs),
    batch_max_bytes        = coalesce((p->>'batch_max_bytes')::bigint, batch_max_bytes),
    queue_priority         = coalesce(p->>'queue_priority', queue_priority),
    bill_schema_errors     = coalesce((p->>'bill_schema_errors')::boolean, bill_schema_errors),
    hard_quota             = coalesce((p->>'hard_quota')::boolean, hard_quota),
    dfe_enabled            = coalesce((p->>'dfe_enabled')::boolean, dfe_enabled),
    is_public              = coalesce((p->>'is_public')::boolean, is_public),
    self_service           = coalesce((p->>'self_service')::boolean, self_service),
    active                 = coalesce((p->>'active')::boolean, active),
    sort_order             = coalesce((p->>'sort_order')::int, sort_order),
    per_cnpj_rpm           = CASE WHEN p ? 'per_cnpj_rpm' THEN nullif((p->>'per_cnpj_rpm')::int, 0) ELSE per_cnpj_rpm END,
    updated_at             = now(),
    updated_by             = p_operator
  WHERE code = p_code;
  RETURN p_code;
END $$;
RESET ROLE;

-- Limites da API (sistemas). Pessoas no portal têm limite próprio, por pessoa, igual em todos os planos (D-R23).
UPDATE plans SET rpm =   60, burst =  20, concurrency =  3 WHERE code = 'SANDBOX';
UPDATE plans SET rpm =  300, burst =  40, concurrency =  8 WHERE code = 'ESSENCIAL';
UPDATE plans SET rpm = 1200, burst =  80, concurrency = 20 WHERE code = 'PROFISSIONAL';
UPDATE plans SET rpm = 2000, burst = 120, concurrency = 25 WHERE code = 'ESCRITORIO';
UPDATE plans SET rpm = 3000, burst = 150, concurrency = 40, per_cnpj_rpm = 300 WHERE code = 'PARCEIRO_OEM';
UPDATE plans SET rpm = 3000, burst = 150, concurrency = 40 WHERE code = 'ENTERPRISE';

-- D-R24: a vitrine diz exatamente o que o plano faz (antes: DF-e e CNPJs sem limite real, SSO e regras configuráveis
-- inexistentes no Enterprise, ZIP de 100 MB para todos).
UPDATE plans SET features = '["Motor oficial da Receita (mesmo de produção)","200 auditorias e 2.000 simulações por mês (para ao esgotar)","1 CNPJ","2 usuários","Lotes de até 100 notas (10 MB)","API: 60 requisições/min"]'
 WHERE code = 'SANDBOX';
UPDATE plans SET features = '["2.000 documentos auditados e 20.000 simulações por mês","3 CNPJs (R$ 29 por CNPJ extra)","3 usuários","Auditoria de saída e de entrada","Lotes de até 2.000 notas (50 MB)","Excedente: R$ 0,08 por documento e R$ 0,004 por simulação, até o teto que você definir","API: 300 requisições/min"]'
 WHERE code = 'ESSENCIAL';
UPDATE plans SET features = '["15.000 documentos auditados e 150.000 simulações por mês","10 CNPJs (R$ 19 por CNPJ extra)","10 usuários","Captura automática de notas de fornecedores (DF-e) com certificado A1","Lotes de até 25.000 notas (500 MB)","Excedente: R$ 0,05 por documento e R$ 0,002 por simulação, até o teto que você definir","API: 1.200 requisições/min"]'
 WHERE code = 'PROFISSIONAL';
UPDATE plans SET features = '["30.000 documentos auditados e 100.000 simulações por mês","50 CNPJs (R$ 12 por CNPJ extra)","Usuários ilimitados","Captura de DF-e","Lotes de até 25.000 notas (500 MB)","Excedente: R$ 0,04 por documento, até o teto que você definir","API: 2.000 requisições/min"]'
 WHERE code = 'ESCRITORIO';
UPDATE plans SET features = '["100.000 documentos por mês","CNPJs ilimitados (R$ 4 por CNPJ ativo)","Uso e limite separados por CNPJ atendido (300 requisições/min cada)","Prioridade máxima na fila","OAuth 2.0 e API keys por sistema","Excedente: R$ 0,02 por documento, até o teto que você definir","API: 3.000 requisições/min"]'
 WHERE code = 'PARCEIRO_OEM';
UPDATE plans SET features = '["Volume e limites sob contrato","Prioridade máxima na fila","Exceções do contribuinte (liminares, regimes especiais) com aprovação","Suporte dedicado"]'
 WHERE code = 'ENTERPRISE';
