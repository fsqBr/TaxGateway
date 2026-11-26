# Runbooks do TaxGateway

Procedimentos de operação (Hardening para GA, SDD §12; D-H04). Cada alerta do `PrometheusRule taxgateway-slo` aponta
para um destes arquivos. **Antes de qualquer comando no cluster, confira o contexto do `kubectl`**: esta máquina de
desenvolvimento tem contextos de outros projetos.

| Situação | Runbook | Severidade típica |
|:--|:--|:--|
| Orçamento de erro queimando (5xx) / API sem métricas | [api-erros-5xx.md](api-erros-5xx.md) | página |
| p95/p99 de simulação ou auditoria acima da meta; `ERR_ENGINE_UNAVAILABLE` | [motor-rfb-degradado.md](motor-rfb-degradado.md) | ticket → página |
| Restaurar o banco (erro humano, corrupção, perda do primário) | [restore-pitr.md](restore-pitr.md) | página |
| Rotação (ou vazamento) da chave de assinatura OAuth | [rotacao-chave-oauth.md](rotacao-chave-oauth.md) | planejado |
| Webhooks em `DEAD` / fila `webhooks.dispatch.dlq` crescendo / lote parado | [webhooks-e-lotes.md](webhooks-e-lotes.md) | ticket |
| Mudança de alíquota (Senado, ente) | [mudanca-aliquota.md](mudanca-aliquota.md) | planejado |
| Incidente de segurança / dados pessoais | [incidente-seguranca.md](incidente-seguranca.md) | página |
| Suspender tenant (inadimplência, abuso) | [suspensao-tenant.md](suspensao-tenant.md) | planejado |

Metas de recuperação (SDD §12): **RPO ≤ 5 min, RTO ≤ 1 h.** Ensaio local de PITR: `bash infra/backup/pitr-drill.sh`.
