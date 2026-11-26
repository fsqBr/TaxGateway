-- V0002: particionamento mensal com pg_partman 5.x (SDD §9, decisões D-105/D-106).
-- RANGE por billing_month, 4 partições à frente, partição DEFAULT como rede de segurança.

SELECT partman.create_parent(p_parent_table := 'public.fiscal_documents', p_control := 'billing_month',
                             p_interval := '1 month', p_premake := 4, p_default_table := true);
SELECT partman.create_parent(p_parent_table := 'public.simulations',      p_control := 'billing_month',
                             p_interval := '1 month', p_premake := 4, p_default_table := true);
SELECT partman.create_parent(p_parent_table := 'public.usage_ledger',     p_control := 'billing_month',
                             p_interval := '1 month', p_premake := 4, p_default_table := true);

-- inherit_privileges = false: partições novas NÃO recebem GRANT. Os roles de runtime têm privilégio só
-- na tabela mãe, onde valem as políticas RLS (acesso direto à partição ignora as políticas da mãe).
-- infinite_time_partitions = true: cria partições futuras mesmo sem dados novos.
UPDATE partman.part_config
   SET inherit_privileges = false,
       infinite_time_partitions = true
 WHERE parent_table IN ('public.fiscal_documents', 'public.simulations', 'public.usage_ledger');

-- Simulações: retenção de 13 meses (a manutenção do partman faz DROP das partições antigas).
UPDATE partman.part_config
   SET retention = '13 months',
       retention_keep_table = false
 WHERE parent_table = 'public.simulations';

-- Saúde do particionamento: linha na DEFAULT = manutenção do partman falhou ou data fora da janela.
-- SECURITY DEFINER (dono: tg_owner) para que a API/worker leiam só a CONTAGEM, nunca os dados.
CREATE FUNCTION app.default_partition_rows()
RETURNS TABLE (parent_table TEXT, row_count BIGINT)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  SELECT 'fiscal_documents', (SELECT count(*) FROM public.fiscal_documents_default)
  UNION ALL
  SELECT 'simulations',      (SELECT count(*) FROM public.simulations_default)
  UNION ALL
  SELECT 'usage_ledger',     (SELECT count(*) FROM public.usage_ledger_default)
$$;
REVOKE ALL ON FUNCTION app.default_partition_rows() FROM PUBLIC;
