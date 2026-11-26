#!/usr/bin/env bash
# Ensaio de backup + PITR (Hardening; D-H03). Sobe um Postgres DESCARTÁVEL (nunca o do compose nem o de produção),
# aplica as migrations reais, faz base backup + arquivamento de WAL, grava dados antes e depois de um instante-alvo,
# restaura num container novo até o instante-alvo e confere:
#   * o que foi gravado ANTES do alvo voltou (inclusive dados de tenant sob RLS e o histórico de migrations);
#   * o que foi gravado DEPOIS do alvo não voltou;
#   * o tempo de restore (comparado ao RTO de 1 h do §12).
# Uso: bash infra/backup/pitr-drill.sh   (requer docker; dotnet opcional para aplicar as migrations)
# Saída: out/backup/pitr-drill.md
set -euo pipefail
export MSYS_NO_PATHCONV=1   # Git Bash no Windows: não converter /caminhos dos containers

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
IMG="${PG_IMAGE:-taxgateway/postgres:18-drill}"
P="tgpitr$$"
PORT="${PITR_PORT:-55432}"
OUT="$ROOT/out/backup"
mkdir -p "$OUT"
hostpath() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }  # docker/dotnet nativos no Windows
log() { printf '[%s] %s\n' "$(date +%H:%M:%S)" "$*"; }

cleanup() {
  docker rm -f "${P}_src" "${P}_dst" >/dev/null 2>&1 || true
  docker volume rm "${P}_data" "${P}_wal" "${P}_base" "${P}_restore" >/dev/null 2>&1 || true
}
trap cleanup EXIT

if [[ -z "${PG_IMAGE:-}" ]]; then   # sempre do infra/postgres atual (bootstrap de roles em dia); cache do docker
  log "construindo $IMG a partir de infra/postgres"
  docker build -q -t "$IMG" "$(hostpath "$ROOT/infra/postgres")" >/dev/null
fi

for v in data wal base restore; do docker volume create "${P}_$v" >/dev/null; done
# O usuário postgres (uid 999) precisa escrever no arquivo de WAL e no destino do base backup.
docker run --rm -v "${P}_wal:/wal" -v "${P}_base:/base" "$IMG" chown 999:999 /wal /base

PGARGS=(-c wal_level=replica -c archive_mode=on -c "archive_command=test ! -f /wal/%f && cp %p /wal/%f" -c archive_timeout=30)

wait_sql() {  # container, timeout(s)
  local c="$1" t="$2" i
  for ((i = 0; i < t * 2; i++)); do
    if docker exec "$c" psql -U tg_bootstrap -d taxgateway -tAc "SELECT 1 FROM pg_roles WHERE rolname='tg_owner'" 2>/dev/null | grep -q 1 \
       && docker exec "$c" psql -U tg_bootstrap -d taxgateway -tAc "SELECT NOT pg_is_in_recovery()" 2>/dev/null | grep -q t; then
      return 0
    fi
    sleep 0.5
  done
  docker logs --tail 30 "$c" >&2
  return 1
}
sql() { docker exec "$1" psql -v ON_ERROR_STOP=1 -U tg_bootstrap -d taxgateway -tAc "$2"; }

log "subindo Postgres de origem ($IMG) com arquivamento de WAL"
docker run -d --name "${P}_src" -e POSTGRES_USER=tg_bootstrap -e POSTGRES_PASSWORD=drill -e POSTGRES_DB=taxgateway \
  -e TG_DEV_PASSWORD=dev-only -p "127.0.0.1:${PORT}:5432" \
  -v "${P}_data:/var/lib/postgresql" -v "${P}_wal:/wal" -v "${P}_base:/base" "$IMG" postgres "${PGARGS[@]}" >/dev/null
wait_sql "${P}_src" 120
sleep 2   # o entrypoint reinicia o servidor depois do init
wait_sql "${P}_src" 60

MIGRATIONS="sem migrations (dotnet ausente)"
if command -v dotnet >/dev/null 2>&1; then
  log "aplicando as migrations reais (tg_owner)"
  dotnet run --project "$(hostpath "$ROOT/src/TaxGateway.Migrator")" -- \
    "Host=127.0.0.1;Port=${PORT};Database=taxgateway;Username=tg_owner;Password=dev-only" | tail -1
  MIGRATIONS="$(sql "${P}_src" "SELECT count(*) FROM migrations.history") migrations"
fi

TENANT="$(cat /proc/sys/kernel/random/uuid 2>/dev/null || python -c 'import uuid;print(uuid.uuid4())')"
sql "${P}_src" "CREATE TABLE public.pitr_drill (fase TEXT PRIMARY KEY, gravado_em TIMESTAMPTZ DEFAULT clock_timestamp())" >/dev/null
sql "${P}_src" "INSERT INTO pitr_drill (fase) VALUES ('antes_do_backup')" >/dev/null
if [[ "$MIGRATIONS" != sem* ]]; then
  # Dado de tenant real (sob RLS no runtime) para provar que o restore traz o esquema e os dados de produção.
  sql "${P}_src" "INSERT INTO tenants (id, name, root_cnpj, plan_code, kms_key_ref) SELECT '$TENANT', 'pitr', '11222333000181', code, 'k' FROM plans LIMIT 1" >/dev/null
fi

log "base backup (pg_basebackup, WAL em stream)"
T0=$(date +%s)
docker exec -u postgres "${P}_src" pg_basebackup -U tg_bootstrap -D /base/b -Fp -X stream -c fast
log "base backup em $(( $(date +%s) - T0 )) s"

sql "${P}_src" "INSERT INTO pitr_drill (fase) VALUES ('depois_do_backup_antes_do_alvo')" >/dev/null
sleep 1
TARGET="$(sql "${P}_src" "SELECT clock_timestamp()")"
sleep 1
sql "${P}_src" "INSERT INTO pitr_drill (fase) VALUES ('depois_do_alvo')" >/dev/null
sql "${P}_src" "SELECT pg_switch_wal()" >/dev/null
ARCHIVED=0
for _ in $(seq 1 60); do   # espera o segmento com o último INSERT ser arquivado (sem isso a exclusão seria trivial)
  if [[ "$(sql "${P}_src" "SELECT failed_count = 0 AND last_archived_wal IS NOT NULL AND last_archived_time > '$TARGET'::timestamptz FROM pg_stat_archiver")" == t ]]; then
    ARCHIVED=1; break
  fi
  sleep 0.5
done
[[ $ARCHIVED == 1 ]] || { log "WAL posterior ao alvo não foi arquivado"; exit 1; }
log "instante-alvo: $TARGET; parando a origem (simula perda do servidor)"
docker stop -t 30 "${P}_src" >/dev/null

log "restaurando base backup + WAL até o alvo em container novo"
T1=$(date +%s)
docker run --rm -v "${P}_base:/base" -v "${P}_restore:/var/lib/postgresql" "$IMG" bash -c "
  mkdir -p /var/lib/postgresql/18 && cp -a /base/b /var/lib/postgresql/18/docker &&
  cat >> /var/lib/postgresql/18/docker/postgresql.auto.conf <<CONF
restore_command = 'cp /wal/%f %p'
recovery_target_time = '$TARGET'
recovery_target_action = 'promote'
CONF
  touch /var/lib/postgresql/18/docker/recovery.signal && chown -R 999:999 /var/lib/postgresql"
docker run -d --name "${P}_dst" -e POSTGRES_PASSWORD=drill -v "${P}_restore:/var/lib/postgresql" -v "${P}_wal:/wal:ro" "$IMG" >/dev/null
wait_sql "${P}_dst" 600
RESTORE_S=$(( $(date +%s) - T1 ))

STOP_LINE="$(docker logs "${P}_dst" 2>&1 | grep -m1 'recovery stopping before commit' || true)"
[[ -n "$STOP_LINE" ]] || OK_STOP=0
FASES="$(sql "${P}_dst" "SELECT string_agg(fase, ',' ORDER BY fase) FROM pitr_drill")"
EXPECTED="antes_do_backup,depois_do_backup_antes_do_alvo"
OK=1
[[ "$FASES" == "$EXPECTED" && "${OK_STOP:-1}" == 1 ]] || OK=0
DETAIL="fases restauradas: $FASES"
if [[ "$MIGRATIONS" != sem* ]]; then
  M2="$(sql "${P}_dst" "SELECT count(*) FROM migrations.history") migrations"
  T2="$(sql "${P}_dst" "SELECT count(*) FROM tenants WHERE id = '$TENANT'")"
  [[ "$M2" == "$MIGRATIONS" && "$T2" == 1 ]] || OK=0
  DETAIL="$DETAIL; $M2 (origem: $MIGRATIONS); tenant restaurado: $T2"
fi
log "$DETAIL; restore em ${RESTORE_S} s"

cat > "$OUT/pitr-drill.md" <<MD
# Ensaio de PITR ($(date '+%d/%m/%Y %H:%M'))

| Item | Resultado |
|:--|:--|
| Imagem | \`$IMG\` |
| Esquema | $MIGRATIONS |
| Instante-alvo | \`$TARGET\` |
| Restaurado | $DETAIL |
| Esperado | \`$EXPECTED\` (o INSERT depois do alvo **não** pode voltar) |
| Log do restore | \`${STOP_LINE##*LOG:  }\` |
| Tempo de restore (base + replay de WAL) | ${RESTORE_S} s (RTO do §12: ≤ 1 h) |
| Veredito | $([[ $OK == 1 ]] && echo "**APROVADO**" || echo "**REPROVADO**") |

Ensaio local com base backup + WAL arquivado (\`archive_timeout=30\` s). Em produção o mecanismo é o PITR do Postgres
gerenciado; o ensaio precisa ser repetido lá (ver \`docs/runbooks/restore-pitr.md\`).
MD
log "relatório: out/backup/pitr-drill.md"
[[ $OK == 1 ]]
