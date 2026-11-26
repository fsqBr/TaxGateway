-- V0018: detalhe do cliente no backoffice por FUNÇÃO, não por leitura direta (corrige a V0017; D-J07 revista).
-- D-203 (testado em AdminAndParamsTests): o tg_admin não lê ledger, chaves nem CNPJs dos clientes. A V0017 tinha dado
-- SELECT por coluna nessas tabelas; aqui isso é desfeito e o backoffice passa a chamar app.admin_tenant_overview(),
-- que devolve só campos fixos e sem segredo de UM tenant por vez (nada de hash, certificado, XML ou documento).

DROP POLICY admin_read ON tenant_cnpjs;
DROP POLICY admin_read ON api_keys;
DROP POLICY admin_read ON usage_ledger;
DROP POLICY admin_read ON invoices;
REVOKE SELECT (tenant_id, cnpj, is_active, created_at) ON tenant_cnpjs FROM tg_admin;
REVOKE SELECT (tenant_id, id, name, key_prefix, scopes, expires_at, revoked_at, last_used_at, created_at) ON api_keys FROM tg_admin;
REVOKE SELECT (tenant_id, billing_month, metric, quantity) ON usage_ledger FROM tg_admin;
REVOKE SELECT (tenant_id, id, number, status, amount_due_cents, amount_paid_cents, period_start, period_end, created_at) ON invoices FROM tg_admin;

-- Dono da função: tg_scheduler (NOLOGIN; só age por funções SECURITY DEFINER), com leitura das mesmas colunas.
GRANT SELECT (tenant_id, cnpj, is_active, created_at) ON tenant_cnpjs TO tg_scheduler;
GRANT SELECT (tenant_id, name, key_prefix, scopes, expires_at, revoked_at, last_used_at, created_at) ON api_keys TO tg_scheduler;
GRANT SELECT (tenant_id, billing_month, metric, quantity) ON usage_ledger TO tg_scheduler;
GRANT SELECT (tenant_id, number, status, amount_due_cents, amount_paid_cents, period_start, period_end, created_at) ON invoices TO tg_scheduler;
CREATE POLICY scheduler_admin_overview ON tenant_cnpjs FOR SELECT TO tg_scheduler USING (true);
CREATE POLICY scheduler_admin_overview ON api_keys     FOR SELECT TO tg_scheduler USING (true);
CREATE POLICY scheduler_admin_overview ON usage_ledger FOR SELECT TO tg_scheduler USING (true);
CREATE POLICY scheduler_admin_overview ON invoices     FOR SELECT TO tg_scheduler USING (true);

SET ROLE tg_scheduler;
CREATE FUNCTION app.admin_tenant_overview(p_tenant UUID)
RETURNS JSONB
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  SELECT jsonb_build_object(
    'cnpjs', (SELECT coalesce(jsonb_agg(jsonb_build_object('cnpj', c.cnpj, 'ativo', c.is_active, 'criado_em', c.created_at) ORDER BY c.created_at), '[]')
                FROM (SELECT cnpj, is_active, created_at FROM public.tenant_cnpjs WHERE tenant_id = p_tenant ORDER BY created_at LIMIT 200) c),
    'api_keys', (SELECT coalesce(jsonb_agg(jsonb_build_object('nome', k.name, 'prefixo', k.key_prefix, 'scopes', k.scopes, 'expira_em', k.expires_at,
                   'revogada_em', k.revoked_at, 'ultimo_uso', k.last_used_at, 'criada_em', k.created_at) ORDER BY k.created_at DESC), '[]')
                   FROM public.api_keys k WHERE k.tenant_id = p_tenant),
    'consumo', (SELECT coalesce(jsonb_agg(jsonb_build_object('competencia', to_char(x.m, 'YYYY-MM'), 'documentos', x.docs, 'simulacoes', x.sims) ORDER BY x.m DESC), '[]')
                  FROM (SELECT billing_month AS m,
                               sum(quantity) FILTER (WHERE metric = 'DOC_AUDIT') AS docs,
                               sum(quantity) FILTER (WHERE metric = 'SIMULATION') AS sims
                          FROM public.usage_ledger
                         WHERE tenant_id = p_tenant AND billing_month >= (date_trunc('month', now()) - interval '2 months')::date
                         GROUP BY billing_month) x),
    'faturas', (SELECT coalesce(jsonb_agg(jsonb_build_object('numero', i.number, 'situacao', i.status, 'valor_cents', i.amount_due_cents,
                  'pago_cents', i.amount_paid_cents, 'inicio', i.period_start, 'fim', i.period_end) ORDER BY i.created_at DESC), '[]')
                  FROM (SELECT number, status, amount_due_cents, amount_paid_cents, period_start, period_end, created_at
                          FROM public.invoices WHERE tenant_id = p_tenant ORDER BY created_at DESC LIMIT 12) i)
  )
$$;
RESET ROLE;
REVOKE ALL ON FUNCTION app.admin_tenant_overview(UUID) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.admin_tenant_overview(UUID) TO tg_admin;
