# SLOs do TaxGateway

Metas do SDD §16.3, conferidas com o que foi medido (Fase 0 e gate da Fase 6) e implementadas como regras do
Prometheus (`infra/k8s/taxgateway/templates/monitoring.yaml`, D-H02). Valores em `values.yaml` → `monitoring.slo`.

| SLO | Meta | Medida (fonte) | Alerta |
|:--|:--|:--|:--|
| Disponibilidade da API (`/v1/*`, 5xx) | 99,95% / 30 dias | `http_server_request_duration_seconds_count` | `TaxGatewayErrorBudgetFastBurn` (14,4×, 1 h e 5 min → página) · `SlowBurn` (6×, 6 h e 30 min → ticket) |
| `/v1/calculo/simular` p95 | ≤ 40 ms | histograma por `http_route` (10 min) | `TaxGatewaySimularP95Alto` |
| `/v1/calculo/simular` p99 | ≤ 100 ms | idem | `TaxGatewaySimularP99Alto` |
| `/v1/auditoria/xml` p95 | ≤ 150 ms | idem | `TaxGatewayAuditoriaP95Alto` |
| Coleta de métricas | sempre | `up{service="api"}` | `TaxGatewayApiSemMetricas` |
| Propagação de parâmetro | ≤ 5 s | teste entre duas instâncias (`ParamPropagationTests`: ~110 ms) | — (verificado por teste, não por métrica) |
| Suspensão de tenant | < 5 s | teste (`IdentityTests`: ~15 ms) | — |
| Lote de 25 mil documentos | ≤ 5 min | gate de capacidade (Fase 6) | *pendente: métrica de duração de lote nos workers* |

## Recalibração com o baseline
* Motor isolado (Fase 0): p95 de 11–13 ms com 20 itens. API ponta a ponta no gate da Fase 6: p95 de 15,0 ms sem lote
  e 18,8 ms com lote de 3.000 NF-e concorrente. **A meta de 40 ms tem folga de ~2×** e foi mantida.
* Os baldes do histograma foram alinhados às metas (5, 15, 40, 100, 150 ms...) para o p95/p99 não depender de interpolação grosseira.
* Repetir a medição no hardware de produção antes do GA e ajustar `monitoring.slo` se necessário.

## Como as métricas saem
A API publica `/metrics` (formato Prometheus) **só na porta de gestão 9464** (D-H01); a porta pública responde 404
nessa rota. O ServiceMonitor coleta `api` e `api-canary`; a análise do canário (Fase 4) usa as mesmas séries.

## Lacunas conhecidas
* Workers ainda não exportam métricas (duração de lote, atraso do outbox, entregas `DEAD`): hoje esses SLOs são
  verificados por testes e pelo gate, não por alerta.
* Os SLOs ainda não foram observados em produção (não há cluster de staging disponível neste ambiente).
