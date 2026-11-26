# Runbook: mudança de alíquota (ex.: Resolução do Senado da CBS 2027)

Fluxo de duas pessoas (D-204): quem propõe não aprova. Prazo conhecido: a resolução da CBS 2027 sai até 15/12/2026.

1. **Fiscal A** propõe, com o ato legal: `POST /admin/v1/params/rates/changes` (escopo `admin:fiscal`) com
   `justificativa`, `vigente_em` e `linhas` (tributo, tipo, esfera, alíquota, `valid_from`, `ato_legal`, `fonte`,
   `provisoria = false`). Em `substitui`, os ids das linhas provisórias (`GET /admin/v1/params/rates`).
2. **Fiscal B** confere o ato publicado (DOU) e aprova: `POST /admin/v1/params/changes/{id}/approve`.
3. Propagação ≤ 5 s em todas as instâncias (pub/sub `params:changed` + verificação a cada 30 s). Conferir o id do
   snapshot no `/health/ready` de mais de uma instância.
4. Simulações gravam o id do snapshot usado; documentos já auditados **não** são recalculados sozinhos. Mudança
   retroativa → reprocessar com o modo Job `reprocess --tenant ID --from AAAA-MM --to AAAA-MM` (D-408).
5. `409 ERR_PARAM_CONFLICT`: sobreposição de vigência com linha ativa não substituída; refazer a proposta.
