# Runbook: restore do banco (PITR)

**Metas:** RPO ≤ 5 min, RTO ≤ 1 h (SDD §12). **Ensaio:** `bash infra/backup/pitr-drill.sh` (local; resultado em
`out/backup/pitr-drill.md`). Repetir **trimestralmente** no Postgres gerenciado de staging (D-H03).

## Quando usar
* Erro humano (DELETE/UPDATE indevido), corrupção lógica, migration destrutiva aplicada por engano.
* Perda do primário: antes, tentar o failover da réplica do serviço gerenciado (mais rápido que PITR).

## Procedimento (Postgres gerenciado)
1. **Congelar a escrita**: API e workers com 0 réplicas, para não divergir do ponto restaurado.
2. **Escolher o instante-alvo** (UTC) imediatamente **antes** do evento, pelos logs e pelo `created_at` dos registros.
3. **Restaurar numa instância NOVA** pelo PITR do provedor (nunca sobre a atual), mesma versão (Postgres 18) e mesmos
   parâmetros (`shared_preload_libraries=pg_partman_bgw`).
4. **Validar** na instância nova:
   * `SELECT max(version) FROM migrations.history` = versão esperada da aplicação;
   * contagens recentes de `fiscal_documents` / `simulations` coerentes com o alvo;
   * roles presentes e **nenhum role de aplicação com BYPASSRLS** (`SELECT rolname FROM pg_roles WHERE rolbypassrls`);
   * partições futuras existentes (o `/health/full` da API checa; o readiness não depende do banco, D-P18).
5. **Trocar a connection string** no secret (`ConnectionStrings__*`) e retomar API e workers.
6. **Reconciliar o intervalo perdido** (entre o alvo e a parada):
   * cobrança: `GET /admin/v1/billing/reconcile?mes=` (o ledger é a fonte; o Asaas deduplica por `externalReference`);
   * webhooks do intervalo se perderam com o outbox; os clientes podem reconsultar os resultados pela API;
   * objetos no S3 sem linha no banco ficam órfãos e somem pelo ciclo de vida da retenção.
7. Registrar o incidente; se houve perda de dados de cliente, avaliar comunicação ([incidente-seguranca.md](incidente-seguranca.md)).

## Procedimento manual (base backup + WAL)
É o que o ensaio local automatiza: copiar o base backup para um diretório de dados novo; acrescentar
`restore_command`, `recovery_target_time` e `recovery_target_action = 'promote'` ao `postgresql.auto.conf`; criar
`recovery.signal`; subir. O log tem de mostrar `recovery stopping before commit of transaction ...`.
