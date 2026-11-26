<p align="center">
  <img src="web/public/brand/logo.svg" alt="TaxGateway" height="56">
</p>

<p align="center"><b>Cálculo e auditoria de IBS, CBS e Imposto Seletivo, por API.</b><br>
Sobre os motores oficiais de cálculo da Receita Federal (Reforma Tributária do Consumo — LC 214/2025).</p>

---

O motor de cálculo da Receita Federal é aberto e gratuito. Colocá-lo em produção não é: o TaxGateway entrega o que
falta em volta dele — leitura do XML, regras de auditoria com fundamentação legal, lotes, captura automática de DF-e,
operação multiempresa (OEM), cobrança por uso, segurança e LGPD.

## O que tem aqui

| Área | O que faz |
|:--|:--|
| **Simulação** | `POST /v1/calculo/simular`: CBS, IBS (UF e município) e IS por item, regime regular e Simples Nacional, com o snapshot de parâmetros usado |
| **Auditoria** | `POST /v1/auditoria/xml`: recalcula NF-e/NFC-e (saída e entrada), valida schema e assinatura ICP-Brasil e aponta divergências com regra, campo e fundamento legal |
| **Lotes e webhooks** | ZIP com milhares de XMLs processado pelos workers (fair-share entre clientes), relatório CSV e eventos assinados (HMAC-SHA256) |
| **DF-e** | Certificado A1 em custódia cifrada, distribuição por NSU com as regras anti-bloqueio da SEFAZ, Ciência da Operação e auditoria automática da entrada |
| **Identidade** | API key e OAuth 2.0 client credentials (OpenIddict), rotação de chave sem 401, revogação e suspensão em < 5 s |
| **OEM e cobrança** | Multi-CNPJ, ledger de uso, fechamento mensal idempotente com o provedor (Asaas) e reconciliação |
| **LGPD** | RLS forçado em todas as tabelas, XML não guardado por padrão, CPF pseudonimizado com chave fora do banco, atendimento a titulares |
| **Interfaces** | Site, portal do cliente, backoffice (React) e referência interativa da API em `/docs` |

## Arquitetura

```
 Cliente / ERP ──HTTPS──► nginx (site + portal)  ──► API .NET 10 ──► motores RFB (sidecar, localhost)
 Operador ──VPN──────────► nginx (backoffice)    ──►      │
                                                          ├─► PostgreSQL 18 (RLS por tenant, pg_partman)
                                                          ├─► Valkey (cache, rate limit, revogação)
                                                          ├─► S3 (XML cifrado por tenant, opcional)
                                                          └─► outbox ─► RabbitMQ ─► Workers (lotes, webhooks, DF-e, cobrança)
```

* **Stack:** .NET 10 (ASP.NET Core minimal APIs), PostgreSQL 18, Valkey 8, RabbitMQ 4, SeaweedFS/S3, OpenBao (KMS),
  React 18 + TypeScript + Vite, nginx, Helm + Argo Rollouts (canário), Prometheus/OpenTelemetry.
* **Motores oficiais:** `calculadora-rtc-regime-regular` e `calculadora-rtc-simples-nacional`, construídos a partir de
  commits fixados em [`infra/rfb/versions.json`](infra/rfb/versions.json), com detecção diária de versões e regressão.

## Estrutura

```
src/        TaxGateway.Api · Core · Infrastructure · Identity · Contracts · Workers · Migrator (+ Dockerfile)
web/        Site, portal do cliente e backoffice (React); nginx e E2E (Playwright)
tests/      Unit · Security (RLS, XML, SSRF…) · Contract (motores) · Golden (ponta a ponta) · load · smoke
infra/      postgres (bootstrap) · rfb (motores) · k8s (Helm) · backup (PITR) · xsd-schemas · icp-brasil
docs/       decisões, desenvolvimento, runbooks, SLOs, marca, rascunhos de RIPD/DPA
SDD.md      Documento de design (fonte da verdade do produto e do roadmap)
```

## Começando

Pré-requisitos: .NET SDK 10.0.401 (ver [`global.json`](global.json)), Docker, Node 22 e Python 3.

```bash
# Motores da RFB (imagens a partir de versions.json) e infraestrutura local (Postgres, Valkey, S3, RabbitMQ, Seq)
bash infra/rfb/build.sh
docker compose up -d                          # o KMS local é um cofre de dev; OpenBao é usado nos testes

# Banco e API
dotnet run --project src/TaxGateway.Migrator -- "Host=127.0.0.1;Database=taxgateway;Username=tg_owner;Password=dev-only"
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/TaxGateway.Api      # http://127.0.0.1:5205/docs/

# Site, portal e backoffice
cd web && npm ci && npm run dev                                                  # http://127.0.0.1:5173
```

O passo a passo completo (tenant de teste, chaves de dev, lotes, DF-e, carga) está em
[`docs/desenvolvimento.md`](docs/desenvolvimento.md).

> As chaves, senhas e a CA em `tests/golden/pki` são **apenas de desenvolvimento e teste** — nunca use em produção.

## Testes

```bash
for s in UnitTests SecurityTests ContractTests GoldenTests; do dotnet test tests/TaxGateway.$s; done
(cd web && npm run typecheck && npm test && npm run e2e)
python tests/smoke/stack_smoke.py           # roteiro ponta a ponta contra a pilha em containers
```

Os testes Golden e de contrato usam Testcontainers e os motores da RFB rodando localmente.

## CI/CD

* **CI** (`.github/workflows/ci.yml`) em todo push e PR: build e testes .NET, front, motores da RFB, Helm e golden.
* **Deploy** (`deploy.yml`): imagens no GHCR → staging → produção com aprovação. Fica desligado até existir cluster:
  ligue com a variável de repositório `DEPLOY_ENABLED=true` e os segredos `KUBECONFIG_STAGING`/`KUBECONFIG_PRODUCTION`.
* **Atualização fiscal** (`fiscal-update.yml`): diária; abre PR com o relatório de regressão quando a RFB publica (ou republica) um motor.

## Documentação

| Documento | Conteúdo |
|:--|:--|
| [`SDD.md`](SDD.md) | Design do produto, arquitetura e roadmap (Fases 0–9) |
| [`docs/decisoes.md`](docs/decisoes.md) | Registro de decisões (ADR) e pendências de cada fase |
| [`docs/desenvolvimento.md`](docs/desenvolvimento.md) | Guia de desenvolvimento e operação local |
| [`docs/runbooks/`](docs/runbooks/README.md) | Procedimentos de operação e incidentes |
| [`docs/slo.md`](docs/slo.md) | SLOs, métricas e alertas |
| [`docs/marca.md`](docs/marca.md) | Identidade visual |
| `/docs` (na API) | Referência interativa da API (OpenAPI) |

## Situação

Fases 0 a 9, Hardening técnico e interfaces web implementados e testados. Dependem de recursos externos ou de
decisão humana (detalhes em [`docs/decisoes.md`](docs/decisoes.md)): ensaio do canário em cluster, homologação real
de DF-e (30 dias sem `cStat 656`), sandbox do Asaas, NFS-e nacional, pentest externo, revisão jurídica de RIPD/DPA e
login de usuários por OIDC.

**Aviso:** indícios de auditoria não constituem parecer tributário.

## Licença

Ainda não definida.
