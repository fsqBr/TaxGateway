# Runbook: webhooks, lotes e outbox

## Webhooks em `DEAD`
As entregas são retentadas com atraso crescente; esgotadas as tentativas, ou com `410 Gone` do destino, ficam `DEAD`.
1. Listar: `GET /v1/webhooks/deliveries?status=DEAD` (escopo `webhooks:manage`).
2. Causas típicas: endpoint do cliente fora do ar, TLS inválido, destino em IP privado recusado pela proteção SSRF
   (esse último é o comportamento correto: não "consertar").
3. Depois que o cliente corrigir: `POST /v1/webhooks/deliveries/{id}/retry`.
4. A fila `webhooks.dispatch.dlq` crescendo indica falha do **consumidor** (não do destino): ver logs dos workers.

## Lote parado em `QUEUED`/`RUNNING`
* Os chunks são reivindicados com lease (`app.claim_batch_chunks`); se um worker morre, o chunk volta quando o lease vence.
* Workers com 0 réplicas ou sem `ConnectionStrings:Worker` → nada é processado.
* Motor de lote (`rtc-regular-batch`) indisponível → chunks falham e são retentados; ver [motor-rfb-degradado.md](motor-rfb-degradado.md).
* Fair-share: um tenant grande não bloqueia os demais (limite por tenant por rodada, D-602).

## Outbox acumulando
Relay parado (RabbitMQ fora ou `ConnectionStrings:Relay` ausente). Os eventos não se perdem: o relay publica com
confirmação do broker quando voltar.
