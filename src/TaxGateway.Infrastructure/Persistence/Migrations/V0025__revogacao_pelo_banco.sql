-- V0025 — Suspensão e revogação lidas do banco de verdade (D-R29).
-- app.revocation_state() era SECURITY DEFINER do dono das migrations, que não tem política nas tabelas com FORCE RLS:
-- devolvia só os jti revogados. Tenant suspenso e cliente OAuth revogado dependiam apenas do Valkey (um SREM liberava o
-- acesso, contrariando D-P10). Agora a função é do tg_scheduler, que lê tenants e clientes OAuth pelas próprias políticas.
GRANT SELECT ON revoked_tokens TO tg_scheduler;
GRANT SELECT (id, status) ON tenants TO tg_scheduler;
GRANT SELECT (client_id, revoked_at) ON oauth_applications TO tg_scheduler;
ALTER FUNCTION app.revocation_state() OWNER TO tg_scheduler;
