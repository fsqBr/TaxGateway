-- V0029 — Painel de integrações do backoffice (D-R39): números agregados (contagens e horários), nunca segredos nem
-- dados de cliente, para o operador saber se e-mail, Stripe, sincronização com a Receita e DF-e estão funcionando.

GRANT SELECT (id, received_at, processed_at, attempts, last_error) ON stripe_events TO tg_scheduler;

SET ROLE tg_scheduler;

CREATE FUNCTION app.integration_status()
RETURNS JSONB
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  SELECT jsonb_build_object(
    'email', (SELECT jsonb_build_object(
        'ultimo_envio', max(n.emailed_at),
        'pendentes', count(*) FILTER (WHERE n.email AND n.emailed_at IS NULL AND n.created_at > now() - interval '3 days'),
        'pendentes_antigos', count(*) FILTER (WHERE n.email AND n.emailed_at IS NULL AND n.created_at > now() - interval '3 days'
                                                AND n.created_at < now() - interval '15 minutes'),
        'enviados_24h', count(*) FILTER (WHERE n.emailed_at > now() - interval '24 hours'))
      FROM public.tenant_notifications n),
    'stripe', (SELECT jsonb_build_object(
        'ultimo_evento', max(e.received_at),
        'pendentes', count(*) FILTER (WHERE e.processed_at IS NULL),
        'pendentes_antigos', count(*) FILTER (WHERE e.processed_at IS NULL AND e.received_at < now() - interval '15 minutes'),
        'com_erro', count(*) FILTER (WHERE e.processed_at IS NULL AND e.attempts > 0),
        'ultimo_erro', (SELECT left(x.last_error, 300) FROM public.stripe_events x
                         WHERE x.processed_at IS NULL AND x.last_error IS NOT NULL ORDER BY x.received_at DESC LIMIT 1),
        'planos_sem_sincronizar', (SELECT count(*) FROM public.plans p
                                    WHERE p.price_cents > 0 AND (p.stripe_synced_at IS NULL OR p.stripe_synced_at < p.updated_at)))
      FROM public.stripe_events e),
    'receita', (SELECT jsonb_build_object(
        'bundle', s.bundle, 'sincronizado_em', s.synced_at, 'consultas', s.probes, 'mudancas', s.changes,
        'ultima_alteracao', (SELECT max(f.created_at) FROM public.fiscal_updates f WHERE f.kind = 'ALIQUOTA'))
      FROM public.rfb_sync_state s ORDER BY s.synced_at DESC LIMIT 1),
    'dfe', (SELECT jsonb_build_object(
        'ligadas', count(*) FILTER (WHERE d.enabled),
        'paradas', count(*) FILTER (WHERE d.enabled AND (
                      NOT EXISTS (SELECT 1 FROM public.certificates c WHERE c.tenant_id = d.tenant_id AND c.cnpj = d.cnpj AND c.not_after > now())
                      OR (d.last_query_at < now() - interval '6 hours' AND d.next_allowed_at < now() - interval '1 hour'))),
        'com_erro', count(*) FILTER (WHERE d.enabled AND d.last_cstat IN ('ERRO','656')),
        'certificados_vencendo', (SELECT count(*) FROM public.certificates c WHERE c.not_after BETWEEN now() AND now() + interval '30 days'),
        'ultima_consulta', max(d.last_query_at))
      FROM public.dfe_sync_state d))
$$;

RESET ROLE;
REVOKE ALL ON FUNCTION app.integration_status() FROM PUBLIC;
SET ROLE tg_scheduler;
GRANT EXECUTE ON FUNCTION app.integration_status() TO tg_admin;
RESET ROLE;
