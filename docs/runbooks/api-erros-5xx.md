# Runbook: erros 5xx na API (queima do orçamento de erro)

**Alertas:** `TaxGatewayErrorBudgetFastBurn` (página), `TaxGatewayErrorBudgetSlowBurn` (ticket), `TaxGatewayApiSemMetricas`.
**SLO:** disponibilidade 99,95% (orçamento de 0,05% de 5xx em `/v1/*`; 4xx não conta).

## 1. Delimitar (5 min)
* Qual rota e qual código:
  `sum by (http_route, http_response_status_code) (rate(http_server_request_duration_seconds_count{service="api",http_response_status_code=~"5.."}[5m]))`
* É o canário? Compare com `service="api-canary"`. Se só o canário erra, a análise `api-slo` do Argo Rollouts aborta
  sozinha; confirme com `kubectl argo rollouts get rollout api` e, se preciso, `kubectl argo rollouts abort api`.
* O que mudou? Último deploy, último `fiscal-update`, última mudança de parâmetro (`GET /admin/v1/params/changes`).

## 2. Por código de erro (corpo `application/problem+json`, campo `code`)
| `code` | Causa provável | Ação |
|:--|:--|:--|
| `ERR_ENGINE_UNAVAILABLE` (503) | Motor sidecar lento/caído ou circuit breaker aberto | [motor-rfb-degradado.md](motor-rfb-degradado.md) |
| 500 genérico | Exceção não tratada | Logs da API pelo horário/rota; rollback se começou no deploy |
| 502/503 do ingress, sem corpo | Pods sem readiness | `kubectl get pods -l app.kubernetes.io/name=api`; readiness exige parâmetros e cadastro do motor carregados (D-112) |

## 3. Dependências
* **Postgres:** `db_client_operation_duration_seconds` e `db_client_connection_count`. Pool esgotado → consultas lentas.
* **Valkey:** sem Valkey, rate limit e cache L2 falham. A API key segue no L1 por 30 s; revogações mantêm o último estado conhecido (D-803).
* **RabbitMQ:** não afeta o síncrono; o outbox acumula e o relay drena quando voltar.

## 4. Mitigar
Rollback (`kubectl argo rollouts undo api`) quando o erro começou num deploy. Escalar réplicas quando é saturação
(CPU do motor; o HPA mira 50%, §16.3.1).

## 5. Encerrar
Registrar causa, duração e orçamento consumido. Se houve perda ou exposição de dados, seguir [incidente-seguranca.md](incidente-seguranca.md).
