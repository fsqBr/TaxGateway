# DPA — Acordo de Tratamento de Dados (RASCUNHO TÉCNICO)

> **Status:** rascunho técnico (D-H06) com os compromissos que a plataforma **já consegue cumprir**, para o jurídico
> transformar em cláusulas. Não é minuta contratual. Revisão jurídica obrigatória antes do uso (SDD §4.3, §12).

## 1. Partes e papéis
* **Controlador:** o cliente (tenant). Decide finalidade e base legal; atende os titulares.
* **Operador:** TaxGateway. Trata os dados só conforme as instruções documentadas do controlador (contrato + configuração do tenant).

## 2. Objeto
Processamento de documentos fiscais eletrônicos e simulações para cálculo e auditoria de IBS/CBS/IS. Categorias de
dados e titulares: ver o RIPD (§2): destinatários pessoa física (CPF, nome, endereço, e-mail) e empresários individuais/MEI.

## 3. Instruções configuráveis pelo controlador
| Instrução | Como o controlador exerce |
|:--|:--|
| Reter ou não o XML bruto | `retencao_xml_dias` no tenant (padrão: **não reter**, `zero_retention`) |
| Prazo de retenção | 30, 90, 365 dias ou 5 anos |
| Localizar/expurgar dados de um titular | `POST /v1/titulares/localizar` / `expurgar` |
| CNPJs atendidos (OEM) | `/v1/oem/cnpjs` |
| Notificações | webhooks do próprio controlador (HTTPS; IPs privados recusados) |

## 4. Compromissos técnicos do operador
* **Localização dos dados:** Brasil (região de nuvem no país; SDD §16.1).
* **Segurança:** isolamento por tenant (RLS), cifragem por tenant, pseudonimização do CPF com chave fora do banco,
  autenticação forte, backup com PITR (RPO ≤ 5 min, RTO ≤ 1 h). Detalhes e evidências no RIPD §4.
* **Pessoal:** acesso a produção restrito, com chaves de operador individuais e escopo (`admin:fiscal`, `admin:commercial`);
  o backoffice **não lê** documentos de tenant (garantido por privilégio no banco e testado).
* **Suboperadores:** provedor de nuvem (computação, banco, objetos, KMS) e provedor de cobrança (Asaas: só dados de
  faturamento do controlador, nenhum dado de titular). Novos suboperadores com aviso prévio *(prazo: definir)*.
* **Incidentes:** comunicar o controlador **sem demora injustificada** *(meta proposta: até 24 h da confirmação)*, com
  o que se sabe, para o controlador cumprir o prazo regulatório perante a ANPD e os titulares (runbook
  `docs/runbooks/incidente-seguranca.md`).
* **Auditoria:** relatório de pentest e evidências de controles sob NDA; registro de solicitações de titular disponível ao controlador.

## 5. Término
* Ao fim do contrato: suspensão do tenant, prazo para exportação *(definir)*, e então **destruição da KEK do tenant**
  (XMLs no S3 ficam ilegíveis) e eliminação dos metadados, salvo guarda legal obrigatória *(definir com o jurídico qual
  guarda se aplica ao operador)*.
* Backups expiram pelo ciclo de retenção do provedor *(definir prazo e citá-lo aqui)*.

## 6. Pontos para o jurídico decidir
1. Prazo de aviso de incidente ao controlador (proposta: 24 h).
2. Prazo de exportação e de eliminação no término.
3. Responsabilidade e limites; foro.
4. Aviso prévio para novos suboperadores e direito de objeção.
