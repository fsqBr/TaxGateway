import { expect, test, type Page } from '@playwright/test';

const PUBLIC = process.env.E2E_PUBLIC ?? 'http://127.0.0.1:8088';
const BACKOFFICE = process.env.E2E_BACKOFFICE ?? 'http://127.0.0.1:8089';
const OPERATOR = process.env.E2E_OPERATOR_KEY ?? 'tg_admin_dev_operador_fiscal_a_0000000000'; // chave SÓ de dev

/** Falha o teste se a página registrar erro de console ou violação de CSP. */
function watchConsole(page: Page): string[] {
  const errors: string[] = [];
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
  page.on('pageerror', (e) => errors.push(e.message));
  return errors;
}

function validCnpj(): string {
  const stem = `${Math.floor(60000000 + Math.random() * 9999999)}0001`;
  const dv = (s: string, w: number[]) => { const r = w.reduce((a, x, i) => a + Number(s[i]) * x, 0) % 11; return r < 2 ? 0 : 11 - r; };
  const d1 = dv(stem, [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]);
  return `${stem}${d1}${dv(stem + d1, [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2])}`;
}

test('landing e referência da API carregam sem erro nem violação de CSP', async ({ page }) => {
  const errors = watchConsole(page);
  await page.goto(PUBLIC + '/');
  await expect(page.getByRole('heading', { level: 1 })).toContainText('IBS, CBS e Imposto Seletivo');
  await page.getByRole('link', { name: 'Acessar o portal' }).first().click();
  await expect(page.getByLabel('API key')).toBeVisible();
  expect(errors).toEqual([]);

  await page.goto(PUBLIC + '/docs/');
  await expect(page).toHaveTitle(/Referência da API/);
});

test('operador cria tenant no backoffice e o cliente usa o portal com a chave emitida', async ({ page }) => {
  const errors = watchConsole(page);
  const nome = `E2E ${Date.now()}`;

  // Backoffice
  await page.goto(BACKOFFICE + '/backoffice');
  await page.getByLabel('Chave de operador').fill(OPERATOR);
  await page.getByRole('button', { name: 'Entrar' }).click();
  await expect(page.getByRole('heading', { name: 'Tenants', level: 1 })).toBeVisible();
  await page.getByLabel('Nome', { exact: true }).fill(nome);
  await page.getByLabel('CNPJ raiz (matriz)').fill(validCnpj());
  await page.getByLabel('Plano').selectOption('GROWTH');
  await page.getByRole('button', { name: 'Criar tenant' }).click();
  const secret = page.getByRole('dialog', { name: /Primeira API key/ }).locator('code');
  await expect(secret).toContainText('tg_');
  const apiKey = (await secret.textContent())!.trim();
  await expect(page.getByRole('cell', { name: new RegExp(nome) })).toBeVisible();

  // Portal
  await page.goto(PUBLIC + '/portal');
  await page.getByLabel('API key').fill(apiKey);
  await page.getByRole('button', { name: 'Entrar' }).click();
  await expect(page.getByRole('heading', { name: 'Visão geral', level: 1 })).toBeVisible();
  await expect(page.getByText(nome).first()).toBeVisible();

  await page.getByRole('link', { name: 'Simular' }).click();
  await page.getByRole('button', { name: 'Simular' }).click();
  await expect(page.getByRole('cell', { name: 'CBS' })).toBeVisible({ timeout: 20_000 });

  await page.getByRole('link', { name: 'API keys' }).click();
  await page.getByLabel('Nome').fill('e2e-nova');
  await page.getByRole('button', { name: 'Criar' }).click();
  await expect(page.getByRole('dialog', { name: 'Nova API key' })).toBeVisible();
  await page.getByRole('button', { name: 'Já guardei' }).click();
  await expect(page.getByRole('cell', { name: 'e2e-nova' })).toBeVisible();

  // Sair limpa a sessão
  await page.getByRole('button', { name: 'Sair' }).click();
  await expect(page.getByLabel('API key')).toBeVisible();
  expect(await page.evaluate(() => sessionStorage.length)).toBe(0);
  expect(errors).toEqual([]);
});

test('todas as telas do portal e do backoffice abrem sem erro', async ({ page }) => {
  const errors = watchConsole(page);
  await page.goto(BACKOFFICE + '/backoffice');
  await page.getByLabel('Chave de operador').fill(OPERATOR);
  await page.getByRole('button', { name: 'Entrar' }).click();
  for (const [link, heading] of [['Alíquotas', 'Alíquotas'], ['Faturamento', 'Faturamento'], ['Revogar token', 'Revogar token OAuth'], ['Tenants', 'Tenants']]) {
    await page.getByRole('link', { name: link, exact: true }).click();
    await expect(page.getByRole('heading', { name: heading, level: 1 })).toBeVisible();
  }

  // Cria um tenant para entrar no portal com a chave emitida.
  await page.getByLabel('Nome', { exact: true }).fill(`E2E telas ${Date.now()}`);
  await page.getByLabel('CNPJ raiz (matriz)').fill(validCnpj());
  await page.getByRole('button', { name: 'Criar tenant' }).click();
  const apiKey = (await page.getByRole('dialog', { name: /Primeira API key/ }).locator('code').textContent())!.trim();

  await page.goto(PUBLIC + '/portal');
  await page.getByLabel('API key').fill(apiKey);
  await page.getByRole('button', { name: 'Entrar' }).click();
  const telas: [string, string][] = [
    ['Auditar XML', 'Auditar XML'], ['Lotes', 'Lotes'], ['Webhooks', 'Webhooks'], ['CNPJs', 'CNPJs atendidos'],
    ['DF-e e certificado', 'DF-e e certificado A1'], ['LGPD: titulares', 'LGPD: direitos do titular'], ['Visão geral', 'Visão geral'],
  ];
  for (const [link, heading] of telas) {
    await page.getByRole('link', { name: link, exact: true }).click();
    await expect(page.getByRole('heading', { name: heading, level: 1 })).toBeVisible();
    await expect(page.getByRole('alert')).toHaveCount(0);
  }

  // LGPD: CPF sem documentos → lista vazia, sem erro.
  await page.getByRole('link', { name: 'LGPD: titulares', exact: true }).click();
  await page.getByLabel('CPF').fill('529.982.247-25');
  await page.getByRole('button', { name: 'Localizar' }).click();
  await expect(page.getByText('0 documento(s)')).toBeVisible();
  expect(errors).toEqual([]);
});
