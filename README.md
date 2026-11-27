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
| **Documentos** | Histórico de tudo o que foi auditado, com filtros, comparação por item (declarado × referência × fundamento) e exportação CSV |
| **Alíquotas** | Catálogo por União, 27 UFs e 5.571 municípios (IBGE) com vigência e ato legal, **sincronizado automaticamente com a base oficial da Receita** (data exata em que cada alíquota passa a valer; o que a equipe cadastra à mão passa por aprovação de duas pessoas); linha do tempo pública das atualizações fiscais; consulta pública por município e data (`/v1/publico/aliquotas`, página `/aliquotas`); IS por produto conferido na auditoria; exceções do contribuinte (liminar, regime especial) calculadas ao lado do oficial; reprocessamento automático em mudança retroativa |
| **Apuração** | `GET /v1/apuracao`: débitos, créditos e saldo estimado do mês a partir das notas auditadas, por município e classificação; fornecedores pelo crédito em risco; CSV para o contador |
| **Classificação** | `GET /v1/classificacao`: CST e cClassTrib candidatos por NCM ou NBS nos dados abertos do motor oficial; classificação em massa por CSV no portal |
| **Lotes e webhooks** | ZIP com milhares de XMLs processado pelos workers (fair-share entre clientes), relatório CSV e eventos assinados (HMAC-SHA256) |
| **DF-e** | Certificado A1 em custódia cifrada, distribuição por NSU com as regras anti-bloqueio da SEFAZ, Ciência da Operação e auditoria automática da entrada |
| **Contas e identidade** | Pessoas com e-mail, senha e MFA obrigatório, papéis e convites de equipe; sistemas com API key ou OAuth 2.0 client credentials (OpenIddict); revogação e suspensão em < 5 s |
| **Planos e cobrança** | Planos editáveis no backoffice; franquia, excedente só até o teto autorizado pelo cliente e régua de inadimplência; cada limite do plano vale em todos os caminhos (API, lote, DF-e); limites de requisição por plano, por pessoa e por CNPJ atendido; assinatura, medição e faturas pela Stripe; verificação da titularidade do CNPJ |
| **Avisos** | Ciclo de vida completo no portal e por e-mail: boas-vindas, assinatura, troca de plano, fatura emitida e vencendo, pagamento, renovação, resumo do mês, franquia e teto, inadimplência, webhook pausado, certificado vencendo, titularidade, credencial nova e login novo, mudança de alíquota (só para quem é afetado, na publicação e no dia em que vale) e motor oficial atualizado; informativos desligáveis por pessoa |
| **Integração** | API keys e clientes OAuth criados no portal, webhooks com reenvio, log das chamadas da API (30 dias) |
| **LGPD** | RLS forçado em todas as tabelas, XML não guardado por padrão, CPF pseudonimizado com chave fora do banco, atendimento a titulares |
| **Interfaces** | Site com cadastro e planos, portal do cliente (também no celular), backoffice com operadores e trilha de auditoria (React), central de ajuda em `/ajuda` e referência interativa da API em `/docs` |
| **Versão** | Versão única (`VERSION`) em `GET /version`, no header `X-TaxGateway-Version` e no portal, com aviso de novidades a partir do [`CHANGELOG.md`](CHANGELOG.md) |

## Arquitetura

```
 Cliente / ERP ──HTTPS──► nginx (site + portal)  ──► API .NET 10 ──► motores RFB (sidecar, localhost)
 Operador ──VPN──────────► nginx (backoffice)    ──► API admin (Deployment separado, só o backoffice alcança)
                                                          ├─► PostgreSQL 18 (RLS por tenant, pg_partman)
                                                          ├─► Valkey (cache, rate limit, revogação)
                                                          ├─► S3 (XML cifrado por tenant, opcional)
                                                          └─► outbox ─► RabbitMQ ─► Workers (lotes, webhooks, DF-e, LCR,
                                                                                       Stripe, avisos por e-mail)
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

Crie uma conta em `/cadastro`: o e-mail de confirmação chega no Mailpit do compose (http://127.0.0.1:8025) e o MFA
pede um app autenticador. O passo a passo completo (primeiro operador do backoffice, Stripe, chaves de dev, lotes,
DF-e, carga) está em [`docs/desenvolvimento.md`](docs/desenvolvimento.md).

> As chaves, senhas e a CA em `tests/golden/pki` são **apenas de desenvolvimento e teste** — nunca use em produção.

## Testes

```bash
for s in UnitTests SecurityTests ContractTests GoldenTests; do dotnet test tests/TaxGateway.$s; done
(cd web && npm run typecheck && npm test && npm run e2e)
python tests/smoke/stack_smoke.py           # roteiro ponta a ponta contra a pilha em containers
python tests/smoke/cliente_sintetico.py     # jornada de uma empresa nova e do ERP dela (precisa do Mailpit)
```

Cobertura: `dotnet test --collect:"XPlat Code Coverage"` + `dotnet reportgenerator` (ferramenta local em
`dotnet-tools.json`) e `cd web && npx vitest run --coverage`. Meta do projeto: **≥ 90%** no back e no front.

Os testes Golden e de contrato usam Testcontainers e os motores da RFB rodando localmente.

## CI/CD

* **CI** (`.github/workflows/ci.yml`) em todo push e PR: build e testes .NET, front, motores da RFB, Helm e golden.
* **Deploy** (`deploy.yml`): roda depois do CI verde na `main`; imagens no GHCR → staging → produção com aprovação, sempre
  pelo mesmo digest (actions fixadas por SHA, imagens base por digest, Dependabot para atualizações). Fica desligado até existir cluster:
  ligue com a variável de repositório `DEPLOY_ENABLED=true` e os segredos `KUBECONFIG_STAGING`/`KUBECONFIG_PRODUCTION`.
* **Atualização fiscal** (`fiscal-update.yml`): diária; abre PR com o relatório de regressão e o resumo do bundle quando a RFB publica (ou republica) um motor; com `FISCAL_AUTO_MERGE=true` o PR entra sozinho quando as verificações passam (o deploy em produção continua com aprovação). As alíquotas da base nova são aplicadas pelos workers (D-R30).

## Documentação

| Documento | Conteúdo |
|:--|:--|
| [`SDD.md`](SDD.md) | Design do produto, arquitetura e roadmap (Fases 0–9) |
| [`docs/decisoes.md`](docs/decisoes.md) | Registro de decisões (ADR) e pendências de cada fase |
| [`docs/revisao-multidisciplinar.md`](docs/revisao-multidisciplinar.md) | Revisão por arquitetura, segurança, produto, design e QA, com prioridades |
| [`docs/desenvolvimento.md`](docs/desenvolvimento.md) | Guia de desenvolvimento e operação local |
| [`docs/runbooks/`](docs/runbooks/README.md) | Procedimentos de operação e incidentes |
| [`docs/slo.md`](docs/slo.md) | SLOs, métricas e alertas |
| [`docs/marca.md`](docs/marca.md) | Identidade visual |
| [`CHANGELOG.md`](CHANGELOG.md) | Novidades de cada versão (fonte do modal e de `/ajuda/novidades`) |
| `/ajuda` (no site) | Central de ajuda: passo a passo, exemplos de código, erros e capturas reais |
| `/docs` (na API) | Referência interativa da API (OpenAPI) |

## Situação

Fases 0 a 9, Hardening técnico, interfaces web, central de ajuda e as correções e lacunas da
[revisão multidisciplinar](docs/revisao-multidisciplinar.md) (P0, contas com MFA, planos e Stripe, plataforma P1 e
jornadas de produto), regras comerciais (teto, inadimplência, titularidade), planos aplicados em todos os caminhos, alíquotas por ente com transparência e exceções, limites por plano, apuração do mês e navegação por tarefa implementados e testados. Dependem de recursos externos ou de decisão humana (detalhes em
[`docs/decisoes.md`](docs/decisoes.md)): conta Stripe real, ensaio do canário em cluster, OIDC do GitHub para o
cluster e assinatura de imagens, homologação real de DF-e (30 dias sem `cStat 656`), NFS-e nacional, pentest externo e
revisão jurídica dos Termos, da Política de Privacidade, do RIPD e do DPA, e os atos fiscais ainda não publicados (CBS definitiva de 2027, referências de 2028–2033, alíquotas próprias de entes e do IS por produto).

**Aviso:** indícios de auditoria não constituem parecer tributário.

## Licença

Ainda não definida.
