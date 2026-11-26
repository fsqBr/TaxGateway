# Runbook: suspender ou reativar um tenant

* **Suspender:** `POST /admin/v1/tenants/{id}/suspend` (escopo `admin:commercial`). Vale em < 5 s em todas as
  instâncias, para token OAuth **e** API key (D-803; medido ~15 ms em teste). As chamadas recebem
  `403 ERR_TENANT_SUSPENDED` e o cliente não obtém token novo.
* **Reativar:** `POST /admin/v1/tenants/{id}/reactivate`.
* **Revogar só um cliente OAuth:** `DELETE /admin/v1/oauth-clients/{client_id}`.
* **Revogar só um token:** `POST /admin/v1/oauth/revoked-tokens` com `jti` e `expira_em`.
* **Limitação conhecida:** lotes já aceitos continuam sendo processados pelos workers (pendência da Fase 8).
* A suspensão não apaga dados. Encerramento (`CLOSED`) e eliminação seguem o contrato/DPA e a retenção legal.
