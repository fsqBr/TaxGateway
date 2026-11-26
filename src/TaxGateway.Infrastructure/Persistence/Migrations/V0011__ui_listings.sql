-- V0011: listagens para o backoffice web (D-W04). O operador vê nome e data dos clientes OAuth, nunca o hash do segredo
-- nem os escopos (continua testado em AdminAndParamsTests).
GRANT SELECT (name, created_at) ON oauth_applications TO tg_admin;
