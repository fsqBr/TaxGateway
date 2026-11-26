# SOFTWARE DESIGN DOCUMENT (SDD) — TAXGATEWAY IBS/CBS/IS ENGINE

**Versão:** 1.0 (Production Baseline)  
**Data:** Novembro de 2026  
**Stack Principal:** .NET 10 (C# 14), PostgreSQL 17+, Redis 7+, MinIO/S3, RabbitMQ, Docker/Kubernetes  
**Sidecar Core:** `ReceitaFederal/calculadora-rtc-regime-regular` & `calculadora-rtc-simples-nacional` (Java 25 + SQLite Read-Only)  

---

## 1. Visão Geral & Objetivo do Sistema

Plataforma SaaS B2B *multi-tenant*, *API-first*, projetada para encapsular os motores oficiais da Receita Federal do Brasil (Reforma Tributária — CBS, IBS e Imposto Seletivo) em uma camada de alta disponibilidade, resiliência, auditoria fiscal em lote, controle de créditos financeiros e faturamento baseado em uso (*metered billing*).

* **Core de Negócio / Gateway / Orquestração:** C# / .NET 10 (ASP.NET Core Minimal APIs + Worker Services).
* **Motor Fiscal Embarcado (Sidecar Stateless):** Containers isolados rodando o código oficial MIT da Receita Federal (`ReceitaFederal/calculadora-rtc-regime-regular` e `calculadora-rtc-simples-nacional` em Java 25 + SQLite embarcado em modo *read-only*).
* **Público-Alvo:** Software Houses / ERPs (modelo OEM multi-CNPJ) e empresas de médio/grande porte (auditoria de XMLs de entrada e pré-emissão de saída).

---

## 2. Topologia de Infraestrutura & Desacoplamento

```text
                    ┌─────────────────────────────────────────┐
                    │         Cloudflare / WAF / TLS          │
                    │    (DDoS Protection, Edge Rate Limit)   │
                    └────────────────────┬────────────────────┘
                                         │ HTTPS (REST / JSON / XML)
                    ┌────────────────────▼────────────────────┐
                    │       C# (.NET 10) API Gateway          │
                    │  • Auth (API Key Hash / JWT OAuth2)     │
                    │  • Tenant & CNPJ Resolver               │
                    │  • Redis Sliding Window Rate Limiter    │
                    │  • XSD & XML Digital Signature Validator│
                    │  • Idempotency Guard (Chave 44 dígitos) │
                    └───┬───────────────────────────────┬─────┘
                        │ Sync (Baixa Latência)         │ Async (Lotes / ZIP)
                        ▼                               ▼
      ┌──────────────────────────────────┐   ┌───────────────────────────────┐
      │   Internal Load Balancer (HTTP)  │   │   Message Broker / Queue      │
      └───────────┬──────────────┬───────┘   │   (RabbitMQ / Redis Streams)  │
                  │              │           └───────────────┬───────────────┘
                  ▼              ▼                           │
      ┌─────────────────┐ ┌─────────────────┐                ▼
      │ Sidecar Java 25 │ │ Sidecar Java 25 │   ┌───────────────────────────────┐
      │ Regime Regular  │ │ Simples Nacional│   │  C# (.NET 10) Batch Workers   │
      │ (SQLite R/O)    │ │ (SQLite R/O)    │   │  • System.Threading.Channels  │
      └─────────────────┘ └─────────────────┘   │  • Paralelismo controlado     │
                                                └───────────────┬───────────────┘
                                                                │
      ┌─────────────────────────────────────────────────────────▼───────────────┐
      │                         Camada de Dados & Telemetria                    │
      │  • PostgreSQL 17+ (RLS Multi-tenant, Auditoria, Billing, Catálogo CNPJ) │
      │  • Redis 7+ (Rate Limit, Cache de Alíquotas/NCM, Idempotência)          │
      │  • S3 / MinIO Object Storage (XMLs brutos criptografados + Relatórios)  │
      │  • OpenTelemetry + Prometheus/Grafana + Seq/ELK (Observabilidade)       │
      └─────────────────────────────────────────────────────────────────────────┘
```

### 2.1. Regras Inegociáveis de Infraestrutura
1. **Isolamento do Motor Java:** Os containers Java 25 (`calculadora-rtc-*`) **nunca** têm porta exposta para a internet. Comunicam-se exclusivamente via rede interna privada Docker/Kubernetes com a API .NET.
2. **Imutabilidade e Read-Only:** O banco SQLite embutido nos containers da Receita Federal é montado como `read-only` (`:ro`). Nenhuma escrita de estado de cliente ocorre dentro dos containers Java.
3. **Zero-Downtime Update (Pipeline Fiscal):** Um GitHub Action / Cron Job monitora diariamente novos releases/tags nos repositórios oficiais da Receita Federal. Ao detectar atualização de tabela (`cClassTrib`, `CST`, `NCM`, alíquotas):
   * Constrói nova imagem Docker do sidecar Java.
   * Sobe o container em ambiente de staging e executa uma suíte de **Testes de Contrato em C#** (validando payloads conhecidos para garantir que o contrato JSON não quebrou).
   * Executa *rolling update* em produção sem derrubar requisições em andamento.

---

## 3. Arquitetura de Integração & Identidade (Como os Clientes Conectam)

Para atender tanto **ERPs legados** (Delphi, VB6, PHP antigo, que preferem integração simples via Header estático) quanto **Sistemas Modernos / Fintechs / Software Houses Enterprise** (que exigem rotação automática de tokens via OAuth 2.0), a plataforma implementa **Autenticação Dual**:

### 3.1. Fluxo A: OAuth 2.0 Client Credentials (RFC 6749 — Padrão Enterprise / OEM)
* **Endpoint de Token:** `POST /oauth/token` (`application/x-www-form-urlencoded`)
* **Credenciais:** O cliente recebe no onboarding um `client_id` (`tg_live_cli_<24chars>`) e um `client_secret` (`tg_live_sec_<48chars>`, exibido uma única vez e salvo como hash Argon2id no banco).
* **Token Emitido:** JWT assinado assimetricamente via **RS256** (chave privada rotacionável a cada 90 dias via endpoint `/.well-known/jwks.json`), com TTL curto de **15 minutos (900s)**.
* **Claims embutidas no JWT (Zero-DB Lookup na borda):**

```json
{
  "iss": "https://api.taxgateway.com.br",
  "sub": "tg_live_cli_9f8a7b6c5d4e3f2a",
  "tid": "550e8400-e29b-41d4-a716-446655440000",
  "plan": "OEM",
  "rpm": 3000,
  "conc": 50,
  "scope": "calc:read audit:write batch:write oem:manage",
  "exp": 1795575400,
  "jti": "987fcdeb-51a2-43d7-9012-345678901234"
}
```

* **Revogação Imediata:** Em caso de comprometimento, o `jti` ou `client_id` é inserido numa *Blacklist* no Redis com TTL igual ao tempo restante do token (`EXPIRE`).

### 3.2. Fluxo B: API Key Direta (Padrão Compatibilidade ERP Legado)
* **Header:** `X-Api-Key: tg_live_sk_<prefix8>_<secret32>`
* **Validação Otimizada (Cache-Aside):**
  1. Gateway extrai o prefixo e calcula `SHA-256` da chave completa.
  2. Consulta chave no Redis (`auth:apikey:{sha256}`, TTL 5 minutos).
  3. Em *cache miss*, consulta PostgreSQL (`api_keys JOIN tenants`), valida `revoked_at IS NULL` e `tenant.status = 'ACTIVE'`, popula o Redis com os metadados do plano e injeta o contexto no `HttpContext.Items`.

### 3.3. Escopo de CNPJ & Segurança Cross-Tenant (`X-On-Behalf-Of-CNPJ`)
* Todo request fiscal exige explicitamente ou infere do XML o CNPJ sujeito da operação.
* **Regra de Validação:**
  * Se `plan != 'OEM'`: o CNPJ extraído (`<emit><CNPJ>` na saída ou `<dest><CNPJ>` na entrada) **deve** constar na tabela `tenant_cnpjs` com `is_active = true`.
  * Se `plan == 'OEM'`: a Software House pode enviar o header `X-On-Behalf-Of-CNPJ: 12345678000199`. Se o CNPJ ainda não estiver cadastrado no tenant OEM e o contrato permitir *Auto-Provisioning*, o gateway registra o CNPJ automaticamente como ativo naquela competência (`YYYY-MM`) para faturamento por CNPJ ativo. Caso contrário, retorna `403 Forbidden (ERR_CNPJ_NOT_PROVISIONED)`.

---

## 4. Segurança Multi-Tenant & Conformidade LGPD

### 4.1. Isolamento em Banco (PostgreSQL Row-Level Security)
* Todas as tabelas transacionais possuem `tenant_id UUID NOT NULL`.
* A conexão do EF Core / Dapper define `SET LOCAL app.current_tenant_id = '...'` via interceptor a cada transação. Mesmo em caso de falha lógica na aplicação, o banco impede vazamento de dados entre clientes.

### 4.2. Certificados Digitais A1 (`.pfx`)
* Caso o cliente habilite a busca automática na Sefaz (Manifestação do Destinatário / Distribuição DF-e), o arquivo `.pfx` e sua senha são criptografados com **Envelope Encryption (AES-256-GCM)** usando chaves gerenciadas (AWS KMS / HashiCorp Vault / Azure Key Vault) e nunca trafegam em logs.

### 4.3. LGPD & Privacy-by-Design
* **Dados B2B (CNPJ x CNPJ):** Não configuram dados pessoais pela LGPD, mas são tratados sob sigilo fiscal e criptografia em repouso.
* **Dados B2C (Notas com `<dest><CPF>`):** Antes de persistir qualquer metadado extraído no PostgreSQL ou enviar eventos para telemetria/logs, o parser C# substitui o valor de `<CPF>` por um hash irreversível com *salt* por tenant (`HMAC-SHA256(tenant_salt, cpf)`), mantendo apenas a flag `is_consumidor_final = true`.
* **Retenção Configurável:** Política de *lifecycle* no Object Storage (S3/MinIO) para expurgo automático de XMLs brutos após 30, 90 ou 365 dias conforme escolha do tenant.

---

## 5. Rate Limiting Dinâmico & Throttling por Contrato

Para proteger os containers Java da Receita Federal contra saturação de CPU/SQLite e garantir isolamento entre clientes (*Noisy Neighbor Protection*), o .NET 10 implementa uma **pipeline de Rate Limiting em duas camadas** apoiada por **Redis Lua Scripts (operações atômicas)**:

### 5.1. Matriz de Limites por Plano Contratual

| Plano | Requisições / Minuto (RPM) | Burst Máximo (Token Bucket) | Concorrência Simultânea | Tamanho Máx. Lote (ZIP) | Prioridade na Fila |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **STARTER** | 120 RPM (2/s) | 10 reqs | 2 conexões | 500 XMLs (15 MB) | `low` |
| **GROWTH** | 600 RPM (10/s) | 30 reqs | 10 conexões | 5.000 XMLs (100 MB) | `normal` |
| **SCALE** | 3.000 RPM (50/s) | 100 reqs | 30 conexões | 25.000 XMLs (500 MB) | `high` |
| **OEM / ENTERPRISE** | 12.000+ RPM (200/s) | 500 reqs | 100+ conexões | 100.000 XMLs (2 GB) | `realtime` |

### 5.2. Headers Padrão IETF RateLimit na Resposta
Toda resposta HTTP (sucesso ou erro) retorna os headers padronizados para que o ERP do cliente implemente *backoff* inteligente:

```http
X-RateLimit-Limit: 600
X-RateLimit-Remaining: 542
X-RateLimit-Reset: 1795575460
X-Concurrency-Limit: 10
X-Concurrency-Active: 3
```

* **Quando excedido:** Retorna `429 Too Many Requests` (RFC 9457 Problem Details) com `Retry-After: <segundos>` e incrementa métrica `taxgateway_ratelimit_rejected_total{tenant_id, plan}`.

---

## 6. Especificação Detalhada dos Endpoints & Contratos (OpenAPI v1)

Todos os erros seguem rigorosamente a **RFC 9457 (`application/problem+json`)**.

### 6.1. `POST /v1/calculo/simular` (Cálculo Síncrono Pré-Emissão)
Calcula IBS (Estadual + Municipal), CBS e Imposto Seletivo (IS) antes da emissão da nota.

* **Headers:** `Authorization: Bearer <jwt>` (ou `X-Api-Key`), `Idempotency-Key: <uuid-v4>` (opcional para simulações, obrigatório para registro auditável).
* **Request Payload:**

```json
{
  "cnpj_emitente": "12345678000199",
  "regime_tributario": "REGIME_REGULAR",
  "data_fato_gerador": "2027-02-15",
  "municipio_origem_ibge": "3550308",
  "municipio_destino_ibge": "3304557",
  "finalidade_operacao": "COMERCIALIZACAO",
  "itens": [
    {
      "numero_item": 1,
      "ncm": "84713012",
      "c_class_trib": "000001",
      "cst_ibs_cbs": "000",
      "valor_base_calculo": 5000.00,
      "quantidade_tributavel": 1.0,
      "unidade_medida": "UN",
      "incluir_imposto_seletivo": false
    }
  ]
}
```

* **Response `200 OK`:**

```json
{
  "simulation_id": "sim_01JDH89K2M4N5P6Q7R8S9T0V",
  "rfb_engine_version": "rtc-regular-2027.01.2",
  "cache_hit": false,
  "totais": {
    "valor_base_total": 5000.00,
    "cbs_valor": 440.00,
    "ibs_uf_valor": 550.00,
    "ibs_mun_valor": 335.00,
    "ibs_total_valor": 885.00,
    "imposto_seletivo_valor": 0.00,
    "carga_tributaria_efetiva_percent": 26.50
  },
  "itens": [
    {
      "numero_item": 1,
      "cbs": { "aliquota": 8.80, "valor": 440.00, "memoria_calculo": "Base 5000.00 * 8.80%" },
      "ibs_uf": { "aliquota": 11.00, "valor": 550.00 },
      "ibs_mun": { "aliquota": 6.70, "valor": 335.00 },
      "xml_snippet_gIBSCBS": "<gIBSCBS><vBC>5000.00</vBC>...</gIBSCBS>"
    }
  ]
}
```

### 6.2. `POST /v1/auditoria/xml` (Auditoria Individual — Entrada ou Saída)
Valida Schema XSD, assinatura digital e audita os valores declarados contra o motor oficial da Receita Federal.

* **Query Param:** `?direction=OUTBOUND|INBOUND` (`OUTBOUND` = valida antes de transmitir à Sefaz; `INBOUND` = valida crédito financeiro de nota recebida de fornecedor).
* **Body:** `application/xml` (XML bruto da NF-e/NFS-e).
* **Response `200 OK`:**

```json
{
  "audit_id": "aud_01JDH90A1B2C3D4E5F6G7H8J",
  "chave_acesso": "35270212345678000199550010000045901123456789",
  "direction": "INBOUND",
  "status": "DIVERGENTE",
  "is_billable": true,
  "resumo_financeiro": {
    "credito_ibs_cbs_declarado": 1240.00,
    "credito_ibs_cbs_devido_rfb": 3080.00,
    "gap_credito_detectado": 1840.00,
    "risco_glosa_fiscal": 0.00
  },
  "ocorrencias": [
    {
      "severidade": "ERROR",
      "codigo": "RFB_DIFF_CCLASSTRIB",
      "numero_item": 3,
      "ncm": "30049099",
      "campo_xml": "det[3]/imposto/IBSCBS/cClassTrib",
      "valor_declarado": "200015",
      "valor_esperado": "000001",
      "mensagem": "Fornecedor aplicou redução de 60% indevida para este NCM. Diferença de crédito de R$ 1.840,00.",
      "impacto_financeiro": 1840.00
    }
  ]
}
```

### 6.3. `POST /v1/auditoria/lotes` (Processamento Assíncrono em Massa)
* **Modo 1 (Upload Direto):** `multipart/form-data` com arquivo `.zip` (stream direto para MinIO/S3 sem buffer em memória).
* **Modo 2 (Presigned URL — Recomendado para Lotes > 100 MB):**
  1. Cliente chama `POST /v1/auditoria/lotes/upload-url` e recebe uma URL pré-assinada do S3/MinIO válida por 15 minutos.
  2. Cliente faz `PUT` direto no storage.
  3. Cliente chama `POST /v1/auditoria/lotes` passando o `storage_object_key`.
* **Response `202 Accepted`:**

```json
{
  "batch_id": "bat_01JDH95X9Y8Z7W6V5U4T3S2R",
  "status": "QUEUED",
  "status_url": "https://api.taxgateway.com.br/v1/auditoria/lotes/bat_01JDH95X9Y8Z7W6V5U4T3S2R",
  "estimated_completion_seconds": 45
}
```

### 6.4. Endpoints OEM / Software House
* `POST /v1/oem/cnpjs`: Provisiona um novo CNPJ cliente sob o tenant OEM.
* `DELETE /v1/oem/cnpjs/{cnpj}`: Desativa o CNPJ cliente (encerra cobrança ativa a partir da próxima competência).
* `GET /v1/oem/usage`: Retorna consumo detalhado por CNPJ ativo na competência atual (`YYYY-MM`).

---

## 7. Sistema de Webhooks com Garantia de Entrega & Segurança HMAC

Quando um lote (`batch_id`) termina ou quando uma divergência crítica de crédito é detectada via sincronização automática Sefaz:

1. **Assinatura Criptográfica:** Todo POST disparado pelo `TaxGateway.Workers` inclui:
   * `X-TaxGateway-Event: batch.audit.completed`
   * `X-TaxGateway-Delivery-Id: dlv_9988776655`
   * `X-TaxGateway-Timestamp: 1795575500`
   * `X-TaxGateway-Signature: sha256=<HMAC_SHA256(webhook_secret, timestamp + "." + raw_body)>`
2. **Política de Retry Exponencial (Dead Letter Queue):**
   * Se o endpoint do cliente não responder `2xx` em até 10 segundos, o worker agenda retentativas com *jitter*: `10s -> 1m -> 5m -> 30m -> 2h -> 12h`.
   * Após 6 falhas consecutivas, o evento vai para `webhook_dead_letters` e o cliente pode reprocessar manualmente via API (`POST /v1/webhooks/deliveries/{id}/retry`).

---

## 8. Integração Interna C# (.NET 10) <-> Sidecar Java 25 (Receita Federal)

Para evitar acoplamento forte com a estrutura interna dos repositórios da Receita Federal (`calculadora-rtc-regime-regular` e `simples-nacional`), o `TaxGateway.Infrastructure` implementa o padrão **Anti-Corruption Layer (ACL)**:

```csharp
public interface IRfbTaxEngineAdapter
{
    Task<RfbCalculationResult> CalculateRegularAsync(RfbCalculationInput input, CancellationToken ct);
    Task<RfbCalculationResult> CalculateSimplesAsync(RfbCalculationInput input, CancellationToken ct);
    Task<RfbXmlValidationResult> ValidateXmlDirectAsync(string rawXml, CancellationToken ct);
    Task<RfbHealthMetadata> GetEngineMetadataAsync(CancellationToken ct);
}
```

### 8.1. Resiliência da Comunicação Interna (Polly v8 Resilience Pipeline)
* **Timeout por Tentativa:** `1.500 ms` (cálculo síncrono) / `5.000 ms` (validação XML complexo).
* **Retry:** Máximo 2 retries com *jitter* de 50ms apenas em falhas transitórias de rede/socket (`HttpRequestException`, `502`, `503`).
* **Circuit Breaker:** Abre o circuito por 15 segundos se a taxa de falha ultrapassar 30% em uma janela de 20 requisições, acionando alerta imediato no Prometheus/Alertmanager.
* **Bulkhead Isolation:** Semáforos separados (`SemaphoreSlim`) para tráfego **Síncrono (Tempo Real)** e **Assíncrono (Workers de Lote)**. Dessa forma, um cliente subindo um `.zip` com 100.000 notas **nunca** degrada o tempo de resposta de outro cliente fazendo cálculo de checkout em tempo real.

---

## 9. Modelagem de Banco de Dados Avançada (PostgreSQL 17+)

Inclui **Particionamento Declarativo por Mês** na tabela de auditoria (essencial para suportar dezenas de milhões de notas sem degradação de índices) e **Row-Level Security (RLS)** obrigatório.

```sql
-- Habilitar extensões essenciais
CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- Tabela de Tenants
CREATE TABLE tenants (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name VARCHAR(200) NOT NULL,
    root_cnpj VARCHAR(14) NOT NULL UNIQUE,
    plan_tier VARCHAR(20) NOT NULL CHECK (plan_tier IN ('STARTER', 'GROWTH', 'SCALE', 'OEM', 'ENTERPRISE')),
    rpm_limit INT NOT NULL DEFAULT 120,
    concurrency_limit INT NOT NULL DEFAULT 2,
    auto_provision_cnpj BOOLEAN NOT NULL DEFAULT FALSE,
    stripe_customer_id VARCHAR(100),
    stripe_subscription_id VARCHAR(100),
    hmac_salt BYTEA NOT NULL DEFAULT gen_random_bytes(32),
    status VARCHAR(20) NOT NULL DEFAULT 'ACTIVE',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Clientes OAuth 2.0 (Client Credentials)
CREATE TABLE oauth_clients (
    client_id VARCHAR(40) PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    name VARCHAR(100) NOT NULL,
    client_secret_hash TEXT NOT NULL, -- Argon2id
    scopes TEXT[] NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- API Keys (Legado / M2M simples)
CREATE TABLE api_keys (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    name VARCHAR(100) NOT NULL,
    key_prefix VARCHAR(20) NOT NULL,
    key_hash VARCHAR(64) NOT NULL UNIQUE, -- SHA-256
    scopes TEXT[] NOT NULL,
    expires_at TIMESTAMPTZ,
    revoked_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- CNPJs vinculados ao Tenant
CREATE TABLE tenant_cnpjs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    cnpj VARCHAR(14) NOT NULL,
    tax_regime VARCHAR(25) NOT NULL CHECK (tax_regime IN ('REGIME_REGULAR', 'SIMPLES_NACIONAL')),
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (tenant_id, cnpj)
);

-- Tabela Principal Particionada por Competência (billing_period)
CREATE TABLE audited_documents (
    id UUID NOT NULL DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    billing_period VARCHAR(7) NOT NULL, -- 'YYYY-MM' (Chave de Partição)
    cnpj_subject VARCHAR(14) NOT NULL,
    direction VARCHAR(15) NOT NULL, -- 'SIMULATION', 'OUTBOUND', 'INBOUND'
    access_key VARCHAR(44),
    idempotency_hash VARCHAR(64) NOT NULL,
    rfb_engine_version VARCHAR(40) NOT NULL,
    audit_status VARCHAR(20) NOT NULL, -- 'CONFORME', 'DIVERGENTE', 'SCHEMA_ERROR'
    declared_tax NUMERIC(15,2) NOT NULL DEFAULT 0,
    calculated_tax NUMERIC(15,2) NOT NULL DEFAULT 0,
    credit_gap NUMERIC(15,2) NOT NULL DEFAULT 0,
    findings JSONB NOT NULL DEFAULT '[]'::jsonb,
    storage_xml_key TEXT,
    is_billable BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, billing_period, id),
    UNIQUE (tenant_id, billing_period, idempotency_hash)
) PARTITION BY LIST (billing_period);

-- Criação de Partições Iniciais (Automação via pg_partman ou Worker mensal)
CREATE TABLE audited_documents_2026_11 PARTITION OF audited_documents FOR VALUES IN ('2026-11');
CREATE TABLE audited_documents_2026_12 PARTITION OF audited_documents FOR VALUES IN ('2026-12');
CREATE TABLE audited_documents_2027_01 PARTITION OF audited_documents FOR VALUES IN ('2027-01');

-- ROW-LEVEL SECURITY (RLS) OBRIGATÓRIO
ALTER TABLE tenant_cnpjs ENABLE ROW LEVEL SECURITY;
ALTER TABLE audited_documents ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_cnpjs ON tenant_cnpjs
    USING (tenant_id = current_setting('app.current_tenant_id', true)::UUID);

CREATE POLICY tenant_isolation_docs ON audited_documents
    USING (tenant_id = current_setting('app.current_tenant_id', true)::UUID);

-- Webhooks configurados por Tenant
CREATE TABLE tenant_webhooks (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    target_url TEXT NOT NULL,
    signing_secret VARCHAR(128) NOT NULL,
    subscribed_events TEXT[] NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Agregador de Consumo Mensal (Metered Billing)
CREATE TABLE usage_rollups (
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    billing_period VARCHAR(7) NOT NULL, -- 'YYYY-MM'
    billable_documents_count BIGINT NOT NULL DEFAULT 0,
    active_cnpjs_count INT NOT NULL DEFAULT 0,
    last_synced_to_billing_at TIMESTAMPTZ,
    PRIMARY KEY (tenant_id, billing_period)
);
```

---

## 10. Fluxo de Metering, Billing & Fechamento de Ciclo

1. **Deduplicação Atômica (Idempotência):**
   * Para XMLs com chave de acesso: `idempotency_hash = SHA256(direction + ":" + access_key)`.
   * Para Simulações sem chave: `idempotency_hash = SHA256(canonical_json_payload)` (se o cliente enviar header `Idempotency-Key`, usa o hash do header).
   * O `INSERT INTO audited_documents ... ON CONFLICT (tenant_id, billing_period, idempotency_hash) DO NOTHING RETURNING id` garante em uma única operação atômica no PostgreSQL se aquele documento deve ou não ser contabilizado na franquia.
2. **Contabilização em Tempo Real (Redis):**
   * Apenas quando o `INSERT` retorna uma nova linha (`is_billable = true`), a API executa `HINCRBY billing:{YYYY-MM}:{tenant_id} docs_count 1`.
   * Se `plan_tier == 'OEM'`, também registra o CNPJ único em um Set no Redis (`SADD billing:{YYYY-MM}:{tenant_id}:cnpjs {cnpj_subject}`) para cobrança precisa de CNPJs ativos no mês.
3. **Sincronização com Gateway Financeiro (Stripe / Asaas):**
   * Um `BillingSyncWorker` roda a cada 10 minutos, lê os contadores consolidados, calcula o excedente (`MAX(0, docs_count - plan_included_docs)`) e envia um *Usage Record* idempotente para o **Stripe Metered Billing** (ou gera fatura de excedente no Asaas/Iugu).

---

## 11. Estrutura de Solução Sugerida (.NET 10)

```text
/ibs-cbs-tax-gateway
├── docker-compose.yml                 # Orquestração local: API .NET, Sidecars Java RFB, Postgres, Redis, MinIO, RabbitMQ, Seq
├── SDD.md                             # Este documento de especificação arquitetural
├── /infra
│   ├── /rfb-sidecar-regular           # Dockerfile empacotando ReceitaFederal/calculadora-rtc-regime-regular (R/O)
│   ├── /rfb-sidecar-simples           # Dockerfile empacotando ReceitaFederal/calculadora-rtc-simples-nacional (R/O)
│   └── /xsd-schemas                   # Pacotes XSD oficiais Sefaz (NF-e / NFS-e 2026-2027)
├── /src
│   ├── TaxGateway.Api                 # ASP.NET Core 10 Minimal APIs, Middlewares (Auth Dual, RLS, RateLimit, Idempotency)
│   ├── TaxGateway.Workers             # BackgroundServices (Batch ZIP Processor, Billing Sync, Webhook Dispatcher)
│   ├── TaxGateway.Core                # Domain Entities, Audit Engine, Credit Gap Calculator, LGPD Anonymizer
│   ├── TaxGateway.Infrastructure      # EF Core + Postgres RLS, Redis Lua Scripts, S3/MinIO, HttpClients Tipados (Polly v8)
│   └── TaxGateway.Contracts           # DTOs públicos versionados (v1), RFC 9457 ProblemDetails, OpenAPI specs
└── /tests
    ├── TaxGateway.UnitTests           # Testes de regras de divergência, anonimização LGPD e idempotência
    └── TaxGateway.ContractTests       # Testes de integração (Testcontainers) contra o container Java oficial da RFB
```

---

## 12. Plano de Execução Incremental (Roadmap para Claude Code)

Execute exatamente nesta ordem para garantir build limpo e testes passando em cada etapa:

* **Fase 1 (Infraestrutura & Segurança Base):** Criar a solução `.NET 10` (`TaxGateway.sln`), `docker-compose.yml` (Postgres 17, Redis 7, MinIO, RabbitMQ, Seq e os dois containers da Receita Federal), migrations SQL com RLS e particionamento, e o `TenantContextMiddleware`.
* **Fase 2 (Autenticação Dual & Rate Limiting):** Implementar o servidor de tokens `POST /oauth/token` (RS256 + JWKS), o handler de `X-Api-Key` com cache Redis, e o middleware de Rate Limiting via Redis Lua Script respeitando os limites dinâmicos de cada `plan_tier`.
* **Fase 3 (Anti-Corruption Layer & XSD Validator):** Implementar a validação nativa de Schemas XSD da NF-e/NFS-e em C# (`XmlSchemaSet`), o serviço de anonimização LGPD (`HMAC-SHA256` em `<CPF>`), e o `RfbTaxEngineAdapter` com Polly v8 (Circuit Breaker + Bulkhead separado para Sync e Batch).
* **Fase 4 (Endpoints Síncronos, Workers de Lote & Webhooks):** Implementar `/v1/calculo/simular`, `/v1/auditoria/xml`, o pipeline de upload `.zip` via stream para MinIO + processamento paralelo com `System.Threading.Channels` no `TaxGateway.Workers`, e o disparador de Webhooks assinados com HMAC e retry exponencial.
* **Fase 5 (Metered Billing & Testes de Contrato):** Implementar a deduplicação atômica de cobrança, o `BillingSyncWorker` (Redis -> Postgres -> Stripe/Asaas) e a suíte de testes de integração (`Testcontainers`) validando isolamento RLS entre tenants e contratos JSON contra os sidecars Java da Receita Federal.