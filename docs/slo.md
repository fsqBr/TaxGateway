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
A API publica `/metrics` (formato Prometheus) **só na porta de gestão 9464** (D-H01), conferida pela porta local da
conexão e não pelo header `Host` (D-P14); a porta pública responde 404 nessa rota. O ServiceMonitor coleta `api`,
`api-canary` e `api-admin`; a análise do canário (Fase 4) usa as séries da `api-canary`. Com `Telemetry:OtlpEndpoint`
configurado, API e workers enviam métricas e traces por OTLP (D-P17), e `Logging:Json=true` liga o log em JSON com
`trace_id`.

### Métricas próprias (medidor "TaxGateway", D-P17)

| Métrica | O que mede | Uso |
|:--|:--|:--|
| `taxgateway_simulation_cache_total{camada}` | Simulações atendidas por L1 (memória), L2 (Valkey) ou `miss` (motor) | Custo e capacidade (§16.3.1) |
| `taxgateway_quota_exhausted_total{metrica}` | Chamadas recusadas por franquia esgotada (402) | Conversão de plano |
| `taxgateway_batch_chunks_total{resultado}` | Chunks de lote `done`, `retry`, `fenced` e `failed` | Saúde dos lotes |
| `taxgateway_stripe_events_total` | Eventos da Stripe processados por tipo e resultado | Cobrança |
| `taxgateway_cache_tampered_total{cache}` | Entradas do Valkey com selo inválido (D-P10) | Alerta `TaxGatewayCacheAdulterado` (página) |
| `taxgateway_crl_max_age_seconds` | Idade da LCR mais antiga no cache da instância (D-P23) | Alerta `TaxGatewayLcrDesatualizada` (> 36 h) |
| `taxgateway_crl_published_total` | LCRs baixadas e publicadas no bucket pelos workers | Diagnóstico da LCR |

## Lacunas conhecidas
* Duração de lote, atraso do outbox e entregas `DEAD` ainda não têm alerta próprio: hoje são verificados por testes,
  pelo gate e pelos avisos ao cliente (webhook pausado, D-J03).
* Os SLOs ainda não foram observados em produção (não há cluster de staging disponível neste ambiente).
