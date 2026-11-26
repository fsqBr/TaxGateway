-- V0005: carga inicial dos parâmetros globais (SDD §5.1, §8.1, §8.3, §8.5, §14.1; decisão D-206).
-- Mudanças posteriores passam pela Admin API (/admin/v1/params, duas pessoas), nunca por migration nova.
-- Toda linha aponta para um param_changes. O revisor fiscal precisa conferir esta carga antes de
-- 01/01/2027; até lá approved_by registra a pendência.

INSERT INTO param_changes (id, domain, scope, payload, justification, proposed_by, approved_by, status, effective_at) VALUES
('01938000-0000-7000-8000-000000000001', 'PLANS', 'GLOBAL', '{"fonte":"SDD §5.1"}',
 'Planos provisórios do §5.1. RPM/burst/concorrência recalibrados no gate da Fase 6; franquia e preço definidos pelo comercial (Fase 7).',
 'seed:V0005', 'revisao-comercial-pendente', 'APPLIED', '2026-01-01'),
('01938000-0000-7000-8000-000000000002', 'ENGINE_ROUTING', 'GLOBAL', '{"fonte":"SDD §8.1"}',
 'Roteamento CRT × tpRegimeIBSCBS (LC 123 arts. 13, 13-A, 18-A; LC 214 art. 41; NT 2026.005).',
 'seed:V0005', 'revisao-fiscal-pendente', 'APPLIED', '2026-01-01'),
('01938000-0000-7000-8000-000000000003', 'CREDIT_RULES', 'GLOBAL', '{"fonte":"SDD §8.3"}',
 'Crédito por destaque até a implantação do split payment (LC 214 arts. 47 e 48).',
 'seed:V0005', 'revisao-fiscal-pendente', 'APPLIED', '2026-01-01'),
('01938000-0000-7000-8000-000000000004', 'RATE_CATALOG', 'GLOBAL', '{"fonte":"SDD §14.1"}',
 'Alíquotas fixadas em lei para 2026 (ADCT art. 125) e IBS de 2027–2028 (ADCT art. 127).',
 'seed:V0005', 'revisao-fiscal-pendente', 'APPLIED', '2026-01-01'),
('01938000-0000-7000-8000-000000000005', 'RATE_CATALOG', 'GLOBAL', '{"fonte":"Acórdão TCU, TC 006.529/2026-7"}',
 'CBS 2027: referência de 11,3% calculada pelo TCU (28/10/2026) menos 0,1 p.p. (LC 214 art. 347). Provisória até a resolução do Senado (prazo 15/12/2026); sem resolução, vale o cálculo do TCU.',
 'seed:V0005', 'revisao-fiscal-pendente', 'APPLIED', '2026-11-25');

-- ---------- Planos (valores provisórios, §5.1) ----------
INSERT INTO plans (code, rpm, burst, concurrency, batch_max_docs, batch_max_bytes, queue_priority, included_docs, included_cnpjs, price_cents) VALUES
('STARTER',     120,  10,  2,    500,     15728640, 'low',      0, NULL, 0),
('GROWTH',      600,  30, 10,   5000,    104857600, 'normal',   0, NULL, 0),
('SCALE',      3000, 100, 30,  25000,    524288000, 'high',     0, NULL, 0),
('OEM',        3000, 100, 30, 100000,   2147483648, 'realtime', 0, NULL, 0),   -- limites por contrato: tenants.limits_override
('ENTERPRISE', 3000, 100, 30, 100000,   2147483648, 'realtime', 0, NULL, 0)
ON CONFLICT (code) DO NOTHING;

-- ---------- Roteamento por regime (§8.1) ----------
INSERT INTO engine_routing (crt, tp_regime_ibscbs, engines, simples_mode, note) VALUES
('3', '-', '{REGULAR}',         NULL,              'Regime normal'),
('1', '-', '{SIMPLES}',         'SN_PLENO',        'IBS e CBS no DAS'),
('2', '-', '{SIMPLES,REGULAR}', 'SN_SUBLIMITE',    'CBS no DAS; IBS fora do DAS pelo regime regular'),
('1', '1', '{REGULAR,SIMPLES}', 'SOMENTE_RECEITA', 'Opção pelo regime regular (LC 214 art. 41); confirmar com casos golden'),
('2', '1', '{REGULAR,SIMPLES}', 'SOMENTE_RECEITA', 'Opção pelo regime regular (LC 214 art. 41); confirmar com casos golden'),
('3', '2', '{}',                NULL,              'Semântica do PENDENTE a confirmar (§14.2): INDETERMINADO'),
('4', '-', '{}',                NULL,              'MEI: valores fixos, sem grupo IBSCBS (regra R050)');

-- ---------- Regra de crédito (§8.3) ----------
INSERT INTO credit_rules (valid_from, default_status, ato_legal) VALUES
('2026-01-01', 'APROPRIAVEL_POR_DESTAQUE', 'LC 214/2025 arts. 47 e 48');

-- ---------- Catálogo de alíquotas (§8.5): referências nacionais 2026–2027 ----------
-- Alíquotas em %, como o motor recebe em aliquotasNominais. Sem linha própria de ente: vale a referência.
INSERT INTO rate_catalog (id, tributo, tipo, esfera, ente_codigo, forma_aplicacao, aliquota, valid_from, valid_to,
                          ato_legal, fonte, provisoria, change_id) VALUES
('01938000-0000-7000-8001-000000000001', 'CBS',     'REFERENCIA', 'UNIAO',     NULL, NULL, 0.9000,  '2026-01-01', '2027-01-01',
 'ADCT art. 125; LC 214/2025 arts. 343 e 346', 'LEI_FEDERAL', FALSE, '01938000-0000-7000-8000-000000000004'),
('01938000-0000-7000-8001-000000000002', 'IBS_UF',  'REFERENCIA', 'UF',        NULL, NULL, 0.1000,  '2026-01-01', '2027-01-01',
 'ADCT art. 125; LC 214/2025 arts. 343 e 346', 'LEI_FEDERAL', FALSE, '01938000-0000-7000-8000-000000000004'),
('01938000-0000-7000-8001-000000000003', 'IBS_MUN', 'REFERENCIA', 'MUNICIPIO', NULL, NULL, 0.0000,  '2026-01-01', '2027-01-01',
 'ADCT art. 125 (IBS de 2026 só estadual)', 'LEI_FEDERAL', FALSE, '01938000-0000-7000-8000-000000000004'),
('01938000-0000-7000-8001-000000000004', 'IBS_UF',  'REFERENCIA', 'UF',        NULL, NULL, 0.0500,  '2027-01-01', '2029-01-01',
 'ADCT art. 127; LC 214/2025 art. 344', 'LEI_FEDERAL', FALSE, '01938000-0000-7000-8000-000000000004'),
('01938000-0000-7000-8001-000000000005', 'IBS_MUN', 'REFERENCIA', 'MUNICIPIO', NULL, NULL, 0.0500,  '2027-01-01', '2029-01-01',
 'ADCT art. 127; LC 214/2025 art. 344', 'LEI_FEDERAL', FALSE, '01938000-0000-7000-8000-000000000004'),
('01938000-0000-7000-8001-000000000006', 'CBS',     'REFERENCIA', 'UNIAO',     NULL, NULL, 11.2000, '2027-01-01', '2028-01-01',
 'Acórdão TCU (TC 006.529/2026-7): referência 11,3%; LC 214/2025 art. 347: -0,1 p.p.', 'TCU', TRUE,
 '01938000-0000-7000-8000-000000000005');
