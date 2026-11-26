# RIPD — Relatório de Impacto à Proteção de Dados (RASCUNHO TÉCNICO)

> **Status:** rascunho técnico preparado pela engenharia em 25/11/2026 (D-H06). **Não é o documento final.** Precisa de
> revisão e assinatura do jurídico e do encarregado (DPO) antes de ser publicado (pendência humana do Hardening, SDD §12).
> Base: SDD §4 e o código nesta revisão.

## 1. Identificação
* **Tratamento:** cálculo e auditoria de IBS/CBS/IS sobre documentos fiscais eletrônicos (NF-e, NFC-e) enviados pelos clientes.
* **Papel do TaxGateway:** **operador** (LGPD art. 5º, VII). **Controlador:** cada cliente (tenant). SDD §4.3.
* **Finalidade:** prestar o serviço contratado (simulação de tributos, auditoria de documentos, apuração de créditos).
* **Base legal (do controlador):** cumprimento de obrigação legal/regulatória (art. 7º, II) e execução de contrato (art. 7º, V). *A confirmar pelo jurídico do controlador.*

## 2. Dados pessoais envolvidos
| Dado | Onde aparece | Como é tratado |
|:--|:--|:--|
| CPF do destinatário pessoa física | `<dest><CPF>` do XML | Em metadados vira `HMAC-SHA256(k_tenant, cpf)`; `k_tenant` fica no cofre (KMS), **fora do banco** |
| Nome, endereço, e-mail do destinatário PF | `<dest>` do XML | **Não** persistidos em metadados; só dentro do XML bruto, se o tenant optar por retenção |
| CNPJ de empresário individual / MEI | emitente ou destinatário | Tratado como CNPJ (identifica a pessoa jurídica; pode identificar o empresário) |
| Dados de simulação | corpo de `/v1/calculo/simular` | Não contêm dado pessoal por desenho (itens, valores, NCM, município) |
| Certificado A1 do cliente (Fase 9) | upload do tenant | Envelope (DEK AES-256-GCM + KEK do tenant), decifrado só em memória |

Não há dado pessoal sensível (art. 5º, II) previsto no fluxo.

## 3. Ciclo de vida
1. **Coleta:** o tenant envia o XML pela API (TLS). Nenhuma coleta direta do titular.
2. **Processamento:** validação XSD e de assinatura; cálculo no motor oficial da RFB (executado **dentro** da nossa infraestrutura; nada é enviado à RFB).
3. **Armazenamento:**
   * metadados e achados no Postgres (região Brasil), com RLS por tenant;
   * XML bruto: **não é gravado** no padrão `zero_retention`; com retenção contratada (30/90/365 dias/5 anos), vai cifrado por tenant para o S3.
4. **Eliminação:** expiração pelo ciclo de vida do S3; expurgo a pedido do controlador (`/v1/titulares/expurgar`, D-H05); no encerramento do contrato, destruição da KEK do tenant (*crypto-shredding*).

## 4. Medidas de segurança (implementadas e testadas)
| Medida | Evidência |
|:--|:--|
| Isolamento por tenant com RLS forçado em todas as tabelas; nenhum role de runtime com BYPASSRLS | `RlsIsolationTests` (matriz tabela × role) |
| CPF pseudonimizado com chave fora do banco | `CpfAnonymizer`, `TitularTests` |
| Cifragem por tenant no S3 e no certificado A1 (envelope) | `XmlVault`, `OpenBaoKeyVault` |
| `zero_retention` como padrão | D-308, `GoldenXmlTests` |
| XML endurecido (sem DTD/XXE, limites de tamanho, anti-wrapping de assinatura) | `XmlSecurityTests` |
| Proteção SSRF nos webhooks | `BatchAndWebhookSecurityTests` |
| Autenticação forte (API key com HMAC + pepper; OAuth com Argon2id), revogação e suspensão em < 5 s | `IdentityTests` |
| Backup com PITR testado | `infra/backup/pitr-drill.sh` |
| Logs sem corpo de XML, CPF ou nome | SDD §4.3 *(o redactor no pipeline de telemetria ainda não está implementado — ver riscos)* |

## 5. Direitos do titular
O titular exerce seus direitos junto ao **controlador**. O operador dá suporte técnico: `POST /v1/titulares/localizar`
e `POST /v1/titulares/expurgar` (escopo `titulares:manage`), com registro de cada solicitação em `titular_requests`
(sem o CPF em claro).

## 6. Riscos e tratamento
| Risco | Prob. | Impacto | Tratamento | Risco residual |
|:--|:--|:--|:--|:--|
| Vazamento do banco expõe CPFs | Baixa | Alto | HMAC com chave fora do banco: sem a chave, força bruta sobre 11 dígitos não é viável | Baixo |
| Acesso de um tenant a dados de outro | Baixa | Alto | RLS forçado + testes que falham o build | Baixo |
| Vazamento do bucket S3 | Baixa | Alto | Cifragem por tenant; bucket privado; URLs pré-assinadas de 5 min | Baixo |
| XML com dado pessoal retido além do necessário | Média | Médio | `zero_retention` padrão; retenção explícita e com prazo; expurgo a pedido | Baixo |
| Dado pessoal em logs/telemetria | Média | Médio | Política de não logar corpo; **falta o redactor automático no OpenTelemetry** | **Médio — pendente** |
| Comprometimento da chave de assinatura OAuth | Baixa | Médio | Rotação com runbook; revogação por cliente/tenant | Baixo |

## 7. Pendências para a versão final
* Revisão jurídica e do DPO; confirmação das bases legais com controladores-modelo.
* Implementar e testar o redactor de CPF/CNPJ na telemetria (SDD §4.3).
* Pentest externo (Hardening) e anexar o relatório.
* Lista de suboperadores definitiva (provedor de nuvem, Asaas) no DPA.
