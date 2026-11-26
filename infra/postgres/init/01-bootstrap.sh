#!/usr/bin/env bash
# Bootstrap do cluster (SDD §4.1, decisões D-103/D-104).
# Roda UMA vez, como superusuário, antes das migrations. Cria roles e extensões que exigem
# privilégio elevado; todo o resto (tabelas, políticas, grants) vem das migrations executadas por tg_owner.
# Em produção o DBA executa o mesmo SQL e as senhas vêm do secret manager (aqui: TG_DEV_PASSWORD).
set -euo pipefail
PW="${TG_DEV_PASSWORD:-dev-only}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" -v pw="$PW" <<'SQL'
-- Roles. Nenhum tem BYPASSRLS (D-103): acessos cross-tenant são políticas explícitas por role.
CREATE ROLE tg_owner   LOGIN PASSWORD :'pw';          -- dono dos objetos; só migrations (CI/CD)
CREATE ROLE tg_app     LOGIN PASSWORD :'pw' NOBYPASSRLS;  -- API
CREATE ROLE tg_worker  LOGIN PASSWORD :'pw' NOBYPASSRLS;  -- workers por tenant
CREATE ROLE tg_billing LOGIN PASSWORD :'pw' NOBYPASSRLS;  -- fechamento de faturamento
CREATE ROLE tg_relay   LOGIN PASSWORD :'pw' NOBYPASSRLS;  -- relay do outbox (cross-tenant, só outbox)
CREATE ROLE tg_admin   LOGIN PASSWORD :'pw' NOBYPASSRLS;  -- Admin API: parâmetros globais e provisionamento (D-203)
CREATE ROLE tg_auth    NOLOGIN NOBYPASSRLS;               -- dono de app.resolve_api_key; ninguém loga com ele
CREATE ROLE tg_scheduler NOLOGIN NOBYPASSRLS;             -- dono das funções de claim (lotes/webhooks) (D-602)

-- tg_owner precisa poder transferir a função para tg_auth (ALTER ... OWNER exige SET ROLE).
GRANT tg_auth TO tg_owner WITH INHERIT FALSE, SET TRUE;
GRANT tg_scheduler TO tg_owner WITH INHERIT FALSE, SET TRUE;

-- Banco e schema public pertencem ao tg_owner; ninguém mais cria objetos.
ALTER DATABASE :"DBNAME" OWNER TO tg_owner;
ALTER SCHEMA public OWNER TO tg_owner;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON DATABASE :"DBNAME" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"DBNAME" TO tg_app, tg_worker, tg_billing, tg_relay, tg_admin;

-- Extensões (pg_partman não é "trusted": precisa de superusuário).
CREATE EXTENSION IF NOT EXISTS pgcrypto;
CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE SCHEMA IF NOT EXISTS partman AUTHORIZATION tg_owner;
CREATE EXTENSION IF NOT EXISTS pg_partman SCHEMA partman;   -- o schema não pode mudar depois
GRANT ALL ON SCHEMA partman TO tg_owner;
GRANT ALL ON ALL TABLES IN SCHEMA partman TO tg_owner;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA partman TO tg_owner;
GRANT EXECUTE ON ALL PROCEDURES IN SCHEMA partman TO tg_owner;
SQL
