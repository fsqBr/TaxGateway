# Novidades do TaxGateway

Todas as mudanças relevantes para quem usa o TaxGateway. A versão em uso aparece no portal, no header
`X-TaxGateway-Version` de toda resposta da API e em `GET /version`. Formato: uma seção por versão, com
**Novidades**, **Melhorias**, **Correções** e **Mudanças na API** (esta última sempre avisa o que pode afetar integrações).

> Este arquivo é a fonte do aviso de novidades exibido no portal e da página `/ajuda/novidades`. Ao lançar uma versão:
> atualize `VERSION`, `web/package.json` e o `Chart.yaml`, e acrescente a seção aqui (o CI confere que as versões batem).

## [0.11.0] - 2026-11-26

### Novidades
- **Conta de pessoa com verificação em duas etapas**: cadastro grátis em `/cadastro` (empresa no plano Sandbox), e-mail confirmado, senha e app autenticador obrigatório, códigos de recuperação, convite de equipe com papéis (dono, administrador, fiscal, desenvolvimento, financeiro, leitura) e troca entre empresas. API keys ficam só para sistemas.
- **Planos e cobrança pela Stripe**: página `/planos`, assinatura com cartão, portal de faturas e franquia com excedente; o Sandbox tem franquia sem excedente.
- **Documentos auditados**: histórico com filtros, detalhe com a comparação por item (campo, declarado, referência e fundamento) e exportação CSV para planilha.
- **Classificação tributária**: CST e cClassTrib candidatos por NCM ou NBS, com descrição oficial e Imposto Seletivo na data.
- **Avisos** no sino do portal e por e-mail: franquia em 80% e esgotada, webhook pausado, certificado A1 vencendo e mudança de alíquota aprovada.
- **Primeiros passos** na Visão geral e **XML de exemplo** com o CNPJ da sua empresa para a primeira auditoria.
- Simulação por **formulário** (o JSON da API continua disponível); lotes acompanhados automaticamente, com progresso.
- Portal usável no **celular** (menu recolhível e tabelas em cartões).
- Backoffice com detalhe do cliente, troca de plano, paginação, operadores com conta e MFA e trilha de auditoria das ações.

### Melhorias
- Webhooks justos entre clientes e pausa automática de endpoint que falha em sequência (retoma sozinho).
- Revogação de certificado (LCR) verificada também em produção e igual em todas as instâncias.
- Desligamentos e implantações sem erro para o cliente; limites de memória e de conexões; métricas e rastreamento (OpenTelemetry).
- Atualizações da plataforma só depois de todos os testes, com o mesmo artefato em homologação e produção.

### Correções
- Repetir um envio de lote com a mesma `Idempotency-Key` devolve o mesmo lote (antes podia duplicar).
- Reprocessamento gratuito só para nota já auditada com o mesmo conteúdo; XML alterado com a mesma chave é auditado de novo.
- `assinatura=opcional` passou a funcionar; `/oauth/token` com credencial errada responde 4xx (antes, 500).
- Cadastro automático de CNPJ sem recusa falsa sob concorrência; faturamento sem cobrança em dobro.
- Entradas inválidas (competência, retenção, chave de upload, JSON do lote) respondem 4xx, nunca 500.

### Mudanças na API
- **Campo desconhecido** no JSON de `POST /v1/calculo/simular` agora responde `400 ERR_CAMPO_DESCONHECIDO` (antes era ignorado). Confira a grafia dos campos.
- `data_fato_gerador` precisa estar entre 2026 e 2100 (`400`, código `FORA_DO_INTERVALO`); NCM, NBS, CST e cClassTrib aceitam só dígitos 0–9.
- `POST /v1/webhooks` recusa evento que não existe (`400`); eventos válidos: `document.audited`, `batch.audit.completed`, `dfe.document.audited`, `certificate.expiring` e `*`.
- Rota inexistente em `/v1` responde `404` em `application/problem+json` (`ERR_ROTA_INEXISTENTE`).
- API key criada sem `scopes` recebe só `simulacao:write` e `auditoria:write`; uma chave que expira só cria chaves que expiram até a mesma data.
- `storage_object_key` do upload de lote agora começa com `uploads/` (use o valor devolvido por `upload-url`, como antes); uploads não usados são apagados em 24 h.
- Planos com franquia rígida (Sandbox) respondem `402 ERR_FRANQUIA_ESGOTADA` ao esgotar a franquia do mês.
- Novos: `GET /v1/auditoria/documentos`, `GET /v1/auditoria/documentos/{id}`, `GET /v1/auditoria/documentos/exportar`, `GET /v1/auditoria/resumo`, `GET /v1/cnpjs/{cnpj}/dfe/documentos`, `GET /v1/classificacao`, `GET /v1/avisos`, `GET /v1/primeiros-passos` e o escopo `auditoria:read`.

## [0.10.0] - 2026-11-26

### Novidades
- Central de ajuda em `/ajuda`, com passo a passo ilustrado, exemplos de código em cURL, C#, Node.js e Python, tabela de erros e glossário.
- Seção **Veja funcionando** no site, com telas reais do portal.
- Aviso de novidades: ao mudar de versão, o portal mostra o que mudou; se uma versão nova for publicada com a aba aberta, aparece o convite para recarregar.
- Versão visível no portal e na API (`GET /version` e header `X-TaxGateway-Version`).
- Captura automática de DF-e com certificado A1, Ciência da Operação e auditoria da entrada.
- Portal do cliente, backoffice e referência interativa da API em `/docs`.

### Melhorias
- Referência da API com exemplos reais de resposta, códigos de sucesso corretos e erros documentados.
- O detector de versões do motor da Receita passa a perceber tag republicada.

### Correções
- Login no portal só redireciona para páginas internas (proteção contra redirecionamento aberto).
- Timeouts de rede dos motores usam o relógio real.
- Cadastrar webhook com endereço que não existe no DNS agora responde `422 ERR_WEBHOOK_URL_BLOQUEADA` (antes, erro interno).
- Código de erro de armazenamento indisponível unificado em `ERR_STORAGE_INDISPONIVEL`.

### Mudanças na API
- Nenhuma mudança incompatível. Novos: `GET /version`, `GET /v1/me`, `GET /v1/api-keys`, `GET /v1/auditoria/lotes`.
