# Novidades do TaxGateway

Todas as mudanças relevantes para quem usa o TaxGateway. A versão em uso aparece no portal, no header
`X-TaxGateway-Version` de toda resposta da API e em `GET /version`. Formato: uma seção por versão, com
**Novidades**, **Melhorias**, **Correções** e **Mudanças na API** (esta última sempre avisa o que pode afetar integrações).

> Este arquivo é a fonte do aviso de novidades exibido no portal e da página `/ajuda/novidades`. Ao lançar uma versão:
> atualize `VERSION`, `web/package.json` e o `Chart.yaml`, e acrescente a seção aqui (o CI confere que as versões batem).

## [0.12.0] - 2026-11-26

### Novidades
- **Teto de excedente**: nos planos pagos, o que passa da franquia só é cobrado até o teto mensal que você definir em Plano e cobrança. O teto começa em zero — nada além da mensalidade sem sua autorização. Avisos em 80% e ao atingir.
- **Pagamento que falhou**: avisos no dia da falha e nos dias 3 e 7; no dia 10 cálculos e auditorias ficam bloqueados (consultas e exportações continuam); no dia 15 a conta é suspensa. Pagou, volta na hora.
- **Verificação da empresa**: confirme a titularidade do CNPJ com o certificado e-CNPJ, com um código no e-mail do cadastro da Receita ou pelo suporte. Exigida para plano pago e captura de notas (DF-e); enviar o A1 da matriz já verifica.
- **Contestar a titularidade**: se outra pessoa cadastrou o CNPJ da sua empresa, prove com o e-CNPJ em Minha conta; o suporte transfere a empresa.
- Backoffice: teto e verificação manual no detalhe do cliente e página **Disputas de CNPJ**.

### Mudanças na API
- `POST /v1/auditoria/xml?reprocess=true` **passa a ser cobrado** (é uma nova auditoria). Reenviar a mesma nota sem `reprocess` continua devolvendo o resultado guardado, sem cobrança.
- Planos pagos fora da franquia: `402 ERR_TETO_EXCEDENTE` ao atingir o teto (padrão 0 = para ao esgotar a franquia).
- Conta com pagamento pendente há 10 dias: cálculo e auditoria respondem `402 ERR_PAGAMENTO_PENDENTE`.
- `POST /v1/cobranca/checkout` e a captura de DF-e exigem empresa verificada: `403 ERR_EMPRESA_NAO_VERIFICADA`.
- `GET /v1/cobranca` traz `excedente`, `verificacao` e `inadimplencia`.
- `POST /v1/conta/cadastro` com CNPJ já cadastrado responde `202` como um cadastro novo (antes `409`); a explicação vai por e-mail.

## [0.11.1] - 2026-11-26

### Correções
- Assinantes da Stripe não recebem mais uma fatura avulsa além da assinatura; simulações descontam a franquia do plano antes do excedente.
- Eventos da Stripe aplicados na ordem certa: uma reentrega atrasada não reativa assinatura cancelada, e boleto/Pix só libera o plano depois de compensado.
- Consumo medido enviado à Stripe sem duplicar em caso de falha e sem perder o uso do fim do mês.
- Verificação em duas etapas protegida contra tentativa e erro: 5 códigos errados encerram a sessão e bloqueiam a conta temporariamente.
- Filtros de documentos com data fora do intervalo respondem 400 (antes, erro interno e CSV cortado).
- Simulação em que o motor recusa todos os itens não é cobrada e traz o aviso `NENHUM_ITEM_CALCULADO`.

### Mudanças na API
- `POST /v1/calculo/simular`: `valor_base_calculo` com até 13 dígitos inteiros e `quantidade` maior que zero (`400`).
- Filtros de `/v1/auditoria/documentos*`: datas entre 2025 e 2100; `limite` inválido responde `400` em problem+json.
- `GET /v1/cnpjs/{cnpj}/dfe/documentos`: `status` desconhecido responde `400`.
- `POST /v1/cobranca/checkout` com assinatura pendente responde `409 ERR_ASSINATURA_PENDENTE` (resolva no portal de cobrança).
- Corpo grande demais responde `413 ERR_PAYLOAD_TOO_LARGE`.
- Backoffice: gerir operadores e ler a trilha exigem o escopo `admin:security`; ninguém concede escopo que não tem.

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
