# Runbook: incidente de segurança / dados pessoais

**Quando:** suspeita de acesso indevido, vazamento de credencial ou chave, exposição de dados de tenant, falha de isolamento.
Papel do TaxGateway: **operador** — quem comunica a ANPD e os titulares é o **controlador** (cliente), com o nosso apoio.
Prazos legais e de contrato: **confirmar com o jurídico** (o DPA ainda é rascunho; ver `docs/lgpd/dpa-rascunho.md`).

## 1. Conter (primeira hora)
* **Credencial de tenant vazada:** revogar a API key (`DELETE /v1/api-keys/{id}` pelo tenant) ou o cliente OAuth
  (`DELETE /admin/v1/oauth-clients/{id}`); em abuso ativo, suspender o tenant (`POST /admin/v1/tenants/{id}/suspend`, < 5 s).
* **Chave de assinatura OAuth vazada:** [rotacao-chave-oauth.md](rotacao-chave-oauth.md), seção "Em caso de vazamento".
* **Pepper de API key vazado:** todas as chaves precisam ser reemitidas (o pepper invalida a verificação de todas). Plano com o comercial antes de executar.
* **Chave de operador (Admin API) vazada:** remover o hash da configuração `Admin:Operators` e fazer rollout.
* **Acesso indevido ao banco/S3:** girar as credenciais no secret manager; preservar logs de acesso do S3 e do banco.

## 2. Preservar evidências
Não apagar logs, pods ou volumes envolvidos. Exportar logs de acesso (S3, banco, ingress) do período. Anotar linha do
tempo em UTC desde a primeira evidência.

## 3. Avaliar
* Quais tenants? Quais dados? Há dado pessoal (CPF em claro só existe dentro de XML retido; metadados guardam HMAC)?
* Os dados estavam cifrados (XML por tenant, A1 em envelope)? A chave também foi exposta?
* Risco ou dano relevante aos titulares? (critério do controlador perante a ANPD)

## 4. Comunicar
* **Controladores afetados:** sem demora injustificada (meta proposta no DPA: até 24 h da confirmação), com natureza
  dos dados, titulares afetados (estimativa), medidas tomadas e contato.
* O controlador comunica a ANPD e os titulares quando houver risco ou dano relevante. A regulamentação da ANPD sobre
  comunicação de incidentes (Resolução CD/ANPD nº 15/2024) fixa prazo de **3 dias úteis** para o controlador —
  *confirmar a norma vigente com o jurídico no momento do incidente.*

## 5. Corrigir e aprender
Corrigir a causa, adicionar teste de regressão (os testes de segurança do projeto falham o build), registrar a decisão
em `docs/decisoes.md` e revisar o RIPD.
