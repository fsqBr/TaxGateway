# SOFTWARE DESIGN DOCUMENT (SDD) — TAXGATEWAY IBS/CBS/IS ENGINE

**Versão:** 2.16 (Fases 0 a 9, Hardening técnico, interfaces web, central de ajuda e versionamento implementados; revisão multidisciplinar aplicada — correções P0, contas com MFA, planos e cobrança pela Stripe, plataforma P1 e jornadas de produto; NFS-e, homologação real da Fase 9 e itens externos pendentes; decisões de implementação em `docs/decisoes.md`. Substitui a 1.0 — ver §0 para o mapa de mudanças; fatos validados em 25/11/2026 contra a legislação, as NTs e o código dos motores)
**Data:** Novembro de 2026
**Stack Principal:** .NET 10 (C# 14), PostgreSQL 18 (`uuidv7()` nativo; mínimo 17), Valkey 8+ (BSD), Object Storage S3-compatível, RabbitMQ 4 (quorum queues), Docker/Kubernetes
**Motores Fiscais (Sidecars):** imagens próprias construídas a partir dos repositórios oficiais MIT `ReceitaFederal/calculadora-rtc-regime-regular` e `ReceitaFederal/calculadora-rtc-simples-nacional` (Java 25, Spring Boot 4.1, SQLite somente leitura gerado via Flyway)

---

## 0. Mapa de Mudanças v1 → v2

| # | Problema na v1 | Resolução na v2 | Seção |
|:--|:--|:--|:--|
| 1 | Assumia imagens Docker oficiais da RFB | Não existem: construímos imagens próprias, multi-stage, com o SQLite gerado no build e versões fixadas num manifesto | §2.2 |
| 2 | Tratava os dois motores como equivalentes | Simples Nacional depende do banco gerado pelo repo do regime regular, não calcula IS e não tem endpoints de XML. O adapter declara as capacidades de cada motor | §2.2, §8 |
| 3 | Monitorava "releases" | A RFB publica **tags** (sem GitHub Releases): o pipeline monitora tags | §2.3 |
| 4 | Deploy automático de nova versão fiscal em produção | Staging → relatório de regressão → **aprovação manual** → canário. Imagens antigas retidas para reprocessar auditorias históricas | §2.3 |
| 5 | Exemplo com IBS de 17,7% em 2027 (errado) | Exemplos corrigidos para a transição (IBS 0,05% + 0,05%; CBS = referência − 0,1 p.p.). Testes usam snapshots do motor, não valores escritos à mão | §6.1, §13 |
| 6 | Auditoria apresentada como veredito; mapeamento XML→motor subestimado | Mapeador XML→motor como componente próprio. Catálogo de regras com natureza `DETERMINISTICA` vs `INDICIO`. Crédito segue LC 214 arts. 47–48 | §6.2, §8.2, §8.3 |
| 7 | Idempotência quebrava entre meses (UNIQUE incluía a competência) | Tabela `document_registry` não particionada com UNIQUE global `(tenant_id, idempotency_hash)` | §9, §10 |
| 8 | Partição LIST em VARCHAR, sem partição DEFAULT | `RANGE` sobre `billing_month DATE`, partição DEFAULT com alerta, pg_partman com premake | §9 |
| 9 | RLS incompleto (sem FORCE, role dono, tabelas sem política, `''::uuid`) | Roles separados, `FORCE ROW LEVEL SECURITY` em tudo, `app.current_tenant()` seguro, função `SECURITY DEFINER` dedicada para autenticação | §4.1, §9 |
| 10 | `signing_secret` de webhook em texto puro | Envelope encryption (KMS), dois segredos ativos para rotação, proteção contra SSRF | §7, §9 |
| 11 | Billing com duas fontes de verdade (Redis + Postgres) sem atomicidade | Ledger append-only no Postgres, gravado **na mesma transação** do documento. Redis fica só para rate limit e cache | §10 |
| 12 | Stripe "Usage Records" (API legada) | Abstração `IBillingProvider`: Asaas como principal (BRL, PIX/boleto) e Stripe via **Billing Meters** como opção | §10.4 |
| 13 | XML bruto no S3 com dados pessoais; salt do HMAC no mesmo banco | Criptografia por tenant (crypto-shredding), modo "zero retention", chave HMAC no KMS, minimização de `xNome`/endereço, CNPJ de MEI tratado como dado pessoal | §4.3 |
| 14 | Headers `X-RateLimit-*` | Headers IETF `RateLimit` / `RateLimit-Policy` (draft-12: `q`, `qu`, `w`, `a`) | §5.3 |
| 15 | Limite de concorrência sem expiração | Leases com TTL num ZSET via Lua | §5.2 |
| 16 | ZIP de até 2 GB sem defesas | Limites por entrada, por total descompactado e por razão de compressão; parser XML endurecido (XXE); sem ZIP aninhado | §6.3, §4.4 |
| 17 | Servidor OAuth escrito à mão | OpenIddict (client credentials, JWKS, rotação de chaves) | §3.1 |
| 18 | Limites de plano sem base empírica | Limites provisórios até o gate de capacidade (k6), com SLOs definidos | §5.1, §12 |
| 19 | *(novo)* CNPJ alfanumérico (vigente desde jul/2026) | Validação `[A-Z0-9]{12}[0-9]{2}`, DV com o novo algoritmo; chave de acesso de 44 caracteres alfanuméricos | §3.3 |
| 20 | *(novo)* Ataque de *signature wrapping* no XMLDSig | Validação que amarra a `Reference URI` ao `Id` do `infNFe` processado | §4.4 |
| 21 | *(novo)* Lote de um tenant monopolizando workers | Fatiamento em *chunks* e escalonamento justo entre tenants | §6.3 |
| 22 | *(novo)* DF-e/A1 citado, mas fora do roadmap | Módulo próprio (Fase 9), com controle de NSU e respeito ao bloqueio por consumo indevido | §6.5 |
| 23 | *(validação)* Desde 01/01/2027 o motor regular **exige** `aliquotasNominais` do chamador | Catálogo de Alíquotas próprio, com ato legal rastreável por alíquota | §8.5 |
| 24 | *(validação)* Motores não têm autenticação (`permitAll`) e fazem chamada de saída para `piloto-cbs.tributos.gov.br` | A rede é o único controle de acesso; egress bloqueado; URL sobrescrita | §2.1 |
| 25 | *(validação)* Políticas RLS da tabela mãe são ignoradas em consulta direta à partição | Privilégios só na tabela mãe; teste garante que nenhuma partição tem GRANT | §4.1, §9 |
| 26 | *(validação)* MinIO community sem manutenção; MassTransit v9 comercial; Stripe usage records removidos | Substituições definidas | §10.4, §11 |
| 27 | *(validação)* Roteamento por CRT, IS no Simples, MEI, arredondamento, crédito | Resolvidos com base na LC 214/227, LC 123, NT 2025.002 v1.53, NT 2026.005 e no código dos motores | §8.1, §8.3, §14 |
| 28 | *(análise do código)* Cobertura nacional implícita | Cadastro de entes, alíquota por ente com forma de aplicação, local de incidência, fusos, eventos | §8.6 |
| 29 | *(análise do código)* Parâmetros espalhados ou fixos no código | Camada de parametrização temporal, versionada, com aprovação e recarga a quente | §15 |
| 30 | *(análise do código)* Sem orçamento de latência | Motor no mesmo pod, referência em memória, cache determinístico, tuning do motor por variável de ambiente, SLOs agressivos medidos | §16 |
| 31 | *(Fase 1)* `tg_auth` com `BYPASSRLS`; modelo só no documento | Nenhum role com `BYPASSRLS` (políticas nomeadas por role, D-103); migrations SQL versionadas como fonte de verdade (D-101) | §4.1, §9, `docs/decisoes.md` |
| 32 | *(Fase 2)* `COPY` binário numa tabela com RLS; `xml_snippet` em toda simulação; corte do Valkey em 2 ms; escrita de parâmetros sem role | INSERT com `unnest` por tenant (D-213); grupos oficiais do DF-e devolvidos sem segunda chamada (D-202); corte de 50 ms (D-211); role `tg_admin`, histórico do catálogo e fluxo de duas pessoas (D-203, D-204, D-214) | §6.1, §15, §16.2, `docs/decisoes.md` |
| 48 | *(Revisão de produto e UX)* Sem consulta de auditorias nem histórico; escolha de cClassTrib sem apoio; nenhum alerta ao cliente; primeira auditoria falhava sem caminho; portal só com JSON e inutilizável no celular; contrato aceitava campo desconhecido | Documentos auditados com filtros, detalhe por item e CSV (`auditoria:read`); consulta de CST/cClassTrib por NCM/NBS; avisos no portal e por e-mail; primeiros passos; XML de exemplo e "cadastrar este CNPJ"; simulação por formulário; layout móvel; contrato estrito (D-J01 a D-J08) | §6, §9, `docs/decisoes.md` |
| 47 | *(Revisão de plataforma)* Produção subia com configuração de dev; Admin API no mesmo pod e Secret da API pública; LCR inalcançável em produção; readiness dependente do banco; memória e conexões sem teto; webhooks sem justiça; deploy com CI vermelho e tags mutáveis | Travas de produção; `api-admin` separada com Secrets por workload e NetworkPolicy default-deny; LCR publicada pelos workers; readiness local; tetos de memória e pool; disjuntor e rodízio de webhooks; sidecars nativos e preStop; deploy após o CI por digest, actions por SHA (D-P13 a D-P31) | §2, §4, §5, §7, §16 |
| 46 | *(Revisão de produto)* Acesso de pessoas por API key no navegador; planos sem preço nem cadastro; cobrança só desenhada para Asaas | Contas com e-mail, senha e MFA obrigatório, papéis e convites; operadores com conta; planos de mercado editáveis no backoffice com histórico; Stripe (assinatura, Billing Meters, portal e webhooks) no lugar do Asaas (D-U01 a D-U10, D-C01 a D-C09) | §3, §10 |
| 45 | *(Revisão multidisciplinar)* 12 problemas P0: motor do Simples ausente no Kubernetes, canário sem tráfego, lote sem idempotência, reprocessamento gratuito para qualquer nota, 500 no OAuth, faturamento duplicável, cache do Valkey adulterável, entre outros | Correções com teste de regressão cada (D-P01 a D-P12); relatório em `docs/revisao-multidisciplinar.md` | §2, §6, §8, §10 |
| 44 | *(Ajuda e versão)* Sem versão visível nem forma de avisar mudanças; sem material para quem não é desenvolvedor | Versão única em `VERSION` (assemblies, `GET /version`, header `X-TaxGateway-Version`, OpenAPI, app); `CHANGELOG.md` alimenta o modal de novidades e `/ajuda/novidades`; aviso de novo deploy; central `/ajuda` com exemplos e capturas reais reproduzíveis (D-W11 a D-W15) | `CHANGELOG.md`, `docs/decisoes.md`, `web/src/help/` |
| 43 | *(Varredura de qualidade)* Tag do motor republicada pela RFB não era detectada e quebrava o rebuild; finais de linha inconsistentes; dependência do front vulnerável | Build por commit fixado; detector de `retag`; LF padronizado; dependências atualizadas e open redirect mitigado; roteiro ponta a ponta da pilha (D-Q01 a D-Q06) | §2.2, §2.3, `docs/decisoes.md` |
| 42 | *(Revisão de UI e docs)* Referência da API com grupos vazios, sem respostas nem erros documentados; UI sem identidade | Grupos refeitos com descrição; códigos, exemplos reais e erros problem+json em todas as rotas, verificados por teste de contrato; marca e sistema de design aplicados em site, portal, backoffice e /docs (D-W08 a D-W10) | `docs/marca.md`, `docs/decisoes.md` |
| 41 | *(Interfaces web)* Backoffice previsto no Marco C não existia; sem portal, landing nem referência navegável; `/oauth/token` falharia atrás do ingress | Referência `/docs` (Scalar) com documentos público e admin separados; app React com alvos público (site + portal) e backoffice; nginx na mesma origem com CSP estrita; X-Forwarded-* só de redes confiáveis (D-W01 a D-W07) | §3.1, §15, `docs/decisoes.md`, `web/` |
| 40 | *(Fase 9)* Teto de consultas e momento de "agora" sem definição operacional; ciência sem formato; teste do critério 656 sem ambiente | Regras anti-656 aplicadas antes de cada consulta com o relógio da aplicação (teto de 20/h para todas as consultas); evento 210210 assinado RSA-SHA1 (leiaute); A1 em envelope com raiz de CNPJ conferida; SEFAZ simulada com mTLS que pune violações (D-901 a D-907) | §4.2, §6.5, `docs/decisoes.md` |
| 39 | *(Hardening)* SLOs e canário dependiam de métricas que a API não publicava; direitos do titular (§4.3) sem implementação; PITR só descrito | Métricas OpenTelemetry em porta de gestão; `PrometheusRule` com queima de orçamento; ensaio de PITR automatizado; `/v1/titulares/*`; runbooks; rascunhos de RIPD/DPA (D-H01 a D-H06) | §4.3, §12, §16.3, `docs/` |
| 38 | *(Fase 8)* Rotação de chave descrita sem procedimento para frota mista; suspensão dependia de cache da API key; header OEM sem regra de conflito com o documento | OpenIddict em modo degradado (clientes em `oauth_applications`); rotação em duas etapas `[antiga, nova]` → `[nova, antiga]`; revogação em memória por pub/sub + releitura de 2 s, valendo também para API key; `X-On-Behalf-Of-CNPJ` precisa bater com o CNPJ sujeito; auto-provisionamento não reativa CNPJ desativado (D-801 a D-806) | §3.1, §3.3, §6.4, `docs/decisoes.md`, `docs/runbooks/` |
| 37 | *(Fase 7)* Preço de excedente e cobrança de SCHEMA_ERROR sem lugar no modelo; ajustes sem tabela; idempotência do provedor só descrita | Colunas de preço e `bill_schema_errors` no plano; `billing_adjustments` (v2+); fechamento em SQL único; Asaas deduplicado por `externalReference`; reconciliação com 1 milhão de eventos (D-701 a D-705) | §10, `docs/decisoes.md` |
| 36 | *(Fase 6)* Fair-share só por filas; varreduras entre tenants sem role; SSRF só na URL | Claim de chunks no banco com `tg_scheduler` (K por tenant, rodízio); webhooks com envelope, rotação, SSRF na conexão; lote com defesas na leitura; gate de capacidade medido (D-601 a D-608) | §6.3, §7, §12, `docs/decisoes.md` |
| 35 | *(Fase 5)* Simples e INBOUND só descritos; R023 heurística; R024 sem fonte de dados | Roteamento pela tabela `engine_routing` na simulação e na auditoria; adapter do Simples; `gTribSN`; INBOUND com crédito (R040) e indício (R021) com contexto `confirmar`; R023 → R025; R024 inativa até haver dados (D-501 a D-507) | §6.1, §6.2, §8.1, §8.3, `docs/decisoes.md` |
| 34 | *(Fase 4)* Pipeline fiscal só descrito; KMS e revogação sem implementação; sem imagem da aplicação nem manifests | `check_tags.py`, contrato, relatório de regressão e PR automático (D-401 a D-403); imagem chiseled e Helm chart com Rollout canário, NetworkPolicy e migrator como Job (D-404, D-405); OpenBao Transit (D-406); LCR em cache (D-407); job de reprocessamento (D-408) | §2.3, §4.2, §4.4, `docs/decisoes.md` |
| 33 | *(Fase 3)* Regras sem "valor ≠ motor" e sem vínculo NCM×cClassTrib; recálculo da base sem método definido; XML sem retenção padrão | R013, R025, R029 e informativas I001–I006 (D-307); motor recebe os componentes do item e calcula a base (D-305); zero_retention por padrão e envelope por objeto (D-308) | §8.3, §4.3, `docs/decisoes.md` |

---

## 1. Visão Geral & Objetivo do Sistema

Plataforma SaaS B2B *multi-tenant* e *API-first* que encapsula os motores oficiais da Receita Federal para a Reforma Tributária do Consumo (CBS, IBS e Imposto Seletivo). Em volta deles a plataforma oferece:

* **Auditoria de documentos fiscais** (saída, antes da transmissão; entrada, para verificar o crédito), com regras explicáveis e fundamentação legal.
* **Processamento em lote** de alto volume, com isolamento entre clientes.
* **Gestão multi-CNPJ / OEM** para Software Houses.
* **Metering e faturamento por uso.**

O motor da RFB é gratuito e aberto. **O valor do produto está no que está em volta dele**: mapeamento XML→motor, catálogo de regras de auditoria, lote, captura automática de DF-e, OEM e operação 24×7.

* **Core / Gateway / Orquestração:** .NET 10 (ASP.NET Core Minimal APIs + Worker Services).
* **Motores Fiscais:** sidecars stateless com o código oficial MIT, sem nenhuma modificação.
* **Público-Alvo:** Software Houses/ERPs (OEM multi-CNPJ) e empresas de médio e grande porte.

### 1.1. Premissas e Limites de Responsabilidade
* O sistema **não** substitui a análise tributária do contribuinte. Achados de natureza `INDICIO` são apresentados como tal na API, nos relatórios e nos Termos de Uso.
* O produto não pode sugerir endosso oficial da RFB. A licença MIT permite uso comercial, mas exige manter o aviso de copyright.
* Decisões tributárias em aberto estão listadas no §14 e precisam de validação de um especialista antes da fase correspondente.

---

## 2. Topologia de Infraestrutura

```text
                    ┌─────────────────────────────────────────┐
                    │         Cloudflare / WAF / TLS          │
                    └────────────────────┬────────────────────┘
                                         │ HTTPS
                    ┌────────────────────▼────────────────────┐
                    │        TaxGateway.Api (.NET 10)         │
                    │  • Auth: API Key | JWT (OpenIddict)     │
                    │  • Tenant/CNPJ Resolver + RLS context   │
                    │  • Rate Limit (token bucket + leases)   │
                    │  • Idempotency-Key                      │
                    │  • XML hardening / XSD / XMLDSig        │
                    └───┬───────────────────────────────┬─────┘
                        │ Sync                          │ Outbox → RabbitMQ
                        ▼                               ▼
      ┌──────────────────────────────────┐   ┌───────────────────────────────┐
      │  Engine Router (Bulkhead Sync)   │   │ TaxGateway.Workers            │
      └───────────┬──────────────┬───────┘   │ • Batch (chunks, fair-share)  │
                  │              │           │ • Webhooks • Billing • DF-e   │
                  ▼              ▼           │ • Partition/Retention         │
      ┌─────────────────┐ ┌─────────────────┐└───────────────┬───────────────┘
      │ rtc-regular     │ │ rtc-simples     │◄───────────────┘ (Bulkhead Batch)
      │ :8080 (SQLite ro)│ │ :8082 (SQLite ro)│
      └─────────────────┘ └─────────────────┘
      ┌─────────────────────────────────────────────────────────────────────────┐
      │ PostgreSQL (RLS forçado, partições RANGE, ledger) • Redis/Valkey        │
      │ S3 (SSE-KMS + envelope por tenant) • KMS/Vault • OTel/Prometheus/Grafana│
      └─────────────────────────────────────────────────────────────────────────┘
```

### 2.1. Regras Inegociáveis
1. **Isolamento dos motores:** os motores **não têm autenticação** (`anyRequest().permitAll()` no Spring Security), então a rede é o único controle de acesso. Sem Ingress; a `NetworkPolicy` de entrada só aceita os pods `api` e `workers` nas portas 8080 (regular) e 8082 (simples), e o Prometheus nas portas de gestão 9101/9103 (`/health`, `/metrics`).
2. **Imutabilidade:** imagem com `readOnlyRootFilesystem: true`, usuário não-root, SQLite montado como somente leitura (perfil `offline`) e um `emptyDir` em `/tmp` para a JVM.
3. **Código oficial sem fork:** construímos a partir de *tag + commit SHA* verificados. Qualquer ajuste fica no build ou no adapter, nunca em patch no código da RFB.
4. **Sem deploy fiscal automático em produção** (ver §2.3).
5. **Egress bloqueado:** no perfil `offline` os motores consultam `piloto-cbs.tributos.gov.br` (variáveis `VERSAO_OFFLINE_URL` no regular e `VERSAO_URL` no simples) e trazem dependências OTLP. A `NetworkPolicy` nega toda saída e as variáveis são sobrescritas. Com isso `/versao/status` fica indisponível; a versão vem das labels da imagem.

### 2.2. Construção das Imagens dos Motores
Os repositórios oficiais **não trazem Dockerfile**. Os dois motores compartilham o mesmo banco SQLite, gerado pelas migrations Flyway do repositório do **regime regular**.

Manifesto versionado `infra/rfb/versions.json`, que é a fonte única das versões em uso:

```json
{
  "bundle": "2026.11.2",
  "regular": {
    "repo": "ReceitaFederal/calculadora-rtc-regime-regular", "tag": "1.7.2",
    "commit": "32df7196356e81f2df08e6023fc2b6dbc48036d3",
    "jar": "api-regime-geral.jar", "port": 8080, "mgmt_port": 9101
  },
  "simples": {
    "repo": "ReceitaFederal/calculadora-rtc-simples-nacional", "tag": "1.0.2",
    "commit": "f90b6525e6d5823e784baecfc6fef0ad2f04b74f",
    "jar": "api-simples-nacional.jar", "port": 8082, "mgmt_port": 9103
  },
  "db_source": "regular"
}
```

O Dockerfile executável está em `infra/rfb/Dockerfile`, validado na Fase 0 com `infra/rfb/build.ps1`. Diferenças em relação ao README, descobertas na execução:
* Os repositórios **não versionam** `.mvn/wrapper/maven-wrapper.jar`, e o `./mvnw` falha num clone limpo. Usamos `mvn` da imagem `maven:3.9-eclipse-temurin-25`.
* O `sqlite-jdbc` extrai a biblioteca nativa em `/tmp` e precisa carregá-la de lá. Com root somente leitura, `/tmp` deve ser montado **com exec** (Docker: `tmpfs /tmp:exec`; Kubernetes: `emptyDir`). Também passamos `--enable-native-access=ALL-UNNAMED`.

Resumo do Dockerfile:

```dockerfile
# Stage 1: banco SQLite gerado pelo Flyway do regime regular
#          (Flyway 12.4 via pluginManagement do Spring Boot 4.1.2; migrations até V0073 na tag 1.7.2)
FROM maven:3.9-eclipse-temurin-25 AS db
ARG REGULAR_TAG REGULAR_SHA
RUN apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*
RUN git clone --depth 1 --branch ${REGULAR_TAG} https://github.com/ReceitaFederal/calculadora-rtc-regime-regular /src \
 && cd /src && test "$(git rev-parse HEAD)" = "${REGULAR_SHA}"
WORKDIR /src
RUN mkdir -p ./calculadora/db \
 && mvn flyway:migrate -Dflyway.configFiles=./flyway/flyway-pro.conf --batch-mode --errors \
 && sha256sum calculadora/db/calculadora-pro.db | cut -d' ' -f1 > /db.sha256

# Stage 2: build do motor (regular ou simples). Os testes oficiais exigem o banco
#          e rodam num job de CI separado (ver Fase 0).
FROM maven:3.9-eclipse-temurin-25 AS build
ARG ENGINE_REPO ENGINE_TAG ENGINE_SHA
RUN apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*
RUN git clone --depth 1 --branch ${ENGINE_TAG} https://github.com/${ENGINE_REPO} /app \
 && cd /app && test "$(git rev-parse HEAD)" = "${ENGINE_SHA}"
WORKDIR /app
RUN mvn -DskipTests --batch-mode package

# Stage 3: runtime mínimo
FROM eclipse-temurin:25-jre
ARG ENGINE_JAR
RUN useradd -r -u 10001 rtc
# a URL do datasource é relativa ao diretório de trabalho: ./calculadora/db/calculadora-pro.db?mode=ro
WORKDIR /opt/rtc
COPY --from=build /app/target/${ENGINE_JAR} app.jar
COPY --from=db /src/calculadora/db/calculadora-pro.db calculadora/db/calculadora-pro.db
COPY --from=db /db.sha256 db.sha256
USER 10001
ENTRYPOINT ["java","-XX:MaxRAMPercentage=75","-Dspring.profiles.active=offline","-jar","app.jar"]
```

* A imagem recebe as labels `rtc.bundle`, `rtc.tag`, `rtc.commit` e `rtc.db_sha256`. Esses valores voltam no campo `rfb_engine_version` de toda resposta e ficam gravados em cada auditoria.
* Os dois motores de um mesmo `bundle` usam **o mesmo** `db_sha256`. O CI falha se divergirem.
* O repositório do Simples não declara o plugin do Flyway e o próprio README manda usar os scripts do regime regular: por isso o banco é gerado **uma vez**, no stage `db`, e copiado para as duas imagens.
* O banco é aberto com `mode=ro` (confirmado em `application-offline.yml`) e os motores só escrevem log no console. O Tomcat embutido precisa de um diretório temporário: `emptyDir` em `/tmp`.
* As variáveis `VERSAO_OFFLINE_URL`/`VERSAO_URL` são sobrescritas no manifesto do Kubernetes (apontando para um stub interno, se o motor não aceitar valor vazio; testar na Fase 0).
* Os pacotes offline oficiais (`calculadora.zip`, `calculadora-jar.zip`) estão retornando 404 (issue #15 do regime regular), o que reforça construir a partir do código.
* O motor pode devolver o header `x-warning-dados-simulados`: ele é propagado como item de `avisos[]` na resposta da API.

### 2.3. Pipeline de Atualização Fiscal
1. **Detecção** (`infra/rfb/check_tags.py`, workflow `fiscal-update`): um job diário consulta `GET /repos/ReceitaFederal/{repo}/tags` e abre um PR que atualiza `versions.json` (tag + SHA). As tags não têm prefixo `v` (ex.: `1.7.2`), apesar de o GOVERNANCE.md dos repositórios citar `vMAJOR.MINOR.PATCH`: o parser aceita as duas formas.
2. **Build e testes de contrato:** constrói as imagens e roda os Testes de Contrato C# contra o Swagger/OpenAPI dos motores (detecta quebra de schema).
3. **Relatório de regressão fiscal:** roda o *corpus golden* (§13) na versão atual e na nova e gera um diff por item (alíquota, base, valor, cClassTrib aceito/rejeitado). O relatório é anexado ao PR.
4. **Staging:** deploy automático no ambiente de staging.
5. **Aprovação manual:** GitHub Environment `production` com revisor obrigatório (engenharia + responsável fiscal), que lê o diff de regressão.
6. **Canário:** 5% do tráfego síncrono por 30 min, com rollback automático se a taxa de erro ou a latência p95 subirem.
7. **Retenção:** as imagens de todos os bundles ficam no registry por **no mínimo 6 anos** (prazo decadencial + margem). Uma auditoria histórica pode ser reproduzida com o bundle original via job efêmero (`dotnet TaxGateway.Api.dll reprocess --tenant ID --from AAAA-MM --to AAAA-MM`, com a API apontando para o motor do bundle desejado; D-408).

---

## 3. Arquitetura de Integração & Identidade

### 3.1. Fluxo A: OAuth 2.0 Client Credentials (Enterprise/OEM)
* Implementado com **OpenIddict 7.x** (Apache 2.0, com TFM .NET 10); não escrevemos servidor de tokens próprio. Endpoint `POST /oauth/token`; JWKS em `/.well-known/jwks.json`.
* Por padrão o OpenIddict **cifra** o access token (JWE). Usamos `DisableAccessTokenEncryption()` para emitir JWT apenas assinado (`typ: at+jwt`), validável localmente pelas instâncias da API via JWKS.
* `client_secret` exibido uma única vez e guardado como hash Argon2id.
* Token JWT (RS256 ou ES256), TTL de 15 min. Chaves rotacionadas a cada 90 dias, com sobreposição: a chave antiga continua publicada no JWKS até expirar o último token assinado por ela.
  * *Implementado (D-804):* RS256; chaves PEM do secret, iguais em todos os pods; rotação em duas etapas (`[antiga, nova]` e depois `[nova, antiga]`) para a frota mista do rollout não gerar 401. Procedimento em `docs/runbooks/rotacao-chave-oauth.md`.
* Claims: `sub`, `tid`, `plan`, `scope`, `jti`, `exp`. Os limites (`rpm`, `conc`) **não** vão no token, porque mudança de plano precisa ter efeito imediato. Eles são lidos do cache de plano (§5).
* **Revogação:** blacklist no Redis por `jti`, `client_id` **e** `tenant_id` (tenant suspenso), com TTL igual ao tempo restante do token.
  * *Implementado (D-803):* cópia em memória em cada instância, atualizada por pub/sub e releitura a cada 2 s; a suspensão de tenant também bloqueia API keys.

### 3.2. Fluxo B: API Key (ERP Legado)
* Header `X-Api-Key: tg_live_sk_<prefix8>_<secret32>`. Chave de alta entropia; armazenamos `HMAC-SHA256(pepper_kms, key)`. O pepper fica no KMS, então um vazamento só do banco não permite verificar chaves offline.
* Validação: Redis (`auth:apikey:{hash}`, TTL 5 min). Em cache miss, chama a função `app.resolve_api_key()` (§9), que é `SECURITY DEFINER` e de propriedade de um role com acesso **somente** a `api_keys` e `tenants`.
* Revogação: invalida a entrada de cache imediatamente (pub/sub entre as instâncias).

### 3.3. Escopo de CNPJ (`X-On-Behalf-Of-CNPJ`)
* **CNPJ alfanumérico** (IN RFB 2.229/2024; atribuído só a novas inscrições desde julho/2026, os CNPJs existentes não mudam). Fonte: NT Conjunta DFe 2025.001.
  * Formato `^[A-Z0-9]{12}[0-9]{2}$`, coluna `CHAR(14)`.
  * DV em módulo 11, com valor do caractere = código ASCII − 48 (A = 17, B = 18…). Pesos do DV1: `5,4,3,2,9,8,7,6,5,4,3,2`; do DV2: `6,5,4,3,2,9,8,7,6,5,4,3,2`. Resto < 2 ⇒ DV 0; senão 11 − resto.
  * Não bloquear letras específicas: a NT cita uma possível vedação de I, O, U, Q e F, ainda não confirmada (§14).
* **Chave de acesso:** posições 7–18 alfanuméricas (raiz + ordem do CNPJ); as demais são numéricas (`^[0-9]{6}[A-Z0-9]{12}[0-9]{26}$`). O DV da chave também usa ASCII − 48 e módulo 11. O schema da NF-e mudou esses campos para o tipo `C` desde 01/07/2026 (NT 2026.004).
* Plano não-OEM: o CNPJ sujeito (`<emit>` em OUTBOUND, `<dest>` em INBOUND) precisa estar em `tenant_cnpjs` ativo.
* Plano OEM: header `X-On-Behalf-Of-CNPJ`. Com `auto_provision_cnpj = true`, o CNPJ é registrado automaticamente. Sem isso, a resposta é `403 ERR_CNPJ_NOT_PROVISIONED`.
  * *Implementado (D-805, D-806):* o header é opcional e, se enviado, precisa ser igual ao CNPJ sujeito (senão `403 ERR_CNPJ_SCOPE`). Um CNPJ desativado pelo tenant não volta pelo auto-provisionamento.
* **Roteamento de motor por regime:** em simulações, vem do campo `regime_tributario`. Em XML, vem do `<CRT>` e do `tpRegimeIBSCBS` do emitente, conforme a tabela configurável do §8.1.

---

## 4. Segurança Multi-Tenant & LGPD

### 4.1. Row-Level Security
* Roles do Postgres:

| Role | Uso | RLS |
|:--|:--|:--|
| `tg_owner` | dono dos objetos, migrations (só no CI/CD) | n/a (não usado em runtime) |
| `tg_app` | API | sujeito (`NOBYPASSRLS`) |
| `tg_worker` | workers por tenant | sujeito |
| `tg_billing` | fechamento de faturamento | políticas `TO tg_billing` somente nas tabelas de uso |
| `tg_relay` | relay do outbox → RabbitMQ | políticas `TO tg_relay` só em `outbox` (lê tudo, atualiza só `published_at`) |
| `tg_auth` | dono da função `resolve_api_key` (`NOLOGIN`) | **sem** `BYPASSRLS`: política `auth_lookup` `FOR SELECT` só em `api_keys`/`tenants` (D-103) |

* **Nenhum** role de runtime tem `BYPASSRLS` ou é superusuário (testado). Acesso entre tenants só existe como política nomeada para um role específico (D-103).
* Roles e extensões são criados por um bootstrap de cluster executado como superusuário (`infra/postgres/init`); as migrations rodam como `tg_owner` (D-104).
* **Todas** as tabelas com `tenant_id` têm `ENABLE` **e** `FORCE ROW LEVEL SECURITY`, com políticas `USING` e `WITH CHECK` sobre `(SELECT app.current_tenant())` (avaliada uma vez por consulta, D-107).
* **Partições:** quando uma partição é consultada diretamente, as políticas da tabela mãe são **ignoradas** (doc do PostgreSQL, seção de herança). Por isso os roles de runtime recebem privilégio **só na tabela mãe**; numa consulta pelo pai, o PostgreSQL checa a permissão apenas nele. O pg_partman roda com `inherit_privileges = false`, e um teste de segurança falha se qualquer partição tiver GRANT para `tg_app`, `tg_worker` ou `tg_billing`.
* O contexto é definido por `SELECT set_config('app.current_tenant_id', $1, true)` dentro de uma transação explícita aberta por `ITenantDb.InTenantAsync` (Npgsql, D-109). É a única forma de obter conexão para tabelas de tenant; o `TenantScope` é invalidado ao fim da unidade de trabalho. Como o contexto é local à transação, o pool não precisa de reset (D-110).
* `app.current_tenant()` usa `NULLIF(..., '')`. Sem contexto, o resultado é `NULL` e a consulta retorna zero linhas, sem erro de cast.
* Um teste automatizado (§13) tenta ler e gravar dados de outro tenant em cada tabela e falha o build se conseguir.

### 4.2. Certificados Digitais A1
* O `.pfx` e a senha são cifrados com **envelope encryption**: uma DEK AES-256-GCM por certificado, embrulhada pela KEK do tenant no KMS/Vault.
* Decifrados só em memória, no worker de DF-e. Nunca aparecem em log, dump ou trace.
* Alerta 30, 15 e 5 dias antes do vencimento (webhook `certificate.expiring`).

### 4.3. LGPD & Privacy-by-Design
* **Papéis:** o TaxGateway é **operador**; o cliente é **controlador**. Há DPA (contrato de tratamento) no onboarding e RIPD (relatório de impacto) mantido.
* **Dados pessoais considerados:** CPF, nome (`xNome`), endereço e e-mail de destinatário pessoa física, além de dados de **empresário individual e MEI** (o CNPJ pode identificar uma pessoa).
* **Metadados no Postgres:** CPF vira `HMAC-SHA256(k_tenant, cpf)`, com `k_tenant` derivada no KMS e **fora do banco** (CPF tem só 11 dígitos; com a chave no banco, um vazamento permitiria força bruta). `xNome`/endereço de PF não são persistidos em metadados.
* **XML bruto no S3:**
  * Cifrado com DEK por tenant (envelope); apagar a KEK do tenant torna todos os XMLs ilegíveis (*crypto-shredding*) no encerramento do contrato.
  * Prefixo por tenant, bucket sem acesso público, leitura só via serviço (URLs pré-assinadas de 5 min) e log de acesso habilitado.
  * Retenção configurável (30/90/365 dias/5 anos) via *lifecycle*.
  * **Modo `zero_retention`:** o XML não é gravado; só ficam metadados e achados.
* **Logs e telemetria:** nunca registram corpo de XML, CPF ou nome. Um *redactor* no pipeline do OpenTelemetry mascara padrões de CPF/CNPJ.
* **Direitos do titular:** endpoint administrativo para localizar documentos pelo HMAC do CPF (a pedido do controlador) e expurgá-los.
  * *Implementado (D-H05):* `POST /v1/titulares/localizar` e `/expurgar`, escopo `titulares:manage` do próprio tenant (o controlador). O expurgo apaga o XML e o vínculo com o CPF e mantém o registro fiscal sem dado pessoal; cada pedido fica em `titular_requests`.

### 4.4. Endurecimento do Processamento de XML
* `XmlReaderSettings`: `DtdProcessing = Prohibit`, `XmlResolver = null` (bloqueia XXE), `MaxCharactersInDocument` limitado por tipo de documento (padrão 5 MB).
* Validação XSD nativa (`XmlSchemaSet` compilado uma vez e em cache) com os pacotes oficiais versionados em `infra/xsd-schemas/` (NF-e/NFC-e com os grupos da RTC; NFS-e padrão nacional).
* **XMLDSig:**
  * Valida a assinatura com `SignedXml`.
  * Exige **exatamente uma** `Reference` cujo `URI` aponte para o `Id` do `infNFe` que será processado. A auditoria usa esse mesmo nó (proteção contra *signature wrapping*).
  * Rejeita transforms fora da lista permitida.
  * Valida a cadeia ICP-Brasil (âncoras versionadas no repositório).
  * Um XML sem `protNFe` (pré-transmissão) aceita a ausência de autorização, mas não a de assinatura, salvo no modo `?assinatura=opcional`.

---

## 5. Rate Limiting & Proteção dos Motores

### 5.1. Planos (tabela `plans`, sobrescrevível por tenant)

> Valores **provisórios**: os limites finais saem do gate de capacidade da Fase 6 (§12), medidos com k6 contra os motores.

| Plano | RPM | Burst | Concorrência | Lote máx. | Prioridade |
|:--|:--|:--|:--|:--|:--|
| STARTER | 120 | 10 | 2 | 500 XMLs / 15 MB | low |
| GROWTH | 600 | 30 | 10 | 5.000 / 100 MB | normal |
| SCALE | 3.000 | 100 | 30 | 25.000 / 500 MB | high |
| OEM / ENTERPRISE | contrato | contrato | contrato | 100.000 / 2 GB | realtime |

### 5.2. Algoritmos (Redis Lua, atômicos)
* **Taxa:** *token bucket* por tenant (`rl:tb:{tenant}`: tokens + último refill), com capacidade = burst e refill = RPM/60 por segundo.
* **Concorrência:** *leases* num ZSET (`rl:conc:{tenant}`), com membro = `request_id` e score = `now + lease_ttl`. O script remove leases vencidos (`ZREMRANGEBYSCORE`), confere `ZCARD < limite` e faz `ZADD`. O lease é liberado no fim da requisição, em `finally`. `lease_ttl` = timeout do endpoint + 5 s, para que uma requisição que caiu não deixe o slot preso.
* **Falha do Redis:** cai para um limitador local em memória (`System.Threading.RateLimiting`) com limite/N instâncias. Há métrica e alerta. Não se rejeita tudo.
* Limites de plano ficam em cache local (30 s) com invalidação via pub/sub.

### 5.3. Headers de Resposta (draft-ietf-httpapi-ratelimit-headers-12)
```http
RateLimit-Policy: "rpm";q=600;w=60, "conc";q=10;qu="concurrent-requests"
RateLimit: "rpm";a=542;w=60, "conc";a=7
```
* O draft ainda não é RFC. Parâmetros: `q` (quota), `qu` (unidade), `w` (janela em segundos), `a` (disponível). A escrita dos headers fica isolada num `RateLimitHeaderWriter`, para acompanhar mudanças até a publicação.
* O draft não tem parâmetro de tempo até o reset: essa informação vai só no `Retry-After` do 429.
* Quando excedido: `429` com `application/problem+json` (RFC 9457) e `Retry-After`. Métrica `taxgateway_ratelimit_rejected_total{plan,reason}`. `tenant_id` **não** é usado como label de métrica (cardinalidade); vai para logs e exemplars.

---

## 6. Endpoints & Contratos (OpenAPI v1)

Todos os erros seguem a **RFC 9457**. Todo `POST` que grava estado aceita `Idempotency-Key`: obrigatório em `/auditoria/*` e `/lotes`, opcional em `/calculo/simular`. Mesma chave com payload diferente retorna `422 ERR_IDEMPOTENCY_KEY_REUSED`. A resposta original fica armazenada por 24 h (`idempotency_requests`).

### 6.1. `POST /v1/calculo/simular`
Request: igual à v1 (`cnpj_emitente`, `regime_tributario`, `data_fato_gerador`, municípios IBGE, itens com `ncm`/`nbs`, `cst_ibs_cbs`, `c_class_trib`, `valor_base_calculo`...), mais um bloco opcional `aliquotas_nominais` por item. Ele sobrescreve o Catálogo de Alíquotas (§8.5), e a resposta registra `fonte_aliquota: CLIENTE`.

Response `200 OK`. **Valores ilustrativos** para 2027:
* IBS em fase de teste: 0,05% UF + 0,05% município (ADCT art. 127; LC 214 art. 344).
* CBS = alíquota de referência − 0,1 p.p. (LC 214 art. 347). O exemplo supõe a referência de 11,3% calculada pelo TCU, **ainda não fixada pelo Senado** (§14).

```json
{
  "simulation_id": "sim_01JDH89K2M4N5P6Q7R8S9T0V",
  "rfb_engine_version": { "bundle": "2026.11.2", "engine": "REGULAR", "tag": "1.7.2" },
  "totais": {
    "valor_base_total": 5000.00,
    "cbs_valor": 560.00,
    "ibs_uf_valor": 2.50,
    "ibs_mun_valor": 2.50,
    "ibs_total_valor": 5.00,
    "imposto_seletivo_valor": 0.00,
    "carga_tributaria_efetiva_percent": 11.30
  },
  "itens": [
    {
      "numero_item": 1,
      "cbs": { "aliquota": 11.20, "valor": 560.00, "fonte_aliquota": "CATALOGO" },
      "ibs_uf": { "aliquota": 0.05, "valor": 2.50 },
      "ibs_mun": { "aliquota": 0.05, "valor": 2.50 },
      "fundamentacao": [{ "c_class_trib": "000001", "texto": "<devolvido por /dados-abertos>" }],
      "xml_snippet_gIBSCBS": "<gIBSCBS>...</gIBSCBS>"
    }
  ],
  "avisos": []
}
```
* **Implementado na Fase 2 (D-202):** cada item traz `tributos_dfe`, com os grupos oficiais (`IBSCBS`, `IS`) exatamente como o motor devolve, e a resposta traz `operacao_dfe` (ex.: `gCompraGov`). Também vêm `fonte_aliquota` e `catalogo_id` por tributo, `param_snapshot_id` e avisos como `ALIQUOTA_PROVISORIA`. O `xml_snippet_gIBSCBS` (via `/xml/generate`) não é gerado por padrão, porque dobraria o custo do motor.
* **Simples (Fase 5, D-502):** `regime_tributario = SIMPLES_NACIONAL` com `crt` (1 ou 2), `tp_regime_ibs_cbs` opcional e, por item, `codigo_atividade_sn` e `aliquotas_simples` (`ibs`, `cbs`, alíquotas efetivas do DAS). A resposta traz `regime_calculo` e o bloco `simples` por item.
* `data_fato_gerador` exige offset; a data fiscal é a local desse offset (D-220). Limite síncrono: 500 itens (D-215).
* Se o motor não suportar a combinação pedida (ex.: IS no Simples), a resposta é `422 ERR_ENGINE_CAPABILITY`. Nunca calculamos por fora do motor.
* Avisos do motor (header `x-warning-dados-simulados`) aparecem em `avisos[]`.

### 6.2. `POST /v1/auditoria/xml?direction=OUTBOUND|INBOUND`
Body `application/xml` (NF-e 55, NFC-e 65 ou NFS-e nacional). Etapas: endurecimento (§4.4) → XSD → assinatura → mapeamento (§8.2) → motor → catálogo de regras (§8.3).

```json
{
  "audit_id": "aud_01JDH90A1B2C3D4E5F6G7H8J",
  "chave_acesso": "35270212ABC345000199550010000045901123456789",
  "direction": "INBOUND",
  "status": "INDICIO",
  "rfb_engine_version": { "bundle": "2026.11.2", "engine": "REGULAR", "tag": "1.7.2" },
  "is_billable": true,
  "replay": false,
  "resumo_financeiro": {
    "tributo_declarado": { "cbs": 1240.00, "ibs": 14.20, "is": 0.00 },
    "tributo_recalculado": { "cbs": 3080.00, "ibs": 35.40, "is": 0.00 },
    "diferenca": 1861.20,
    "credito_potencial": 3115.40,
    "credito_status": "APROPRIAVEL_POR_DESTAQUE"
  },
  "ocorrencias": [
    {
      "regra": "R021",
      "natureza": "INDICIO",
      "confianca": "MEDIA",
      "severidade": "WARNING",
      "numero_item": 3,
      "campo_xml": "det[3]/imposto/IBSCBS/gIBSCBS/cClassTrib",
      "valor_declarado": "200015",
      "valor_referencia": "000001",
      "mensagem": "cClassTrib com redução de alíquota condicionada a requisitos que não podem ser verificados só pelo XML. Confirme o enquadramento com o fornecedor.",
      "fundamentacao": "<texto de /dados-abertos para o cClassTrib declarado>",
      "impacto_financeiro_estimado": 1840.00
    }
  ],
  "aviso_legal": "Indícios não constituem parecer tributário."
}
```
* **Fase 5 (D-503 a D-505):** `direction=INBOUND` audita a nota de entrada do destinatário, com `credito_potencial` e `credito_status` (R040) e indício R021. O contexto opcional `confirmar=R021` registra que o cliente comprovou o enquadramento. Status `INDICIO` quando só há indícios. Simples (gTribSN), sublimite e MEI (R050) são roteados pela tabela `engine_routing`.
* **Implementado na Fase 3 (OUTBOUND, NF-e/NFC-e; D-301 a D-314):** `Idempotency-Key` obrigatória; `reprocess=true`; `assinatura=opcional`. A resposta traz também `param_snapshot_id`, `regras_versao`, `reprocessamento`, `xml_armazenado` e `avisos`. O recálculo envia ao motor os componentes do item (o motor calcula o vBC) e as alíquotas do catálogo (D-305).
* `status`: `CONFORME` | `DIVERGENTE` (há ao menos uma regra `DETERMINISTICA` violada) | `INDICIO` (só indícios) | `SCHEMA_ERROR` | `ASSINATURA_INVALIDA` | `INDETERMINADO` (o motor não suporta o caso).
* Reenvio da mesma chave de acesso, em qualquer mês, devolve a auditoria existente com `replay: true` e `is_billable: false`. Para reauditar com outro bundle, use `?reprocess=true`, que também não é cobrado (§10).

### 6.3. `POST /v1/auditoria/lotes`
* **Modo 1:** `multipart/form-data`, transmitido em *stream* direto para o S3 (sem buffer, `MultipartReader`). Limitado a 100 MB.
* **Modo 2 (recomendado):** `POST /v1/auditoria/lotes/upload-url` gera uma URL pré-assinada (15 min, `Content-Length` fixado). O cliente faz `PUT` e depois `POST /v1/auditoria/lotes` com o `storage_object_key`.
* **Defesas do ZIP**, aplicadas durante a leitura em stream:
  * só arquivos `.xml`; ZIP aninhado é rejeitado;
  * nomes de entrada ignorados para gravação (nada é extraído em disco, então não há *path traversal*);
  * até 5 MB descompactados por entrada;
  * total descompactado ≤ 10× o limite do plano;
  * razão de compressão por entrada ≤ 100:1;
  * número de entradas ≤ limite do plano.
  * Uma violação aborta o lote com `BATCH_REJECTED_UNSAFE_ARCHIVE`.
* **Processamento:** o lote é fatiado em *chunks* de 500 documentos. **Implementado (D-602):** os chunks ficam na tabela `batch_chunks` e são reservados por `app.claim_batch_chunks` com **fair-share por tenant**: no máximo K chunks em voo por tenant, rodízio entre tenants e prioridade do plano. Um lote de 100 mil notas não bloqueia os lotes dos outros clientes. O RabbitMQ fica com eventos e webhooks.
* Response `202 Accepted` com `batch_id`, `status_url` e `estimated_completion_seconds` (calculado pela vazão medida). O resultado final é um relatório CSV/XLSX no S3, entregue por URL pré-assinada e pelo webhook `batch.audit.completed`.

### 6.4. Endpoints OEM
* `POST /v1/oem/cnpjs` · `DELETE /v1/oem/cnpjs/{cnpj}` (encerra a cobrança na competência seguinte) · `GET /v1/oem/usage?month=YYYY-MM` (por CNPJ, lido do ledger).
* *Implementado (D-806):* também `GET /v1/oem/cnpjs`; escopo novo `cnpjs:manage`. Admin: `POST /admin/v1/tenants/{id}/oauth-clients`, `/suspend`, `/reactivate`, `DELETE /admin/v1/oauth-clients/{id}`, `POST /admin/v1/oauth/revoked-tokens`.
* `GET /v1/usage` (todos os planos): consumo e prévia da fatura da competência.

### 6.5. Captura Automática de DF-e (Fase 9)
* `PUT /v1/cnpjs/{cnpj}/certificado` (upload do A1) · `POST /v1/cnpjs/{cnpj}/dfe/sync` · `GET /v1/cnpjs/{cnpj}/dfe/status`.
* O worker consome **NFeDistribuicaoDFe** (Ambiente Nacional) com mTLS e controla `ultNSU` por CNPJ (`dfe_sync_state`).
* Regras da NT 2014.002 v1.40, todas aplicadas pelo worker para nunca disparar rejeição por consumo indevido (`cStat 656`):
  * Depois de "nenhum documento" (`cStat 137`, ou `ultNSU = maxNSU`), espera **1 hora** antes de nova consulta daquele CNPJ. O bloqueio é gravado em `next_allowed_at`.
  * A consulta sempre parte do `ultNSU` devolvido (NSU fora de sequência gera 656).
  * No máximo 20 consultas por hora em `consChNFe`/`consNSU`.
  * Depois de um 656, o CNPJ fica bloqueado por 1 hora, e uma nova tentativa nesse período reinicia a contagem.
* Todos os aplicativos do mesmo CNPJ compartilham a mesma sequência de NSU. O onboarding avisa o cliente para não rodar outro consumidor de DF-e em paralelo; a alternativa é o TaxGateway ser o único consumidor e redistribuir os XMLs.
* Os documentos ficam disponíveis por **90 dias**: um CNPJ que entra tarde perde o que for mais antigo.
* Um `resNFe` dispara manifestação de **Ciência da Operação** (configurável) para obter o `procNFe` completo, que entra automaticamente na auditoria INBOUND.
* *Implementado (D-901 a D-907):* também `GET`/`DELETE /v1/cnpjs/{cnpj}/certificado`; escopo `dfe:manage`; o `sync` não antecipa um bloqueio em curso; o teto de 20 consultas/h vale para todas as consultas do CNPJ (mais conservador que a NT); webhooks `dfe.document.audited` e `certificate.expiring`. URLs padrão de homologação.
* Fase seguinte: NFS-e nacional (ADN) e CT-e.

---

## 7. Webhooks

> **Implementado na Fase 6 (D-603 a D-605).** Onde este texto e a implementação divergirem, vale o registro de decisões.

* **Assinatura:** `TaxGateway-Signature: t=<unix>,v1=<HMAC_SHA256(secret, t + "." + raw_body)>[,v1=...]`, mais `TaxGateway-Event` e `TaxGateway-Delivery`. O cliente deve rejeitar `t` com mais de 5 min de diferença (anti-replay). Há uma função de verificação de referência (`WebhookSignature.Verify`).
* **Segredos:** cifrados por envelope com a KEK do tenant. Na rotação (`POST /v1/webhooks/{id}/rotate-secret`), o segredo anterior continua assinando por 24 h (assinaturas `v1=...,v1=...`).
* **Agenda implementada:** 1 min → 5 min → 30 min → 2 h → 6 h → 12 h → 24 h e depois `DEAD`. **410 Gone** desativa o endpoint na hora. Desativação automática por 72 h de falhas e aviso por e-mail ficam pendentes.
* **Proteção contra SSRF:**
  * só `https`;
  * o DNS é resolvido no momento do envio e IPs privados, loopback, link-local e metadata (`169.254.169.254`) são bloqueados;
  * sem seguir redirects;
  * timeout de 10 s e resposta lida até no máximo 64 KB.
* **Entrega:** via *transactional outbox* (o evento é gravado na mesma transação do fato) → RabbitMQ → dispatcher.
  * Retries com jitter: `10s → 1m → 5m → 30m → 2h → 12h`. Depois da 6ª falha, o status passa a `DEAD`.
  * Reprocessamento manual: `POST /v1/webhooks/deliveries/{id}/retry`.
  * A ordem não é garantida: o cliente deduplica por `Delivery-Id`.
* Um endpoint com 100% de falha por 72 h é desativado automaticamente e o tenant é notificado por e-mail.

---

## 8. Integração com os Motores (Anti-Corruption Layer)

```csharp
public enum RfbEngine { Regular, Simples }

[Flags]
public enum EngineCapability
{
    CalcCbsIbs = 1, CalcIs = 2, XmlGenerate = 4, XmlValidate = 8,
    BaseCalculoUnificada = 16, DadosAbertos = 32
}

public interface IRfbTaxEngineAdapter
{
    RfbEngine Engine { get; }
    EngineCapability Capabilities { get; }   // Regular: todas; Simples: CalcCbsIbs
    Task<RfbCalculationResult> CalculateAsync(RfbCalculationInput input, CancellationToken ct);
    Task<RfbBaseCalculoResult> ComputeUnifiedBaseAsync(RfbBaseCalculoInput input, CancellationToken ct);
    Task<RfbXmlValidationResult> ValidateXmlGroupAsync(string xml, CancellationToken ct);
    Task<RfbEngineMetadata> GetMetadataAsync(CancellationToken ct);  // /versao + labels da imagem
}

public interface IRfbReferenceData   // cache Redis de /dados-abertos, chaveado por bundle
{
    Task<CClassTribInfo?> GetCClassTribAsync(string code, DateOnly date, CancellationToken ct);
    Task<bool> IsCstCompatibleAsync(string cst, string cClassTrib, DateOnly date, CancellationToken ct);
}
```
* Chamar uma capacidade ausente lança `EngineCapabilityNotSupportedException`, que vira `422 ERR_ENGINE_CAPABILITY` ou o status `INDETERMINADO`.
* Usa os endpoints `/observabilidade/...` (erros por item) em vez dos endpoints *fail-fast*, para apontar todos os problemas de uma vez.
* O cache de dados abertos é chaveado por `bundle`: um deploy de nova versão invalida tudo sem precisar de purge manual.

### 8.1. Roteamento por Regime (tabela `engine_routing`, configurável)

| `CRT` | `tpRegimeIBSCBS` | Motor(es) | Base |
|:--|:--|:--|:--|
| 3 — Regime Normal | ausente | REGULAR | — |
| 1 — Simples Nacional | ausente | SIMPLES (`SN_PLENO`): IBS e CBS dentro do DAS (`gIBSSN`/`gCBSSN`) | LC 123 art. 13, IX–X; NT 2026.005 |
| 2 — Excesso de sublimite | ausente | SIMPLES (`SN_SUBLIMITE`, CBS no DAS) **+** REGULAR para o IBS, que sai do DAS (`gIBSUF`/`gIBSMun`, `gCBS` com alíquota zerada) | LC 123 art. 13-A; NT 2026.005 |
| 1 ou 2 | 1 (opção pelo regime regular) | REGULAR para IBS/CBS; SIMPLES em modo `SOMENTE_RECEITA` | LC 214 art. 41 §§3º–5º; NT 2026.005 — confirmar com casos golden (§14) |
| 3 | 2 | o motor do Simples devolve `PENDENTE` | **semântica a confirmar (§14)**; até lá, `INDETERMINADO` |
| 4 — MEI | — | nenhum: o MEI recolhe valores fixos (Anexo VII da LC 123) e **não informa** o grupo IBSCBS; o motor do Simples responde 422 | LC 123 art. 18-A; NT 2026.005; código do motor. A regra R050 verifica a ausência do grupo |

* O motor do Simples **não calcula a alíquota efetiva** (RBT12) nem substitui o PGDAS-D: ele recebe `aliquotasEfetivas`. Na simulação, o cliente informa essas alíquotas. Na auditoria, usamos as declaradas no XML e verificamos se estão dentro da faixa do anexo (regra R024, `INDICIO`).
* **Imposto Seletivo** é devido fora do DAS mesmo pelos optantes (LC 123 art. 13 §1º, XIV-A). O motor do Simples só recebe o `vIS` já calculado; o cálculo de IS é feito pelo motor REGULAR. As regras de IS do motor regular ainda estão incompletas (ROADMAP: Release 16; issues #6 e #20). Até lá, divergências de IS saem como `INDETERMINADO` e não são cobradas.

### 8.2. Mapeador XML → Motor (componente central do produto)
* `INfeRtcMapper` (NF-e/NFC-e) e `INfseRtcMapper` (NFS-e nacional), versionados por **leiaute/NT**: grupos `IBSCBS`, `gIBSCBS`, `gIBSUF`, `gIBSMun`, `gCBS`, `IS` e totais `IBSCBSTot`.
* Dois produtos para cada item:
  1. **Valores declarados**: CST, cClassTrib, vBC, alíquotas, valores, redução e diferimento.
  2. **Input do motor**: data do fato gerador (`dhEmi`), municípios, NCM/NBS, CST e cClassTrib declarados, e valores que compõem a base (para `base-calculo-unificada`).
* Campos não mapeáveis geram a ocorrência `MAP_UNSUPPORTED_FIELD`, sem falha silenciosa.
* Coberto por testes de propriedade e pelo corpus golden (§13).

### 8.3. Catálogo de Regras de Auditoria (`TaxGateway.Core/Audit/Rules`)
Cada regra tem `id`, `versão`, `natureza`, `severidade`, `fundamentação` e testes próprios. Os achados guardam `regra@versão`.

| Regra | Descrição | Natureza |
|:--|:--|:--|
| R001 | XML inválido no XSD | DETERMINISTICA |
| R002 | Assinatura inválida ou cadeia não ICP-Brasil | DETERMINISTICA |
| R010 | vBC declarado ≠ base unificada calculada pelo motor | DETERMINISTICA |
| R011 | valor ≠ vBC × alíquota − vDif − vDevTrib, com tolerância de ±0,01 por item (mesma regra das rejeições 1041/1052/1069 da NT 2025.002) | DETERMINISTICA |
| R012 | alíquota declarada ≠ alíquota do Catálogo (§8.5) ajustada pela redução do cClassTrib | DETERMINISTICA |
| R020 | CST incompatível com cClassTrib | DETERMINISTICA |
| R022 | cClassTrib inexistente ou fora de vigência na data | DETERMINISTICA |
| R030 | `IBSCBSTot` ≠ soma exata dos itens (rejeições 1076/1080/1084/1085/1091) | DETERMINISTICA |
| R013 | valor do tributo ≠ valor recalculado pelo motor (±0,01 por item) — *Fase 3, D-307* | DETERMINISTICA |
| R025 | NCM/NBS inexistente ou não vinculado ao cClassTrib na tabela oficial — *Fase 3, D-307* | DETERMINISTICA |
| R029 | grupo/campo obrigatório para o cClassTrib ausente ou inconsistente (apontado pelo motor) — *Fase 3, D-307* | DETERMINISTICA |
| I001–I006 | cobertura: grupo não suportado, item não implementado no motor, IS divergente, CRT fora do escopo, item sem IBSCBS, catálogo sem alíquota (deixam o documento INDETERMINADO) | INFORMATIVA |
| R050 | MEI (CRT 4) com grupo IBSCBS informado | DETERMINISTICA |
| R021 | cClassTrib de redução, isenção ou diferimento com requisitos não verificáveis pelo XML | INDICIO |
| R023 | NCM/NBS atípico para o cClassTrib declarado — *substituída pela R025 (tabela oficial do motor, determinística; D-506)* | — |
| R024 | Alíquota efetiva do Simples declarada fora da faixa do anexo — *inativa até a tabela de faixas ser carregada como parâmetro (D-506)* | INDICIO |
| R040 | Crédito calculado e `credito_status` conforme a regra vigente (abaixo) | INFORMATIVA |

* **Contexto opcional:** o cliente pode enviar `contexto` (ex.: destinação do bem, perfil do adquirente). Regras `INDICIO` que dependem desse contexto passam a ser avaliadas de forma determinística quando ele está presente.
* **Crédito (LC 214 arts. 47 e 48):**
  * Enquanto o split payment e o recolhimento pelo adquirente não estiverem implantados, a prova de extinção do débito é dispensada (art. 48). O crédito depende só do destaque correto em documento idôneo: `credito_status = APROPRIAVEL_POR_DESTAQUE`, desde que não haja violação determinística.
  * Quando a implantação ocorrer (data não confirmada; a NT 2026.006 já define o vínculo NF-e ↔ split payment), o status padrão passa a `PENDENTE_EXTINCAO`. A integração com split payment ou apuração promove o status a `CONFIRMADO`.
  * A data de corte fica num parâmetro (`credit_rules`), não no código.
* **Fornecedor do Simples:** o crédito equivale ao IBS/CBS pago no DAS, pela alíquota informada no documento (LC 214 art. 47 §9º; LC 123 art. 23 §§1º-A e 2º).
* **Arredondamento:**
  * Os motores usam `HALF_EVEN`. É convenção do projeto (ADR003 do repositório), sem base legal.
  * Valores monetários com 2 casas e alíquotas com 4; o `vBC` é arredondado antes de multiplicar.
  * O TaxGateway usa o resultado do motor e nunca arredonda de novo. A tolerância de ±0,01 da R011 absorve a diferença de emissores que arredondam com `HALF_UP`.

### 8.4. Resiliência (Polly v8)
* **Timeout:** 1,5 s (síncrono) / 5 s (validação XML e lote).
* **Retry:** 2 tentativas com jitter, só em erros transitórios (`HttpRequestException`, 502, 503, 504). O cálculo é idempotente, então o retry é seguro.
* **Circuit breaker:** abre por 15 s com mais de 30% de falha em janela mínima de 20 chamadas, com alerta.
* **Bulkhead:** pools e `HttpClient`s separados para Sync e Batch. O Batch também tem **réplicas dedicadas** dos sidecars (Deployments `rtc-*-batch`), para que o lote nunca dispute CPU com o checkout em tempo real.

### 8.5. Catálogo de Alíquotas (obrigatório desde 2027)
A partir de 01/01/2027 o motor regular **exige** `aliquotasNominais` por item (CBS, IBS estadual, IBS municipal e, quando houver, IS) e **proíbe** esse campo antes dessa data. O motor aplica as reduções e regras do cClassTrib, mas não escolhe a alíquota nominal.

O `RateCatalog` resolve a alíquota por (tributo, ente federativo, data), nesta ordem:
1. **Fonte curada (primária):** tabela `rate_catalog`, mantida pelo time fiscal. Cada linha tem ato legal (resolução do Senado, lei estadual ou municipal), vigência e aprovador. Mudanças passam pelo mesmo fluxo de aprovação do §2.3.
   * Confirmado no código: a base 1.7.2 **não tem** alíquotas de CBS/IBS com vigência após 31/12/2026 (a migration V0053 apagou essas vigências e a V0015 removeu as alíquotas padrão fictícias). Por isso `/dados-abertos` não serve como fonte para 2027 enquanto não houver lei e um bundle novo.
2. **Conferência:** quando um bundle trouxer alíquotas (`/aliquota-uniao`, `/aliquota-uf`, `/aliquota-municipio`), o pipeline do §2.3 compara essas alíquotas com o catálogo e aponta as diferenças no PR.
3. **Cliente:** `aliquotas_nominais` no request (só em simulação).

* Quando recebe `aliquotasNominais`, o motor marca o item como `CALCULO_SIMULADO`. A API devolve isso como aviso, informando que a alíquota veio do catálogo TaxGateway, e não como erro.

* Toda resposta e toda auditoria gravam `fonte_aliquota` e o id da linha do catálogo.
* **Fallback legal da CBS:** o Senado tem até 15/12/2026 para fixar a alíquota de referência (LC 214 art. 349, prazo prorrogado pelo art. 353 §2º). Se não fixar até 22/12, valem as alíquotas calculadas pelo TCU (art. 349 §2º).
* Antes de 2027 o campo não é enviado, porque o motor o rejeita.

### 8.6. Cobertura Nacional (União, 27 UFs e 5.570 Municípios)

**Cadastro de entes**
* A base do motor traz as 27 UFs e os **5.570 municípios** (tabela `MUNICIPIO`, com vigências do IBGE desde a V0069), além do código de "município exterior".
* O gateway espelha essa lista a partir de `/dados-abertos/ufs` e `/ufs/municipios` a cada bundle, e valida na borda o código IBGE de 7 dígitos e a UF compatível (`422 ERR_MUNICIPIO_INVALIDO`), antes de chamar o motor.

**Modelo de alíquota por ente** (o mesmo modelo do motor, para não divergir)
* Uma **alíquota de referência** por tributo e vigência (CBS da União; IBS UF; IBS municipal), equivalente à tabela `ALIQUOTA_REFERENCIA` do motor.
* Uma **alíquota própria do ente**, por UF ou município, com `forma_aplicacao` = `SUBSTITUICAO` | `ACRESCIMO` | `DECRESCIMO` sobre a referência, equivalente à tabela `ALIQUOTA_PADRAO`.
* Resolução: `aliquota(ente, data) = referência(tributo, data)` ajustada pela linha própria do ente vigente na data. Sem linha própria, vale a referência.
* Com isso, a transição 2029–2033 (referências que mudam ano a ano) e as leis de cada ente são apenas **dados com vigência**, sem código novo.
* Carga inicial e manutenção: um pipeline de ingestão de resoluções do Senado e de publicações do Comitê Gestor do IBS e dos entes, com diff, aprovação de duas pessoas e vigência futura. A fonte oficial consolidada das alíquotas dos entes ainda precisa ser confirmada (§14).

**Local de incidência (princípio do destino)**
* O IBS é do UF e do município do **destino** da operação. O mapeador determina, por item, o município de incidência a partir dos campos do leiaute. Para NFS-e, usa os endpoints do próprio motor (`/calculadora/nfse/locais-incidencia`, `/locais-fornecimento`).
* O input do motor tem **um** `municipio` por operação. Itens com locais de incidência diferentes são agrupados, com uma chamada por grupo, e os resultados são recompostos na ordem original. Confirmar na Fase 0 se existe override por item.

**Datas e fusos**
* O Brasil tem quatro fusos. A data do fato gerador é a data **local** do `dhEmi`, com o offset do próprio documento; nunca converter para UTC antes de extrair a data, porque isso muda a data em emissões perto da meia-noite.
* A competência de cobrança usa `America/Sao_Paulo`.

**Casos nacionais que o motor já cobre** (via cClassTrib e as tabelas da base): regimes específicos (combustíveis monofásicos, serviços financeiros, imóveis), redutor de compras governamentais, Zona Franca de Manaus e áreas de livre comércio, operações com o exterior.

**Eventos e ciclo de vida**
* Cancelamento (evento 110111) marca o documento como cancelado, estorna o crédito potencial e dispara o webhook `document.cancelled`.
* Devolução (`finNFe = 4`) e notas de crédito/débito da reforma são auditadas vinculadas ao documento de origem.

**Outros documentos fiscais:** a base do motor já parametriza CT-e, BP-e, NFCom (V0051) e DIR/DUIMP (V0048). Os mapeadores desses documentos entram depois da Fase 5.

**ICMS/ISS na transição (2029–2032):** os motores da RFB tratam só de CBS, IBS e IS. Calcular ICMS/ISS (com ST e DIFAL por UF) é outro produto. Decisão em aberto (§14); a arquitetura deixa um ponto de extensão `ITaxModule` para isso.

---

## 9. Modelagem de Dados (PostgreSQL)

> **Desde a Fase 1, a fonte de verdade são as migrations** em `src/TaxGateway.Infrastructure/Persistence/Migrations/` (D-101). O SQL abaixo é a visão de desenho. Diferenças já aplicadas nas migrations:
> * `billing_month` das tabelas particionadas é `DATE` + CHECK, porque o pg_partman não aceita domínio (D-106).
> * `param_snapshot_id` em `fiscal_documents`/`simulations`, FK no `outbox` e CHECKs extras (D-108).
> * Políticas para `tg_relay`/`tg_auth` e a função `app.default_partition_rows()` (D-103, D-106).

```sql
CREATE EXTENSION IF NOT EXISTS pgcrypto;
CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE SCHEMA IF NOT EXISTS partman;
CREATE EXTENSION IF NOT EXISTS pg_partman SCHEMA partman;   -- o schema não pode ser trocado depois
CREATE SCHEMA app;

-- Contexto de tenant seguro ('' ou ausente => NULL => zero linhas)
CREATE FUNCTION app.current_tenant() RETURNS uuid LANGUAGE sql STABLE AS
$$ SELECT NULLIF(current_setting('app.current_tenant_id', true), '')::uuid $$;

-- Domínios
CREATE DOMAIN cnpj_t AS CHAR(14) CHECK (VALUE ~ '^[A-Z0-9]{12}[0-9]{2}$');
CREATE DOMAIN access_key_t AS CHAR(44) CHECK (VALUE ~ '^[0-9]{6}[A-Z0-9]{12}[0-9]{26}$');
CREATE DOMAIN month_t AS DATE CHECK (EXTRACT(DAY FROM VALUE) = 1);

-- Planos
CREATE TABLE plans (
    code               TEXT PRIMARY KEY,           -- STARTER, GROWTH, SCALE, OEM, ENTERPRISE
    rpm                INT NOT NULL,
    burst              INT NOT NULL,
    concurrency        INT NOT NULL,
    batch_max_docs     INT NOT NULL,
    batch_max_bytes    BIGINT NOT NULL,
    queue_priority     TEXT NOT NULL,
    included_docs      BIGINT NOT NULL,
    included_cnpjs     INT,
    price_cents        BIGINT NOT NULL
);

CREATE TABLE tenants (
    id                    UUID PRIMARY KEY,         -- UUIDv7 gerado na aplicação
    name                  TEXT NOT NULL,
    root_cnpj             cnpj_t NOT NULL UNIQUE,
    plan_code             TEXT NOT NULL REFERENCES plans(code),
    limits_override       JSONB NOT NULL DEFAULT '{}',
    auto_provision_cnpj   BOOLEAN NOT NULL DEFAULT FALSE,
    xml_retention_days    INT,                      -- NULL = zero_retention
    kms_key_ref           TEXT NOT NULL,            -- KEK do tenant (crypto-shredding)
    billing_provider      TEXT NOT NULL DEFAULT 'ASAAS',
    billing_customer_ref  TEXT,
    status                TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','SUSPENDED','CLOSED')),
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE oauth_applications (  -- gerenciada pelo OpenIddict; vínculo com o tenant
    client_id   TEXT PRIMARY KEY,
    tenant_id   UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE
);

CREATE TABLE api_keys (
    id          UUID PRIMARY KEY,
    tenant_id   UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    name        TEXT NOT NULL,
    key_prefix  TEXT NOT NULL,
    key_hash    BYTEA NOT NULL UNIQUE,              -- HMAC-SHA256(pepper_kms, key)
    scopes      TEXT[] NOT NULL,
    expires_at  TIMESTAMPTZ,
    revoked_at  TIMESTAMPTZ,
    last_used_at TIMESTAMPTZ,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE tenant_cnpjs (
    tenant_id     UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    cnpj          cnpj_t NOT NULL,
    regime_hint   TEXT CHECK (regime_hint IN ('REGIME_REGULAR','SIMPLES_NACIONAL')),
    is_active     BOOLEAN NOT NULL DEFAULT TRUE,
    deactivated_from month_t,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, cnpj)
);

-- Deduplicação GLOBAL (não particionada): resolve a idempotência entre meses
CREATE TABLE document_registry (
    tenant_id          UUID NOT NULL REFERENCES tenants(id),
    idempotency_hash   BYTEA NOT NULL,              -- SHA256(kind || ':' || access_key)
    kind               TEXT NOT NULL CHECK (kind IN ('OUTBOUND','INBOUND')),
    access_key         access_key_t NOT NULL,
    first_document_id  UUID NOT NULL,
    first_billing_month month_t NOT NULL,
    latest_document_id UUID NOT NULL,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, idempotency_hash)
);

-- Idempotency-Key HTTP (24h)
CREATE TABLE idempotency_requests (
    tenant_id     UUID NOT NULL REFERENCES tenants(id),
    key           TEXT NOT NULL,
    request_hash  BYTEA NOT NULL,
    status_code   INT,
    response_body BYTEA,                           -- comprimido
    expires_at    TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (tenant_id, key)
);

-- Auditorias (particionada por mês, RANGE)
CREATE TABLE fiscal_documents (
    id                  UUID NOT NULL,              -- UUIDv7
    billing_month       month_t NOT NULL,
    tenant_id           UUID NOT NULL REFERENCES tenants(id),
    batch_id            UUID,
    kind                TEXT NOT NULL CHECK (kind IN ('OUTBOUND','INBOUND')),
    model               TEXT NOT NULL CHECK (model IN ('NFE55','NFCE65','NFSE')),
    access_key          access_key_t,
    cnpj_subject        cnpj_t NOT NULL,
    cnpj_counterparty   cnpj_t,
    is_consumidor_final BOOLEAN NOT NULL DEFAULT FALSE,
    cpf_hmac            BYTEA,
    engine              TEXT NOT NULL,
    engine_bundle       TEXT NOT NULL,
    rules_version       TEXT NOT NULL,
    audit_status        TEXT NOT NULL CHECK (audit_status IN
                          ('CONFORME','DIVERGENTE','INDICIO','SCHEMA_ERROR','ASSINATURA_INVALIDA','INDETERMINADO')),
    declared            JSONB NOT NULL,             -- {cbs, ibs_uf, ibs_mun, is} por documento
    recalculated        JSONB NOT NULL,
    difference_total    NUMERIC(15,2) NOT NULL DEFAULT 0,
    credit_potential    NUMERIC(15,2) NOT NULL DEFAULT 0,
    credit_status       TEXT,
    findings            JSONB NOT NULL DEFAULT '[]',
    xml_storage_key     TEXT,                       -- NULL em zero_retention
    xml_sha256          BYTEA NOT NULL,
    is_billable         BOOLEAN NOT NULL,
    reprocess_of        UUID,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (billing_month, id)
) PARTITION BY RANGE (billing_month);
CREATE INDEX ON fiscal_documents (tenant_id, billing_month, created_at DESC);
CREATE INDEX ON fiscal_documents (tenant_id, access_key);
CREATE INDEX ON fiscal_documents (tenant_id, batch_id) WHERE batch_id IS NOT NULL;

-- Simulações (particionada, retenção de 13 meses)
CREATE TABLE simulations (
    id             UUID NOT NULL,
    billing_month  month_t NOT NULL,
    tenant_id      UUID NOT NULL REFERENCES tenants(id),
    cnpj_subject   cnpj_t NOT NULL,
    engine         TEXT NOT NULL,
    engine_bundle  TEXT NOT NULL,
    request        JSONB NOT NULL,
    result         JSONB NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (billing_month, id)
) PARTITION BY RANGE (billing_month);

-- Lotes
CREATE TABLE batches (
    id              UUID PRIMARY KEY,
    tenant_id       UUID NOT NULL REFERENCES tenants(id),
    status          TEXT NOT NULL CHECK (status IN ('QUEUED','RUNNING','COMPLETED','FAILED','REJECTED')),
    storage_key     TEXT NOT NULL,
    total_docs      INT, processed_docs INT NOT NULL DEFAULT 0, failed_docs INT NOT NULL DEFAULT 0,
    report_key      TEXT,
    rejection_code  TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at    TIMESTAMPTZ
);

-- Ledger de uso (fonte única de verdade do billing; append-only)
CREATE TABLE usage_ledger (
    id             BIGINT GENERATED ALWAYS AS IDENTITY,
    billing_month  month_t NOT NULL,
    tenant_id      UUID NOT NULL REFERENCES tenants(id),
    metric         TEXT NOT NULL CHECK (metric IN ('DOC_AUDIT','SIMULATION')),
    cnpj_subject   cnpj_t NOT NULL,
    source_id      UUID NOT NULL,                  -- fiscal_documents.id / simulations.id
    quantity       INT NOT NULL DEFAULT 1,
    occurred_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (billing_month, id),
    UNIQUE (billing_month, metric, source_id)
) PARTITION BY RANGE (billing_month);

-- CNPJs ativos por competência (cobrança OEM)
CREATE TABLE cnpj_monthly_activity (
    tenant_id      UUID NOT NULL REFERENCES tenants(id),
    billing_month  month_t NOT NULL,
    cnpj           cnpj_t NOT NULL,
    first_seen_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, billing_month, cnpj)
);

-- Fechamento
CREATE TABLE billing_periods (
    tenant_id       UUID NOT NULL REFERENCES tenants(id),
    billing_month   month_t NOT NULL,
    status          TEXT NOT NULL CHECK (status IN ('OPEN','CLOSING','CLOSED','INVOICED')),
    docs_count      BIGINT, simulations_count BIGINT, active_cnpjs INT,
    amount_cents    BIGINT,
    provider_ref    TEXT,
    provider_idempotency_key TEXT UNIQUE,          -- tenant:month:v{n}
    closed_at       TIMESTAMPTZ,
    PRIMARY KEY (tenant_id, billing_month)
);

-- Webhooks
CREATE TABLE tenant_webhooks (
    id                 UUID PRIMARY KEY,
    tenant_id          UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    target_url         TEXT NOT NULL CHECK (target_url LIKE 'https://%'),
    secrets_ciphertext BYTEA NOT NULL,             -- envelope: até 2 segredos ativos
    dek_wrapped        BYTEA NOT NULL,
    subscribed_events  TEXT[] NOT NULL,
    is_active          BOOLEAN NOT NULL DEFAULT TRUE,
    disabled_reason    TEXT,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE outbox (
    id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id    UUID NOT NULL,
    topic        TEXT NOT NULL,
    payload      JSONB NOT NULL,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    published_at TIMESTAMPTZ
);

CREATE TABLE webhook_deliveries (
    id            UUID PRIMARY KEY,
    tenant_id     UUID NOT NULL REFERENCES tenants(id),
    webhook_id    UUID NOT NULL REFERENCES tenant_webhooks(id) ON DELETE CASCADE,
    event         TEXT NOT NULL,
    payload       JSONB NOT NULL,
    status        TEXT NOT NULL CHECK (status IN ('PENDING','DELIVERED','RETRYING','DEAD')),
    attempts      INT NOT NULL DEFAULT 0,
    next_attempt_at TIMESTAMPTZ,
    last_status_code INT,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- DF-e / Certificados (Fase 9)
CREATE TABLE certificates (
    id             UUID PRIMARY KEY,
    tenant_id      UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    cnpj           cnpj_t NOT NULL,
    pfx_ciphertext BYTEA NOT NULL,
    password_ciphertext BYTEA NOT NULL,
    dek_wrapped    BYTEA NOT NULL,
    thumbprint     TEXT NOT NULL,
    not_after      TIMESTAMPTZ NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE dfe_sync_state (
    tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    cnpj            cnpj_t NOT NULL,
    ult_nsu         BIGINT NOT NULL DEFAULT 0,
    max_nsu         BIGINT,
    next_allowed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_cstat      TEXT,
    PRIMARY KEY (tenant_id, cnpj)
);

-- Versões dos motores implantadas
CREATE TABLE engine_bundles (
    bundle        TEXT PRIMARY KEY,
    manifest      JSONB NOT NULL,
    db_sha256     TEXT NOT NULL,
    approved_by   TEXT NOT NULL,
    deployed_at   TIMESTAMPTZ,
    retired_at    TIMESTAMPTZ
);

-- Roteamento de motor por CRT + tpRegimeIBSCBS (§8.1)
CREATE TABLE engine_routing (
    crt               TEXT NOT NULL,
    tp_regime_ibscbs  TEXT NOT NULL DEFAULT '-',           -- '-' = ausente
    engines           TEXT[] NOT NULL DEFAULT '{}',        -- {} = INDETERMINADO / não se aplica
    simples_mode      TEXT,                                -- SN_PLENO, SN_SUBLIMITE, SOMENTE_RECEITA
    note              TEXT,
    PRIMARY KEY (crt, tp_regime_ibscbs)
);

-- Catálogo de alíquotas nominais (§8.5)
CREATE TABLE rate_catalog (
    id               UUID PRIMARY KEY,
    tributo          TEXT NOT NULL CHECK (tributo IN ('CBS','IBS_UF','IBS_MUN','IS')),
    tipo             TEXT NOT NULL CHECK (tipo IN ('REFERENCIA','PROPRIA_ENTE')),
    esfera           TEXT NOT NULL CHECK (esfera IN ('UNIAO','UF','MUNICIPIO')),
    ente_codigo      TEXT,                                 -- código IBGE da UF (2) ou do município (7); NULL para referência/União
    forma_aplicacao  TEXT CHECK (forma_aplicacao IN ('SUBSTITUICAO','ACRESCIMO','DECRESCIMO')),  -- só PROPRIA_ENTE
    aliquota         NUMERIC(9,4) NOT NULL,
    valid_from       DATE NOT NULL,
    valid_to         DATE,
    ato_legal        TEXT NOT NULL,
    fonte            TEXT NOT NULL CHECK (fonte IN ('SENADO','TCU','CG_IBS','LEI_ENTE','BUNDLE_RFB')),
    change_id        UUID NOT NULL,                        -- param_changes (quem propôs/aprovou)
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK ((tipo = 'REFERENCIA') = (ente_codigo IS NULL)),
    EXCLUDE USING gist (tributo WITH =, tipo WITH =, COALESCE(ente_codigo,'') WITH =,
                        daterange(valid_from, valid_to) WITH &&)   -- sem vigências sobrepostas (btree_gist)
);

-- Trilha de toda mudança de parâmetro (§15): nada é editado sem registro e aprovação
CREATE TABLE param_changes (
    id            UUID PRIMARY KEY,
    domain        TEXT NOT NULL,                           -- rate_catalog, engine_routing, plans, audit_rules...
    scope         TEXT NOT NULL,                           -- GLOBAL | PLAN:<code> | TENANT:<id> | CNPJ:<cnpj>
    payload       JSONB NOT NULL,                          -- antes/depois
    justification TEXT NOT NULL,
    proposed_by   TEXT NOT NULL,
    approved_by   TEXT,                                    -- obrigatório e diferente de proposed_by nos domínios fiscais
    status        TEXT NOT NULL CHECK (status IN ('PROPOSED','APPROVED','REJECTED','APPLIED')),
    effective_at  TIMESTAMPTZ NOT NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK (approved_by IS NULL OR approved_by <> proposed_by)
);

-- Snapshot imutável de todos os parâmetros vigentes; o id é gravado em cada cálculo/auditoria
CREATE TABLE param_snapshots (
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    content_sha256 TEXT NOT NULL UNIQUE,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Configuração de regras de auditoria por tenant (dentro dos limites do §15)
CREATE TABLE tenant_audit_rule_settings (
    tenant_id   UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    rule_id     TEXT NOT NULL,
    enabled     BOOLEAN NOT NULL DEFAULT TRUE,
    severity    TEXT CHECK (severity IN ('INFO','WARNING','ERROR')),
    params      JSONB NOT NULL DEFAULT '{}',
    PRIMARY KEY (tenant_id, rule_id)
);

-- Parâmetros da regra de crédito (§8.3)
CREATE TABLE credit_rules (
    valid_from     DATE PRIMARY KEY,
    default_status TEXT NOT NULL CHECK (default_status IN ('APROPRIAVEL_POR_DESTAQUE','PENDENTE_EXTINCAO')),
    ato_legal      TEXT NOT NULL
);

-- ===== Partições (pg_partman, RANGE mensal, premake 4, com DEFAULT) =====
SELECT partman.create_parent('public.fiscal_documents', 'billing_month', '1 month', p_premake := 4, p_default_table := true);
SELECT partman.create_parent('public.simulations',      'billing_month', '1 month', p_premake := 4, p_default_table := true);
SELECT partman.create_parent('public.usage_ledger',     'billing_month', '1 month', p_premake := 4, p_default_table := true);
-- Partições novas não herdam GRANTs (as políticas RLS da mãe não valem em acesso direto à partição):
UPDATE partman.part_config SET inherit_privileges = false;
-- Alerta: qualquer linha nas tabelas *_default gera incidente (sinal de que a manutenção do partman falhou).
-- Retenção de simulations: 13 meses (partman retention + DROP de partição).

-- ===== RLS (ENABLE + FORCE em todas as tabelas com tenant_id) =====
DO $$
DECLARE t TEXT;
BEGIN
  FOREACH t IN ARRAY ARRAY['tenant_cnpjs','document_registry','idempotency_requests','fiscal_documents',
    'simulations','batches','usage_ledger','cnpj_monthly_activity','billing_periods','tenant_webhooks',
    'outbox','webhook_deliveries','certificates','dfe_sync_state','api_keys','oauth_applications',
    'tenant_audit_rule_settings']
  LOOP
    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON %I TO tg_app, tg_worker
                    USING (tenant_id = app.current_tenant())
                    WITH CHECK (tenant_id = app.current_tenant())', t);
  END LOOP;
END $$;

ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenants FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_self ON tenants TO tg_app, tg_worker USING (id = app.current_tenant());

-- Billing: leitura cross-tenant restrita às tabelas de uso
CREATE POLICY billing_read ON usage_ledger          TO tg_billing USING (true);
CREATE POLICY billing_read ON cnpj_monthly_activity TO tg_billing USING (true);
CREATE POLICY billing_rw   ON billing_periods       TO tg_billing USING (true) WITH CHECK (true);
CREATE POLICY billing_read ON tenants               TO tg_billing USING (true);

-- Autenticação por API Key (antes de existir contexto de tenant)
CREATE FUNCTION app.resolve_api_key(p_hash BYTEA)
RETURNS TABLE (tenant_id UUID, key_id UUID, scopes TEXT[], plan_code TEXT, tenant_status TEXT)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public AS $$
  SELECT k.tenant_id, k.id, k.scopes, t.plan_code, t.status
  FROM api_keys k JOIN tenants t ON t.id = k.tenant_id
  WHERE k.key_hash = p_hash AND k.revoked_at IS NULL
    AND (k.expires_at IS NULL OR k.expires_at > now())
$$;
ALTER FUNCTION app.resolve_api_key(BYTEA) OWNER TO tg_auth;   -- tg_auth: sem BYPASSRLS; política auth_lookup FOR SELECT em api_keys/tenants (D-103)
REVOKE ALL ON FUNCTION app.resolve_api_key(BYTEA) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.resolve_api_key(BYTEA) TO tg_app;
```

---

## 10. Metering, Billing & Fechamento

### 10.1. Regras de Cobrança

| Evento | Cobra? |
|:--|:--|
| Primeira auditoria de uma chave de acesso (`kind` + `access_key`), em qualquer mês | Sim (`DOC_AUDIT`) |
| Reenvio da mesma chave (`replay`) | Não |
| `?reprocess=true` (nova versão do motor ou das regras) | Não |
| `SCHEMA_ERROR` / `ASSINATURA_INVALIDA` | Sim (o processamento ocorreu). Configurável por plano |
| `INDETERMINADO` por limitação do motor | Não |
| Simulação | Sim (`SIMULATION`), exceto replay do mesmo `Idempotency-Key` |
| CNPJ ativo (OEM) | Sim: CNPJs distintos em `cnpj_monthly_activity` na competência |

* **Competência** = mês de **recebimento** (`America/Sao_Paulo`), não a data de emissão do documento. Um lote que atravessa a virada do mês é cobrado por documento, na competência do recebimento de cada um.

### 10.2. Gravação Atômica (uma transação, sem Redis)
```sql
BEGIN;
SELECT set_config('app.current_tenant_id', :tenant, true);

INSERT INTO document_registry (tenant_id, idempotency_hash, kind, access_key,
       first_document_id, first_billing_month, latest_document_id)
VALUES (:tenant, :hash, :kind, :key, :doc_id, :month, :doc_id)
ON CONFLICT (tenant_id, idempotency_hash) DO NOTHING
RETURNING first_document_id;
-- Sem linha retornada => replay: devolve a auditoria existente (sem nova cobrança). Fim.

INSERT INTO fiscal_documents (...) VALUES (...);
INSERT INTO usage_ledger (billing_month, tenant_id, metric, cnpj_subject, source_id)
VALUES (:month, :tenant, 'DOC_AUDIT', :cnpj, :doc_id);
INSERT INTO cnpj_monthly_activity (tenant_id, billing_month, cnpj)
VALUES (:tenant, :month, :cnpj) ON CONFLICT DO NOTHING;
INSERT INTO outbox (tenant_id, topic, payload) VALUES (...);   -- se houver evento
COMMIT;
```
* O ledger é append-only (sem `UPDATE` de contador quente, sem contenção em tenants grandes).
* O consumo exibido em `GET /v1/usage` vem de um `COUNT` no ledger da partição corrente, com cache de 60 s.

### 10.3. Fechamento de Ciclo
1. No dia 1, às 03:00, o `BillingCloseWorker` (role `tg_billing`) muda as competências anteriores de `OPEN` para `CLOSING`.
2. Agrega o ledger e a atividade, aplica o plano (franquia, excedente, CNPJs ativos) e grava `billing_periods`.
3. Envia a cobrança ao provedor com `provider_idempotency_key = tenant:month:v1`. Um reenvio nunca gera cobrança duplicada.
4. Muda o status para `INVOICED`. Ajustes posteriores geram `v2` (nota de crédito ou débito), nunca uma edição da `v1`.

### 10.4. Provedores (`IBillingProvider`)
* **Stripe (padrão desde a v2.16, D-C01 a D-C09):** cliente, assinatura com a mensalidade do plano e o excedente medido por *Billing Meters* (documentos e CNPJs ativos), Checkout para assinar, portal do cliente para cartão e faturas, webhook assinado (eventos gravados em `stripe_events` e processados pelos workers) e cobrança avulsa idempotente no fechamento. A API de usage records foi removida na versão `2025-03-31.basil`.
* **Asaas:** substituído pela Stripe por decisão de produto (D-C01); a abstração `IBillingProvider` continua, se um provedor local voltar a ser necessário (PIX/boleto).
  * O worker envia *meter events* incrementais a partir de um *watermark* do ledger, com `identifier` único por lote de eventos.
  * O Stripe só garante a unicidade do `identifier` numa janela de pelo menos 24 h. A deduplicação definitiva é o watermark gravado no Postgres.
* O adapter é escolhido por `tenants.billing_provider`.

---

## 11. Estrutura de Solução (.NET 10)

```text
/taxgateway
├── docker-compose.yml               # Dev local: rtc-regular, rtc-simples, postgres, valkey, s3 (SeaweedFS, D-007), rabbitmq, seq
├── /.github/workflows/ci.yml        # CI: build+testes+SBOM, imagens dos motores, testes oficiais, golden (D-009)
├── SDD.md
├── Directory.Packages.props        # versões centralizadas (D-111)
├── /docs/decisoes.md                # registro de decisões de implementação
├── /infra
│   ├── /rfb                         # versions.json, Dockerfile (runtime + test-reports), build.ps1/build.sh, testes oficiais e exceções (D-006)
│   ├── /s3                          # credenciais do S3 local (dev)
│   ├── /postgres                    # imagem postgres:18 + pg_partman e bootstrap de roles/extensões (D-102, D-104)
│   ├── /xsd-schemas                 # XSDs oficiais versionados por NT (NF-e PL_010h, D-301)
│   ├── /icp-brasil                  # raízes ICP-Brasil v5, v10–v13 + ACs intermediárias (D-302)
│   ├── /k8s                         # Helm (D-405): Rollout canário da API + sidecar, motores de lote, NetworkPolicies, HPA, Job do migrator
├── /src                             # src/Dockerfile: imagens api/workers/migrator (chiseled, D-404)
│   ├── TaxGateway.Api               # Minimal APIs, auth dual, rate limit, idempotência, ProblemDetails
│   ├── TaxGateway.Workers           # Batch, Outbox, Webhooks, Billing, DF-e, manutenção
│   ├── TaxGateway.Core              # Domínio, mapeadores XML, catálogo de regras, LGPD, cálculo de cobrança
│   ├── TaxGateway.Infrastructure    # Npgsql (TenantDb + migrations SQL embutidas), Redis Lua, S3, KMS, adapters RFB (Polly), RabbitMQ
│   ├── TaxGateway.Migrator          # aplica as migrations como tg_owner (Job pré-deploy)
│   ├── TaxGateway.Identity          # OpenIddict
│   └── TaxGateway.Contracts         # DTOs v1, OpenAPI
└── /tests
    ├── TaxGateway.UnitTests         # Regras, mapeadores, CNPJ alfanumérico, anonimização, cobrança
    ├── TaxGateway.SecurityTests     # RLS cross-tenant, XXE, signature wrapping, zip bomb, SSRF
    ├── TaxGateway.ContractTests     # Testcontainers contra as imagens rtc-* do bundle
    ├── TaxGateway.GoldenTests       # API em processo + motor real contra o corpus (tests/golden/corpus/v0, D-008)
    └── /load                        # k6
```

> **Mensageria:** `RabbitMQ.Client` 7.x direto ou Wolverine. **Não usar MassTransit**: a v9 é comercial e a v8 (Apache 2.0) só tem correções garantidas até o fim de 2026. Prioridade de filas: uma fila por nível, porque as quorum queues do RabbitMQ 4 só têm 2 níveis de prioridade.
> **Object storage:** o MinIO community não é mais mantido (sem binários desde out/2025, repositório sem manutenção). No ambiente local: **SeaweedFS** (Apache 2.0; escolhido na Fase 0, D-007). Em produção, S3 gerenciado com SSE-KMS.
> **Cache:** Valkey (BSD). Redis 8 só depois de avaliar o licenciamento (RSAL/SSPL/AGPL).

---

## 12. Roadmap Segmentado

Cada fase termina com **build verde, testes passando e o critério de saída atendido**. Uma fase só começa depois da anterior, exceto quando marcada como "paralela".

### Marco A — Alpha Fechado (design partners)

**Fase 0: Fundações** — *concluída em 25/11/2026 (D-001 a D-010)*
* Solução .NET 10, CI (build, testes, análise estática, SBOM), `docker-compose`.
* Build das imagens `rtc-regular` e `rtc-simples` a partir de `versions.json` (§2.2), com a verificação de `db_sha256`.
* Job de CI que roda os testes oficiais de cada motor contra o banco gerado. Falhas conhecidas (ex.: issue #6 do regime regular, testes de IS) ficam numa lista de exceções versionada.
* Verificar se `/dados-abertos` traz as alíquotas de 2027 (define a fonte primária do §8.5) e se os motores sobem com a URL de versão sobrescrita.
* Escolher o object storage S3-compatível do ambiente local.
* Captura do **corpus golden v0**: cerca de 300 casos reais e anonimizados de simulação e XML, com as respostas atuais dos motores gravadas como snapshot.
* *Saída:* `docker compose up` sobe tudo; os dois motores respondem `/versao`; o CI publica as imagens com as labels de versão.
* *Resultado:*
  * testes oficiais: regular com 1.314 testes e 0 falhas; Simples com 376 e 0 falhas; lista de exceções vazia;
  * S3 local com SeaweedFS;
  * corpus golden v0 sintético (302 casos);
  * workflow de CI validado no actionlint;
  * label `rtc.db_sha256` com checagem de banco único;
  * SBOM CycloneDX.
  * Pendente: casos reais anonimizados no corpus, que dependem de design partners.

**Fase 1: Dados & Tenancy** — *concluída em 25/11/2026 (D-101 a D-116)*
* Roles, schema do §9, pg_partman, `FORCE RLS`, interceptor de transação e tenant, `app.resolve_api_key`.
* *Saída:* a suíte `SecurityTests/RLS` prova o isolamento em **todas** as tabelas; um INSERT numa partição inexistente vai para a DEFAULT e dispara alerta.
* *Resultado:* 159 testes verdes (matriz tabela × role × leitura/inserção/atualização/remoção, partições sem GRANT, roles sem bypass, billing/relay restritos, ledger append-only, DEFAULT reportada, contexto sem vazamento no pool, migrator idempotente e com checksum). A suíte foi validada por teste de mutação.

**Fase 2: API Key, Rate Limit & Simulação (Regime Regular)**
* Autenticação por API Key, token bucket e leases (Lua), headers IETF, ProblemDetails, `Idempotency-Key`.
* Validação de CNPJ alfanumérico.
* Adapter `Regular` com Polly e bulkhead; `POST /v1/calculo/simular`; cache de dados abertos por bundle.
* `RateCatalog` (§8.5) carregado com as alíquotas de 2026–2028, pronto para 01/01/2027.
* *Saída:* simulação reproduz 100% do corpus golden; teste de concorrência prova que nenhum lease fica preso.
* *Status:* **concluída em 25/11/2026 (D-201 a D-220)**.
* *Resultado:*
  * golden: 302 casos (493 itens) reproduzidos 100%, validado por mutação;
  * leases: 400 requisições concorrentes nunca passam do limite e nenhum lease fica preso, mesmo com exceção ou queda;
  * 179 testes de segurança e 29 unitários verdes.
* *Também entregue:*
  * Admin API com fluxo de duas pessoas para o catálogo (antecipada da previsão do §15.1);
  * role `tg_admin`;
  * snapshot de parâmetros com recarga a quente.

**Fase 3: Pipeline XML & Auditoria OUTBOUND** — *concluída em 25/11/2026 (D-301 a D-314)*
* Endurecimento do XML, XSD, XMLDSig com anti-wrapping e ICP-Brasil.
* Mapeador NF-e/NFC-e; regras R001–R030 (determinísticas).
* S3 com envelope por tenant, modo `zero_retention`, anonimizador LGPD com chave no KMS.
* `document_registry` + ledger na mesma transação.
* *Saída:* `SecurityTests` (XXE, wrapping) verdes; corpus golden de XML com 0 falsos positivos determinísticos; replay entre meses não gera cobrança.
* *Resultado:*
  * 18 testes de XML: XSD oficial PL_010h, XXE/DTD/expansão de entidades, tamanho, adulteração, *wrapping* (validado por mutação), Reference/assinatura duplicadas, transform fora da lista, cadeia fora da ICP-Brasil, certificado de outra empresa ou vencido;
  * corpus XML de 144 documentos com 0 falsos positivos e 8 mutações detectadas pela regra certa;
  * replay 40 dias depois: 1 cobrança para 2 auditorias;
  * XML cifrado por tenant no S3 (SeaweedFS) e zero_retention; CPF só como HMAC.
  * Pendentes: revogação (LCR), KMS e lifecycle de produção (Fase 4); grupos de monofasia e créditos no mapeador (Fase 5).

### Marco B — Beta Pago

**Fase 4: Pipeline de Atualização Fiscal** *(paralela às Fases 5–6)* — *implementada em 25/11/2026 (D-401 a D-408); ensaio do canário pendente de cluster*
* Detecção de tags, PR automático, testes de contrato, diff de regressão, staging, aprovação manual, canário, retenção de imagens, job de reprocessamento.
* *Saída:* simular uma tag nova e percorrer o fluxo até o canário com rollback testado.
* *Resultado:*
  * tag nova simulada: o detector atualiza versions.json e o bundle;
  * contrato das imagens reais verde e com mutação detectada;
  * regressão de 302 casos com 0 diferenças entre bundles iguais;
  * imagens da aplicação (chiseled) com smoke ok;
  * Helm chart validado (lint + kubeconform);
  * workflows `fiscal-update` e `deploy` validados no actionlint;
  * KMS OpenBao e revogação por LCR testados;
  * reprocessamento testado.
* *Pendente:* o ensaio do canário com rollback precisa de um cluster de staging do projeto (os contextos kubectl disponíveis eram de outro projeto e não foram usados).

**Fase 5: Simples Nacional, Roteamento por CRT & INBOUND** — *implementada em 25/11/2026 (D-501 a D-507), exceto NFS-e nacional*
* Adapter `Simples` com capacidades declaradas; tabela `engine_routing` (validada no §14).
* Auditoria INBOUND: regras R021, R023 e R024 (indícios), R040 (crédito), R050 (MEI), campo `contexto` opcional.
* NFS-e nacional no mapeador.
* *Saída:* revisão das regras `INDICIO` por especialista tributário, com os Termos de Uso e o `aviso_legal` aprovados pelo jurídico.
* *Resultado:*
  * simulação e auditoria roteadas pela tabela `engine_routing` (pleno, sublimite, opção pelo regime regular, MEI);
  * golden de simulação com 347 casos (45 do Simples) e XML com 171 documentos (27 do Simples), sem falso positivo;
  * entrada com crédito (R040) e indício (R021) com contexto `confirmar`.
* *Pendente:* NFS-e nacional no mapeador; revisão do especialista e do jurídico (critério de saída humano); dados da R024.

**Fase 6: Lotes, Outbox & Webhooks + Gate de Capacidade** — *implementada em 25/11/2026 (D-601 a D-608)*
* Upload por stream e por URL pré-assinada, defesas de ZIP, fatiamento e fair-share, relatório do lote.
* Outbox → RabbitMQ (quorum), dispatcher de webhooks com SSRF guard, rotação de segredos, DLQ e endpoint de retry.
* **Gate de capacidade (k6):** vazão por réplica de cada motor, latência p95 síncrona com lote pesado em paralelo. Os limites do §5.1 são recalibrados com base nessa medição.
* *Saída:*
  * lote de 100 mil XMLs de um tenant não eleva o p95 síncrono de outro tenant acima do SLO;
  * zip bomb é rejeitada;
  * webhook para um IP privado é bloqueado.
* *Resultado:*
  * zip bomb, ZIP aninhado e entrada não-XML recusados;
  * webhook para IP privado/metadados recusado no cadastro e na conexão;
  * ponta a ponta auditoria → outbox → RabbitMQ → webhook assinado;
  * lote de 12 notas → relatório → `batch.audit.completed`;
  * fair-share testado no banco;
  * **gate local:** p95 síncrono de 15,0 ms → 18,8 ms com um lote de 3.000 NF-e de outro tenant (SLO 40 ms), 0% de falhas, ~48 docs/s por worker.
* *Pendente:* repetir o gate com 100 mil documentos no staging e recalibrar o §5.1.

**Fase 7: Metering & Billing** — *implementada em 25/11/2026 (D-701 a D-705)*
* Ledger, `cnpj_monthly_activity`, `GET /v1/usage`, fechamento de ciclo, provedor Asaas (e Stripe Meters, se necessário), notas de ajuste `v2`.
* *Saída:* reconciliação automática (ledger × `billing_periods` × provedor) com diferença zero num mês simulado com 1 milhão de eventos; um reenvio ao provedor não duplica a cobrança.
* *Resultado:*
  * **atendida** com 1.000.000 de eventos no Postgres real;
  * reconciliação zerada, valor conferido pela fórmula do plano;
  * reenvio sem duplicar; ajustes v2/v3;
  * `GET /v1/usage`.
* *Pendente:* teste com a conta Stripe real (modo de teste) — chaves, webhook e portal do cliente configurados (ver `docs/decisoes.md`, "Para ligar em produção").

### Marco C — GA (Disponibilidade Geral)

**Fase 8: OAuth/OpenIddict & OEM** — *implementada em 25/11/2026 (D-801 a D-806)*
* Client credentials, JWKS com rotação, blacklist (`jti`/cliente/tenant), escopos.
* Endpoints OEM, auto-provisionamento, uso por CNPJ.
* *Saída:* rotação de chave sem erro 401 durante a troca; suspensão de tenant é efetiva em menos de 5 s.
* *Resultado:*
  * **atendida** em teste de integração (`IdentityTests`) com duas instâncias da API sobre Postgres e Valkey reais;
  * tokens de cada chave aceitos nas duas instâncias durante a troca (zero 401);
  * suspensão efetiva na outra instância em ~15 ms, para token e API key.
* *Pendente:* ensaio da rotação num cluster real (junto com o canário da Fase 4); OIDC do backoffice no lugar da chave de operador; abatimento automático de créditos (previsto aqui e não feito). Backoffice web, portal do cliente, landing e referência da API **feitos depois, em 25/11/2026 (D-W01 a D-W07)**; o login de usuários humanos por OIDC continua pendente.

**Hardening para GA** *(transversal, encerra o Marco C)* — *parte técnica implementada em 25/11/2026 (D-H01 a D-H06)*
* Pentest externo, RIPD e DPA publicados, runbooks, backup/PITR com restore testado, DR documentado (RPO ≤ 5 min, RTO ≤ 1 h).
* *Resultado:*
  * ensaio de PITR **aprovado** localmente (restore em 3 s, parada exata no alvo): `infra/backup/pitr-drill.sh`;
  * runbooks em `docs/runbooks/`, um por alerta;
  * métricas da API e SLOs como alertas do Prometheus (`docs/slo.md`);
  * direitos do titular implementados (§4.3);
  * RIPD e DPA em **rascunho técnico** (`docs/lgpd/`).
* *Pendente (humano/externo):* pentest externo; revisão jurídica e publicação de RIPD/DPA; ensaio de PITR no Postgres gerenciado; observação dos SLOs em produção. *Pendente (engenharia):* redactor de CPF/CNPJ na telemetria; métricas dos workers.
* SLOs ativos:

* SLOs ativos: os do §16.3, recalibrados com o baseline medido na Fase 0.

### Marco D — Captura Automática

**Fase 9: DF-e & Certificados A1** — *implementada em 25/11/2026 (D-901 a D-907); saída real pendente*
* Custódia do A1 (envelope KMS), alertas de vencimento, Distribuição DF-e com controle de NSU e bloqueio de 1 h, Ciência da Operação, auditoria INBOUND automática, webhook `dfe.document.audited`.
* Depois: NFS-e nacional (ADN) e CT-e.
* *Saída:* 30 dias em homologação sem nenhum `cStat 656`.
* *Resultado local:* contra uma SEFAZ simulada com mTLS que responde 656 a qualquer violação, **3 dias simulados com 73 consultas e zero 656**, ciência → `procNFe` → auditoria INBOUND → webhook.
* *Pendente:* os **30 dias em homologação real** (precisa de e-CNPJ A1 de homologação e acesso à SEFAZ); confirmar `cOrgao` 91 e a SOAPAction do evento; NFS-e nacional e CT-e.

---

## 13. Estratégia de Testes

* **Corpus golden:** casos versionados (entrada + resposta do motor como snapshot + achados esperados). **Nenhum valor de alíquota é escrito à mão**; snapshots são regenerados apenas pelo pipeline do §2.3, com aprovação humana do diff.
* **Testes de contrato:** Testcontainers subindo a imagem do bundle e validando o schema OpenAPI dos motores.
* **Segurança:** RLS cross-tenant (todas as tabelas, leitura e escrita), XXE, signature wrapping, zip bomb, path traversal, SSRF, reutilização de `Idempotency-Key`.
* **Propriedade:** mapeador XML (gerar XML a partir do resultado com `/xml/generate` e remapear deve ser idempotente).
* **Carga:** k6 (gate da Fase 6, repetido a cada bundle novo, de forma resumida).
* **Billing:** simulação de mês com replays, reprocessamentos e virada de competência no meio do lote.

---

## 14. Decisões Tributárias e Técnicas

### 14.1. Resolvidas (validadas em 25/11/2026)
Conclusões tiradas de fontes primárias, com recomendação de revisão por um especialista tributário antes da fase correspondente.

| Tema | Decisão | Fonte |
|:--|:--|:--|
| Alíquotas 2026 | CBS 0,9% e IBS estadual 0,1%, compensáveis com PIS/Cofins; não se aplicam ao Simples | ADCT art. 125; LC 214 arts. 343, 346, 348 |
| Alíquotas 2027–2028 | IBS 0,05% + 0,05%; CBS = referência − 0,1 p.p. | ADCT art. 127; LC 214 arts. 344, 347; NT 2025.002 v1.53 |
| CRT 2 (sublimite) | CBS no DAS; IBS fora do DAS, pelo regime regular | LC 123 art. 13-A; NT 2026.005 |
| CRT 4 (MEI) | Valores fixos; não informa IBSCBS; o motor responde 422 | LC 123 art. 18-A; NT 2026.005; código do motor |
| IS no Simples | Fora do DAS; calculado pelo motor regular | LC 123 art. 13 §1º, XIV-A |
| Arredondamento e tolerância | `HALF_EVEN` no motor; ±0,01 por item; totais = soma exata | ADR003 do motor; NT 2025.002 v1.53 |
| Crédito | Por destaque até a implantação do split payment ou do recolhimento pelo adquirente | LC 214 arts. 47 e 48 |
| Crédito de fornecedor do Simples | Valor pago no DAS, pela alíquota informada no documento | LC 214 art. 47 §9º; LC 123 art. 23 |
| CNPJ alfanumérico | Formato, DV e chave de acesso conforme §3.3 | NT Conjunta 2025.001; NT 2026.004 |
| DF-e | Espera de 1 h após 137, até 20 consultas/h, NSU sequencial, documentos por 90 dias | NT 2014.002 v1.40 |
| Stack | MassTransit fora; MinIO só como referência; Stripe via Meters; OpenIddict 7 sem cifrar o token | §11, §10.4, §3.1 |
| Alíquotas 2027+ no motor | Não existem na base 1.7.2 (V0053/V0015): o catálogo curado é a fonte primária | código do motor |
| Modelo de alíquota por ente | Referência + alíquota própria com forma SUBSTITUICAO/ACRESCIMO/DECRESCIMO, igual ao motor | `ALIQUOTA_REFERENCIA`/`ALIQUOTA_PADRAO` do motor |
| Alíquotas 2027 no motor (teste) | `/aliquota-uniao`, `/aliquota-uf` e `/aliquota-municipio` respondem 404 para 2027. Sem `aliquotasNominais`, o cálculo retorna `REG-037` | Fase 0, executado |
| Município por item | Não existe: `municipio`/`uf` só no nível da operação (`OperacaoInput`). O agrupamento do §8.6 é necessário | código + Fase 0 |
| Payload de 990 itens | Aceito: 168 KB de entrada, 1 MB de resposta, HTTP 200 | Fase 0, executado |
| pg_partman e domínio | O pg_partman 5.5 não aceita domínio na coluna de partição: `billing_month` é `DATE` + CHECK | Fase 1, executado (D-106) |
| Egress dos motores | Com `VERSAO_OFFLINE_URL` apontando para endereço inválido, os motores sobem normalmente; só `/versao/status` passa a responder "não atualizado" (não é usado) | Fase 0, executado |
| Testes oficiais dos motores | Bundle 2026.11.2: regular com 1.314 testes e 0 falhas; Simples com 376 testes e 0 falhas (a issue #6 não reproduz mais) | Fase 0, executado (D-006) |
| Alíquota de referência da CBS 2027 (cálculo do TCU) | Plenário do TCU, 28/10/2026: 11,3468%, arredondada para 11,3%; redutor de compras governamentais de 44,2%. Carregada como **provisória** (CBS 2027 = 11,2%) até a resolução do Senado | TCU (TC 006.529/2026-7); D-206 |
| `COPY` em tabela com RLS | O PostgreSQL não suporta `COPY FROM` em tabela com RLS: a gravação em lote usa `INSERT ... unnest` | documentação do PostgreSQL; D-213 |
| Cadastro de entes do motor | 27 UFs + `EX` (exterior, município 9999999); 5.573 códigos | Fase 2, executado (D-218) |
| Validação da base pelo motor | Com `gComponentesBC`, o motor calcula o vBC (vProd + frete + seguro + outras − desconto − ICMS...) e recusa base declarada divergente com `REG-034`. Exige `tpDoc` 55/65 | Fase 3, executado (D-305) |
| Erros do motor por item | `REG-004/006` CST × cClassTrib; `REG-005` cClassTrib inexistente; `REG-002/011/012` NCM/NBS; `REG-009` gTribRegular ausente; `CAL-007` não implementado | Fase 3, executado (D-307) |

### 14.2. Em Aberto

| Tema | Pergunta | Responsável | Até |
|:--|:--|:--|:--|
| Alíquota de referência da CBS 2027 | O Senado fixa até 15/12/2026? Sem resolução, vale a do TCU (11,3%, já carregada como provisória, D-206). A troca é uma mudança pela Admin API com duas pessoas | Fiscal (acompanhar) | antes de 01/01/2027 |
| CBS 2028 e redutor de compras governamentais | Valor de 2028 ainda não existe. O redutor (44,2%) não vem no motor 1.7.2 (`pRedutor = 0.00`) | Fiscal + engenharia | bundle de dezembro / 2027 |
| Fonte oficial das alíquotas dos entes | Onde o Comitê Gestor do IBS e os entes vão publicar as alíquotas próprias (formato consumível por máquina)? | Fiscal | antes de 2029 (antes disso só vale a referência) |
| Documentos grandes na API síncrona | Provisório (D-215): até 500 itens síncronos; acima disso, 422 orientando o lote. Revisar com o gate de capacidade | Produto + engenharia | Fase 6 |
| ICMS/ISS na transição 2029–2032 | Fica fora do produto ou vira um módulo próprio (`ITaxModule`)? | Produto | antes do Marco C |
| CRT 3 + `tpRegimeIBSCBS = 2` | O que significa o `PENDENTE` devolvido pelo motor do Simples? | Especialista + issue no repositório da RFB | Fase 5 |
| CRT 1/2 com opção pelo regime regular | A combinação `SOMENTE_RECEITA` + REGULAR se confirma nos casos golden? | Especialista | Fase 5 |
| IS no motor regular | Quando a Release 16 entrega as regras de IS? | Engenharia (acompanhar ROADMAP) | Fase 5 |
| Data de implantação do split payment | Muda o `credito_status` padrão | Fiscal | pós-GA |
| Letras vedadas no CNPJ | A vedação de I, O, U, Q e F se confirma? Até lá, todas as letras são aceitas (D-207) | Engenharia (acompanhar a RFB) | contínuo |
| Rejeição 1150 | Há tolerância na NT 2025.002 v1.53? (R011/R013 usam ±0,01) | Especialista | Fase 5 |
| NTs 2026.006/007/008 | Impacto no mapeador (split payment, lista centralizada de contribuintes, valor líquido `vProdLiq`, já presente no PL_010h) | Engenharia + especialista | Fase 5 |
| Município de incidência no XML | `cMunFGIBS` → `cMunFG` (D-306) se confirma? | Especialista | antes de 2029 |
| Cobrança de `SCHEMA_ERROR` | Implementado por plano (`plans.bill_schema_errors`, D-703); falta o comercial definir o valor de cada plano | Produto/Comercial | antes do Marco B |
| Termos de Uso | Limitação de responsabilidade sobre indícios | Jurídico | Fase 5 |
| Marca | Nome, site e material não podem sugerir produto oficial da RFB | Jurídico/Marketing | Marco B |

---

## 15. Parametrização

### 15.1. Princípios
1. **O que muda com a lei, o contrato ou a operação é dado, não código.** Nenhuma alíquota, prazo, limite ou regra de cobrança fica fixa no código.
2. **Temporal:** todo parâmetro fiscal tem `valid_from`/`valid_to`. Nada é editado no lugar: uma mudança cria uma nova versão com vigência, o que permite agendar uma lei que entra em vigor no futuro e reprocessar o passado com os valores da época.
3. **Rastreável:** toda mudança passa por `param_changes` (quem propôs, quem aprovou, justificativa, ato legal). Domínios fiscais exigem **dois aprovadores diferentes**.
4. **Reprodutível:** cada cálculo e cada auditoria gravam `engine_bundle` + `params_snapshot_id`. O mesmo input com o mesmo par sempre gera o mesmo resultado.
5. **Recarga a quente:** os parâmetros vivem em memória como um snapshot imutável. Uma mudança aplicada gera um snapshot novo, publicado via pub/sub; cada instância troca a referência de forma atômica, sem restart e sem lock no caminho da requisição.
6. **Interfaces:** Admin API `/admin/v1/params/*` (escopos `admin:fiscal`, `admin:commercial`, `admin:ops`) desde a Fase 2, e backoffice web no Marco C. *Implementado (D-W03):* app `web/` com alvo `backoffice` em host interno; login provisório por chave de operador até o OIDC. O cliente altera só o que é do próprio tenant, pela API pública.

### 15.2. Catálogo de Parâmetros

| Categoria | Parâmetro | Escopo | Quem altera | Aprovação |
|:--|:--|:--|:--|:--|
| **Fiscal** | Alíquota de referência (CBS, IBS UF, IBS Mun, IS) por vigência | Global | Time fiscal | 2 pessoas |
| | Alíquota própria do ente + forma de aplicação | UF / Município | Time fiscal (pipeline de ingestão) | 2 pessoas |
| | Roteamento CRT × `tpRegimeIBSCBS` → motor | Global | Time fiscal | 2 pessoas |
| | Regra de crédito (data de corte do split payment) | Global | Time fiscal | 2 pessoas |
| | Faixas e percentuais dos anexos do Simples (R024) | Global | Time fiscal | 2 pessoas |
| | Tolerâncias (R011 ±0,01; compra governamental ±0,04) | Global, com override por tenant | Time fiscal | 2 pessoas |
| | Catálogo de regras de auditoria (natureza, severidade padrão) | Global | Time fiscal + engenharia | 2 pessoas + deploy |
| **Tenant** | Ligar/desligar regras, severidade, parâmetros da regra | Tenant | Cliente (API) | — |
| | Contexto padrão (perfil do adquirente, destinação) | Tenant / CNPJ | Cliente | — |
| | Retenção do XML / `zero_retention` | Tenant | Cliente | — |
| | Webhooks, eventos assinados, IP allowlist por API key | Tenant | Cliente | — |
| | Captura DF-e: ciência automática, CNPJs monitorados | CNPJ | Cliente | — |
| **Comercial** | Plano: RPM, burst, concorrência, limites de lote, franquia, preço | Plano | Comercial | 1 pessoa |
| | Override de limites e preço por contrato | Tenant | Comercial | 2 pessoas |
| | Cobrar ou não `SCHEMA_ERROR`; auto-provisionamento OEM | Plano / Tenant | Comercial | 1 pessoa |
| **Operacional** | Feature flags (por tenant, percentual, plano) | Global / Tenant | Engenharia | 1 pessoa |
| | Timeouts, retries, circuit breaker por motor | Global | Engenharia | 1 pessoa |
| | Tamanho de chunk do lote e K do fair-share | Global / Plano | Engenharia | 1 pessoa |
| | Agenda de retry dos webhooks; limites de payload (itens, MB) | Global / Plano | Engenharia | 1 pessoa |

### 15.3. O que **não** é editável
* A base do motor (cClassTrib, CST, tratamentos tributários, que ficam como fórmulas JEXL dentro do SQLite, NCM/NBS aplicáveis) é **oficial** e só muda com um bundle novo (§2.3). O único ponto de sobreposição permitido é a alíquota nominal, via catálogo (§8.5).

### 15.4. Ajustes dos Motores sem Fork (variáveis de ambiente do Spring)

| Variável | Padrão no repositório | Ajuste | Motivo |
|:--|:--|:--|:--|
| `SPRING_DATASOURCE_HIKARI_MAXIMUM_POOL_SIZE` | 10 (padrão do Hikari; o yml não define) | igual ao número de vCPUs × 2, medido | Tomcat com até 250 threads disputando 10 conexões ao SQLite |
| `SPRING_THREADS_VIRTUAL_ENABLED` | regular: desligado; simples: ligado | testar ligado no regular | menos threads de plataforma sob carga |
| `SERVER_TOMCAT_THREADS_MAX` | 250 | conforme o teste de carga | — |
| `JAVA_TOOL_OPTIONS` | — | `-XX:MaxRAMPercentage=75 -XX:+UseZGC` (ou G1), AOT cache do Java 25 | latência de cauda e cold start |
| `VERSAO_OFFLINE_URL` / `VERSAO_URL` | portal piloto da RFB | stub interno | sem egress (§2.1) |

Os caches Caffeine do motor já vêm dimensionados no yml (cerca de 100 MB, expiração de 1 dia). O heap precisa comportá-los com folga: mínimo de 1 GB por réplica.

---

## 16. Performance & Tecnologia

### 16.1. Por que esta stack

| Camada | Escolha | Motivo |
|:--|:--|:--|
| Gateway | **.NET 10 (LTS, suporte até nov/2028)**, Kestrel, Minimal APIs | Entre os servidores HTTP mais rápidos do mercado, baixa alocação, async nativo, ecossistema maduro para XML/XMLDSig/X.509 (núcleo do produto) |
| Motores | Java 25 oficiais, **sem alteração** | É a referência legal; reescrever seria divergir da RFB |
| Banco | PostgreSQL 18 | RLS, particionamento, I/O assíncrono, `uuidv7()` |
| Cache/limites | Valkey | latência submilissegundo, Lua atômico, licença BSD |
| Fila | RabbitMQ 4 quorum + **KEDA** | KEDA escala workers e réplicas batch dos motores pelo tamanho da fila |
| Infra | Kubernetes gerenciado **em região no Brasil** (AWS `sa-east-1`, Azure Brazil South ou GCP `southamerica-east1`), multi-AZ | menor latência para clientes brasileiros e residência de dados (LGPD) |
| Borda | Cloudflare (POPs no Brasil) | TLS terminado perto do cliente, WAF, proteção DDoS |

**Alternativas descartadas**
* **Gateway em Java chamando o motor in-process:** o motor é uma aplicação Spring Boot, não uma biblioteca. Acoplar às classes internas quebraria a cada tag. Uma chamada HTTP em localhost custa menos de 1 ms.
* **Gateway em Go/Rust:** ganho marginal frente ao .NET 10, com perda no ecossistema de XMLDSig/ICP-Brasil.

### 16.2. Técnicas de Latência
1. **Motor no mesmo pod da API** (sidecar de verdade): chamada em `localhost`, `HttpClient` com `SocketsHttpHandler` e pool de conexões keep-alive, sem salto de rede. O lote usa réplicas próprias (§8.4).
2. **Aquecimento antes de receber tráfego:** o readiness só fica verde depois de reenviar uma amostra do corpus golden ao motor (JIT e caches Caffeine aquecidos). Avaliar o AOT cache do Java 25 para reduzir o cold start, medindo o ganho.
3. **Tudo que é referência fica em memória:** catálogo de alíquotas (5.570 municípios × vigências: poucos MB, busca por intervalo ordenado), roteamento, limites de plano, regras, cadastro de municípios e API keys (L1 de 30 s). Nenhum acesso ao banco no caminho da simulação.
4. **Uma chamada por documento:** o motor aceita vários itens por operação, então os itens são agrupados só por município de incidência (§8.6).
5. **Cache determinístico de resultado:** chave = `SHA-256(engine_bundle, params_snapshot_id, request canônico)`. L1 em memória (60 s) e L2 em Valkey (24 h). ERPs repetem muito a mesma simulação de carrinho. É seguro porque a chave muda quando muda o motor ou qualquer parâmetro.
6. **Persistência fora do caminho crítico das simulações:** gravação em lote assíncrono (`Channel` → `INSERT ... SELECT unnest(...)` por tenant a cada 50 ms ou 1.000 linhas; `COPY FROM` não é permitido em tabela com RLS, D-213).
   * *Trade-off aceito e documentado:* a queda de um pod pode perder até cerca de 50 ms de eventos de simulação. O efeito é subcobrança, a favor do cliente.
   * As **auditorias** continuam com a transação síncrona do §10.2.
7. **Caminho crítico enxuto no .NET:**
   * Npgsql direto (sem EF Core) no caminho crítico; EF Core só no admin.
   * `System.Text.Json` com source generators, ReadyToRun, Dynamic PGO e Server GC.
   * `XmlReader` em streaming e `XmlSchemaSet` compilado no startup.
8. **Rate limit:** uma única ida ao Valkey (script Lua faz token bucket e lease juntos). Se o Valkey estiver fora ou acima de 50 ms (corte de 2 ms inviável pela resolução de timer, D-211), o limitador cai para o modo local por 5 s (§5.2).
9. **Compressão:** Brotli/gzip só acima de 1 KB.
10. **Observabilidade sem custo na latência:** amostragem *tail-based* (100% dos erros e lentos, 10% do resto); exportação assíncrona.

### 16.3. Orçamento de Latência e SLOs
Medidos no servidor (sem a rede do cliente), em região no Brasil. **São metas a confirmar com o baseline da Fase 0**; o tempo interno do motor é o maior desconhecido.

| Etapa de `/calculo/simular` (até 20 itens) | Orçamento |
|:--|:--|
| Autenticação (L1) + rate limit (Valkey) | ≤ 1 ms |
| Validação + catálogo + roteamento (memória) | ≤ 1 ms |
| Motor (localhost, aquecido, sem fila) | **medido: p50 ≈ 10 ms, p95 ≈ 11–13 ms** (20 itens) |
| Serialização + gravação assíncrona | ≤ 1 ms |

| SLO | Meta |
|:--|:--|
| Disponibilidade da API | 99,95% |
| `/calculo/simular` (até 20 itens), cache miss | p50 ≤ 15 ms · p95 ≤ 40 ms · p99 ≤ 100 ms |
| `/calculo/simular`, cache hit | p95 ≤ 5 ms |
| `/auditoria/xml` (até 100 itens) | p95 ≤ 150 ms |
| Lote de 25 mil documentos | ≤ 5 min |
| Propagação de mudança de parâmetro | ≤ 5 s em todas as instâncias |

*Implementação (D-H01, D-H02):* metas mantidas após o gate da Fase 6 (p95 medido de 18,8 ms com lote concorrente). Alertas e regras em `infra/k8s/taxgateway/templates/monitoring.yaml`; resumo e lacunas em `docs/slo.md`.

### 16.3.1. Baseline Medido na Fase 0 (motor regular 1.7.2, isolado, k6, `/observabilidade/regime-geral`, 2027 com alíquotas nominais)
Ambiente: Docker Desktop local, container limitado a 2 vCPU / 2 GB. Os valores servem para dimensionamento relativo; repetir no hardware de produção.

| Itens por documento | p95, 1 requisição por vez | Vazão (2 vCPU, saturado) | Itens/s |
|:--|:--|:--|:--|
| 1 | 1,5 ms | ~650–715 req/s | ~700 |
| 20 | 11 ms | ~100–115 req/s | ~2.200 |
| 100 | 48 ms | ~23 req/s | ~2.300 |
| 500 | — | ~4,3 req/s | ~2.150 |
| 990 (chamada isolada, a frio) | 2,5 s | — | — |

**Conclusões**
1. **O custo é proporcional ao número de itens:** cerca de **1 ms de CPU por item** (≈ 1.100 itens/s por vCPU). A vazão cresce de forma **linear** com CPU (4 vCPU: ~225 req/s de 20 itens).
2. **Uma requisição sozinha já usa ~1,6 núcleo:** o motor paraleliza internamente. A latência mínima não cai com mais concorrência; ela só piora quando há fila.
3. Desligar a memória de cálculo (`APPLICATION_MEMORIACALCULO_ENABLED=false`) **não** muda o desempenho. Não vale a pena perder a memória de cálculo.
4. **Consequências para o desenho:**
   * Para cumprir p95 ≤ 40 ms, os motores síncronos precisam operar com **utilização de CPU abaixo de ~50%**. O HPA escala por CPU com alvo de 50%.
   * Sizing de referência: 1 vCPU de motor ≈ 55 simulações/s de 20 itens com folga. Um cliente OEM a 200 req/s × 20 itens precisa de cerca de 8 vCPU de motor síncrono.
   * O **cache determinístico** (§16.2, item 5) é a principal alavanca de custo e latência: um hit evita todo o custo do motor.
   * Documentos grandes (> 100 itens) vão para a fila de lote mesmo quando chegam pela API síncrona, com resposta `202`, ou são processados com timeout próprio de 5 s. Decisão de produto em aberto (§14).
   * Lote: 25 mil documentos × 30 itens ≈ 750 mil itens ≈ 680 vCPU·s, ou seja, cerca de 45 s com 16 vCPU de motores de lote. A meta de ≤ 5 min tem folga.

### 16.4. Gate de Performance
* **Fase 0:** baseline do motor isolado (k6 direto no sidecar): latência por número de itens, vazão por vCPU, efeito do pool do Hikari e das virtual threads.
* **Cada fase:** teste de regressão de performance no CI (k6 com limites). O build falha se o p95 piorar mais de 10% em relação ao baseline.
* **Fase 6:** gate de capacidade completo (§12).
