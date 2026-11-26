import { expect, test, type Page } from '@playwright/test';
import { BACKOFFICE, login, mailLink, OPERATOR_KEY, PASSWORD, PUBLIC, signupAndLogin } from './helpers';

// E2E contra os nginx do compose (D-W07, D-U01): contas de pessoas com MFA, e-mails pelo Mailpit.

/** Falha o teste se a página registrar erro de console, violação de CSP ou resposta HTTP >= 400 (fora das esperadas). */
function watchConsole(page: Page, allow: RegExp[] = []): string[] {
  const errors: string[] = [];
  page.on('response', (r) => {
    const line = `HTTP ${r.status()} ${r.request().method()} ${r.url()}`;
    if (r.status() >= 400 && !allow.some((a) => a.test(line))) errors.push(line);
  });
  page.on('console', (m) => { if (m.type() === 'error' && !/Failed to load resource/.test(m.text())) errors.push(m.text()); });
  page.on('pageerror', (e) => errors.push(e.message));
  return errors;
}

// O login checa /conta/sessao antes de entrar (401 esperado sem sessão).
const SESSION_PROBE = /HTTP 401 GET .*\/conta\/sessao$/;

test('página de planos mostra a vitrine vinda do backoffice', async ({ page }) => {
  const errors = watchConsole(page);
  await page.goto(PUBLIC + '/planos');
  await expect(page.getByRole('heading', { name: 'Comece grátis. Pague pelo que usar.' })).toBeVisible();
  for (const nome of ['Sandbox', 'Essencial', 'Profissional', 'Escritório contábil']) {
    await expect(page.getByRole('heading', { name: nome, level: 3 })).toBeVisible();
  }
  await expect(page.getByText('Grátis').first()).toBeVisible();
  expect(errors).toEqual([]);
});

test('landing e referência da API carregam sem erro nem violação de CSP', async ({ page }) => {
  const errors = watchConsole(page, [SESSION_PROBE]);
  await page.goto(PUBLIC + '/');
  await expect(page.getByRole('heading', { level: 1 })).toContainText('IBS, CBS e Imposto Seletivo');
  await page.getByRole('link', { name: 'Acessar o portal' }).first().click();
  await expect(page.getByLabel('E-mail')).toBeVisible();
  expect(errors).toEqual([]);

  await page.goto(PUBLIC + '/docs/');
  await expect(page).toHaveTitle(/Referência da API/);
});

test('cliente se cadastra, confirma o e-mail, ativa MFA e usa o portal; chave de API criada pela pessoa', async ({ page, request }) => {
  const errors = watchConsole(page, [SESSION_PROBE]);
  const { email, empresa, secret } = await signupAndLogin(page, request);
  await expect(page.getByRole('heading', { name: 'Visão geral', level: 1 })).toBeVisible();
  await expect(page.getByText(empresa).first()).toBeVisible();
  expect(await page.evaluate(() => sessionStorage.length + localStorage.length - (localStorage.getItem('tg.versao.vista') ? 1 : 0))).toBe(0);   // nenhuma credencial no navegador
  const cookies = await page.context().cookies();
  expect(cookies.find((c) => c.name.endsWith('tg_sess'))?.httpOnly).toBe(true);

  await page.getByRole('link', { name: 'Simular' }).click();
  await page.getByRole('button', { name: 'Simular' }).click();
  await expect(page.getByRole('cell', { name: 'CBS' })).toBeVisible({ timeout: 20_000 });

  await page.getByRole('link', { name: 'API keys' }).click();
  await page.getByLabel('Nome').fill('e2e-erp');
  await page.getByRole('button', { name: 'Criar' }).click();
  await expect(page.getByRole('dialog', { name: 'Nova API key' })).toBeVisible();
  await page.getByRole('button', { name: 'Já guardei' }).click();
  await expect(page.getByRole('cell', { name: 'e2e-erp' })).toBeVisible();

  // Sair encerra a sessão; entrar de novo pede o código do app.
  await page.getByRole('button', { name: 'Sair' }).click();
  await expect(page.getByLabel('E-mail')).toBeVisible();
  await login(page, PUBLIC, email, false, secret);
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();   // volta para onde estava (API keys) ou para a visão geral
  await expect(page.getByRole('link', { name: 'Sair' }).or(page.getByRole('button', { name: 'Sair' }))).toBeVisible();
  expect(errors).toEqual([]);
});

test('dono convida pessoa da equipe; convidada cria a conta pelo link e entra com o papel', async ({ browser, request }) => {
  const owner = await browser.newPage();
  await signupAndLogin(owner, request);
  await owner.getByRole('link', { name: 'Equipe' }).click();
  const guestEmail = `convidada-${Date.now()}@exemplo.test`;
  await owner.getByLabel('E-mail').fill(guestEmail);
  await owner.getByLabel('Papel').selectOption('fiscal');
  await owner.getByRole('button', { name: 'Enviar convite' }).click();
  await expect(owner.getByText(`Convite enviado para ${guestEmail}`)).toBeVisible();

  const guest = await browser.newPage();
  const link = await mailLink(request, guestEmail, '/portal/convite');
  await guest.goto(link.replace(/^https?:\/\/[^/]+/, PUBLIC));
  await expect(guest.getByText(/como fiscal/)).toBeVisible();
  await guest.getByLabel('Seu nome').fill('Joana Fiscal');
  await guest.getByLabel('Crie uma senha').fill(PASSWORD);
  await guest.getByRole('button', { name: 'Criar conta e aceitar' }).click();
  await expect(guest.getByText('Conta criada. Entre e configure a verificação em duas etapas.')).toBeVisible();
  await login(guest, PUBLIC, guestEmail, true);
  await expect(guest.getByRole('heading', { name: 'Visão geral', level: 1 })).toBeVisible();
  await expect(guest.getByRole('link', { name: 'Auditar XML' })).toBeVisible();
  await expect(guest.getByRole('link', { name: 'API keys' })).toHaveCount(0);   // fiscal não gerencia chaves
  await expect(guest.getByRole('link', { name: 'Equipe', exact: true })).toHaveCount(0);

  await owner.reload();
  await expect(owner.getByRole('cell', { name: 'Joana Fiscal' })).toBeVisible();
});

test('operador recebe convite, define senha, ativa MFA e opera o backoffice', async ({ page, request }) => {
  const errors = watchConsole(page, [SESSION_PROBE]);
  const email = `op-${Date.now()}@taxgateway.test`;
  // O primeiro operador vem do comando create-operator; aqui um operador por chave de automação convida outro.
  const r = await request.post(`${BACKOFFICE}/admin/v1/operadores`, {
    headers: { 'X-Admin-Key': OPERATOR_KEY, 'X-TaxGateway-Csrf': '1' },
    data: { email, nome: 'Operadora E2E', escopos: ['admin:commercial'] },
  });
  expect(r.status()).toBe(202);
  const link = await mailLink(request, email, '/backoffice/redefinir-senha');
  await page.goto(link.replace(/^https?:\/\/[^/]+/, BACKOFFICE));
  await page.getByLabel('Nova senha').fill(PASSWORD);
  await page.getByLabel('Repita a senha').fill(PASSWORD);
  await page.getByRole('button', { name: 'Salvar senha' }).click();
  await expect(page.getByText('Senha definida. Entre com a nova senha.')).toBeVisible();
  await login(page, BACKOFFICE, email, true);
  await expect(page.getByRole('heading', { name: 'Tenants', level: 1 })).toBeVisible();
  for (const [link, heading] of [['Planos', 'Planos'], ['Faturamento', 'Faturamento'], ['Revogar token', 'Revogar token OAuth'], ['Operadores', 'Operadores'], ['Trilha de auditoria', 'Trilha de auditoria'], ['Tenants', 'Tenants']]) {
    await page.getByRole('link', { name: link, exact: true }).click();
    await expect(page.getByRole('heading', { name: heading, level: 1 })).toBeVisible();
  }

  await expect(page.getByRole('link', { name: 'Alíquotas' })).toHaveCount(0);   // sem admin:fiscal
  expect(errors).toEqual([]);
});

test('todas as telas do portal abrem sem erro', async ({ page, request }) => {
  const errors = watchConsole(page, [SESSION_PROBE]);
  await signupAndLogin(page, request);
  const telas: [string, string][] = [
    ['Auditar XML', 'Auditar XML'], ['Lotes', 'Lotes'], ['Documentos auditados', 'Documentos auditados'], ['Classificação (NCM/NBS)', 'Classificação tributária'],
    ['Avisos', 'Avisos'], ['Webhooks', 'Webhooks'], ['CNPJs', 'CNPJs atendidos'],
    ['DF-e e certificado', 'DF-e e certificado A1'], ['LGPD: titulares', 'LGPD: direitos do titular'], ['Equipe', 'Equipe'], ['Plano e cobrança', 'Plano e cobrança'], ['Visão geral', 'Visão geral'],
  ];
  for (const [link, heading] of telas) {
    await page.getByRole('link', { name: link, exact: true }).click();
    await expect(page.getByRole('heading', { name: heading, level: 1 })).toBeVisible();
    await expect(page.getByRole('alert')).toHaveCount(0);
  }

  await page.getByRole('link', { name: /Maria/ }).click();
  await expect(page.getByRole('heading', { name: 'Minha conta', level: 1 })).toBeVisible();

  // LGPD: CPF sem documentos → lista vazia, sem erro.
  await page.getByRole('link', { name: 'LGPD: titulares', exact: true }).click();
  await page.getByLabel('CPF').fill('529.982.247-25');
  await page.getByRole('button', { name: 'Localizar' }).click();
  await expect(page.getByText('0 documento(s)')).toBeVisible();
  expect(errors).toEqual([]);
});

test('primeira auditoria com o XML de exemplo, comparação por item, histórico e classificação', async ({ page, request }) => {
  const errors = watchConsole(page, [SESSION_PROBE]);
  await signupAndLogin(page, request);
  await expect(page.getByText('Comece por aqui')).toBeVisible();

  // XML de exemplo com o CNPJ da própria empresa (D-J06): audita sem o usuário ter uma nota à mão.
  await page.getByRole('link', { name: 'Auditar XML', exact: true }).click();
  await page.getByRole('button', { name: 'Usar XML de exemplo' }).click();
  await expect(page.getByText(/Exemplo de homologação/)).toBeVisible();
  await page.getByRole('button', { name: 'Auditar', exact: true }).click();
  await expect(page.getByRole('heading', { name: /^Chave / })).toBeVisible();
  await expect(page.getByText('ASSINATURA_AUSENTE').first()).toBeVisible();
  await page.getByRole('button', { name: 'Comparação por item' }).click();
  const drawer = page.getByRole('dialog', { name: 'Detalhe do documento' });
  await expect(drawer.getByRole('region', { name: 'Item 1' })).toBeVisible();
  await page.keyboard.press('Escape');

  // Histórico (D-J01): o documento aparece e o detalhe abre.
  await page.getByRole('link', { name: 'Documentos auditados', exact: true }).click();
  await expect(page.getByRole('button', { name: /Ver detalhe do documento/ }).first()).toBeVisible();
  await page.getByRole('button', { name: /Ver detalhe do documento/ }).first().click();
  await expect(page.getByRole('dialog', { name: 'Detalhe do documento' })).toBeVisible();
  await page.keyboard.press('Escape');

  // Classificação (D-J02): NCM de bebida açucarada está no Imposto Seletivo; a regra geral vem primeiro.
  await page.getByRole('link', { name: 'Classificação (NCM/NBS)', exact: true }).click();
  await page.getByLabel('NCM (8 dígitos)').fill('2202.10.00');
  await page.getByRole('button', { name: 'Consultar' }).click();
  await expect(page.getByText('Sujeito ao Imposto Seletivo')).toBeVisible();
  await expect(page.getByRole('cell', { name: '000001' }).first()).toBeVisible();

  // Primeiros passos: "Audite uma nota" agora está feito.
  await page.getByRole('link', { name: 'Visão geral', exact: true }).click();
  await expect(page.locator('.onboarding li.done', { hasText: 'Audite uma nota' })).toBeVisible();
  expect(errors).toEqual([]);
});

test('central de ajuda, capturas, versão e novidades pelo nginx (sem violação de CSP)', async ({ page, request }) => {
  const errors = watchConsole(page);
  const broken: string[] = [];
  page.on('response', (r) => { if (r.url().includes('/ajuda/img/') && r.status() >= 400) broken.push(`${r.status()} ${r.url()}`); });

  // Versão: /version.json do build e GET /version da API passando pelo nginx, com o header em toda resposta.
  const asset = await (await request.get(PUBLIC + '/version.json')).json();
  const apiVersion = await request.get(PUBLIC + '/version');
  expect(apiVersion.headers()['x-taxgateway-version']).toBe(asset.version);
  expect((await apiVersion.json()).versao).toBe(asset.version);

  await page.goto(PUBLIC + '/ajuda');
  await expect(page.getByRole('heading', { name: 'Como podemos ajudar?' })).toBeVisible();
  await page.getByRole('searchbox', { name: 'Buscar na ajuda' }).fill('assinatura');
  await page.getByRole('link', { name: /Receber eventos/ }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Receber eventos (webhooks)' })).toBeVisible();
  for (const slug of ['primeiros-passos', 'simulacao', 'auditoria', 'lotes', 'credenciais', 'oem', 'dfe', 'lgpd', 'erros', 'glossario', 'portal', 'suporte']) {
    await page.goto(`${PUBLIC}/ajuda/${slug}`);
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    for (const img of await page.locator('.help-figure img').all()) {
      await img.scrollIntoViewIfNeeded();
      await expect.poll(() => img.evaluate((el: HTMLImageElement) => el.complete && el.naturalWidth)).toBeGreaterThan(0);
    }
  }
  await page.goto(PUBLIC + '/ajuda/novidades');
  await expect(page.getByRole('heading', { level: 2, name: new RegExp(`Versão ${asset.version}`) })).toBeVisible();

  // Landing: "Veja funcionando" troca a captura por aba.
  await page.goto(PUBLIC + '/#funcionando');
  await page.getByRole('tab', { name: 'Auditoria' }).click();
  const shot = page.locator('.showcase-frame img');
  await expect(shot).toHaveAttribute('src', '/ajuda/img/auditoria.jpg');
  await expect.poll(() => shot.evaluate((el: HTMLImageElement) => el.complete && el.naturalWidth)).toBeGreaterThan(0);

  expect(broken).toEqual([]);
  expect(errors).toEqual([]);
});

test('modal de novidades aparece uma vez quando a versão sobe', async ({ page, request }) => {
  const version = (await (await request.get(PUBLIC + '/version.json')).json()).version as string;
  await page.goto(PUBLIC + '/');
  await page.evaluate(() => localStorage.setItem('tg.versao.vista', '0.0.1'));   // "versão anterior" vista
  await signupAndLogin(page, request);
  const dialog = page.getByRole('dialog', { name: `Novidades da versão ${version}` });
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Entendi' }).click();
  await expect(dialog).toHaveCount(0);
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Visão geral', level: 1 })).toBeVisible();
  await expect(page.getByRole('dialog')).toHaveCount(0);
});
