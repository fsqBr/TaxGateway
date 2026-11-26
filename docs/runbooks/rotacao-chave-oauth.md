# Runbook: rotação da chave de assinatura OAuth

**Quando:** a cada 90 dias (SDD §3.1) ou imediatamente se houver suspeita de vazamento da chave privada.
**Decisão:** D-804. **Duração:** ~30 min, sem janela de manutenção e sem 401 para os clientes.

## Pré-requisitos
* Acesso de escrita ao secret manager do ambiente (o Secret `taxgateway-runtime` é sincronizado pelo ExternalSecrets).
* Acesso de leitura ao Argo Rollouts / `kubectl` do cluster **do TaxGateway** (confira o contexto antes de qualquer comando).

## Passos

1. **Gerar a chave nova** (RSA 2048, PKCS#8):
   ```bash
   openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out oauth-AAAAMM.pem
   ```
   `kid` sugerido: `oauth-AAAAMM` (ex.: `oauth-202702`).

2. **Etapa 1: publicar a nova só para validar.** No secret, mantenha a atual no índice 0 e adicione a nova no índice 1:
   ```
   Identity__SigningKeys__0__Kid = <kid atual>     Identity__SigningKeys__0__Pem = <PEM atual>
   Identity__SigningKeys__1__Kid = oauth-AAAAMM    Identity__SigningKeys__1__Pem = <PEM nova>
   ```
   Faça o rollout e espere **todos** os pods da API ficarem prontos. Confira:
   `curl -s https://<host>/.well-known/jwks.json | jq '[.keys[].kid]'` deve listar as duas.

3. **Etapa 2: inverter a ordem.** A nova vai para o índice 0 (passa a assinar) e a antiga para o 1 (só valida). Rollout.
   Durante o rollout, pods antigos (que já conhecem a nova) e novos (que conhecem a antiga) aceitam os tokens uns dos outros.

4. **Etapa 3: remover a antiga** pelo menos **15 minutos** depois que o passo 3 terminou (TTL do access token). Rollout.

5. **Destruir** o arquivo PEM local e registrar a rotação (data, kid novo, kid removido) no registro de operações.

## Verificação
* Taxa de `401` em `/v1/*` sem pico durante os passos 2–4 (painel da API, série por status).
* `POST /oauth/token` devolve token com `kid` novo no header após o passo 3.

## Em caso de vazamento
Pule a espera do passo 4: remova a chave comprometida assim que a nova estiver assinando em todos os pods. Tokens assinados
pela chave vazada passam a falhar (401) e os clientes pedem token novo — é o comportamento desejado.

## Reversão
Se o passo 3 der problema, volte a ordem do secret para a do passo 2 e faça rollout: as duas chaves continuam publicadas.
