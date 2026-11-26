# Revisão multidisciplinar do TaxGateway (26/11/2026)

Pedido do usuário: olhar o sistema como **arquiteto**, **segurança da informação**, **produto**, **design (jornadas)** e
**QA**, procurar brechas de funcionalidade e melhorias nas que já existem. Cinco revisões independentes, feitas sobre o
código, os documentos e a pilha local em execução (testes reais via API e navegador). Os achados marcados com ✔ foram
reproduzidos ou conferidos no código depois da revisão; os demais são leitura de código com evidência citada.

> Nada foi corrigido nesta etapa — o documento é o diagnóstico. As correções entram como decisões em `docs/decisoes.md`.

> **Situação em 26/11/2026 (v0.11.0):** aplicados todos os **P0** (D-P01..D-P12), os **P1 de plataforma, segurança e
> observabilidade** (D-P13..D-P31), os P1 de produto e jornadas — contas com MFA e papéis (D-U01..D-U10), planos e
> cobrança pela Stripe (D-C01..D-C09), histórico de documentos, classificação por NCM/NBS, avisos por e-mail, primeiros
> passos, primeira auditoria guiada e comparação por item (D-J01..D-J08) — e parte do P2 de QA (contrato estrito, 404
> em problem+json). **Ficam abertos:** os itens que dependem de recurso externo (conta Stripe real, cluster para o
> canário, OIDC do GitHub e assinatura de imagens, homologação de DF-e, pentest, revisão jurídica), o digest do motor
> por documento (Arq 13), os tipos do front gerados do OpenAPI (D-J08) e as funcionalidades de mercado do P2 (NFS-e,
> CT-e, apuração assistida, simulador de preço, laudo PDF, regras configuráveis por cliente).

---

## 1. Resumo executivo

**O núcleo é forte.** Isolamento entre clientes (RLS forçado, sem BYPASSRLS, testado), XML seguro (XXE, wrapping,
zip bomb, ZIP aninhado — todos resistiram a ataque real), SSRF verificado na conexão, criptografia por envelope com
chave por cliente, regras de auditoria com fundamento legal, ledger de cobrança que bateu exatamente com o uso,
golden de 518 casos contra o motor oficial e um registro de decisões exemplar.

**Os problemas estão em três lugares:**

1. **Na fronteira entre código e operação.** O chart do Kubernetes não reflete o que o código e os runbooks assumem.
   Como nunca houve cluster, o primeiro staging vai expor tudo de uma vez: o Simples Nacional inteiro responderia 503,
   o canário nunca recebe tráfego, a verificação de revogação de certificado não sai do pod, o motor fica aberto ao
   cluster e o plano administrativo mora no mesmo pod público.
2. **Nos caminhos assíncronos e de concorrência.** Lotes (idempotência ignorada, lease sem cerca, ingestão inteira em
   memória), faturamento (pode cobrar em dobro no provedor), webhooks (um cliente lento trava todos) e o
   auto-cadastro de CNPJ (403 falso sob concorrência) não têm a disciplina do caminho síncrono.
3. **No produto em volta da API.** Hoje é uma API para desenvolvedor com um console. Não há como um cliente **entrar
   sozinho** (cadastro, trial, preço), o fiscal **não consegue rever uma auditoria** depois de sair da tela, não há
   usuários humanos (login é por API key), e faltam documentos que o mercado exige em 2026–2027 (NFS-e nacional,
   consulta de cClassTrib por NCM).

### Quadro de saúde

| Área | Estado | Por quê |
|:--|:--|:--|
| Isolamento entre clientes e XML | 🟢 | RLS e defesas de XML/ZIP/SSRF resistiram a ataque real |
| Cálculo (simulação) | 🟢/🟡 | Golden forte; aceita campo digitado errado sem aviso, dígitos Unicode, datas sem limite |
| Auditoria de XML | 🟡 | `assinatura=opcional` não funciona; `reprocess` não cobra nunca; replay ignora o conteúdo |
| Lotes | 🔴 | Idempotência ignorada; lease sem cerca; ingestão em memória (OOM); 500 no modo 2 |
| Cobrança | 🟡 | Ledger exato; faturamento pode duplicar no provedor; preços zerados |
| Webhooks | 🟡 | SSRF bom; sem justiça entre clientes; aceita evento inexistente |
| Identidade e acesso | 🟡 | Escopos e IDOR ok; escopo padrão = tudo; cache de auth no Valkey sem integridade; `/oauth/token` dá 500 |
| Deploy e Kubernetes | 🔴 | Simples ausente no pod, canário sem tráfego, LCR bloqueada, admin no pod público |
| Observabilidade | 🔴 | Métricas próprias não exportadas; sem tracing nem log estruturado |
| CI e processo de teste | 🟡/🔴 | E2E, smoke, carga, cobertura e format fora do CI |
| Produto e funil | 🔴 | Sem cadastro/trial/preço, sem histórico de auditorias, sem usuários |
| Jornadas e UI | 🟡 | Visual consistente; primeiro uso falha, mobile fraco, confirmações genéricas |

---

## 2. Prioridades consolidadas

Esforço: **P** = horas a 1 dia · **M** = 1 a 3 dias · **G** = uma semana ou mais. Origem: Arq, Seg, Prod, UX, QA.

### P0 — bloqueia staging/produção ou perde dinheiro (corrigir antes de qualquer cliente)

| # | Achado | Origem | Esforço |
|:--|:--|:--|:--|
| 1 | ✔ **Pod da API sem o motor do Simples**: `api-rollout.yaml` só inclui o sidecar `regular`; o adapter do Simples aponta para `127.0.0.1:8082`, onde nada escuta. Todo cálculo de CRT 1/2 e MEI vira 503 em Kubernetes (o compose sobrescreve a URL, por isso os testes não pegaram). Incluir o sidecar e um teste de render do Helm que exija URL para cada motor | Arq | P |
| 2 | ✔ **Canário sem tráfego**: Rollout sem `trafficRouting`; o nginx aponta para o Service estável; a análise mede `api-canary`, por onde ninguém passa (vetor vazio → aborta ou passa no vazio). Ingress da API com traffic routing do Argo, ou Service `api-all` + `setCanaryScale`, e análise com volume mínimo | Arq | M |
| 3 | ✔ **Lote ignora a `Idempotency-Key`** (só confere que existe): repetir após timeout duplica o lote, o processamento e os eventos. Usar o `IdempotencyStore` como nas demais rotas | QA | P |
| 4 | ✔ **`reprocess=true` em documento nunca auditado não cobra**: qualquer cliente audita tudo de graça. Aceitar `reprocess` só quando houver auditoria anterior; incluir a query no hash de idempotência | Seg | P |
| 5 | ✔ **`/oauth/token` responde 500 a credencial inválida** (texto com acento no header `WWW-Authenticate`): qualquer anônimo gera 5xx, queima o error budget e reprova a análise do canário. Mensagens ASCII e teste de contrato do 401 | Seg | P |
| 6 | ✔ **403 falso no auto-cadastro de CNPJ sob concorrência** (`ON CONFLICT DO NOTHING` devolve 0 linhas para quem perde a corrida). Reler `is_active` quando vier 0 | QA | P |
| 7 | **Faturamento roda em todas as réplicas de workers** sem exclusão mútua; o provedor faz "busca e depois cria" → cobrança duplicada no Asaas em deploy/reinício. `pg_try_advisory_lock` + estado `CHARGING` | Arq | P |
| 8 | **Lotes sem cerca no lease**: chunk que passa de 600 s é reprocessado, `done_chunks` ultrapassa o total e o lote fica preso em RUNNING sem evento. Fencing por `attempts`, heartbeat, claim só de `QUEUED`, conclusão atômica e reconciliador | Arq | M |
| 9 | **Ingestão de lote inteira em memória** (até 10× o limite do plano — 1 GB no GROWTH — num pod de 1 GiB) e chunk por contagem nos workers (até 2,5 GB). Um upload legítimo derruba o pod da API e as simulações de todos. Streaming, chunk por bytes, ingestão fora da API | Arq, Seg | M |
| 10 | **Cache de autenticação e de simulação no Valkey sem integridade**: quem escreve no Valkey troca o tenant de uma chave e ganha todos os escopos (PoC reproduzida), tira suspensões e envenena resultados fiscais compartilhados. HMAC nas entradas, ACL+TLS, NetworkPolicy; revogação com o banco como fonte de verdade | Seg | M |
| 11 | **`?assinatura=opcional` não funciona** (o XSD exige `Signature` e roda antes) e o cliente é cobrado pelo `SCHEMA_ERROR` | QA | P |
| 12 | **Entradas inválidas que viram 500**: `/v1/usage?mes=abc`, lote com JSON inválido, `storage_object_key` inexistente/numérica/com `../`, `retencao_xml_dias` negativo. Matriz de testes "nunca 500 por entrada do cliente" | QA, Seg | P |

### P1 — antes do primeiro cliente pagante

**Plataforma e segurança**
- **Separar o plano administrativo** (Arq 7, Seg B2/B8): Deployment `api-admin` próprio, Secrets por workload, NetworkPolicy default-deny com ingress só do backoffice, **log de auditoria das ações de operador** (hoje criar tenant, cliente OAuth, suspender e revogar não deixam rastro) e rate limit/lockout no `X-Admin-Key` (30 tentativas seguidas, nenhum 429). **M/G**
- **Motor aberto ao cluster** (Arq 6): sidecar escuta em 0.0.0.0 e não há NetworkPolicy de entrada no pod da API — qualquer pod calcula sem auth. `SERVER_ADDRESS=127.0.0.1` + ingress policy. **P**
- **Revogação por LCR bloqueada pela NetworkPolicy** (Arq 8, Seg B4): em produção toda nota sai "revogação não verificada", e o resultado varia por pod. Job/worker com egress em allowlist para os hosts da ICP-Brasil, cache compartilhado e métrica de idade. **M**
- **Escopo padrão = todos** (Seg A4): `scopes: []` cria chave e cliente OAuth com `titulares:manage` (expurgo irreversível), `dfe:manage` e `keys:manage`. Padrão mínimo; sensíveis só explícitos. Chave que expira não pode criar chave eterna (A5). **P**
- **Travas de produção** (Seg B7, Arq 17): falhar ao subir em Production com KMS local, pepper/master key de dev, CA de teste, `AllowInsecureForTests`, `AllowEphemeralKeys`; tirar `appsettings.Development.json` da imagem; KMS com circuit breaker e 503. **P**
- **Pré-autenticação sem limite** (Seg B6, Arq 22): `/oauth/token` roda Argon2id de 19 MiB por tentativa sem limite (≈1 GB com 50 concorrentes); chaves falsas fazem uma consulta ao banco cada. `limit_req` no nginx/ingress, semáforo do Argon2, rate limit por IP. **P/M**
- **`/metrics` na porta pública forjando `Host: x:9464`** (Seg A6) e **issuer OAuth derivado do Host** (A7). Endpoint de métricas por porta local; issuer fixo. **P**
- **Uploads do modo 2 ficam no S3 sem retenção** quando o lote é recusado, sem limite de tamanho no presign e fora do expurgo LGPD (Seg B3). Lifecycle de 1 dia, apagar na recusa, `content-length-range`. **P**
- **Supply chain** (Seg B5): kubeconfig de produção estático usado por action de terceiro por tag; binário baixado sem sha256; imagens base por tag. OIDC do GitHub para o cluster, actions por SHA, imagens por digest e assinadas. **M**
- **SSRF**: bloquear NAT64/6to4/Teredo/faixas de documentação (Seg A8). **P**

**Resiliência e observabilidade**
- **Métricas próprias não exportadas, sem tracing, sem log JSON** (Arq 10): o `Meter("TaxGateway")` (descarte de simulação = subcobrança, 429, modo local do rate limit) nunca sai. `AddMeter`, OTel tracing, log JSON com `trace_id`/`tenant_id`, e medidores de circuit breaker, cache hit, fila do writer, KMS. **P/M**
- **Readiness depende do Postgres** (Arq 9): um failover do banco tira 100% dos pods do balanceador. Readiness só com dependências locais. **P**
- **Resiliência do motor amplifica sobrecarga** (Arq 11): limitador por fora do timeout (fila sem teto, ~18 s), retry de timeout triplica carga no pico, `Retry-After: 1` fixo com CB de 15 s. **P**
- **Memória sem teto** (Arq 12): `MemoryCache` sem `SizeLimit`, fila do writer limitada por contagem (100 mil × dezenas de KB). **P**
- **Idempotency-Key presa em 409 por 24 h** após queda no meio do processamento (Arq 16). Lease curto. **P**
- **Desligamento**: sem `preStop`, grace period nem sidecar nativo → 5xx a cada deploy/scale-in (Arq 18). **P**
- **Valkey único** mistura cache volátil e estado de segurança (revogações podem ser expulsas por LRU) (Arq 21). **P**
- **Pool de conexões** sem orçamento (até milhares de conexões com HPA 30) (Arq 20). **P/M**
- **Webhooks sem justiça entre clientes** e uma chamada ao KMS por entrega (Arq 14). Limite por tenant/endpoint no claim, CB por endpoint, cache do segredo. **M**

**Deploy e reprodutibilidade fiscal**
- **Deploy desacoplado do CI** (roda com CI vermelho), workers sem canário, migrations sem `lock_timeout` nem regra expand/contract (Arq 19). **M**
- **Imagens dos motores reconstruídas a cada deploy com tag mutável**; `engine_bundles` nunca populada; o bundle gravado no documento vem da configuração, não do motor (Arq 13). Construir uma vez, tag imutável, deploy por digest, gravar o digest por documento. **M**

**Produto (para poder vender)**
- **Caminho de entrada** (Prod L12, UX A1): cadastro self-service com tenant **sandbox** automático, preço e planos (hoje `price_cents = 0`), CTA "Criar conta grátis" e "Falar com vendas", página de planos. **M**
- **Histórico e consulta de auditorias e DF-e** (Prod L4, UX C1): não existe `GET` de auditoria nem listagem de documentos — o DF-e audita sozinho, mas quem não tem webhook nunca vê o resultado. Rotas paginadas e tela "Documentos" com filtros, detalhe por chave e exportação. **M/G**
- **Contas humanas com OIDC + MFA e papéis**, convite de usuários e, para escritórios, troca de empresa (Prod L11). **M**
- **Consulta de cClassTrib/CST por NCM/NBS** e modo "sugerir" na simulação (Prod L1) — maior dor de integração e diferencial contra a calculadora gratuita. **M**
- **Alertas de mudança fiscal + e-mail transacional** (Prod L9/L15): `params.rate.changed`, `engine.bundle.updated`, certificado vencendo, webhook morto, franquia em 80%. A CBS 2027 ainda pode mudar até 15/12. **P**
- **Termos de Uso, Privacidade, DPA e contato** na landing e no rodapé (UX A5, Prod). **M (conteúdo)**

**Jornadas (primeiro valor)**
- **Primeira auditoria falha com erro técnico** (`ERR_CNPJ_NOT_PROVISIONED`) sem caminho (UX B1): mensagem com ação "Cadastrar este CNPJ", XML de exemplo, dropzone em pt-BR (o input nativo mostra "Choose File"). **M**
- **Onboarding na Visão geral vazia** (UX B2): checklist "Comece por aqui". **M**
- **Comparação declarado × recalculado por item e campo** (UX C2): a landing promete "Regra, Campo, Declarado, Referência, Fundamento", o portal mostra só totais. **M**
- **Sessão expirada (401) e rota inexistente** (UX F1/F2): hoje a tela fica cheia de erros soltos ou em branco. **P**
- **Confirmações destrutivas** (UX F3/D2): `window.confirm("Revogar?")` em revogar chave, suspender tenant, fechar faturamento e **expurgo LGPD irreversível**. Modal com objeto, impacto, motivo e "digite para confirmar". **M**

### P2 — evolução (próximos meses)

**Funcionalidades de mercado (Prod)**
- **NFS-e nacional**, obrigatória para os municípios desde jan/2026 e base do IBS/CBS sobre serviços; depois a captura pelo ADN. **G**
- **CT-e**, depois NF3e/NFCom/BP-e, sob demanda. **G cada**
- **Painel de débitos e créditos por competência** e, depois, **conciliação com a apuração assistida** da RFB/CGIBS, a "killer feature" de 2027. **G**
- **Simulador de preço/markup e Simples × regime regular.** **M**
- **Exportações**: XLSX/CSV por período e **laudo PDF** por documento ou competência. **M**
- **Regras configuráveis por cliente**: a tabela `tenant_audit_rule_settings` existe sem rota.
- **`sugestao_correcao` por ocorrência**; ativar a R024 (hoje morta).
- **Manifestações conclusivas do DF-e**: Confirmação, Desconhecimento, Operação não realizada.
- **OEM**: provisionamento em massa, relatório por cliente final, white-label.
- **SDKs** (.NET, Java, Node, **Delphi**), coleção Postman e **status page** com SLA.
- **Métricas de produto**: tempo até a primeira chamada, % ativação em 7 dias, R$ em divergência entregue por cliente, taxa de INDETERMINADO por motivo, health score no backoffice.

**Qualidade de API (QA)**
- **Replay por chave ignora o conteúdo do XML**: um XML adulterado com a mesma chave recebe "CONFORME". Comparar o hash e expor `xml_sha256`.
- **Campos desconhecidos ignorados**: `aliquotas_nominal` digitado errado passa e usa o catálogo. Recusar ou avisar.
- **Validação mais estrita**:
  - `\d` aceita dígitos Unicode;
  - faltam limites de data e valor;
  - simulação 100% inconsistente é cobrada (decisão de produto);
  - webhooks aceitam evento inexistente, URL de 5.000 caracteres e duplicatas;
  - listagens sem paginação.
- **Contrato inconsistente**:
  - `total` × `total_documentos`;
  - `eventos` × `events`;
  - `mes` × `month`;
  - códigos de lote sem `ERR_`;
  - 404 sem problem+json;
  - `Content-Type` ignorado.

**Arquitetura de escala (Arq)**
- **Custo de `simulations`**: request e resultado completos por 13 meses, dezenas de TB no cenário OEM. Guardar hash e resumo, mandar o bruto para S3 e reter por plano.
- **Tabelas que só crescem**: outbox, entregas, chunks.
- **Agregações do ledger sob demanda**: rollup e `statement_timeout`.
- **Claim de lotes O(N)** e prioridade de plano quase inócua.
- **Lote em comboio**, e uma falha do motor cancela o slot.
- **RabbitMQ como desvio** Postgres→Rabbit→Postgres: remover ou usar de verdade.
- **Todo o tráfego da API passa pelo nginx do site**: Ingress próprio, `proxy_request_buffering off` nos lotes, PDB e HPA.
- **Limites de módulo**: os workers referenciam o projeto da API; extrair `TaxGateway.Application`.
- **Rate limit cego ao custo**: 1 token por requisição de 1 ou 500 itens.
- **Workers sem probes**: o consumidor do RabbitMQ pode ficar mudo.
- **Política de versão da API e do bundle**: `Deprecation`/`Sunset`, janela N/N-1 de bundle, troca de bundle com diff no golden em vez de canário por latência.

**Processo de teste (QA)**
- **E2E e smoke no CI**: compose + `stack_smoke.py` + Playwright.
- **Gates no CI**: `dotnet format`, `npm audit` e `dotnet list package --vulnerable`.
- **Cobertura** com piso.
- **Tipos do front gerados do OpenAPI**: hoje cerca de 20 interfaces escritas à mão já divergem.
- **Suíte "API de borda"**.
- **Gate automático na regressão fiscal** do `fiscal-update`.
- **Motores construídos uma vez por run.**
- **Mutation testing** nas regras.
- **Carga noturna** com orçamento de p95.
- **Corpus de XMLs reais anonimizados.**
- **Testes ausentes**:
  - 24 dos 35 códigos `ERR_*` sem asserção;
  - regras I001 e I003–I007 sem teste;
  - telas operacionais do portal e do backoffice sem teste.

**Jornadas e UI (UX)**
- **Simular por formulário**, além do JSON, com alíquota e fonte por tributo e "copiar como cURL".
- **Lotes**:
  - acompanhamento automático;
  - detalhe por nota;
  - limpar o arquivo após enviar (hoje convida a reenviar).
- **API keys**:
  - escopos em lista com descrição;
  - presets ("ERP: simular + auditar").
- **Webhooks**:
  - "enviar evento de teste";
  - detalhe da entrega;
  - validar `https://` no cliente.
- **Consumo**:
  - histórico de 6 meses;
  - prévia da fatura em R$;
  - exportação;
  - explicar o cache de 60 s.
- **CNPJ**: máscara e validação de dígito.
- **DF-e**: seletor de CNPJ cadastrado e de UF por nome, em vez de código IBGE.
- **Backoffice — tenants**:
  - lista primeiro, com paginação e filtros;
  - detalhe 360° do tenant;
  - motivo e trilha da suspensão.
- **Backoffice — telas sem permissão**: mostrar um estado claro (hoje Alíquotas mostra dois erros e o formulário ativo).
- **Backoffice — faturamento**:
  - estado "desligado" antecipado;
  - resumo antes de fechar;
  - divergências em tabela.
- **Central de ajuda**:
  - trilha **"Para o fiscal"** (entender o resultado, catálogo de regras R0xx, auditar sem programar);
  - artigos de conta e planos;
  - contato real;
  - link de ajuda contextual em cada tela do portal.
- **Mobile**:
  - drawer de navegação (hoje cerca de 480 px de menu antes do conteúdo);
  - tabelas em cards;
  - overflow da chave de 44 dígitos e dos artigos.
- **Acessibilidade**:
  - skip link;
  - `aria-describedby` nas dicas;
  - anúncio de resultado após Auditar e Simular;
  - trap de foco no modal;
  - setas nas abas;
  - rótulos contextuais em botões repetidos.
- **Microcopy**:
  - enums crus (OUTBOUND, PENDING, PROPOSED);
  - "documento(s)";
  - "Crédito potencial de 0.00".
- **Alternador de tema** Claro/Escuro/Sistema.

---

## 3. Mapa das jornadas (o que existe × o que falta)

| Jornada | Existe | Falta |
|:--|:--|:--|
| Descobrir o produto | Landing, "Veja funcionando", segurança, docs, ajuda | Proposta para o fiscal, "para quem", prova/confiança, termos e privacidade |
| Contratar / começar | — | Cadastro, trial/sandbox, preço, contato comercial |
| Primeiro acesso | Login por API key | Onboarding, XML de exemplo, erro de CNPJ com ação |
| Simular | JSON, totais, avisos | Formulário, detalhe por item/alíquota/fonte, cURL |
| Auditar uma nota | Upload, totais, ocorrências | Comparação por item e campo, histórico, permalink, exportar |
| Analisar divergências recorrentes | — | Lista filtrável, marcar como revisada/justificada, relatório |
| Lotes | Envio, lista, CSV | Progresso automático, detalhe por nota |
| Integrar (dev) | API keys, webhooks, docs, ajuda com código | Evento de teste, detalhe de entrega, presets, sandbox explícito, SDKs |
| Consumo e fatura | Cards da competência, uso por CNPJ | Histórico, R$, faturas, exportação |
| CNPJs / DF-e / certificado | Cadastro, PFX, captura | Lista de notas capturadas, manifestações conclusivas, alerta de vencimento no portal |
| LGPD titulares | Localizar, expurgar | Confirmação forte, protocolo do atendimento |
| Conta e acesso | Sair | Usuários humanos, convite, papéis, SSO, recuperar acesso, 401 tratado |
| Backoffice: tenants | Criar, buscar, suspender, OAuth | Detalhe 360°, paginação, motivo e trilha, troca de plano, nova chave |
| Backoffice: faturamento | Fechar, reconciliar, ajuste | Pré-visualização, estado desligado, busca de tenant |

---

## 4. O que já está bom (manter)

- RLS com ENABLE+FORCE em todas as tabelas com `tenant_id`, `set_config` local à transação, roles sem BYPASSRLS, `SECURITY DEFINER` com `search_path` fixo; IDOR testado e bloqueado em chaves, webhooks, lotes e relatórios.
- XML: DTD proibido, XSD embutido, anti-wrapping (`PinnedSignedXml`), algoritmos em lista fechada; ZIP: zip slip neutralizado, bomba e aninhado recusados.
- SQL 100% parametrizado; AES-256-GCM com AAD = caminho do objeto; Argon2id; comparação em tempo constante; segredos exibidos uma vez.
- Webhooks com HMAC + timestamp e IP validado na conexão (anti DNS rebinding); front com CSP estrita, credencial só em header, `RichText` sem HTML.
- Idempotência da simulação e da auditoria unitária correta sob concorrência; consumo bateu exatamente com o uso.
- Golden de 347 simulações + 171 XMLs contra o motor real; testes oficiais da RFB no CI; trilha de duas pessoas para alíquota.
- Imagens não-root, root FS somente leitura, `drop ALL`, seccomp; registro de decisões com motivo e alternativas.

---

## 5. Plano sugerido

| Onda | Conteúdo | Resultado |
|:--|:--|:--|
| **1. Correções P0** (≈1 semana) | Itens 1–12 da tabela P0, cada um com teste de regressão | Pronto para um staging que não cai no primeiro deploy e não perde dinheiro |
| **2. Plataforma para produção** (≈2–3 semanas) | Separação do admin + log de auditoria, NetworkPolicies, LCR, travas de produção, observabilidade, resiliência, deploy por digest condicionado ao CI, E2E/smoke no CI | Primeiro staging real e ensaio do canário |
| **3. Vendável** (≈6–8 semanas, até a virada de 2027) | Cadastro + sandbox + preço; usuários com OIDC; histórico de auditorias e DF-e; cClassTrib por NCM; alertas e e-mail; onboarding e primeiro valor; termos e privacidade; design partners | Funil próprio e clientes piloto |
| **4. Valor fiscal** (T1–T2 2027) | NFS-e nacional, painel de créditos, exportações e laudo, simulador de preço/regime, regras por cliente, SDKs, status page | Produto para o fiscal e o contador |
| **5. Plataforma da apuração** (2º sem. 2027+) | Conciliação com a apuração assistida, CT-e e demais DF-e, split payment, escala (custo de `simulations`, claim de lotes, rollups) | Diferencial competitivo |

**Pendências não técnicas que bloqueiam venda:** Termos de Uso e aviso legal (jurídico), revisão das regras INDICIO
por especialista, RIPD/DPA publicados, pentest externo — precisam de dono e data na onda 3.

**Dados deixados na pilha local pelos testes:** tenants "Teste UX", "Cliente UX 2" (suspenso), "QA …", "SecTest A/B",
um cliente OAuth de teste, lotes e webhooks para example.com. Só no banco de desenvolvimento.
