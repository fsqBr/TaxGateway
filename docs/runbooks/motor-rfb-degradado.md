# Runbook: motor da RFB lento ou indisponível

**Alertas:** `TaxGatewaySimularP95Alto`, `TaxGatewaySimularP99Alto`, `TaxGatewayAuditoriaP95Alto`; aumento de
`503 ERR_ENGINE_UNAVAILABLE`. **Metas (§16.3):** simular p95 ≤ 40 ms e p99 ≤ 100 ms; auditoria p95 ≤ 150 ms.

## Contexto
O motor regular (`rtc-regular`) roda como sidecar no pod da API e é chamado em localhost; o do Simples (`rtc-simples`)
também. Os lotes usam motores dedicados (`rtc-regular-batch`). A chamada síncrona tem timeout de 1,5 s, retry só para
erro transitório, circuit breaker e bulkhead (`Engines:Regular:SyncMaxConcurrency` / `SyncMaxQueue`).

## Diagnóstico
1. **CPU do motor saturada** (causa mais comum; ≈ 1 ms de CPU por item, §16.3.1):
   `rate(container_cpu_usage_seconds_total{container="rtc-regular"}[5m])` perto do limite → escalar a API (o motor vem junto).
2. **Latência da chamada ao motor**, vista pela API: `http_client_request_duration_seconds` com `server_address="127.0.0.1"`.
3. **Documentos grandes**: muitos itens por requisição empurram o p95; o limite síncrono é 500 itens (D-215).
4. **Motor reiniciando** (OOM/JVM): `kubectl describe pod` e eventos; a JVM usa `MaxRAMPercentage=75`.
5. **Bundle novo** recém-implantado? Comparar com o relatório de regressão do `fiscal-update` (Fase 4).

## Mitigação
* Escalar: subir `minReplicas` do HPA temporariamente.
* Se começou com um bundle novo: rollback da imagem (`kubectl argo rollouts undo api`). Imagens antigas ficam retidas ≥ 6 anos.
* Lote disputando motor não deveria acontecer (motores separados, §8.4); se acontecer, reduzir as réplicas dos workers.

## Não fazer
Não aumentar o timeout síncrono para "esconder" a lentidão: prende conexões e derruba a disponibilidade.
