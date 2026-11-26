import { createHmac } from 'node:crypto';
import { expect, type APIRequestContext, type Page } from '@playwright/test';

// Apoio dos E2E com contas de pessoas (D-U01): e-mails pelo Mailpit do compose e código TOTP calculado aqui.

export const PUBLIC = process.env.E2E_PUBLIC ?? 'http://127.0.0.1:8090';
export const BACKOFFICE = process.env.E2E_BACKOFFICE ?? 'http://127.0.0.1:8091';
export const MAILPIT = process.env.E2E_MAILPIT ?? 'http://127.0.0.1:8025';
export const OPERATOR_KEY = process.env.E2E_OPERATOR_KEY ?? 'tg_admin_dev_operador_fiscal_a_0000000000'; // chave estática SÓ de dev (automação)
export const PASSWORD = 'Uma frase longa e boa 2027!';

export function validCnpj(): string {
  const stem = `${Math.floor(60000000 + Math.random() * 9999999)}0001`;
  const dv = (s: string, w: number[]) => { const r = w.reduce((a, x, i) => a + Number(s[i]) * x, 0) % 11; return r < 2 ? 0 : 11 - r; };
  const d1 = dv(stem, [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]);
  return `${stem}${d1}${dv(stem + d1, [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2])}`;
}

function base32Decode(s: string): Buffer {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = '';
  for (const c of s.replace(/=+$/, '')) bits += alphabet.indexOf(c).toString(2).padStart(5, '0');
  const bytes: number[] = [];
  for (let i = 0; i + 8 <= bits.length; i += 8) bytes.push(parseInt(bits.slice(i, i + 8), 2));
  return Buffer.from(bytes);
}

/** Código TOTP (RFC 6238) do passo atual + `offset` passos. */
export function totp(secret: string, offset = 0): string {
  const step = Math.floor(Date.now() / 1000 / 30) + offset;
  const counter = Buffer.alloc(8);
  counter.writeBigInt64BE(BigInt(step));
  const h = createHmac('sha1', base32Decode(secret)).update(counter).digest();
  const o = h[h.length - 1] & 0x0f;
  const bin = ((h[o] & 0x7f) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3];
  return String(bin % 1_000_000).padStart(6, '0');
}

/** Espera o próximo passo de 30 s (o mesmo código não vale duas vezes: anti-replay). */
export async function nextStep(page: Page) {
  const ms = 30_000 - (Date.now() % 30_000) + 300;
  await page.waitForTimeout(ms);
}

/** Último link com `path` enviado para `email` (Mailpit). */
export async function mailLink(request: APIRequestContext, email: string, path: string): Promise<string> {
  let link: string | undefined;
  await expect.poll(async () => {
    const list = await (await request.get(`${MAILPIT}/api/v1/search?query=${encodeURIComponent(`to:"${email}"`)}`)).json() as { messages: { ID: string }[] };
    for (const m of list.messages) {
      const msg = await (await request.get(`${MAILPIT}/api/v1/message/${m.ID}`)).json() as { Text: string };
      const found = msg.Text.match(/https?:\/\/\S+/g)?.find((l) => l.includes(path));
      if (found) { link = found; return true; }
    }
    return false;
  }, { timeout: 20_000 }).toBe(true);
  return link!;
}

/** Cria conta pela tela de cadastro, confirma pelo e-mail, entra e ativa o MFA. Devolve o segredo TOTP. */
export async function signupAndLogin(page: Page, request: APIRequestContext, opts: { email?: string; empresa?: string } = {}) {
  const email = opts.email ?? `e2e-${Date.now()}-${Math.floor(Math.random() * 1e6)}@exemplo.test`;
  const empresa = opts.empresa ?? `E2E ${Date.now()}`;
  await page.goto(`${PUBLIC}/cadastro`);
  await page.getByLabel('Seu nome').fill('Maria Fiscal');
  await page.getByLabel('E-mail de trabalho').fill(email);
  await page.getByLabel('Senha').fill(PASSWORD);
  await page.getByLabel('Empresa', { exact: true }).fill(empresa);
  await page.getByLabel(/CNPJ da empresa/).fill(validCnpj());
  await page.getByRole('checkbox').check();
  await page.getByRole('button', { name: 'Criar conta' }).click();
  await expect(page.getByRole('heading', { name: 'Confira seu e-mail' })).toBeVisible();

  const link = await mailLink(request, email, '/portal/confirmar-email');
  await page.goto(link.replace(/^https?:\/\/[^/]+/, PUBLIC));
  await expect(page.getByRole('heading', { name: 'E-mail confirmado' })).toBeVisible();

  const secret = await login(page, PUBLIC, email, true);
  return { email, empresa, secret };
}

/** Entra (e-mail + senha) e passa pelo MFA: configura (lê a chave da tela) ou verifica com o segredo dado. */
export async function login(page: Page, base: string, email: string, firstTime: boolean, secret?: string): Promise<string> {
  const area = base === BACKOFFICE ? 'backoffice' : 'portal';
  await page.goto(`${base}/${area}/entrar`);
  await page.getByLabel('E-mail').fill(email);
  await page.getByLabel('Senha').fill(PASSWORD);
  await page.getByRole('button', { name: 'Entrar' }).click();
  if (firstTime) {
    await expect(page.getByRole('heading', { name: 'Proteja sua conta' })).toBeVisible();
    const key = (await page.locator('ol.help-steps code').textContent())!.trim();
    await page.getByLabel('Código de 6 dígitos').fill(totp(key));
    await page.getByRole('button', { name: 'Ativar e continuar' }).click();
    await expect(page.getByRole('heading', { name: 'Guarde seus códigos de recuperação' })).toBeVisible();
    await page.getByRole('button', { name: 'Já guardei, continuar' }).click();
    return key;
  }
  await expect(page.getByRole('heading', { name: 'Verificação em duas etapas' })).toBeVisible();
  await nextStep(page);
  await page.getByLabel('Código de 6 dígitos').fill(totp(secret!));
  await page.getByRole('button', { name: 'Entrar' }).click();
  return secret!;
}
