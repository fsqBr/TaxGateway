import {
  Bell, BookOpenCheck, Building2, Calculator, CreditCard, DownloadCloud, FileCheck2, FileSearch, KeyRound, Layers, LifeBuoy, ListChecks, ListTree, Rocket,
  Scale, ShieldAlert, Sparkles, UserRoundCheck, Users, Webhook,
} from 'lucide-react';
import type { Article } from './types';

// Conteúdo conferido contra o código e contra respostas reais da API (D-W12). Ao mudar um contrato, atualize aqui
// e no OpenApiDocs; as capturas são regeneradas por `npm run screenshots`.

const IMG = (name: string) => `/ajuda/img/${name}.jpg`;

const SIMULAR_JSON = `{
  "cnpj_emitente": "11222333000181",
  "regime_tributario": "REGIME_REGULAR",
  "data_fato_gerador": "2027-06-15T14:00:00-03:00",
  "municipio_ibge": 3550308,
  "uf": "SP",
  "itens": [
    { "numero": 1, "cst_ibs_cbs": "000", "c_class_trib": "000001",
      "valor_base_calculo": 1000, "ncm": "84713012", "quantidade": 1, "unidade": "UN" }
  ]
}`;

const SIMULAR_RESPOSTA = `{
  "simulation_id": "sim_01938a6e5b1c7d2a9e4f0a1b2c3d4e5f",
  "regime_calculo": "REGULAR",
  "rfb_engine_version": { "bundle": "2026.11.2", "engine": "REGULAR", "tag": "1.7.2", "db_version": "V0073" },
  "param_snapshot_id": 11,
  "totais": {
    "valor_base_total": 1000.0,
    "cbs_valor": 112.0,
    "ibs_uf_valor": 0.5,
    "ibs_mun_valor": 0.5,
    "ibs_total_valor": 1.0,
    "imposto_seletivo_valor": 0,
    "carga_tributaria_efetiva_percent": 11.3
  },
  "itens": [ { "numero_item": 1, "cbs": { "aliquota": 11.2, "valor": 112.0, "fonte_aliquota": "CATALOGO" }, "...": "..." } ],
  "avisos": [ { "codigo": "ALIQUOTA_PROVISORIA", "mensagem": "Alíquota de CBS 11.20% é provisória: ..." } ]
}`;

export const ARTICLES: Article[] = [
  {
    slug: 'primeiros-passos',
    title: 'Primeiros passos',
    summary: 'O que é o TaxGateway, como conseguir uma credencial e fazer a primeira chamada em 5 minutos.',
    category: 'Começar',
    icon: Rocket,
    keywords: 'começar inicio tutorial credencial primeira chamada portal login',
    blocks: [
      { kind: 'p', text: 'O **TaxGateway** calcula e audita os tributos da Reforma Tributária do Consumo — **CBS**, **IBS** (estadual e municipal) e **Imposto Seletivo** — usando os motores oficiais de cálculo publicados pela Receita Federal. Você integra por uma API REST com JSON; o portal serve para acompanhar o consumo e administrar credenciais, webhooks e cadastros.' },
      { kind: 'h', text: 'Em 4 passos' },
      { kind: 'steps', items: [
        '**Crie sua conta grátis** em [/cadastro](/cadastro) com seu e-mail, uma senha e o CNPJ da empresa. A conta começa no plano **Sandbox**, com o motor oficial e sem cartão.',
        '**Confirme o e-mail e ative a verificação em duas etapas** (MFA) com um app autenticador — é obrigatória. Guarde os códigos de recuperação.',
        '**No portal, crie uma API key para cada sistema** (ERP, checkout…) em **API keys**, só com os escopos de que ele precisa. A chave aparece uma única vez: guarde num cofre de segredos.',
        '**Faça a primeira simulação** com o exemplo abaixo (troque a chave e o CNPJ pelo seu).',
        '**Leia a [referência da API](/docs/)** e os artigos desta central conforme for integrando cada função.',
      ] },
      { kind: 'image', src: IMG('login'), alt: 'Tela de login do portal com e-mail e senha', caption: 'Login do portal: e-mail, senha e o código do app autenticador.' },
      { kind: 'callout', tone: 'success', text: 'Pessoas entram com **conta** (e-mail, senha e MFA); **sistemas** usam **API key** ou **OAuth**. Nunca cole uma API key no navegador nem a compartilhe com pessoas.' },
      { kind: 'code', title: 'Primeira chamada', tabs: [
        { label: 'cURL', lang: 'bash', code: `curl -X POST https://SEU-HOST/v1/calculo/simular \\
  -H "X-Api-Key: $TAXGATEWAY_KEY" \\
  -H "Content-Type: application/json" \\
  -d '${SIMULAR_JSON.replace(/\n\s*/g, ' ')}'` },
      ] },
      { kind: 'image', src: IMG('visao-geral'), alt: 'Visão geral do portal com consumo da competência', caption: 'Visão geral: documentos auditados, simulações, CNPJs ativos e limite do plano.' },
      { kind: 'callout', tone: 'info', text: 'Ambientes: use chaves `tg_test_sk_…` para testes e `tg_live_sk_…` em produção. Nunca coloque chaves no código-fonte nem no front-end do seu sistema.' },
    ],
  },
  {
    slug: 'simulacao',
    title: 'Simular CBS, IBS e IS',
    summary: 'Calcule os tributos de uma operação antes de emitir a nota: campos, regimes, avisos e exemplos.',
    category: 'Usar a API',
    icon: Calculator,
    keywords: 'simulacao simular calculo cbs ibs is imposto seletivo aliquota regime simples nacional checkout',
    blocks: [
      { kind: 'p', text: '`POST /v1/calculo/simular` calcula, item a item, CBS, IBS estadual, IBS municipal e Imposto Seletivo com o motor oficial da Receita. Use no checkout, no orçamento ou antes de emitir a nota. Exige o escopo `simulacao:write`.' },
      { kind: 'h', text: 'Campos principais' },
      { kind: 'table', head: ['Campo', 'O que é', 'Exemplo'], rows: [
        ['`cnpj_emitente`', 'CNPJ de quem vende (precisa estar cadastrado no seu tenant)', '`11222333000181`'],
        ['`regime_tributario`', '`REGIME_REGULAR` ou `SIMPLES_NACIONAL`', '`REGIME_REGULAR`'],
        ['`data_fato_gerador`', 'Data e hora com fuso; define as alíquotas vigentes', '`2027-06-15T14:00:00-03:00`'],
        ['`municipio_ibge`, `uf`', 'Local da operação (código IBGE de 7 dígitos)', '`3550308`, `SP`'],
        ['`itens[].cst_ibs_cbs`', 'Código de situação tributária do IBS/CBS', '`000`'],
        ['`itens[].c_class_trib`', 'Classificação tributária (tabela oficial)', '`000001`'],
        ['`itens[].valor_base_calculo`', 'Base de cálculo do item, em reais', '`1000`'],
        ['`itens[].ncm` ou `nbs`', 'Classificação do produto ou serviço', '`84713012`'],
      ] },
      { kind: 'code', title: 'Exemplo', tabs: [
        { label: 'Requisição', lang: 'json', code: SIMULAR_JSON },
        { label: 'Resposta (real)', lang: 'json', code: SIMULAR_RESPOSTA },
        { label: 'C#', lang: 'csharp', code: `using var http = new HttpClient { BaseAddress = new Uri("https://SEU-HOST/") };
http.DefaultRequestHeaders.Add("X-Api-Key", Environment.GetEnvironmentVariable("TAXGATEWAY_KEY"));
using var resp = await http.PostAsync("v1/calculo/simular",
    new StringContent(json, Encoding.UTF8, "application/json"));
resp.EnsureSuccessStatusCode();
var resultado = await resp.Content.ReadFromJsonAsync<JsonElement>();
Console.WriteLine(resultado.GetProperty("totais").GetProperty("cbs_valor"));` },
        { label: 'Node.js', lang: 'javascript', code: `const resp = await fetch('https://SEU-HOST/v1/calculo/simular', {
  method: 'POST',
  headers: { 'X-Api-Key': process.env.TAXGATEWAY_KEY, 'Content-Type': 'application/json' },
  body: JSON.stringify(requisicao),
});
if (!resp.ok) throw new Error((await resp.json()).code);   // ex.: ERR_CNPJ_NOT_PROVISIONED
const { totais } = await resp.json();` },
        { label: 'Python', lang: 'python', code: `import os, requests
r = requests.post("https://SEU-HOST/v1/calculo/simular",
                  headers={"X-Api-Key": os.environ["TAXGATEWAY_KEY"]}, json=requisicao, timeout=10)
r.raise_for_status()
print(r.json()["totais"]["cbs_valor"])` },
      ] },
      { kind: 'image', src: IMG('simular'), alt: 'Tela Simular do portal com o resultado por tributo', caption: 'No portal, a tela Simular aceita o mesmo JSON e mostra o resultado por tributo e os avisos.' },
      { kind: 'h', text: 'Lendo a resposta' },
      { kind: 'list', items: [
        '`totais` soma todos os itens; `itens` traz alíquota, valor e a **fonte da alíquota** (catálogo oficial ou informada por você).',
        '`param_snapshot_id` identifica o conjunto de alíquotas e regras usado — guarde junto com o seu documento para auditoria.',
        '`rfb_engine_version` diz qual versão do motor da Receita calculou.',
        '`avisos` não são erros: por exemplo, `ALIQUOTA_PROVISORIA` (alíquota ainda não oficializada) ou `DADOS_SIMULADOS_*` (motor sinalizou dados simulados).',
      ] },
      { kind: 'callout', tone: 'info', text: 'Simples Nacional: informe `regime_tributario = SIMPLES_NACIONAL` e as alíquotas efetivas do DAS em `itens[].aliquotas_simples` (o motor não calcula a receita bruta dos últimos 12 meses). Optante que escolheu apurar IBS/CBS pelo regime regular: `tp_regime_ibs_cbs = "1"`.' },
      { kind: 'callout', tone: 'warning', text: 'Simulações síncronas aceitam até 500 itens. Acima disso, use [lotes](/ajuda/lotes).' },
    ],
  },
  {
    slug: 'auditoria',
    title: 'Auditar um XML de NF-e/NFC-e',
    summary: 'Recalcule uma nota autorizada e veja cada divergência com regra, campo, valor de referência e fundamento legal.',
    category: 'Usar a API',
    icon: FileCheck2,
    keywords: 'auditoria xml nfe nfce divergencia ocorrencia regra entrada saida inbound outbound credito',
    blocks: [
      { kind: 'p', text: '`POST /v1/auditoria/xml` recebe o XML da nota (o `NFe` ou o `nfeProc` com o protocolo), valida o schema e a assinatura digital ICP-Brasil, recalcula os tributos com o motor oficial e aplica as regras de auditoria. Exige o escopo `auditoria:write` e o header `Idempotency-Key`.' },
      { kind: 'table', head: ['Parâmetro', 'Quando usar'], rows: [
        ['`direction=OUTBOUND`', 'Você **emitiu** a nota (padrão). O CNPJ do emitente precisa ser seu.'],
        ['`direction=INBOUND`', 'Você **recebeu** a nota. O destinatário precisa ser seu; a resposta traz o crédito potencial.'],
        ['`reprocess=true`', 'Reaudita uma nota já auditada (por exemplo, depois de uma mudança oficial de alíquota), sem cobrar de novo.'],
      ] },
      { kind: 'code', title: 'Exemplo', tabs: [
        { label: 'cURL', lang: 'bash', code: `curl -X POST "https://SEU-HOST/v1/auditoria/xml?direction=INBOUND" \\
  -H "X-Api-Key: $TAXGATEWAY_KEY" \\
  -H "Idempotency-Key: $(uuidgen)" \\
  -H "Content-Type: application/xml" \\
  --data-binary @nota.xml` },
      ] },
      { kind: 'image', src: IMG('auditoria'), alt: 'Resultado de auditoria com ocorrência de regra e fundamentação', caption: 'Resultado de uma auditoria de entrada: situação, valores declarados × recalculados e as ocorrências.' },
      { kind: 'h', text: 'Situações possíveis' },
      { kind: 'table', head: ['`status`', 'Significado'], rows: [
        ['`CONFORME`', 'Tudo bate com o cálculo oficial.'],
        ['`DIVERGENTE`', 'Há diferença determinística (alíquota, base, valor, CST…). Veja as ocorrências.'],
        ['`INDICIO`', 'Algo pede confirmação humana (por exemplo, uma redução que depende de enquadramento).'],
        ['`SCHEMA_ERROR`', 'O XML não segue o leiaute oficial.'],
        ['`ASSINATURA_INVALIDA`', 'A assinatura não confere ou a cadeia ICP-Brasil não é confiável.'],
        ['`INDETERMINADO`', 'Não foi possível concluir (ex.: combinação de regime sem regra de cálculo).'],
      ] },
      { kind: 'h', text: 'Anatomia de uma ocorrência' },
      { kind: 'code', tabs: [{ label: 'Ocorrência', lang: 'json', code: `{
  "regra": "R012",
  "natureza": "DETERMINISTICA",
  "severidade": "ERROR",
  "numero_item": 1,
  "campo_xml": "det/imposto/IBSCBS/gIBSCBS/gCBS/pCBS",
  "valor_declarado": "9.40",
  "valor_referencia": "11.20",
  "mensagem": "Alíquota declarada ≠ alíquota do Catálogo ajustada pela redução do cClassTrib",
  "fundamentacao": "LC 214/2025 arts. 14 a 19 e 343 a 347"
}` }] },
      { kind: 'callout', tone: 'success', text: 'A mesma nota (mesma chave de acesso) auditada de novo devolve o resultado anterior com `replay: true` e não é cobrada outra vez.' },
      { kind: 'callout', tone: 'warning', text: 'Indícios de auditoria não constituem parecer tributário. Achados determinísticos refletem as regras oficiais vigentes na data de emissão.' },
    ],
  },
  {
    slug: 'lotes',
    title: 'Auditar muitas notas (lotes)',
    summary: 'Envie um ZIP com milhares de XMLs, acompanhe o progresso e baixe o relatório.',
    category: 'Usar a API',
    icon: Layers,
    keywords: 'lote zip batch muitas notas relatorio csv assincrono',
    blocks: [
      { kind: 'steps', items: [
        'Monte um **ZIP só com arquivos `.xml`** (sem ZIP dentro de ZIP), até **100 MB**.',
        'Envie com `POST /v1/auditoria/lotes?direction=OUTBOUND` (corpo `application/zip`, header `Idempotency-Key`). A resposta é **202** com o `batch_id`.',
        'Acompanhe em `GET /v1/auditoria/lotes/{id}` — ou, melhor, assine o evento `batch.audit.completed` num [webhook](/ajuda/webhooks).',
        'Baixe o relatório CSV em `GET /v1/auditoria/lotes/{id}/relatorio`: uma linha por nota, com situação, ocorrências e diferença.',
      ] },
      { kind: 'image', src: IMG('lotes'), alt: 'Tela Lotes com envio de ZIP e lista de lotes concluídos', caption: 'No portal: envio do ZIP e lista dos últimos lotes com o relatório para baixar.' },
      { kind: 'callout', tone: 'info', text: 'Arquivos muito grandes: peça uma URL de upload com `POST /v1/auditoria/lotes/upload-url`, envie o ZIP direto para ela (PUT) e depois crie o lote com `{"storage_object_key": "..."}`.' },
      { kind: 'p', text: 'O processamento é distribuído de forma justa entre clientes: um lote grande não trava o seu checkout nem o de outro cliente.' },
    ],
  },
  {
    slug: 'webhooks',
    title: 'Receber eventos (webhooks)',
    summary: 'Eventos assinados quando uma auditoria, um lote ou um DF-e fica pronto — com código para validar a assinatura.',
    category: 'Integração',
    icon: Webhook,
    keywords: 'webhook evento assinatura hmac sha256 callback notificacao retry reenvio',
    blocks: [
      { kind: 'p', text: 'Cadastre uma URL **HTTPS pública** com `POST /v1/webhooks` (escopo `webhooks:manage`). O segredo de assinatura aparece **uma única vez** na resposta. Endereços de rede privada são recusados por segurança.' },
      { kind: 'table', head: ['Evento', 'Quando'], rows: [
        ['`document.audited`', 'Uma auditoria de XML terminou.'],
        ['`batch.audit.completed`', 'Um lote terminou (traz totais de conformes e falhas).'],
        ['`dfe.document.audited`', 'Uma nota capturada da SEFAZ foi auditada automaticamente.'],
        ['`certificate.expiring`', 'O certificado A1 vence em 30, 15 ou 5 dias.'],
      ] },
      { kind: 'code', title: 'Formato da entrega', tabs: [{ label: 'HTTP', lang: 'http', code: `POST /hooks/taxgateway HTTP/1.1
Content-Type: application/json
TaxGateway-Event: document.audited
TaxGateway-Signature: t=1795651200,v1=20c4d2aac7cefd4a7418877853c004d8c11fafafd0588e9f836b3fb5929fc069

{"id":"9f1c2e4a-0000-4000-8000-000000000001","event":"document.audited","created_at":"2026-11-25T12:00:00Z","data":{"chave_acesso":"35270611222333000181550010000000011000000010","status":"CONFORME"}}` }] },
      { kind: 'h', text: 'Validando a assinatura' },
      { kind: 'p', text: 'A assinatura é **HMAC-SHA256**, com o segredo inteiro (`whsec_…`) como chave, sobre o texto `"{t}.{corpo bruto}"`, em hexadecimal. Use o **corpo bruto** (antes de interpretar o JSON), rejeite horários com mais de 5 minutos de diferença e aceite qualquer `v1` válido (durante a rotação do segredo chegam dois).' },
      { kind: 'code', tabs: [
        { label: 'Node.js', lang: 'javascript', code: `import crypto from 'node:crypto';

export function assinaturaValida(header, corpoBruto, segredo, toleranciaSeg = 300) {
  const partes = header.split(',').map((p) => p.split('='));
  const t = partes.find(([k]) => k === 't')?.[1];
  if (!t || Math.abs(Date.now() / 1000 - Number(t)) > toleranciaSeg) return false;
  const esperado = crypto.createHmac('sha256', segredo).update(t + '.').update(corpoBruto).digest();
  return partes.filter(([k]) => k === 'v1').some(([, v]) => {
    const recebido = Buffer.from(v, 'hex');
    return recebido.length === esperado.length && crypto.timingSafeEqual(recebido, esperado);
  });
}` },
        { label: 'C#', lang: 'csharp', code: `static bool AssinaturaValida(string header, byte[] corpoBruto, string segredo, TimeSpan tolerancia)
{
    var partes = header.Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToList();
    var t = partes.FirstOrDefault(p => p[0] == "t")?[1];
    if (t is null || !long.TryParse(t, out var unix)
        || (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unix)).Duration() > tolerancia)
        return false;
    var esperado = HMACSHA256.HashData(Encoding.UTF8.GetBytes(segredo), [.. Encoding.UTF8.GetBytes(t + "."), .. corpoBruto]);
    return partes.Where(p => p[0] == "v1")
                 .Any(p => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(p[1]), esperado));
}` },
        { label: 'Python', lang: 'python', code: `import hashlib, hmac, time

def assinatura_valida(header: str, corpo_bruto: bytes, segredo: str, tolerancia: int = 300) -> bool:
    partes = [p.split("=", 1) for p in header.split(",") if "=" in p]
    t = next((v for k, v in partes if k == "t"), None)
    if t is None or abs(time.time() - int(t)) > tolerancia:
        return False
    esperado = hmac.new(segredo.encode(), t.encode() + b"." + corpo_bruto, hashlib.sha256).hexdigest()
    return any(hmac.compare_digest(v.lower(), esperado) for k, v in partes if k == "v1")` },
      ] },
      { kind: 'callout', tone: 'success', text: '**Vetor de teste** para conferir a sua implementação: segredo `whsec_vetor_de_teste_nao_use`, `t=1795651200` e o corpo do exemplo acima devem dar `v1=20c4d2aac7cefd4a7418877853c004d8c11fafafd0588e9f836b3fb5929fc069` (valide com o "agora" igual a `t`).' },
      { kind: 'h', text: 'Reenvios' },
      { kind: 'p', text: 'Responda **2xx em até alguns segundos** e processe depois. Sem 2xx, reenviamos após 1 min, 5 min, 30 min, 2 h, 6 h, 12 h e 24 h; esgotadas as tentativas (ou com `410 Gone`), a entrega fica como **Falhou** e pode ser reenviada pelo portal ou por `POST /v1/webhooks/deliveries/{id}/retry`. Trate eventos repetidos pelo `id` (podem chegar mais de uma vez).' },
      { kind: 'image', src: IMG('webhooks'), alt: 'Tela Webhooks com cadastro e histórico de entregas', caption: 'No portal: webhooks cadastrados, rotação do segredo e histórico de entregas com reenvio.' },
    ],
  },
  {
    slug: 'conta-e-equipe',
    title: 'Conta, MFA e equipe',
    summary: 'Como entrar com segurança, recuperar o acesso e dar acesso às pessoas certas da sua empresa.',
    category: 'Começar',
    icon: Users,
    keywords: 'conta login senha mfa dois fatores autenticador codigo recuperacao convite equipe papel usuario esqueci senha bloqueio',
    blocks: [
      { kind: 'p', text: 'Cada pessoa tem a própria conta. A entrada sempre pede **senha + código do app autenticador** (MFA, verificação em duas etapas): sem o celular, ninguém entra só com a senha.' },
      { kind: 'h', text: 'Ativar o MFA (primeiro acesso)' },
      { kind: 'steps', items: [
        'Instale um app autenticador: Google Authenticator, Microsoft Authenticator, 1Password, Authy ou similar.',
        'No primeiro acesso, leia o **QR code** com o app (ou digite a chave mostrada).',
        'Digite o código de 6 dígitos do app e guarde os **10 códigos de recuperação** (copie ou baixe o .txt).',
      ] },
      { kind: 'callout', tone: 'warning', text: 'Perdeu o celular? Na tela do código, use **"Perdi o celular: usar código de recuperação"**. Cada código vale uma vez. Depois, em **Minha conta**, gere novos códigos. Sem celular e sem códigos, peça a quem administra a conta da empresa.' },
      { kind: 'h', text: 'Senha' },
      { kind: 'list', items: [
        'Mínimo de 12 caracteres; uma frase funciona bem. Senhas muito comuns ou com seu nome/e-mail são recusadas.',
        'Esqueceu? Use **Esqueci minha senha** no login: o link vale 1 hora e, depois da troca, todas as sessões abertas são encerradas.',
        'Após 5 tentativas erradas, a conta fica bloqueada por 15 minutos e você recebe um e-mail de aviso.',
      ] },
      { kind: 'h', text: 'Equipe e papéis' },
      { kind: 'p', text: 'Donos e administradores convidam pessoas em **Equipe**. O convite chega por e-mail e vale 7 dias.' },
      { kind: 'table', head: ['Papel', 'Pode'], rows: [
        ['**Dono / Administrador**', 'Tudo, inclusive equipe, plano e cobrança.'],
        ['**Fiscal**', 'Simular, auditar, lotes, DF-e e certificado, CNPJs e atendimento a titulares (LGPD).'],
        ['**Desenvolvedor**', 'Simular, auditar, criar API keys e webhooks.'],
        ['**Financeiro**', 'Consumo, plano e faturas.'],
        ['**Leitura**', 'Ver o consumo.'],
      ] },
      { kind: 'p', text: 'Escritórios e software houses que atendem várias empresas trocam de empresa no topo do menu lateral. Cada pessoa vê só as empresas às quais foi convidada.' },
      { kind: 'callout', tone: 'info', text: 'Em **Minha conta** você vê as sessões abertas (dispositivo, IP, último uso) e pode sair de todas as outras de uma vez.' },
    ],
  },
  {
    slug: 'credenciais',
    title: 'API keys, OAuth e escopos',
    summary: 'Credenciais dos SEUS SISTEMAS: chaves por sistema, escopos mínimos e OAuth 2.0 quando preferir tokens.',
    category: 'Integração',
    icon: KeyRound,
    keywords: 'api key chave oauth token client credentials escopo permissao revogar seguranca jwt',
    blocks: [
      { kind: 'p', text: 'Toda chamada usa **uma** destas credenciais: o header `X-Api-Key` ou um token OAuth 2.0 em `Authorization: Bearer`.' },
      { kind: 'h', text: 'Escopos' },
      { kind: 'table', head: ['Escopo', 'Permite'], rows: [
        ['`simulacao:write`', 'Simular'],
        ['`auditoria:write`', 'Auditar XML e lotes'],
        ['`usage:read`', 'Ler consumo e uso por CNPJ'],
        ['`keys:manage`', 'Criar e revogar API keys'],
        ['`webhooks:manage`', 'Gerenciar webhooks e entregas'],
        ['`cnpjs:manage`', 'Gerenciar CNPJs atendidos (OEM)'],
        ['`dfe:manage`', 'Certificado A1 e captura de DF-e'],
        ['`titulares:manage`', 'Atender titulares (LGPD)'],
      ] },
      { kind: 'callout', tone: 'info', text: 'Boa prática: **uma chave por sistema**, só com os escopos de que ele precisa. Sem escopos informados, a chave nova recebe só `simulacao:write` e `auditoria:write`; escopos sensíveis (chaves, certificado, LGPD) precisam ser pedidos explicitamente. Uma chave nunca recebe escopo que quem a criou não tem.' },
      { kind: 'image', src: IMG('api-keys'), alt: 'Tela API keys com escopos e lista de chaves', caption: 'No portal: criação de chaves com escopos, último uso e revogação imediata.' },
      { kind: 'h', text: 'OAuth 2.0 (client credentials)' },
      { kind: 'p', text: 'Peça ao time do TaxGateway um `client_id` e `client_secret`. Troque por um token de 15 minutos e renove antes de expirar. Os limites do plano não vão no token: mudam na hora, sem reemitir.' },
      { kind: 'code', tabs: [{ label: 'cURL', lang: 'bash', code: `curl -X POST https://SEU-HOST/oauth/token \\
  -d grant_type=client_credentials -d client_id=$CLIENT_ID -d client_secret=$CLIENT_SECRET \\
  -d "scope=simulacao:write usage:read"
# {"access_token":"eyJhbGciOiJSUzI1NiIs...","token_type":"Bearer","expires_in":899}

curl https://SEU-HOST/v1/usage -H "Authorization: Bearer $TOKEN"` }] },
      { kind: 'p', text: 'Revogar uma API key ou suspender um cliente tem efeito em todas as instâncias em menos de 5 segundos.' },
    ],
  },
  {
    slug: 'oem',
    title: 'Vários CNPJs (OEM)',
    summary: 'Software houses: atenda todos os clientes com uma integração, com uso separado por CNPJ.',
    category: 'Cadastro e conformidade',
    icon: Building2,
    keywords: 'oem software house multi cnpj cliente on behalf of uso por cnpj',
    blocks: [
      { kind: 'p', text: 'Cada CNPJ em nome do qual você chama a API precisa estar **ativo** no seu tenant. Cadastre com `POST /v1/oem/cnpjs` (escopo `cnpjs:manage`) ou peça o **auto-provisionamento** (o CNPJ entra no primeiro uso).' },
      { kind: 'list', items: [
        'Envie `X-On-Behalf-Of-CNPJ: <cnpj>` para deixar explícito o cliente atendido; se o CNPJ do documento for outro, a chamada é recusada com `ERR_CNPJ_SCOPE`.',
        '`DELETE /v1/oem/cnpjs/{cnpj}` desativa na hora; a cobrança daquele CNPJ termina na competência seguinte. Um CNPJ desativado **não** volta pelo auto-provisionamento.',
        '`GET /v1/oem/usage?month=AAAA-MM` traz auditorias e simulações por CNPJ, direto do registro de cobrança.',
      ] },
      { kind: 'image', src: IMG('cnpjs'), alt: 'Tela CNPJs com cadastro e uso por CNPJ', caption: 'No portal: CNPJs atendidos e uso por CNPJ na competência.' },
    ],
  },
  {
    slug: 'dfe',
    title: 'Capturar notas recebidas (DF-e)',
    summary: 'Com o certificado A1, buscamos na SEFAZ as notas em que você é destinatário e auditamos a entrada.',
    category: 'Cadastro e conformidade',
    icon: DownloadCloud,
    keywords: 'dfe distribuicao nsu sefaz certificado a1 pfx ciencia da operacao manifestacao destinatario 656',
    blocks: [
      { kind: 'steps', items: [
        'Em **DF-e e certificado**, informe o CNPJ e envie o **certificado A1** (`.pfx`) e a senha. Conferimos senha, validade e se o certificado é do mesmo CNPJ (raiz). O PFX fica cifrado e nunca volta pela API.',
        'Clique em **Ligar captura** (ou `POST /v1/cnpjs/{cnpj}/dfe/sync`), informando a UF e se devemos manifestar a **Ciência da Operação** automaticamente.',
        'Pronto: as notas chegam sozinhas. Com a ciência, a nota completa é liberada pela SEFAZ e auditada como **entrada**; você recebe o evento `dfe.document.audited`.',
      ] },
      { kind: 'image', src: IMG('dfe'), alt: 'Tela DF-e com certificado A1 e situação da captura', caption: 'No portal: certificado do CNPJ e situação da captura (último NSU, próxima consulta, documentos).' },
      { kind: 'callout', tone: 'warning', text: 'A SEFAZ bloqueia por 1 hora quem consulta demais (rejeição **656**). Respeitamos as regras automaticamente: depois de "nenhum documento" esperamos 1 hora e fazemos no máximo 20 consultas por hora. Por isso **não existe botão de consultar agora** — e não rode outro sistema de captura no mesmo CNPJ ao mesmo tempo.' },
      { kind: 'p', text: 'A SEFAZ guarda os documentos por cerca de 90 dias: notas mais antigas que isso, emitidas antes de você ligar a captura, não podem mais ser baixadas.' },
    ],
  },
  {
    slug: 'lgpd',
    title: 'LGPD: pedidos de titulares',
    summary: 'Localize e expurgue os dados de uma pessoa física quando ela pedir.',
    category: 'Cadastro e conformidade',
    icon: UserRoundCheck,
    keywords: 'lgpd titular cpf expurgo dados pessoais privacidade controlador operador',
    blocks: [
      { kind: 'p', text: 'Você é o **controlador** dos dados; o TaxGateway é o **operador**. Quando um titular pedir, use `POST /v1/titulares/localizar` e, se for o caso, `POST /v1/titulares/expurgar` (escopo `titulares:manage`).' },
      { kind: 'list', items: [
        'O CPF não fica guardado: buscamos por um código derivado dele com uma chave exclusiva do seu tenant.',
        'O expurgo apaga o XML guardado e o vínculo com o CPF; o registro fiscal (valores e achados) continua, sem dado pessoal.',
        'Cada pedido fica registrado para a sua prestação de contas.',
        'Por padrão o XML **não é guardado**; só fica retido se o seu contrato pedir.',
      ] },
      { kind: 'image', src: IMG('titulares'), alt: 'Tela LGPD com busca por CPF', caption: 'No portal: localizar e expurgar por CPF.' },
    ],
  },
  {
    slug: 'erros',
    title: 'Erros e limites',
    summary: 'Todos os códigos de erro, o que significam e o que fazer. Limites de uso e idempotência.',
    category: 'Referência',
    icon: ShieldAlert,
    keywords: 'erro codigo problem json 400 401 403 404 409 422 429 503 limite rate limit idempotency',
    blocks: [
      { kind: 'p', text: 'Todo erro volta em `application/problem+json`. Trate pelo campo `code` (estável); `title` e `detail` são para pessoas. Guarde o `trace_id` para falar com o suporte.' },
      { kind: 'code', tabs: [{ label: 'Exemplo', lang: 'json', code: `{
  "type": "urn:taxgateway:erro:ERR_FORBIDDEN_SCOPE",
  "title": "Escopo insuficiente",
  "status": 403,
  "detail": "Esta operação exige o escopo 'simulacao:write'.",
  "code": "ERR_FORBIDDEN_SCOPE",
  "trace_id": "00-efa3b112783764ad81aec4a4f41d910a-02518ac9e5bca1a8-00"
}` }] },
      { kind: 'table', head: ['`code`', 'HTTP', 'O que fazer'], rows: [
        ['`ERR_UNAUTHENTICATED`', '401', 'Chave/token ausente, inválido, revogado ou expirado. Confira o header.'],
        ['`ERR_FORBIDDEN_SCOPE`', '403', 'A credencial não tem o escopo da operação. Crie uma chave com o escopo.'],
        ['`ERR_TENANT_SUSPENDED`', '403', 'Conta suspensa. Fale com o time do TaxGateway.'],
        ['`ERR_CNPJ_NOT_PROVISIONED`', '403', 'O CNPJ não está ativo no seu tenant. Cadastre em CNPJs.'],
        ['`ERR_CNPJ_SCOPE`', '403', 'O CNPJ do documento difere do `X-On-Behalf-Of-CNPJ`.'],
        ['`ERR_VALIDATION`', '400', 'Campo obrigatório ausente ou inválido; veja `detail` e `errors`.'],
        ['`ERR_JSON_INVALIDO` / `ERR_BAD_REQUEST`', '400', 'Corpo malformado.'],
        ['`ERR_XML_REJEITADO`', '400', 'XML ilegível ou fora do padrão aceito.'],
        ['`ERR_CHAVE_INVALIDA`', '400', 'Chave de acesso ou modelo de documento inválido.'],
        ['`ERR_DIRECTION_INVALIDA`', '400', 'Use `direction=OUTBOUND` ou `INBOUND`.'],
        ['`ERR_IDEMPOTENCY_KEY_OBRIGATORIA`', '400', 'Envie `Idempotency-Key` (até 255 caracteres) nas auditorias e lotes.'],
        ['`ERR_IDEMPOTENCY_KEY_INVALIDA`', '400', '`Idempotency-Key` com mais de 255 caracteres.'],
        ['`ERR_IDEMPOTENCY_KEY_REUSED`', '422', 'A mesma chave foi usada com outro corpo. Gere uma nova para cada operação diferente.'],
        ['`ERR_IDEMPOTENCY_IN_PROGRESS`', '409', 'A primeira chamada com essa chave ainda está em processamento. Aguarde e repita.'],
        ['`ERR_NOT_FOUND`', '404', 'Recurso inexistente (ou de outro tenant).'],
        ['`ERR_CERT_REQUIRED`', '409', 'Envie o certificado A1 antes de ligar a captura de DF-e.'],
        ['`ERR_CERT_INVALID`', '400', 'PFX ilegível ou senha errada.'],
        ['`ERR_CERT_EXPIRED` / `ERR_CERT_CNPJ_MISMATCH`', '422', 'Certificado vencido ou de outro CNPJ (raiz).'],
        ['`ERR_MUNICIPIO_INVALIDO`', '422', 'Código IBGE não pertence à UF informada.'],
        ['`ERR_REGIME_INDETERMINADO`', '422', 'Combinação de regime (CRT) sem regra de cálculo.'],
        ['`ERR_ALIQUOTA_INDEFINIDA`', '422', 'Ainda não há alíquota oficial para a data (ex.: CBS de 2028). Informe `aliquotas_nominais` ou aguarde a definição.'],
        ['`ERR_TOO_MANY_ITEMS`', '422', 'Mais de 500 itens: use lotes.'],
        ['`ERR_EMITENTE_NAO_SUPORTADO` / `ERR_DESTINATARIO_NAO_SUPORTADO`', '422', 'O documento não tem CNPJ válido do lado auditado.'],
        ['`ERR_ENGINE_REJECTED` / `ERR_ENGINE_CAPABILITY`', '422', 'O motor oficial recusou os dados ou não suporta o caso. Veja `detail`.'],
        ['`ERR_WEBHOOK_URL_BLOQUEADA`', '422', 'URL do webhook em rede privada/interna ou não HTTPS.'],
        ['`ERR_RATE_LIMITED`', '429', 'Limite do plano. Respeite o header `Retry-After`.'],
        ['`ERR_ENGINE_UNAVAILABLE`', '503', 'Motor de cálculo momentaneamente indisponível. Repita com espera.'],
        ['`ERR_STORAGE_INDISPONIVEL`', '503', 'Armazenamento não disponível nesta instalação.'],
        ['`ERR_INTERNAL`', '500', 'Falha inesperada. Envie o `trace_id` ao suporte.'],
      ] },
      { kind: 'h', text: 'Limites e idempotência' },
      { kind: 'list', items: [
        'Cada resposta traz os headers `RateLimit-*` com o seu limite por minuto e o quanto resta; ao estourar, `429` com `Retry-After`.',
        'Use um `Idempotency-Key` novo (um UUID) por operação: se a rede cair, repita com a **mesma** chave — não haverá cobrança nem processamento em dobro.',
        'Toda resposta traz `X-TaxGateway-Version` com a versão da plataforma (veja [novidades](/ajuda/novidades)).',
      ] },
    ],
  },
  {
    slug: 'glossario',
    title: 'Glossário',
    summary: 'IBS, CBS, IS, CST, cClassTrib, NSU, cStat, competência e outros termos usados aqui.',
    category: 'Referência',
    icon: BookOpenCheck,
    keywords: 'glossario termos ibs cbs is cst cclasstrib nsu cstat competencia ciencia snapshot oem',
    blocks: [
      { kind: 'table', head: ['Termo', 'Significado'], rows: [
        ['**CBS**', 'Contribuição sobre Bens e Serviços — tributo federal da Reforma Tributária do Consumo (LC 214/2025).'],
        ['**IBS**', 'Imposto sobre Bens e Serviços — tributo de estados e municípios, com parcela estadual e municipal.'],
        ['**IS**', 'Imposto Seletivo — incide sobre bens e serviços prejudiciais à saúde ou ao meio ambiente.'],
        ['**CST IBS/CBS**', 'Código de situação tributária do IBS e da CBS informado em cada item.'],
        ['**cClassTrib**', 'Código de classificação tributária (tabela oficial) que define reduções e tratamentos específicos.'],
        ['**Motor oficial**', 'Calculadora de IBS/CBS/IS publicada pela Receita Federal; o TaxGateway usa as versões oficiais, fixadas e testadas.'],
        ['**Bundle fiscal**', 'Conjunto de versões dos motores e da base oficial em uso (ex.: `2026.11.2`).'],
        ['**Snapshot de parâmetros**', 'Fotografia das alíquotas e regras usadas num cálculo (`param_snapshot_id`), para auditoria futura.'],
        ['**OUTBOUND / INBOUND**', 'Nota emitida por você (saída) / recebida por você (entrada).'],
        ['**Competência**', 'Mês de referência do consumo e da cobrança (`AAAA-MM`).'],
        ['**OEM**', 'Modelo para software houses que atendem vários CNPJs com uma integração.'],
        ['**DF-e**', 'Documentos fiscais eletrônicos distribuídos pela SEFAZ ao destinatário.'],
        ['**NSU**', 'Número sequencial único da distribuição de DF-e; a consulta sempre continua do último NSU.'],
        ['**cStat 137 / 138 / 656**', 'Respostas da SEFAZ: nenhum documento / documentos encontrados / consumo indevido (bloqueio de 1 hora).'],
        ['**Ciência da Operação**', 'Manifestação do destinatário (evento 210210) que libera a nota completa na distribuição.'],
        ['**Certificado A1**', 'Certificado digital em arquivo (`.pfx`) da empresa, usado para falar com a SEFAZ.'],
        ['**Idempotency-Key**', 'Identificador que você envia para que repetir a mesma operação não a execute (nem cobre) duas vezes.'],
      ] },
    ],
  },
  {
    slug: 'documentos-auditados',
    title: 'Documentos auditados: histórico, detalhe e exportação',
    summary: 'Consultar tudo o que foi auditado — pela API, pelo portal, em lote ou pelo DF-e — e exportar para planilha.',
    category: 'Usar a API',
    icon: FileSearch,
    keywords: 'historico documentos auditados consulta filtro exportar csv excel planilha detalhe item comparacao',
    blocks: [
      { kind: 'p', text: 'Toda auditoria fica guardada como **metadados fiscais** (valores declarados por item, resultado e achados — sem XML, nome ou CPF). No portal, abra **Documentos auditados**; pela API, use `GET /v1/auditoria/documentos` (escopo `auditoria:read` ou `auditoria:write`).' },
      { kind: 'h', text: 'Filtros' },
      { kind: 'table', head: ['Filtro', 'Exemplo', 'Observação'], rows: [
        ['`de` / `ate`', '`de=2027-01-01&ate=2027-01-31`', 'Datas de Brasília; padrão: últimos 90 dias; até 400 dias por consulta.'],
        ['`status`', '`status=DIVERGENTE,INDICIO`', 'Uma ou mais situações, separadas por vírgula.'],
        ['`direcao`', '`direcao=entrada`', '`saida` (você emitiu) ou `entrada` (você recebeu).'],
        ['`cnpj`', '`cnpj=11222333000181`', 'Emitente ou contraparte, 14 caracteres sem pontuação.'],
        ['`chave`', '`chave=3527…`', 'Chave de acesso de 44 dígitos.'],
        ['`lote`', '`lote=<batch_id>`', 'Só os documentos de um lote.'],
      ] },
      { kind: 'p', text: 'A resposta traz até 50 documentos (`limite` até 200) e um `proximo_cursor`: passe-o em `cursor` para a próxima página. `GET /v1/auditoria/resumo` devolve os totais por situação, a diferença e o crédito potencial do período.' },
      { kind: 'image', src: IMG('documentos'), alt: 'Lista de documentos auditados com filtros e totais do período', caption: 'Documentos auditados: filtros, totais do período e exportação.' },
      { kind: 'h', text: 'Detalhe e comparação por item' },
      { kind: 'p', text: '`GET /v1/auditoria/documentos/{id}` devolve os itens como declarados (NCM, CST, cClassTrib, base, alíquotas e valores) e a auditoria completa. No portal, **Detalhe** mostra cada item com os achados daquele item: **regra, campo do XML, valor declarado, valor de referência e fundamento legal**.' },
      { kind: 'image', src: IMG('documento-detalhe'), alt: 'Detalhe do documento com declarado, recalculado e achados por item', caption: 'Detalhe: resumo declarado × recalculado e, por item, a regra, o campo, o declarado, a referência e o fundamento.' },
      { kind: 'h', text: 'Exportar' },
      { kind: 'p', text: '`GET /v1/auditoria/documentos/exportar` gera CSV com os mesmos filtros: `nivel=documento` (uma linha por nota) ou `nivel=ocorrencia` (uma linha por achado). O arquivo usa `;` e vírgula decimal, então abre direto no Excel em português; até 50 mil linhas por arquivo.' },
      { kind: 'callout', tone: 'info', text: 'Quem só precisa consultar (contador, auditor interno) pode receber o papel **Leitura** na Equipe: vê documentos e resultados, mas não envia nem altera nada.' },
    ],
  },
  {
    slug: 'classificacao-tributaria',
    title: 'Encontrar o CST e o cClassTrib pelo NCM ou NBS',
    summary: 'Consulta dos códigos de classificação candidatos a partir do NCM (mercadoria) ou NBS (serviço).',
    category: 'Para o fiscal',
    icon: ListTree,
    keywords: 'cclasstrib cst classificacao tributaria ncm nbs codigo situacao tributaria reducao anexo',
    blocks: [
      { kind: 'p', text: 'Cada item da NF-e precisa de um **CST** (3 dígitos) e de um **cClassTrib** (6 dígitos; os 3 primeiros são o CST). Em **Classificação (NCM/NBS)** no portal — ou `GET /v1/classificacao?ncm=22021000` pela API — você informa o código do produto ou serviço e a data da operação e recebe:' },
      { kind: 'list', items: [
        'a **descrição oficial** do NCM/NBS e se ele está sujeito ao **Imposto Seletivo**;',
        'as **classificações compatíveis** com a nomenclatura e com o modelo do documento, começando pela regra geral (`000001`, tributação integral), com redução de CBS/IBS e se dá crédito ao adquirente;',
        'um filtro por palavras do produto (ex.: "hortícola", "medicamento") para chegar mais rápido às situações específicas.',
      ] },
      { kind: 'image', src: IMG('classificacao'), alt: 'Consulta de classificação por NCM com a lista de CST e cClassTrib', caption: 'Consulta por NCM: descrição oficial, Imposto Seletivo e classificações compatíveis.' },
      { kind: 'callout', tone: 'warning', text: 'A consulta é um **atalho de pesquisa, não um parecer**. As reduções dos anexos da LC 214/2025 dependem do enquadramento exato do produto ou serviço, que é responsabilidade do contribuinte. Na dúvida, confirme com o seu consultor tributário.' },
      { kind: 'p', text: 'Depois de escolher, simule a operação em **Simular** com o CST e o cClassTrib: o motor oficial mostra a alíquota e o valor resultantes.' },
    ],
  },
  {
    slug: 'entender-o-resultado',
    title: 'Como ler o resultado de uma auditoria',
    summary: 'Situações, divergência × indício, diferença e crédito potencial — e o que fazer em cada caso.',
    category: 'Para o fiscal',
    icon: Scale,
    keywords: 'resultado auditoria conforme divergente indicio fundamento achado ocorrencia credito diferenca o que fazer fiscal',
    blocks: [
      { kind: 'table', head: ['Situação', 'O que significa', 'O que fazer'], rows: [
        ['**CONFORME**', 'O declarado bate com o recálculo do motor oficial.', 'Nada.'],
        ['**DIVERGENTE**', 'Há diferença determinística: a regra oficial dá outro valor.', 'Corrija a parametrização do ERP; se a nota já foi autorizada, avalie a correção (nota complementar/ajuste) com o fiscal.'],
        ['**INDÍCIO**', 'Algo merece revisão (ex.: classificação incomum para o NCM), mas não é erro certo.', 'Confira o enquadramento; se estiver certo, você pode confirmar o contexto na próxima auditoria (`confirmar=R021`).'],
        ['**SCHEMA_ERROR**', 'O XML não segue o leiaute oficial.', 'Corrija a geração do XML no emissor.'],
        ['**ASSINATURA_INVALIDA**', 'Assinatura digital ausente, inválida ou de certificado revogado.', 'Confirme se é o XML autorizado original.'],
        ['**INDETERMINADO**', 'Não foi possível recalcular (ex.: motor indisponível).', 'Reenvie mais tarde; não é cobrado como divergência.'],
      ] },
      { kind: 'h', text: 'Divergência × indício' },
      { kind: 'p', text: '**Divergência** (determinística) é quando a regra oficial, aplicada aos mesmos dados, dá outro resultado — por exemplo, alíquota de CBS diferente da vigente. **Indício** é um alerta que depende de contexto que só você conhece. Cada achado traz a **regra**, o **campo do XML**, o valor **declarado**, o de **referência** e o **fundamento legal**.' },
      { kind: 'h', text: 'Diferença e crédito' },
      { kind: 'p', text: '**Diferença** é o total recalculado menos o declarado. Na **entrada** (notas que você recebeu), o **crédito potencial** estima o crédito de CBS/IBS que a nota permite apropriar, conforme a classificação e o destaque — a apropriação efetiva segue as regras de extinção do débito do fornecedor.' },
      { kind: 'callout', tone: 'info', text: 'Os achados refletem as regras oficiais vigentes **na data de emissão** da nota. Indícios não constituem parecer tributário.' },
    ],
  },
  {
    slug: 'avisos',
    title: 'Avisos da conta e e-mails',
    summary: 'Franquia, webhook pausado, certificado vencendo e mudança de alíquota — no sino do portal e por e-mail.',
    category: 'Para o fiscal',
    icon: Bell,
    keywords: 'avisos alertas notificacao email franquia webhook pausado certificado vencendo mudanca aliquota',
    blocks: [
      { kind: 'table', head: ['Aviso', 'Quando', 'Quem recebe por e-mail'], rows: [
        ['Franquia em 80% / esgotada', 'Ao passar de 80% e de 100% da franquia do mês.', 'Donos, administradores e financeiro.'],
        ['Webhook pausado', 'Depois de 5 falhas seguidas no mesmo endpoint (as entregas retomam sozinhas).', 'Donos, administradores e desenvolvimento.'],
        ['Certificado A1 vencendo', 'A 30, 15 e 5 dias do vencimento.', 'Donos, administradores e fiscal.'],
        ['Mudança de alíquota', 'Quando uma alteração do catálogo de alíquotas é aprovada.', 'Donos, administradores e fiscal.'],
      ] },
      { kind: 'image', src: IMG('avisos'), alt: 'Página de avisos da conta', caption: 'Avisos: cada um com a severidade, o que aconteceu e onde resolver.' },
      { kind: 'p', text: 'No portal, o **sino** mostra quantos avisos não foram lidos. Pela API: `GET /v1/avisos` e `POST /v1/avisos/{id}/lido`. Só recebem e-mail as pessoas com e-mail confirmado.' },
    ],
  },
  {
    slug: 'planos-e-cobranca',
    title: 'Planos, franquia e cobrança',
    summary: 'Como funcionam os planos, a franquia, o excedente e o pagamento pela Stripe.',
    category: 'Começar',
    icon: CreditCard,
    keywords: 'plano preco franquia excedente cobranca fatura stripe cartao sandbox upgrade',
    blocks: [
      { kind: 'p', text: 'Toda conta começa no **Sandbox** (grátis, com franquia pequena e **sem excedente**: ao esgotar, as chamadas recebem `402 ERR_FRANQUIA_ESGOTADA` até o mês seguinte). Os planos pagos têm mensalidade com franquia de auditorias e simulações; o que passar é cobrado como **excedente** na fatura seguinte. Compare os planos em [/planos](/planos).' },
      { kind: 'steps', items: [
        'Em **Plano e cobrança**, escolha o plano e clique em **Assinar**: o pagamento é feito na página segura da Stripe (cartão).',
        'Ao confirmar, o plano novo vale na hora para limites e franquia.',
        'Faturas, cartão e cancelamento ficam no **portal de cobrança** (botão "Gerenciar pagamento").',
      ] },
      { kind: 'image', src: IMG('cobranca'), alt: 'Tela de plano e cobrança com o plano atual e as opções', caption: 'Plano e cobrança: plano atual, franquia e assinatura.' },
      { kind: 'callout', tone: 'info', text: 'Replays (a mesma nota auditada de novo com o mesmo conteúdo) e reprocessamentos de documento já auditado **não** são cobrados.' },
    ],
  },
  {
    slug: 'portal',
    title: 'Tour pelo portal',
    summary: 'Cada tela do portal do cliente e para que serve.',
    category: 'Começar',
    icon: ListChecks,
    keywords: 'portal telas tour tela visao geral menu',
    blocks: [
      { kind: 'table', head: ['Tela', 'Para quê'], rows: [
        ['Visão geral', 'Primeiros passos, consumo da competência, histórico dos últimos meses e limites do plano.'],
        ['Simular', 'Calcular uma operação por formulário (ou com o mesmo JSON da API).'],
        ['Classificação (NCM/NBS)', 'Encontrar CST e cClassTrib candidatos para um produto ou serviço.'],
        ['Auditar XML', 'Enviar uma nota (ou usar o XML de exemplo) e ver o resultado com a comparação por item.'],
        ['Lotes', 'Enviar ZIPs, acompanhar o progresso e baixar relatórios.'],
        ['Documentos auditados', 'Histórico com filtros, detalhe por item e exportação CSV.'],
        ['Avisos (sino)', 'Franquia, webhook pausado, certificado vencendo e mudanças de alíquota.'],
        ['Plano e cobrança', 'Assinar ou trocar de plano e ver faturas.'],
        ['API keys', 'Criar, limitar e revogar chaves dos seus sistemas.'],
        ['Equipe', 'Convidar pessoas e definir papéis (donos e administradores).'],
        ['Minha conta', 'Senha, MFA, códigos de recuperação e sessões abertas.'],
        ['Webhooks', 'Cadastrar URLs, girar segredos e reenviar entregas.'],
        ['CNPJs', 'CNPJs atendidos e uso por CNPJ.'],
        ['DF-e e certificado', 'Certificado A1 e captura de notas recebidas.'],
        ['LGPD: titulares', 'Atender pedidos de titulares.'],
      ] },
      { kind: 'image', src: IMG('visao-geral'), alt: 'Visão geral do portal', caption: 'Menu à esquerda, com o tenant, o plano e a versão da plataforma no rodapé.' },
      { kind: 'callout', tone: 'info', text: 'Quando o TaxGateway é atualizado, o portal mostra as novidades da versão uma vez. Se uma versão nova sair enquanto a página está aberta, aparece um aviso para recarregar.' },
    ],
  },
  {
    slug: 'suporte',
    title: 'Suporte',
    summary: 'Como pedir ajuda e o que enviar para resolvermos rápido.',
    category: 'Referência',
    icon: LifeBuoy,
    keywords: 'suporte ajuda contato chamado trace id',
    blocks: [
      { kind: 'list', items: [
        'Envie o **`trace_id`** do erro, o horário (com fuso), a rota chamada e o `code`.',
        'Informe a versão (`GET /version` ou o rodapé do portal).',
        '**Nunca** envie a API key, o `client_secret`, o segredo do webhook ou o certificado A1 — não pedimos isso.',
      ] },
      { kind: 'p', text: 'Veja também a [referência da API](/docs/) e as [novidades](/ajuda/novidades).' },
    ],
  },
];

export const NOVIDADES_ICON = Sparkles;

export const CATEGORIES: Article['category'][] = ['Começar', 'Usar a API', 'Para o fiscal', 'Integração', 'Cadastro e conformidade', 'Referência'];

/** Busca simples sem acento e sem caixa, em título, resumo, palavras-chave e texto. */
export function searchArticles(query: string, articles: Article[] = ARTICLES): Article[] {
  const norm = (s: string) => s.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();
  const terms = norm(query).split(/\s+/).filter(Boolean);
  if (!terms.length) return articles;
  const scored = articles.map((a) => {
    const head = norm(`${a.title} ${a.summary} ${a.keywords ?? ''}`);
    const body = norm(a.blocks.map((b) => JSON.stringify(b)).join(' '));
    let score = 0;
    for (const t of terms) {
      if (head.includes(t)) score += 3;
      else if (body.includes(t)) score += 1;
      else return { a, score: -1 };
    }
    return { a, score };
  });
  return scored.filter((x) => x.score > 0).sort((x, y) => y.score - x.score).map((x) => x.a);
}
