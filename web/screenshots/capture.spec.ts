import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import zip from './zip';
import { signupAndLogin } from '../e2e/helpers';

// Capturas reais do portal para a central de ajuda e a landing (D-W13). Reproduzível: cria uma conta de demonstração
// pelo cadastro (e-mail no Mailpit, MFA), sobe o plano pelo backoffice, gera uso real pela API (motores oficiais,
// workers, webhooks) e fotografa cada tela.
//
//   docker compose -f docker-compose.yml -f docker-compose.capacity.yml -f docker-compose.web.yml up -d
//   cd web && npm run screenshots
//
// SHOT_UI: onde abrir o portal (padrão: site do compose). SHOT_PUBLIC/SHOT_BACKOFFICE: nginx público e do backoffice.
// Saída: web/public/ajuda/img/*.jpg (versionadas). Só dados de teste: PKI de teste, corpus golden, chave de operador de dev.

const UI = process.env.SHOT_UI ?? 'http://127.0.0.1:8090';
const PUBLIC = process.env.SHOT_PUBLIC ?? 'http://127.0.0.1:8090';
const BACKOFFICE = process.env.SHOT_BACKOFFICE ?? 'http://127.0.0.1:8091';
const OPERATOR = process.env.SHOT_OPERATOR_KEY ?? 'tg_admin_dev_operador_fiscal_a_0000000000'; // chave SÓ de dev
const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(HERE, '../..');
const OUT = resolve(HERE, '../public/ajuda/img');
const CORPUS = resolve(ROOT, 'tests/golden/corpus-xml/v0');
const EMITENTE = '11222333000181';   // CNPJ da PKI de teste (certificado A1 de teste)

const xml = (f: string) => readFileSync(resolve(CORPUS, f));
const uuid = () => crypto.randomUUID();


async function ok(r: Awaited<ReturnType<APIRequestContext['get']>>, what: string) {
  if (!r.ok()) throw new Error(`${what}: ${r.status()} ${(await r.text()).slice(0, 300)}`);
  return r.status() === 204 ? undefined : r.json();
}

async function shot(page: Page, name: string, opts: { fullPage?: boolean } = {}) {
  await page.waitForLoadState('networkidle');
  await page.evaluate(() => document.fonts.ready);
  await page.mouse.move(0, 0);
  await page.screenshot({ path: resolve(OUT, `${name}.jpg`), type: 'jpeg', quality: 84, fullPage: opts.fullPage ?? false, animations: 'disabled' });
}

test.use({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1, colorScheme: 'light', locale: 'pt-BR', timezoneId: 'America/Sao_Paulo' });

test('capturas do portal com dados de demonstração', async ({ page, request }) => {
  test.setTimeout(300_000);
  const ADM = { 'X-Admin-Key': OPERATOR };
  const api = (path: string) => `${PUBLIC}${path}`;

  // 1) Tela de login (sem credencial real).
  await page.goto(`${UI}/portal/entrar`);
  await page.getByLabel('E-mail').fill('maria@comercialexemplo.com.br');
  await page.getByLabel('Senha').fill('••••••••••••••••');
  await shot(page, 'login');

  // 2) Conta de demonstração pelo cadastro (com MFA) e plano Profissional pelo backoffice.
  await signupAndLogin(page, request, { empresa: 'Comercial Exemplo Ltda' });
  const S = { 'X-TaxGateway-Csrf': '1' };
  const me = await ok(await page.request.get(api('/v1/me'), { headers: S }), 'me');
  await ok(await request.post(`${BACKOFFICE}/admin/v1/tenants/${me.tenant_id}/plano`, { headers: ADM, data: { plano: 'PROFISSIONAL' } }), 'plano');
  await ok(await page.request.post(api('/v1/oem/cnpjs'), { headers: S, data: { cnpj: EMITENTE } }), 'cnpj emitente');
  const key = await ok(await page.request.post(api('/v1/api-keys'), { headers: S, data: { nome: 'erp-producao',
    scopes: ['simulacao:write', 'auditoria:write', 'auditoria:read', 'cnpjs:manage', 'webhooks:manage', 'dfe:manage', 'keys:manage', 'usage:read'] } }), 'api key');
  const K = { 'X-Api-Key': key.api_key as string };

  // 3) Uso real: simulações, auditorias de saída, CNPJs atendidos, chaves por sistema, webhook e um lote.
  const sim = (valor: number, uf: string, ibge: number) => ({ cnpj_emitente: EMITENTE, regime_tributario: 'REGIME_REGULAR',
    data_fato_gerador: '2027-06-15T14:00:00-03:00', municipio_ibge: ibge, uf,
    itens: [{ numero: 1, cst_ibs_cbs: '000', c_class_trib: '000001', valor_base_calculo: valor, ncm: '84713012', quantidade: 1, unidade: 'UN' }] });
  for (const [v, uf, ibge] of [[1000, 'SP', 3550308], [2450.9, 'RJ', 3304557], [189.9, 'MG', 3106200]] as const) {
    await ok(await request.post(api('/v1/calculo/simular'), { headers: K, data: sim(v, uf, ibge) }), 'simular');
  }
  for (const f of ['A-000001-RO.xml', 'A-000003-AM.xml', 'A-000005-PA.xml', 'A-200002-SE.xml']) {
    await ok(await request.post(api('/v1/auditoria/xml?direction=OUTBOUND'), { headers: { ...K, 'Idempotency-Key': uuid(), 'Content-Type': 'application/xml' }, data: xml(f) }), `auditar ${f}`);
  }
  for (const c of ['11444777000161', '45723174000110']) {
    await ok(await request.post(api('/v1/oem/cnpjs'), { headers: K, data: { cnpj: c } }), 'cnpj');
  }
  await ok(await request.post(api('/v1/api-keys'), { headers: K, data: { nome: 'bi-consumo', scopes: ['usage:read'] } }), 'api key');
  await ok(await request.post(api('/v1/webhooks'), { headers: K, data: { url: 'https://example.com/hooks/taxgateway', eventos: ['batch.audit.completed', 'document.audited'] } }), 'webhook');

  const files = ['A-200006-RJ.xml', 'A-200009-SC.xml', 'A-200010-RS.xml', 'A-200012-MT.xml', 'A-200013-GO.xml', 'A-200015-RO.xml', 'A-200020-AP.xml', 'A-200032-ES.xml'];
  const body = zip(files.map((f) => ({ name: f, data: xml(f) })));
  const lote = await ok(await request.post(api('/v1/auditoria/lotes?direction=OUTBOUND'), { headers: { ...K, 'Idempotency-Key': uuid(), 'Content-Type': 'application/zip' }, data: body }), 'lote');
  await expect.poll(async () => (await ok(await request.get(api(`/v1/auditoria/lotes/${lote.batch_id}`), { headers: K }), 'lote')).status, { timeout: 120_000 }).toBe('COMPLETED');
  await expect.poll(async () => (await ok(await request.get(api('/v1/webhooks/deliveries'), { headers: K }), 'entregas')).length, { timeout: 60_000 }).toBeGreaterThan(0);

  // Certificado A1 da PKI de teste (a captura de DF-e NÃO é ligada: chamaria a SEFAZ).
  const pfx = readFileSync(resolve(ROOT, 'tests/golden/pki/test-leaf-11222333000181.pfx')).toString('base64');
  await ok(await request.put(api(`/v1/cnpjs/${EMITENTE}/certificado`), { headers: K, data: { pfx_base64: pfx, senha: 'teste' } }), 'certificado');

  // O consumo tem cache de até 60 s (a Visão geral foi aberta no login, antes do uso): espera o número aparecer.
  await expect.poll(async () => (await ok(await request.get(api('/v1/usage'), { headers: K }), 'uso')).documentos_auditados, { timeout: 90_000, intervals: [5_000] }).toBeGreaterThan(0);

  // 4) Telas.
  await page.goto(`${UI}/portal`);
  await expect(page.getByRole('heading', { name: 'Visão geral', level: 1 })).toBeVisible();
  await expect(page.getByText('Consumo dos últimos meses')).toBeVisible();
  await shot(page, 'visao-geral');

  await page.getByRole('link', { name: 'Simular operação', exact: true }).click();
  await page.getByRole('button', { name: 'Simular', exact: true }).click();
  await expect(page.getByRole('cell', { name: 'CBS' })).toBeVisible({ timeout: 30_000 });
  await shot(page, 'simular');

  await page.getByRole('link', { name: 'Classificação (NCM/NBS)', exact: true }).click();
  await page.getByLabel('NCM (8 dígitos)').fill('1006.30.21');
  await page.getByRole('button', { name: 'Consultar' }).click();
  await expect(page.getByRole('cell', { name: '000001' }).first()).toBeVisible({ timeout: 30_000 });
  await shot(page, 'classificacao');

  // Auditoria de ENTRADA de uma nota com redução de alíquota que depende de enquadramento → indício R021.
  await page.getByRole('link', { name: 'Enviar XML', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Auditar XML', level: 1 })).toBeVisible();   // a Classificação também tem campo de arquivo
  await page.locator('input[type=file]').setInputFiles(resolve(CORPUS, 'A-200006-arroz-RJ.xml'));
  await page.getByLabel('Direção').selectOption('INBOUND');
  await page.getByRole('button', { name: 'Auditar', exact: true }).click();
  await expect(page.getByText(/R0\d\d/).first()).toBeVisible({ timeout: 30_000 });
  await shot(page, 'auditoria');
  await page.getByRole('button', { name: 'Comparação por item' }).click();
  await expect(page.getByRole('region', { name: 'Item 1' })).toBeVisible();
  await shot(page, 'documento-detalhe');
  await page.keyboard.press('Escape');

  await page.getByRole('link', { name: 'Notas auditadas', exact: true }).click();
  await expect(page.getByRole('button', { name: /Ver detalhe do documento/ }).first()).toBeVisible();
  await shot(page, 'documentos');

  for (const [link, heading, name] of [
    ['Lotes (ZIP)', 'Lotes', 'lotes'], ['Credenciais (API e OAuth)', 'API keys', 'api-keys'], ['Webhooks', 'Webhooks', 'webhooks'],
    ['Apuração do mês', 'Apuração do mês', 'apuracao'], ['Alíquotas e exceções', 'Alíquotas e exceções', 'aliquotas'],
    ['Dados e verificação', 'Empresa', 'empresa'], ['Log de chamadas', 'Log de chamadas', 'log-chamadas'],
    ['CNPJs', 'CNPJs atendidos', 'cnpjs'], ['Avisos', 'Avisos', 'avisos'], ['Plano e cobrança', 'Plano e cobrança', 'cobranca'],
  ] as const) {
    // O sino muda o rótulo com os não lidos ("Avisos: 2 não lido(s)").
    await page.getByRole('navigation', { name: 'Portal' }).getByRole('link', { name: link, exact: true }).click();
    await expect(page.getByRole('heading', { name: heading, level: 1 })).toBeVisible();
    await shot(page, name);
  }

  await page.getByRole('link', { name: 'Notas de fornecedores (DF-e)', exact: true }).click();
  await page.getByLabel('CNPJ').fill(EMITENTE);
  await page.getByRole('button', { name: 'Abrir' }).click();
  await expect(page.getByText(/Vence em/).first()).toBeVisible();
  await shot(page, 'dfe');

  await page.getByRole('link', { name: 'LGPD: titulares', exact: true }).click();
  await page.getByLabel('CPF').fill('529.982.247-25');
  await page.getByRole('button', { name: 'Localizar' }).click();
  await expect(page.getByText('0 documento(s)')).toBeVisible();
  await shot(page, 'titulares');

  // Site público: matriz de comparação dos planos (D-R34) e linha do tempo das atualizações fiscais (D-R30).
  await page.goto(`${UI}/planos`);
  // O cabeçalho fixo cobriria a tabela (CSSOM: permitido pela CSP, ao contrário de <style> inline).
  await page.evaluate(() => { const h = document.querySelector<HTMLElement>('.site-header'); if (h) h.style.position = 'static'; });
  await page.locator('#comparar').scrollIntoViewIfNeeded();
  await expect(page.getByRole('region', { name: 'Comparação dos planos' })).toBeVisible();
  await page.locator('#comparar').screenshot({ path: resolve(OUT, 'planos-comparar.jpg'), type: 'jpeg', quality: 84, animations: 'disabled' });
  await page.goto(`${UI}/aliquotas`);
  await expect(page.getByRole('list', { name: 'Atualizações fiscais' })).toBeVisible();
  await page.locator('#atualizacoes').screenshot({ path: resolve(OUT, 'atualizacoes-fiscais.jpg'), type: 'jpeg', quality: 84, animations: 'disabled' });
});
