# Ambiente de desenvolvimento

Guia de execução local. As decisões ficam em `docs/decisoes.md`.

## Subir

```bash
./infra/rfb/build.ps1                  # ou infra/rfb/build.sh: imagens dos motores (uma vez por bundle)
docker compose up -d                   # motores, postgres, valkey, s3 (SeaweedFS), rabbitmq, seq
TG_OWNER_CONNECTION="Host=127.0.0.1;Database=taxgateway;Username=tg_owner;Password=dev-only" \
  dotnet run --project src/TaxGateway.Migrator
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://127.0.0.1:5205 \
  dotnet run --project src/TaxGateway.Api --no-launch-profile
```

> **Volume criado antes da Fase 2:** o bootstrap só roda num volume novo. Crie o `tg_admin` uma vez:
> `docker compose exec postgres psql -U tg_bootstrap -d taxgateway -c "CREATE ROLE tg_admin LOGIN PASSWORD 'dev-only' NOBYPASSRLS; GRANT CONNECT ON DATABASE taxgateway TO tg_admin;"`
> Outra opção é recriar o volume: `docker compose down -v`.

`/health/ready` só fica verde com o banco, os parâmetros e o cadastro de municípios do motor carregados.

## Tenant e simulação

```bash
# Operadores de dev (appsettings.Development.json): ..._fiscal_a_... e ..._fiscal_b_...
curl -s -X POST http://127.0.0.1:5205/admin/v1/tenants \
  -H 'X-Admin-Key: tg_admin_dev_operador_fiscal_a_0000000000' -H 'Content-Type: application/json' \
  -d '{"nome":"Dev","cnpj_raiz":"12.ABC.345/01DE-35","plano":"STARTER","cnpjs":["11222333000181"]}'
# -> api_key (exibida uma vez)

curl -s -X POST http://127.0.0.1:5205/v1/calculo/simular -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' -d '{
  "cnpj_emitente":"11222333000181","regime_tributario":"REGIME_REGULAR",
  "data_fato_gerador":"2027-03-10T10:00:00-03:00","municipio_ibge":3550308,"uf":"SP",
  "itens":[{"nbs":"122011200","cst_ibs_cbs":"000","c_class_trib":"000001","valor_base_calculo":1200}]}'
```

## Mudar uma alíquota (duas pessoas, D-204/D-214)

1. `GET /admin/v1/params/rates`: listar as linhas ativas e escolher o id a substituir.
2. `POST /admin/v1/params/rates/changes`: o operador A propõe, com `justificativa`, `linhas` e `substitui`.
3. `POST /admin/v1/params/changes/{id}/approve`: o operador B aprova. A mudança é aplicada, o snapshot é recarregado e as outras instâncias são avisadas por pub/sub.

## Testes

```bash
dotnet test tests/TaxGateway.UnitTests
dotnet test tests/TaxGateway.SecurityTests       # Docker: Postgres e Valkey em container
dotnet test tests/TaxGateway.GoldenTests         # precisa do motor regular (RTC_REGULAR_URL; padrão: o do compose)
GOLDEN_REGENERATE=1 dotnet test tests/TaxGateway.GoldenTests   # regenera o corpus: revisar o diff antes de aceitar
bash infra/rfb/test-engines.sh regular           # testes oficiais da RFB contra a lista de exceções
```

## Auditoria de XML (Fase 3)

```bash
# Tenant com retenção de 90 dias (sem retencao_xml_dias = zero_retention)
curl -s -X POST http://127.0.0.1:5205/admin/v1/tenants -H 'X-Admin-Key: tg_admin_dev_operador_fiscal_a_0000000000' \
  -H 'Content-Type: application/json' -d '{"nome":"Dev","cnpj_raiz":"11444777000161","plano":"GROWTH","retencao_xml_dias":90,"auto_provisionar_cnpj":true}'

curl -s -X POST 'http://127.0.0.1:5205/v1/auditoria/xml?direction=OUTBOUND' -H "X-Api-Key: $KEY" \
  -H 'Idempotency-Key: 1' -H 'Content-Type: application/xml' --data-binary @nfe.xml
```

* Em dev, só a ICP-Brasil real é âncora. XML assinado pela PKI de teste (`tests/golden/pki`) sai como `ASSINATURA_INVALIDA`.
* `?assinatura=opcional` aceita XML sem assinatura; `?reprocess=true` reaudita sem cobrar.
* Os XMLs ficam cifrados no SeaweedFS (bucket `taxgateway-xml`, prefixo `t/{tenant}/`).

## Pipeline fiscal e deploy (Fase 4)

```bash
python infra/rfb/check_tags.py                         # há tag nova nos motores da RFB?
python infra/rfb/regression_report.py --base http://atual:8080/api/ --candidate http://novo:8080/api/ --out relatorio.md
dotnet test tests/TaxGateway.ContractTests             # contrato OpenAPI das imagens do bundle (RTC_IMAGE_PREFIX)
docker build -f src/Dockerfile --target api -t taxgateway/api:dev .     # também: workers, migrator
helm lint infra/k8s/taxgateway && helm template tg infra/k8s/taxgateway
```

* KMS de produção: `Kms:OpenBao:Address` e `Token` (OpenBao/Vault Transit). Sem eles, o cofre local de dev.
* Revogação: as LCRs são baixadas em segundo plano. `Xml:StrictRevocation=true` recusa a assinatura enquanto a LCR não estiver no cache.
* Reprocessar: `dotnet TaxGateway.Api.dll reprocess --tenant <id> --from 2027-01 --to 2027-03`.

## Simples e entrada (Fase 5)

```bash
# Simulação do Simples pleno (CRT 1): alíquotas efetivas do DAS por item
curl -s -X POST http://127.0.0.1:5205/v1/calculo/simular -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' -d '{
  "cnpj_emitente":"11222333000181","regime_tributario":"SIMPLES_NACIONAL","crt":"1",
  "data_fato_gerador":"2027-06-15T10:00:00-03:00","municipio_ibge":3550308,"uf":"SP",
  "itens":[{"ncm":"84713012","cst_ibs_cbs":"000","c_class_trib":"000001","valor_base_calculo":1000,
            "codigo_atividade_sn":"01","aliquotas_simples":{"ibs":0.5,"cbs":1.2}}]}'

# Auditoria de entrada (o destinatário é o tenant); confirmar=R021 quando o enquadramento foi comprovado
curl -s -X POST 'http://127.0.0.1:5205/v1/auditoria/xml?direction=INBOUND' -H "X-Api-Key: $KEY" \
  -H 'Idempotency-Key: 2' -H 'Content-Type: application/xml' --data-binary @nfe-recebida.xml
```

## Lotes, webhooks e gate de capacidade (Fase 6)

```bash
# Webhook (escopo webhooks:manage): o segredo aparece uma única vez
curl -s -X POST http://127.0.0.1:5205/v1/webhooks -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"url":"https://meu-erp.exemplo/hooks/taxgateway","eventos":["document.audited","batch.audit.completed"]}'

# Lote (modo 1): ZIP só com .xml, Idempotency-Key obrigatória → 202 com batch_id
curl -s -X POST 'http://127.0.0.1:5205/v1/auditoria/lotes?direction=OUTBOUND' -H "X-Api-Key: $KEY" \
  -H 'Idempotency-Key: lote-1' -H 'Content-Type: application/zip' --data-binary @notas.zip
curl -s http://127.0.0.1:5205/v1/auditoria/lotes/$BATCH -H "X-Api-Key: $KEY"            # status
curl -s http://127.0.0.1:5205/v1/auditoria/lotes/$BATCH/relatorio -H "X-Api-Key: $KEY"  # CSV

# Workers (relay do outbox, webhooks, lotes)
ConnectionStrings__Worker=... ConnectionStrings__Relay=... dotnet run --project src/TaxGateway.Workers

# Gate de capacidade (Fase 6)
CAPACITY_ZIP=$(pwd)/out/capacity/lote.zip CAPACITY_DOCS=3000 dotnet test tests/TaxGateway.GoldenTests --filter CapacityCorpusGenerator
docker compose -f docker-compose.yml -f docker-compose.capacity.yml up -d
bash tests/load/capacity-gate.sh        # → out/capacity/relatorio.md
```

> Banco criado antes da Fase 6: crie o role `tg_scheduler` antes do migrator:
> `CREATE ROLE tg_scheduler NOLOGIN NOBYPASSRLS; GRANT tg_scheduler TO tg_owner WITH INHERIT FALSE, SET TRUE;`

## Consumo e faturamento (Fase 7)

```bash
curl -s http://127.0.0.1:5205/v1/usage -H "X-Api-Key: $KEY"                      # competência corrente
# Admin (exige ConnectionStrings:Billing = tg_billing; provedor em Billing:Asaas:BaseUrl/ApiKey)
curl -s -X POST 'http://127.0.0.1:5205/admin/v1/billing/close?mes=2026-11' -H "X-Admin-Key: $OP"
curl -s 'http://127.0.0.1:5205/admin/v1/billing/reconcile?mes=2026-11' -H "X-Admin-Key: $OP"
curl -s -X POST http://127.0.0.1:5205/admin/v1/billing/adjustments -H "X-Admin-Key: $OP" -H 'Content-Type: application/json' \
  -d '{"tenant_id":"...","competencia":"2026-11","valor_centavos":2500,"motivo":"..."}'
```

O worker de fechamento roda nos Workers quando `ConnectionStrings:Billing` está configurada.

## OAuth e OEM (Fase 8)

Em dev a API usa chave de assinatura efêmera e aceita `/oauth/token` sem TLS (`Identity:AllowEphemeralKeys`/`AllowHttp` no
`appsettings.Development.json`). Fora de dev, sem `Identity__SigningKeys__0__Kid/Pem` a API **não sobe** (D-804).

```bash
# Cliente OAuth do tenant (Admin, escopo admin:commercial): o client_secret aparece uma única vez
curl -s -X POST http://127.0.0.1:5205/admin/v1/tenants/$TENANT/oauth-clients -H "X-Admin-Key: $OP" \
  -H 'Content-Type: application/json' -d '{"nome":"erp","scopes":["simulacao:write","usage:read"]}'
# Token (15 min) e uso
TOKEN=$(curl -s -X POST http://127.0.0.1:5205/oauth/token \
  -d grant_type=client_credentials -d client_id=$CID -d client_secret=$CSECRET | jq -r .access_token)
curl -s http://127.0.0.1:5205/v1/usage -H "Authorization: Bearer $TOKEN"
curl -s http://127.0.0.1:5205/.well-known/jwks.json

# Suspender / reativar tenant (vale em < 5 s para token e API key) e revogar cliente ou token
curl -s -X POST http://127.0.0.1:5205/admin/v1/tenants/$TENANT/suspend -H "X-Admin-Key: $OP"
curl -s -X DELETE http://127.0.0.1:5205/admin/v1/oauth-clients/$CID -H "X-Admin-Key: $OP"

# OEM (escopo cnpjs:manage): CNPJs atendidos, chamada em nome de um deles e uso por CNPJ
curl -s -X POST http://127.0.0.1:5205/v1/oem/cnpjs -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' -d '{"cnpj":"11222333000181"}'
curl -s -X POST http://127.0.0.1:5205/v1/calculo/simular -H "X-Api-Key: $KEY" -H 'X-On-Behalf-Of-CNPJ: 11222333000181' ...
curl -s 'http://127.0.0.1:5205/v1/oem/usage?month=2026-11' -H "X-Api-Key: $KEY"
curl -s -X DELETE http://127.0.0.1:5205/v1/oem/cnpjs/11222333000181 -H "X-Api-Key: $KEY"
```

Testes: `dotnet test tests/TaxGateway.GoldenTests --filter IdentityTests` (sobe uma segunda e uma terceira instância da API
para provar a rotação sem 401 e a suspensão em outra instância). Rotação em produção: `docs/runbooks/rotacao-chave-oauth.md`.

## Operação: métricas, PITR e titular (Hardening)

```bash
# Métricas (só na porta de gestão; a pública devolve 404 em /metrics)
ASPNETCORE_URLS="http://127.0.0.1:5205;http://127.0.0.1:9464" dotnet run --no-launch-profile --project src/TaxGateway.Api
curl -s http://127.0.0.1:9464/metrics | grep http_server_request_duration_seconds_count

# Ensaio de backup + PITR (Postgres descartável; relatório em out/backup/pitr-drill.md)
bash infra/backup/pitr-drill.sh

# Direitos do titular (escopo titulares:manage): localizar e expurgar pelo CPF
curl -s -X POST http://127.0.0.1:5205/v1/titulares/localizar -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' -d '{"cpf":"52998224725"}'
curl -s -X POST http://127.0.0.1:5205/v1/titulares/expurgar  -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' -d '{"cpf":"52998224725"}'
```

Runbooks: `docs/runbooks/README.md`. SLOs: `docs/slo.md`. LGPD (rascunhos): `docs/lgpd/`.

## DF-e e certificado A1 (Fase 9)

Os workers apontam para a SEFAZ de **homologação** por padrão (`Dfe:Ambiente=2`, `Dfe:DistUrl`, `Dfe:EventoUrl`).
Produção exige configurar as três explicitamente. `Dfe:WorkerEnabled=false` desliga o laço (os testes usam isso).

```bash
# Certificado A1 do CNPJ (escopo dfe:manage): a resposta traz só thumbprint, titular e vencimento
curl -s -X PUT http://127.0.0.1:5205/v1/cnpjs/11444777000161/certificado -H "X-Api-Key: $KEY" \
  -H 'Content-Type: application/json' -d "{\"pfx_base64\":\"$(base64 -w0 a1.pfx)\",\"senha\":\"...\"}"
# Ligar a captura (não antecipa um bloqueio de 1 h em curso) e acompanhar
curl -s -X POST http://127.0.0.1:5205/v1/cnpjs/11444777000161/dfe/sync -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' -d '{"c_uf_autor":35}'
curl -s http://127.0.0.1:5205/v1/cnpjs/11444777000161/dfe/status -H "X-Api-Key: $KEY"
```

Testes: `dotnet test tests/TaxGateway.GoldenTests --filter DfeTests` (SEFAZ simulada com mTLS; ~2,5 min no Windows).

## Interfaces web: docs, site, portal e backoffice

Referência da API: `http://127.0.0.1:5205/docs/` (o documento `admin` aparece só em Development).

```bash
# App web em dev (Vite com proxy para a API em :5205; mostra site, portal e backoffice juntos)
cd web && npm ci && npm run dev            # http://127.0.0.1:5173  ·  /portal  ·  /backoffice
npm run typecheck && npm test              # tipos + testes de componente (Vitest)

# Imagens nginx (mesma origem da API) e E2E no navegador (Playwright/Chromium)
docker build ./web --target public     -t taxgateway/web:dev
docker build ./web --target backoffice -t taxgateway/web-backoffice:dev
docker run -d --name tg-web -p 127.0.0.1:8088:8080 -e API_UPSTREAM=host.docker.internal:5205 taxgateway/web:dev
docker run -d --name tg-bo  -p 127.0.0.1:8089:8080 -e API_UPSTREAM=host.docker.internal:5205 taxgateway/web-backoffice:dev
cd web && npx playwright install chromium && npm run e2e

# Ou tudo no compose (API do compose de capacidade + web)
docker compose -f docker-compose.yml -f docker-compose.capacity.yml -f docker-compose.web.yml up -d --build web web-backoffice
# site/portal: http://127.0.0.1:8090 · backoffice: http://127.0.0.1:8091/backoffice (8080 é o motor)
```

Login provisório: o portal pede uma **API key** do tenant; o backoffice, a **chave de operador** (em dev,
`tg_admin_dev_operador_fiscal_a_0000000000`). A credencial fica só na aba (`sessionStorage`).
Atrás de proxy, configure `Http:TrustedProxyNetworks` na API (CIDR do ingress/nginx), senão `/oauth/token` recusa HTTP.

## Varredura completa (antes de entregar)

```bash
dotnet build TaxGateway.slnx -c Release                     # analisadores, aviso = erro
dotnet format TaxGateway.slnx --verify-no-changes            # LF e estilo (D-Q01)
dotnet list TaxGateway.slnx package --vulnerable --include-transitive
for s in UnitTests SecurityTests ContractTests GoldenTests; do dotnet test tests/TaxGateway.$s; done
(cd web && npm run typecheck && npm test && npm audit --omit=dev)
python infra/rfb/check_tags.py --self-test && python infra/rfb/regression_report.py --self-test
bash infra/backup/pitr-drill.sh
# Pilha em containers + roteiro ponta a ponta (15 passos pelos nginx)
for t in api workers migrator; do docker build -f src/Dockerfile --target $t -t taxgateway/$t:dev .; done
docker compose -f docker-compose.yml -f docker-compose.capacity.yml -f docker-compose.web.yml up -d --build api workers rtc-regular-batch web web-backoffice
python tests/smoke/stack_smoke.py
```
