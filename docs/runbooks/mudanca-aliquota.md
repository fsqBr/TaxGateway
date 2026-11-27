# Runbook: mudança de alíquota (ex.: Resolução do Senado da CBS 2027)

**Primeiro: a Receita já publicou na base da calculadora?** Desde a D-R30 o TaxGateway **sincroniza sozinho** com a
base oficial (dados abertos da calculadora): quando a Receita publica uma alíquota (num bundle novo do motor ou na base
do motor em uso), os workers aplicam no catálogo com a vigência publicada, avisam as contas afetadas e reprocessam o
que for retroativo. Confira em `GET /v1/publico/atualizacoes-fiscais`, no log `Alíquota sincronizada com a Receita` dos
workers e em `rfb_sync_state`. **Este runbook é para o que a Receita ainda não publicou na base** (ex.: a CBS 2027
logo depois da resolução do Senado, antes do bundle novo) e para corrigir algo à mão.

Fluxo de duas pessoas (D-204, D-R04): quem propõe não aprova, e quem aprova não pode ter criado (nem sido criado por)
quem propôs. Prazo conhecido: a resolução da CBS 2027 sai até 15/12/2026 (hoje a linha de 2027 é **provisória**). Quando
a Receita publicar na base, a linha manual continua se o valor e a vigência forem os mesmos; se forem diferentes, a
sincronização supera a linha manual pela oficial (fonte `BUNDLE_RFB`) e o aviso sai para as contas afetadas.

## Antes: o que precisa de atenção

- **Cobertura** (D-R22): o banner da tela Alíquotas do backoffice e `GET /admin/v1/params/cobertura` mostram até quando
  cada tributo tem referência publicada. O alerta `TaxGatewayAliquotaSemCobertura` dispara com menos de 90 dias.
- **Conferência com o motor** (D-R22): `TaxGatewayCatalogoDivergeDoMotor` indica que o catálogo e a base do motor
  oficial divergem nas datas em que o motor decide a alíquota (bundle novo?). Veja o log `CatalogEngineCheck` dos
  workers.

## Passo a passo

1. **Fiscal A** monta a proposta na tela **Alíquotas** do backoffice:
   - linha a linha (o formulário mostra só os campos que valem para o tributo: forma de aplicação para alíquota própria
     do ente, produto para o IS) **ou** importando um **CSV** (`tributo;tipo;esfera;ente_codigo;forma_aplicacao;produto;
     aliquota;valid_from;valid_to;ato_legal;fonte;provisoria`, até 6.000 linhas — todos os municípios cabem numa
     proposta);
   - **Conferir** (`POST /admin/v1/params/rates/changes/preview`): mostra erros por linha (código IBGE inexistente,
     tributo × esfera, faixa 0–100%) e, para cada linha nova, o que está vigente na mesma chave — marque em
     "substituir" as linhas que a nova vigência encerra (ex.: a CBS 2027 provisória);
   - **Propor** (`POST /admin/v1/params/rates/changes`). A vigência é a de cada linha (`valid_from`): uma mudança
     futura entra no catálogo na aprovação e passa a valer na data.
2. **Fiscal B** confere o ato publicado (DOU, diário do ente) e **aprova** (`POST /admin/v1/params/changes/{id}/approve`)
   ou **recusa com o motivo** (`POST /admin/v1/params/changes/{id}/reject`).
3. Propagação ≤ 5 s em todas as instâncias (pub/sub `params:changed` + verificação a cada 30 s). Conferir o id do
   snapshot no `/health/ready` de mais de uma instância e a consulta pública `GET /v1/publico/aliquotas`.
4. **Avisos e reprocessamento são automáticos** (D-R19): mudança nacional avisa todas as contas; mudança de UF ou
   município avisa só quem teve documento naquele ente nos últimos 12 meses. Linha com vigência no passado gera pedidos
   de reprocessamento por conta e ente, executados pelos workers (`ReprocessWorker`) sem cobrança, com aviso
   "auditorias recalculadas". O modo Job `reprocess --tenant ID --from AAAA-MM --to AAAA-MM` continua disponível
   para casos manuais.
5. `409 ERR_PARAM_CONFLICT`: sobreposição de vigência com linha ativa não substituída — use a prévia e marque a linha
   em "substituir".

## Imposto Seletivo (D-R17)

As linhas de IS têm `produto` (`NCM:<prefixo>` ou `CCLASS:<cClassTrib>`; o prefixo mais longo vence). Sem linha para o
produto, a auditoria avisa `IS_NAO_CONFERIDO` e usa o declarado. Com linha, o declarado é conferido (regra R014).

## Exceções de clientes (D-R20)

Liminares e regimes especiais de um cliente NÃO entram no catálogo: o próprio cliente cadastra em
"Alíquotas e exceções" no portal, com o documento e a aprovação de outra pessoa da empresa. O cálculo oficial não muda.
