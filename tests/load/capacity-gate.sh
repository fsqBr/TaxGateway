#!/usr/bin/env bash
# Gate de capacidade da Fase 6 (SDD §12; D-608). Pré-requisitos: compose base + capacity no ar, imagens
# taxgateway/api:dev e workers:dev, e out/capacity/lote.zip (gerado pelo CapacityCorpusGenerator).
# Saída: out/capacity/relatorio.md com p95 síncrono sem e com o lote de outro tenant.
set -euo pipefail
cd "$(dirname "$0")/../.."
API=http://127.0.0.1:5300
OP='X-Admin-Key: tg_admin_dev_operador_fiscal_a_0000000000'
RATE="${RATE:-30}"
DURATION="${DURATION:-60s}"
py=python3; "$py" -c 1 >/dev/null 2>&1 || py=python
export PYTHONIOENCODING=utf-8

for _ in $(seq 1 90); do curl -fs "$API/health/ready" >/dev/null && break; sleep 2; done

cnpj() { "$py" - <<'PY'
import random
def dv(s, w): r = sum((ord(c) - 48) * x for c, x in zip(s, w)) % 11; return '0' if r < 2 else str(11 - r)
b = ''.join(random.choice('0123456789') for _ in range(8)) + '0001'
d1 = dv(b, [5,4,3,2,9,8,7,6,5,4,3,2]); d2 = dv(b + d1, [6,5,4,3,2,9,8,7,6,5,4,3,2]); print(b + d1 + d2)
PY
}
tenant() { curl -fs -X POST "$API/admin/v1/tenants" -H "$OP" -H 'Content-Type: application/json' \
  -d "{\"nome\":\"$1\",\"cnpj_raiz\":\"$(cnpj)\",\"plano\":\"$2\",\"auto_provisionar_cnpj\":true}" | "$py" -c 'import json,sys;print(json.load(sys.stdin)["api_key"])'; }
KEY_SYNC=$(tenant "gate-sync" SCALE)
KEY_BATCH=$(tenant "gate-lote" OEM)

k6run() {
  MSYS_NO_PATHCONV=1 docker run --rm --network taxgateway_data -v "$(pwd -W 2>/dev/null || pwd)/tests/load:/s" \
    -e API_URL=http://api:8000 -e API_KEY="$KEY_SYNC" -e RATE="$RATE" -e DURATION="$DURATION" \
    grafana/k6:latest run --quiet --summary-export "/s/$1.json" /s/sync-under-batch.js >/dev/null
}

echo "== 1/2: só carga síncrona ($RATE req/s, 20 itens)"
k6run sem-lote

echo "== 2/2: mesma carga + lote de outro tenant"
created=$(curl -fs -X POST "$API/v1/auditoria/lotes" -H "X-Api-Key: $KEY_BATCH" -H "Idempotency-Key: gate-$RANDOM" \
  -H 'Content-Type: application/zip' --data-binary @out/capacity/lote.zip)
batch=$(echo "$created" | "$py" -c 'import json,sys;print(json.load(sys.stdin)["batch_id"])')
start=$(date +%s)
k6run com-lote
while :; do
  st=$(curl -fs "$API/v1/auditoria/lotes/$batch" -H "X-Api-Key: $KEY_BATCH")
  s=$(echo "$st" | "$py" -c 'import json,sys;d=json.load(sys.stdin);print(d["status"],d["processados"],d["total_documentos"])')
  [[ "$s" == COMPLETED* ]] && break
  sleep 2
done
elapsed=$(( $(date +%s) - start ))

"$py" - "$elapsed" "$s" <<'PY'
import json, sys
def m(f):
    d = json.load(open(f"tests/load/{f}.json"))["metrics"]
    t = d["http_req_duration"]; return t["p(50)"], t["p(95)"], t["p(99)"], d["http_reqs"]["count"], d["http_req_failed"]["value"]
a, b = m("sem-lote"), m("com-lote")
elapsed, status = int(sys.argv[1]), sys.argv[2].split()
docs = int(status[2])
rep = f"""# Gate de capacidade (Fase 6) — execução local

| Cenário | p50 (ms) | p95 (ms) | p99 (ms) | requisições | falhas |
|:--|--:|--:|--:|--:|--:|
| Só síncrono | {a[0]:.1f} | {a[1]:.1f} | {a[2]:.1f} | {a[3]} | {a[4]:.2%} |
| Síncrono + lote de outro tenant | {b[0]:.1f} | {b[1]:.1f} | {b[2]:.1f} | {b[3]} | {b[4]:.2%} |

* Lote: {docs} NF-e auditadas em {elapsed} s (~{docs/max(elapsed,1):.0f} docs/s, motor de lote dedicado, 2 vCPU).
* SLO de referência (§16.3): p95 ≤ 40 ms (20 itens, cache miss). Variação do p95 com o lote: {b[1]-a[1]:+.1f} ms.
"""
open("out/capacity/relatorio.md", "w", encoding="utf-8").write(rep)
print(rep)
PY
