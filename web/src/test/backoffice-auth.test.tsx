import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App from '../App';
import { ApiError, AUTH_EVENT, backoffice, codeOf, CSRF_HEADER, describeError, downloadFile, request } from '../lib/api';
import { PLANO_ESCOLHIDO_KEY } from '../auth/AuthPages';
import { currentMonth } from '../lib/format';
import { useSession } from '../auth/session';

// O QR code usa <canvas>, que o jsdom não implementa.
vi.mock('qrcode', () => ({ default: { toCanvas: vi.fn(() => Promise.resolve()) } }));

type Req = { method: string; url: string; path: string; query: URLSearchParams; headers: Record<string, string>; body?: unknown };
type Handler = (req: Req) => { status?: number; json?: unknown } | undefined;

let calls: Req[] = [];

function mockApi(handler: Handler) {
  calls = [];
  vi.stubGlobal('fetch', vi.fn(async (input: string, init: RequestInit = {}) => {
    const u = new URL(input, 'http://x');
    const req: Req = {
      method: init.method ?? 'GET',
      url: input,
      path: u.pathname,
      query: u.searchParams,
      headers: (init.headers ?? {}) as Record<string, string>,
      body: typeof init.body === 'string' ? JSON.parse(init.body) : undefined,
    };
    calls.push(req);
    const r = handler(req) ?? { status: 404, json: { code: 'ERR_NOT_FOUND', title: 'Não encontrado' } };
    const status = r.status ?? 200;
    const text = r.json === undefined ? '' : JSON.stringify(r.json);
    return new Response(text || null, { status, headers: { 'content-type': status >= 400 ? 'application/problem+json' : 'application/json' } });
  }));
}

/** fetch que devolve uma resposta crua (tipo de conteúdo e statusText escolhidos pelo teste). */
function rawFetch(make: () => Response) {
  const fn = vi.fn(async (_input: string, _init?: RequestInit) => make());
  vi.stubGlobal('fetch', fn);
  return fn;
}

const renderAt = (path: string) => render(<MemoryRouter initialEntries={[path]}><App /></MemoryRouter>);
const problem = (status: number, code: string, title: string, detail?: string) => ({ status, json: { code, title, detail } });
const unauth = problem(401, 'ERR_UNAUTHENTICATED', 'Sessão expirada');
const posted = (path: string, method = 'POST') => calls.find((c) => c.path === path && c.method === method);

const operador = (escopos: string[], mfa = 'ok'): Handler => (r) => {
  if (r.path === '/admin/v1/conta/sessao') return { json: { usuario: { id: 'o1', nome: 'Fiscal B', email: 'fb@tg.test' }, mfa, escopos } };
  if (r.path === '/admin/v1/me') return { json: { operador: 'fb@tg.test', scopes: escopos } };
  return undefined;
};

const chain = (...hs: Handler[]): Handler => (r) => {
  for (const h of hs) {
    const x = h(r);
    if (x) return x;
  }
  return undefined;
};

afterEach(() => {
  vi.restoreAllMocks();
});

// ---------------------------------------------------------------------------------------------------------------------
// Cliente HTTP (lib/api.ts)
// ---------------------------------------------------------------------------------------------------------------------

describe('cliente HTTP: request, erros e download', () => {
  it('corpo cru com e sem content-type, query sem vazios e idempotência', async () => {
    const fn = rawFetch(() => new Response('{"ok":true}', { status: 200, headers: { 'content-type': 'application/json' } }));
    const r = await request<{ ok: boolean }>('portal', '/v1/x', { method: 'PUT', rawBody: 'a;b', contentType: 'text/csv', query: { a: 1, b: undefined, c: null, d: '', e: true, 'f g': 'h/i' }, idempotent: true });
    expect(r).toEqual({ ok: true });
    const [url, init] = fn.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/v1/x?a=1&e=true&f%20g=h%2Fi');
    const h = init.headers as Record<string, string>;
    expect(init.body).toBe('a;b');
    expect(h['Content-Type']).toBe('text/csv');
    expect(h[CSRF_HEADER]).toBe('1');
    expect(h['Idempotency-Key']).toMatch(/^[0-9a-f-]{36}$/);

    await request('portal', '/v1/y', { rawBody: 'x', query: { vazio: '' } });
    const [url2, init2] = fn.mock.calls[1] as [string, RequestInit];
    expect(url2).toBe('/v1/y');
    expect((init2.headers as Record<string, string>)['Content-Type']).toBeUndefined();
    expect((init2.headers as Record<string, string>)['Idempotency-Key']).toBeUndefined();
    expect(init2.method).toBe('GET');
  });

  it('sucesso que não é JSON devolve o texto; JSON inválido também', async () => {
    rawFetch(() => new Response('texto puro', { status: 200, headers: { 'content-type': 'text/plain' } }));
    expect(await request('portal', '/v1/t')).toBe('texto puro');
    rawFetch(() => new Response('{quebrado', { status: 200, headers: { 'content-type': 'application/json' } }));
    expect(await request('portal', '/v1/t')).toBe('{quebrado');
    rawFetch(() => new Response(null, { status: 204 }));
    expect(await request('portal', '/v1/t')).toBe('');
  });

  it('erro sem JSON usa HTTP_<status> e o statusText; erro OAuth usa error/error_description', async () => {
    rawFetch(() => new Response('<html>bad gateway</html>', { status: 502, statusText: 'Bad Gateway', headers: { 'content-type': 'text/html' } }));
    const e1 = (await request('portal', '/v1/a').catch((e) => e)) as ApiError;
    expect(e1).toBeInstanceOf(ApiError);
    expect(e1.code).toBe('HTTP_502');
    expect(e1.title).toBe('Bad Gateway');
    expect(e1.message).toBe('Bad Gateway');

    rawFetch(() => new Response(JSON.stringify({ error: 'invalid_client', error_description: 'Cliente desconhecido' }), { status: 400, headers: { 'content-type': 'application/json' } }));
    const e2 = (await request('portal', '/oauth/token').catch((e) => e)) as ApiError;
    expect(e2.code).toBe('invalid_client');
    expect(e2.title).toBe('Cliente desconhecido');
    expect(codeOf(e2)).toBe('invalid_client');
    expect(codeOf(new Error('x'))).toBeUndefined();
  });

  it('avisa a aplicação em 401 e ERR_MFA_NECESSARIO, exceto quando silent', async () => {
    const seen: unknown[] = [];
    const on = (ev: Event) => seen.push((ev as CustomEvent).detail);
    window.addEventListener(AUTH_EVENT, on);
    try {
      mockApi((r) => (r.path === '/admin/v1/a' ? unauth : r.path === '/admin/v1/b' ? problem(403, 'ERR_MFA_NECESSARIO', 'MFA') : problem(403, 'ERR_FORBIDDEN_SCOPE', 'Escopo')));
      await backoffice('/admin/v1/a').catch(() => undefined);
      await backoffice('/admin/v1/b').catch(() => undefined);
      await backoffice('/admin/v1/c').catch(() => undefined);
      await backoffice('/admin/v1/a', { silent: true }).catch(() => undefined);
      expect(seen).toEqual([{ realm: 'backoffice', code: 'ERR_UNAUTHENTICATED' }, { realm: 'backoffice', code: 'ERR_MFA_NECESSARIO' }]);
    } finally {
      window.removeEventListener(AUTH_EVENT, on);
    }
  });

  it('describeError: 401, 429, mensagens amigáveis, detalhe e erros comuns', () => {
    expect(describeError(new ApiError(401, 'ERR_CREDENCIAIS', 'x'))).toBe('E-mail ou senha incorretos.');
    expect(describeError(new ApiError(401, 'ERR_UNAUTHENTICATED', 'x'))).toContain('Entre novamente');
    expect(describeError(new ApiError(429, 'ERR_RATE', 'x'))).toContain('Muitas requisições');
    expect(describeError(new ApiError(402, 'ERR_TETO_EXCEDENTE', 'x'))).toContain('teto de excedente');
    expect(describeError(new ApiError(400, 'ERR_X', 'Título', 'Detalhe'))).toBe('Título. Detalhe');
    expect(describeError(new ApiError(400, 'ERR_X', 'Só título'))).toBe('Só título');
    expect(new ApiError(400, 'ERR_X', 'T', 'D').message).toBe('T: D');
    expect(describeError(new Error('falhou'))).toBe('falhou');
    expect(describeError('texto')).toBe('texto');
  });

  it('downloadFile baixa com a sessão, filtra a query e libera a URL', async () => {
    const create = vi.fn(() => 'blob:arquivo');
    const revoke = vi.fn();
    Object.defineProperty(URL, 'createObjectURL', { value: create, configurable: true, writable: true });
    Object.defineProperty(URL, 'revokeObjectURL', { value: revoke, configurable: true, writable: true });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    const fn = rawFetch(() => new Response('a;b\n1;2', { status: 200, headers: { 'content-type': 'text/csv' } }));
    await downloadFile('/v1/relatorio.csv', 'relatorio.csv', { mes: '2026-10', vazio: undefined, cnpj: '' });
    const [url, init] = fn.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/v1/relatorio.csv?mes=2026-10');
    expect(init.credentials).toBe('same-origin');
    expect((init.headers as Record<string, string>)[CSRF_HEADER]).toBe('1');
    expect(create).toHaveBeenCalledTimes(1);
    expect(click).toHaveBeenCalledTimes(1);
    expect(revoke).toHaveBeenCalledWith('blob:arquivo');

    await downloadFile('/v1/sem-query.csv', 'x.csv');
    expect(fn.mock.calls[1][0]).toBe('/v1/sem-query.csv');
    await downloadFile('/v1/query-vazia.csv', 'x.csv', { a: undefined });
    expect(fn.mock.calls[2][0]).toBe('/v1/query-vazia.csv');
  });

  it('downloadFile com erro: problem+json, JSON sem campos e resposta que não é JSON', async () => {
    rawFetch(() => new Response(JSON.stringify({ code: 'ERR_SEM_DADOS', title: 'Sem dados', detail: 'Nada no mês' }), { status: 404, headers: { 'content-type': 'application/problem+json' } }));
    const e1 = (await downloadFile('/v1/r.csv', 'r.csv').catch((e) => e)) as ApiError;
    expect(e1).toMatchObject({ status: 404, code: 'ERR_SEM_DADOS', title: 'Sem dados', detail: 'Nada no mês' });

    rawFetch(() => new Response('{}', { status: 500, headers: { 'content-type': 'application/json' } }));
    const e2 = (await downloadFile('/v1/r.csv', 'r.csv').catch((e) => e)) as ApiError;
    expect(e2).toMatchObject({ code: 'HTTP_500', title: 'Falha no download' });

    rawFetch(() => new Response('<html/>', { status: 502, statusText: 'Bad Gateway', headers: { 'content-type': 'text/html' } }));
    const e3 = (await downloadFile('/v1/r.csv', 'r.csv').catch((e) => e)) as ApiError;
    expect(e3).toMatchObject({ code: 'HTTP_502', title: 'Falha no download', detail: 'Bad Gateway' });
  });
});

// ---------------------------------------------------------------------------------------------------------------------
// Sessão (auth/session.tsx)
// ---------------------------------------------------------------------------------------------------------------------

const tenantA = {
  tenant_id: 't1', nome: 'Empresa X', cnpj_raiz: '11222333', plano: 'STARTER', situacao: 'ACTIVE', auto_provisionar_cnpj: true, retencao_xml_dias: 90,
  criado_em: '2026-11-01T10:00:00Z',
  clientes_oauth: [
    { client_id: 'c-ativo', nome: 'erp', revogado_em: null, criado_em: '2026-11-02T10:00:00Z' },
    { client_id: 'c-rev', nome: 'velho', revogado_em: '2026-11-03T10:00:00Z', criado_em: '2026-10-02T10:00:00Z' },
  ],
};
const tenantB = { ...tenantA, tenant_id: 't2', nome: 'Empresa Y', cnpj_raiz: '44555666', situacao: 'SUSPENDED', auto_provisionar_cnpj: false, retencao_xml_dias: null, clientes_oauth: [] };

const tenantsBase: Handler = (r) => {
  if (r.path === '/admin/v1/tenants' && r.method === 'GET') return { json: { tenants: [tenantA, tenantB], total: 2, pagina: 1 } };
  if (r.path === '/admin/v1/plans') return { json: { planos: [{ codigo: 'STARTER' }, { codigo: 'PRO' }] } };
  return undefined;
};

describe('sessão do backoffice', () => {
  it('401 em qualquer chamada leva ao login do backoffice', async () => {
    let expirou = false;
    mockApi(chain((r) => {
      if (r.path === '/admin/v1/conta/sessao' && expirou) return unauth;
      if (r.path === '/admin/v1/tenants') { expirou = true; return unauth; }
      return undefined;
    }, operador([]), tenantsBase));
    renderAt('/backoffice');
    // Obs.: o aviso "Sua sessão terminou" (state.expirou) não aparece: setSessao(null) renderiza antes da navegação
    // (o react-router 7 navega em startTransition) e o <Navigate> do RequireSession troca o state. Ver relatório.
    expect(await screen.findByText('Conta de operador (e-mail, senha e MFA).')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Esqueci minha senha' })).toHaveAttribute('href', '/backoffice/esqueci-senha');
  });

  it('ERR_MFA_NECESSARIO vai ao MFA; evento de outra área é ignorado', async () => {
    mockApi(chain(operador([]), tenantsBase));
    renderAt('/backoffice');
    await screen.findByRole('heading', { name: 'Tenants', level: 1 });
    const before = calls.filter((c) => c.path === '/admin/v1/tenants').length;
    act(() => { window.dispatchEvent(new CustomEvent(AUTH_EVENT, { detail: { realm: 'portal', code: 'ERR_UNAUTHENTICATED' } })); });
    expect(screen.getByRole('heading', { name: 'Tenants', level: 1 })).toBeInTheDocument();
    // A sessão em cache passa a "verificar": a pessoa fica na verificação (antes voltava para a área em laço).
    act(() => { window.dispatchEvent(new CustomEvent(AUTH_EVENT, { detail: { realm: 'backoffice', code: 'ERR_MFA_NECESSARIO' } })); });
    expect(await screen.findByRole('heading', { name: 'Verificação em duas etapas' })).toBeInTheDocument();
    expect(calls.filter((c) => c.path === '/admin/v1/tenants').length).toBe(before);
  });

  it('sessão sem MFA concluído leva à verificação; useSession fora do provider falha', async () => {
    mockApi(operador([], 'verificar'));
    renderAt('/backoffice/planos');
    expect(await screen.findByRole('heading', { name: 'Verificação em duas etapas' })).toBeInTheDocument();
    const Probe = () => { useSession(); return null; };
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    expect(() => render(<Probe />)).toThrow('useSession fora do SessionProvider');
  });
});

// ---------------------------------------------------------------------------------------------------------------------
// Telas de conta (auth/AuthPages.tsx)
// ---------------------------------------------------------------------------------------------------------------------

describe('telas de conta', () => {
  it('login: erro de bloqueio e e-mail não confirmado com reenvio', async () => {
    let tentativa = 0;
    mockApi((r) => {
      if (r.path === '/v1/conta/sessao') return unauth;
      if (r.path === '/v1/conta/entrar') {
        tentativa++;
        return tentativa === 1 ? problem(423, 'ERR_CONTA_BLOQUEADA', 'Conta bloqueada', 'Tente em 15 minutos') : problem(403, 'ERR_EMAIL_NAO_CONFIRMADO', 'E-mail não confirmado');
      }
      if (r.path === '/v1/conta/reenviar-confirmacao') return { status: 202, json: {} };
      return undefined;
    });
    renderAt('/portal/entrar');
    await userEvent.type(await screen.findByLabelText('E-mail'), 'maria@x.test');
    await userEvent.type(screen.getByLabelText('Senha'), 'qualquer coisa');
    await userEvent.click(screen.getByRole('button', { name: 'Entrar' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Conta bloqueada. Tente em 15 minutos');
    expect(screen.queryByRole('button', { name: 'Reenviar e-mail de confirmação' })).toBeNull();

    await userEvent.click(screen.getByRole('button', { name: 'Entrar' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Reenviar e-mail de confirmação' }));
    expect(await screen.findByText(/enviamos um novo link para maria@x.test/)).toBeInTheDocument();
    expect(posted('/v1/conta/reenviar-confirmacao')!.body).toEqual({ email: 'maria@x.test' });
    expect(screen.getByRole('link', { name: 'Criar conta grátis' })).toBeInTheDocument();
  });

  it('login do backoffice com sucesso vai ao MFA', async () => {
    let logado = false;
    mockApi((r) => {
      if (r.path === '/admin/v1/conta/sessao') return logado ? operador([], 'verificar')(r) : unauth;
      if (r.path === '/admin/v1/conta/entrar') { logado = true; return { json: { mfa: 'verificar' } }; }
      return undefined;
    });
    renderAt('/backoffice/entrar');
    await userEvent.type(await screen.findByLabelText('E-mail'), 'fb@tg.test');
    await userEvent.type(screen.getByLabelText('Senha'), 'senha longa demais');
    await userEvent.click(screen.getByRole('button', { name: 'Entrar' }));
    expect(await screen.findByRole('heading', { name: 'Verificação em duas etapas' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Criar conta grátis' })).toBeNull();
  });

  it('MFA configurar: segredo, QR, código inválido, códigos de recuperação e continuar', async () => {
    let mfa = 'configurar';
    let ativacoes = 0;
    mockApi(chain((r) => {
      if (r.path === '/admin/v1/conta/sessao') return operador([], mfa)(r);
      if (r.path === '/admin/v1/conta/mfa/iniciar') return { json: { segredo: 'JBSWY3DPEHPK3PXP', otpauth_uri: 'otpauth://totp/TG:fb?secret=JBSWY3DPEHPK3PXP' } };
      if (r.path === '/admin/v1/conta/mfa/ativar') {
        ativacoes++;
        if (ativacoes === 1) return problem(400, 'ERR_CODIGO_INVALIDO', 'Código inválido');
        mfa = 'ok';
        return { json: { codigos_recuperacao: ['AAAA-1111', 'BBBB-2222'] } };
      }
      return undefined;
    }, operador([]), tenantsBase));
    const writeText = vi.fn(async () => undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

    renderAt('/backoffice/mfa');
    expect(await screen.findByRole('heading', { name: 'Proteja sua conta' })).toBeInTheDocument();
    expect(await screen.findByText('JBSWY3DPEHPK3PXP')).toBeInTheDocument();
    const QRCode = (await import('qrcode')).default as unknown as { toCanvas: ReturnType<typeof vi.fn> };
    await waitFor(() => expect(QRCode.toCanvas).toHaveBeenCalled());

    const codigo = screen.getByLabelText('Código de 6 dígitos');
    await userEvent.type(codigo, '12a');
    expect(screen.getByRole('button', { name: 'Ativar e continuar' })).toBeDisabled();
    await userEvent.type(codigo, '3456');
    await userEvent.click(screen.getByRole('button', { name: 'Ativar e continuar' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Código inválido');
    await userEvent.click(screen.getByRole('button', { name: 'Ativar e continuar' }));

    expect(await screen.findByRole('heading', { name: 'Guarde seus códigos de recuperação' })).toBeInTheDocument();
    expect(posted('/admin/v1/conta/mfa/ativar')!.body).toEqual({ codigo: '123456' });
    expect(screen.getByText('AAAA-1111')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Baixar \.txt/ }).getAttribute('href')).toContain(encodeURIComponent('AAAA-1111\nBBBB-2222'));
    await userEvent.click(screen.getByRole('button', { name: /Copiar/ }));
    expect(await screen.findByRole('button', { name: /Copiado/ })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Já guardei, continuar' }));
    expect(await screen.findByRole('heading', { name: 'Tenants', level: 1 })).toBeInTheDocument();
  });

  it('códigos de recuperação: falha ao copiar mantém "Copiar"', async () => {
    let mfa = 'configurar';
    mockApi((r) => {
      if (r.path === '/admin/v1/conta/sessao') return operador([], mfa)(r);
      if (r.path === '/admin/v1/conta/mfa/iniciar') return { json: { segredo: 'S', otpauth_uri: 'otpauth://x' } };
      if (r.path === '/admin/v1/conta/mfa/ativar') { mfa = 'ok'; return { json: { codigos_recuperacao: ['CCCC-3333'] } }; }
      return undefined;
    });
    renderAt('/backoffice/mfa');
    await userEvent.type(await screen.findByLabelText('Código de 6 dígitos'), '654321');
    Object.defineProperty(navigator, 'clipboard', { value: { writeText: vi.fn(async () => { throw new Error('negado'); }) }, configurable: true });
    await userEvent.click(screen.getByRole('button', { name: 'Ativar e continuar' }));
    await screen.findByText('CCCC-3333');
    await userEvent.click(screen.getByRole('button', { name: /Copiar/ }));
    expect(screen.getByRole('button', { name: /Copiar/ })).toBeInTheDocument();
  });

  it('MFA verificar: código de recuperação recusado, volta ao código do app e entra', async () => {
    let mfa = 'verificar';
    mockApi(chain((r) => {
      if (r.path === '/admin/v1/conta/sessao') return operador([], mfa)(r);
      if (r.path === '/admin/v1/conta/mfa/verificar') {
        if ((r.body as { codigo_recuperacao?: string }).codigo_recuperacao) return problem(400, 'ERR_CODIGO_INVALIDO', 'Código de recuperação inválido');
        mfa = 'ok';
        return { status: 204 };
      }
      return undefined;
    }, operador([]), tenantsBase));
    renderAt('/backoffice');
    expect(await screen.findByText(/Olá, Fiscal\. Digite o código do app autenticador\./)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Perdi o celular: usar código de recuperação' }));
    await userEvent.type(screen.getByLabelText('Código de recuperação'), 'abcd-1234');
    await userEvent.click(screen.getByRole('button', { name: 'Entrar' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Código de recuperação inválido');
    expect(posted('/admin/v1/conta/mfa/verificar')!.body).toEqual({ codigo_recuperacao: 'ABCD-1234' });
    await userEvent.click(screen.getByRole('button', { name: 'Usar o código do app' }));
    await userEvent.type(screen.getByLabelText('Código de 6 dígitos'), '123456');
    await userEvent.click(screen.getByRole('button', { name: 'Entrar' }));
    expect(await screen.findByRole('heading', { name: 'Tenants', level: 1 })).toBeInTheDocument();
  });

  it('MFA sem sessão volta ao login', async () => {
    mockApi((r) => (r.path === '/admin/v1/conta/sessao' ? unauth : undefined));
    renderAt('/backoffice/mfa');
    expect(await screen.findByText('Conta de operador (e-mail, senha e MFA).')).toBeInTheDocument();
  });

  it('esqueci a senha: erro e depois confirmação de envio', async () => {
    let n = 0;
    mockApi((r) => {
      if (r.path === '/admin/v1/conta/sessao') return unauth;
      if (r.path === '/admin/v1/conta/senha/esqueci') return ++n === 1 ? problem(429, 'ERR_RATE', 'Devagar') : { status: 202, json: {} };
      return undefined;
    });
    renderAt('/backoffice/esqueci-senha');
    await userEvent.type(await screen.findByLabelText('E-mail'), 'fb@tg.test');
    await userEvent.click(screen.getByRole('button', { name: 'Enviar link' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Muitas requisições');
    await userEvent.click(screen.getByRole('button', { name: 'Enviar link' }));
    expect(await screen.findByText(/Se houver uma conta com fb@tg.test/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: '← Voltar ao login' })).toHaveAttribute('href', '/backoffice/entrar');
  });

  it('redefinir senha: senhas diferentes, erro do servidor e sucesso volta ao login com aviso', async () => {
    let n = 0;
    mockApi((r) => {
      if (r.path === '/admin/v1/conta/sessao') return unauth;
      if (r.path === '/admin/v1/conta/senha/redefinir') return ++n === 1 ? problem(400, 'ERR_TOKEN', 'Link expirado') : { status: 204 };
      return undefined;
    });
    renderAt('/backoffice/redefinir-senha?token=tk1');
    expect(await screen.findByRole('heading', { name: 'Defina sua senha' })).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText('Nova senha'), 'uma frase longa e boa');
    await userEvent.type(screen.getByLabelText('Repita a senha'), 'outra frase');
    await userEvent.click(screen.getByRole('button', { name: 'Salvar senha' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('As senhas não conferem.');
    expect(posted('/admin/v1/conta/senha/redefinir')).toBeUndefined();
    await userEvent.clear(screen.getByLabelText('Repita a senha'));
    await userEvent.type(screen.getByLabelText('Repita a senha'), 'uma frase longa e boa');
    await userEvent.click(screen.getByRole('button', { name: 'Salvar senha' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Link expirado');
    await userEvent.click(screen.getByRole('button', { name: 'Salvar senha' }));
    expect(await screen.findByText('Senha definida. Entre com a nova senha.')).toBeInTheDocument();
    expect(posted('/admin/v1/conta/senha/redefinir')!.body).toEqual({ token: 'tk1', senha: 'uma frase longa e boa' });
  });

  it('redefinir senha do portal tem outro título', async () => {
    mockApi((r) => (r.path === '/v1/conta/sessao' ? unauth : undefined));
    renderAt('/portal/redefinir-senha');
    expect(await screen.findByRole('heading', { name: 'Criar nova senha' })).toBeInTheDocument();
  });

  it('confirmar e-mail: sucesso lembra o plano escolhido', async () => {
    sessionStorage.setItem(PLANO_ESCOLHIDO_KEY, 'PROFISSIONAL_PLUS');
    mockApi((r) => (r.path === '/v1/conta/sessao' ? unauth : r.path === '/v1/conta/confirmar-email' ? { status: 204 } : undefined));
    renderAt('/portal/confirmar-email?token=abc');
    expect(await screen.findByRole('heading', { name: 'E-mail confirmado' })).toBeInTheDocument();
    expect(screen.getByText(/Profissional Plus/)).toBeInTheDocument();
    expect(posted('/v1/conta/confirmar-email')!.body).toEqual({ token: 'abc' });
    expect(screen.getByRole('link', { name: 'Ir para o login' })).toBeInTheDocument();
  });

  it('confirmar e-mail: link inválido', async () => {
    mockApi((r) => (r.path === '/v1/conta/sessao' ? unauth : r.path === '/v1/conta/confirmar-email' ? problem(400, 'ERR_TOKEN', 'Link inválido', 'Peça outro') : undefined));
    renderAt('/portal/confirmar-email');
    expect(await screen.findByRole('heading', { name: 'Não foi possível confirmar' })).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent('Link inválido. Peça outro');
    expect(posted('/v1/conta/confirmar-email')!.body).toEqual({ token: '' });
  });

  it('convite sem conta: cria a conta e vai ao login com aviso', async () => {
    mockApi((r) => {
      if (r.path === '/v1/conta/sessao') return unauth;
      if (r.path === '/v1/conta/convite') return { json: { empresa: 'Empresa X', email: 'novo@x.test', papel: 'fiscal', tem_conta: false } };
      if (r.path === '/v1/conta/convite/aceitar') return { json: { proximo: 'entrar' } };
      return undefined;
    });
    renderAt('/portal/convite?token=tk9');
    expect(await screen.findByText(/Você foi convidado\(a\) para acessar/)).toHaveTextContent('Empresa X');
    expect(calls.find((c) => c.path === '/v1/conta/convite')!.query.get('token')).toBe('tk9');
    await userEvent.type(screen.getByLabelText('Seu nome'), 'Nova Pessoa');
    await userEvent.type(screen.getByLabelText('Crie uma senha'), 'uma frase longa e boa');
    await userEvent.click(screen.getByRole('button', { name: 'Criar conta e aceitar' }));
    expect(await screen.findByText('Conta criada. Entre e configure a verificação em duas etapas.')).toBeInTheDocument();
    expect(posted('/v1/conta/convite/aceitar')!.body).toEqual({ token: 'tk9', nome: 'Nova Pessoa', senha: 'uma frase longa e boa' });
  });

  it('convite com conta e sessão do mesmo e-mail: aceita e abre o portal', async () => {
    const sessao = { usuario: { id: 'u1', nome: 'Maria', email: 'maria@x.test' }, mfa: 'ok', empresas: [{ tenant_id: 't1', nome: 'Empresa X', cnpj_raiz: '11222333000181', papel: 'fiscal', plano: 'PROFISSIONAL', situacao: 'ACTIVE' }], empresa_ativa: 't1', papel: 'fiscal', escopos: ['usage:read'] };
    mockApi((r) => {
      if (r.path === '/v1/conta/sessao') return { json: sessao };
      if (r.path === '/v1/conta/convite') return { json: { empresa: 'Empresa X', email: 'maria@x.test', papel: 'fiscal', tem_conta: true } };
      if (r.path === '/v1/conta/convite/aceitar') return { json: { proximo: 'portal' } };
      if (r.path === '/v1/me') return { json: { tenant_id: 't1', nome: 'Empresa X', cnpj_raiz: '11222333000181', plano: 'PROFISSIONAL', situacao: 'ACTIVE', credencial: 'sessao', auto_provisionar_cnpj: false, scopes: [], limites: { rpm: 1, concorrencia: 1 } } };
      if (r.path === '/v1/usage') return { json: { competencia: r.query.get('mes') ?? currentMonth(), plano: 'PROFISSIONAL', documentos_auditados: 0, simulacoes: 0, cnpjs_ativos: 0, franquia_documentos: 1, excedente_documentos: 0, atualizado_em: '2026-11-25T12:00:00Z' } };
      return undefined;
    });
    renderAt('/portal/convite?token=tk');
    await userEvent.click(await screen.findByRole('button', { name: 'Aceitar convite' }));
    expect(await screen.findByRole('heading', { name: 'Visão geral' })).toBeInTheDocument();
    expect(posted('/v1/conta/convite/aceitar')!.body).toEqual({ token: 'tk' });
  });

  it('convite com conta sem sessão pede login; convite inválido mostra o erro', async () => {
    mockApi((r) => {
      if (r.path === '/v1/conta/sessao') return unauth;
      if (r.path === '/v1/conta/convite') return r.query.get('token') === 'ruim' ? problem(404, 'ERR_CONVITE', 'Convite inválido ou expirado') : { json: { empresa: 'Empresa X', email: 'maria@x.test', papel: 'admin', tem_conta: true } };
      return undefined;
    });
    const { unmount } = renderAt('/portal/convite?token=ok');
    expect(await screen.findByRole('link', { name: 'Entrar com maria@x.test para aceitar' })).toHaveAttribute('href', '/portal/entrar');
    unmount();
    renderAt('/portal/convite?token=ruim');
    expect(await screen.findByRole('alert')).toHaveTextContent('Convite inválido ou expirado');
  });

  it('cadastro com ?plano pago (vitrine indisponível usa o nome do código) e erro do servidor', async () => {
    let n = 0;
    mockApi((r) => {
      if (r.path === '/v1/conta/sessao') return unauth;
      if (r.path === '/v1/conta/planos') return problem(503, 'ERR_INDISPONIVEL', 'Fora do ar');
      if (r.path === '/v1/conta/cadastro') return ++n === 1 ? problem(409, 'ERR_EMAIL_EM_USO', 'E-mail já cadastrado') : { status: 202, json: { mensagem: 'ok' } };
      return undefined;
    });
    renderAt('/cadastro?plano=essencial_mais');
    expect(await screen.findByText('Você escolheu o plano Essencial Mais.')).toBeInTheDocument();
    expect(sessionStorage.getItem(PLANO_ESCOLHIDO_KEY)).toBe('ESSENCIAL_MAIS');
    await userEvent.type(screen.getByLabelText('Seu nome'), 'Maria');
    await userEvent.type(screen.getByLabelText('E-mail de trabalho'), 'maria@x.test');
    await userEvent.type(screen.getByLabelText('Senha'), 'uma frase longa e boa');
    await userEvent.type(screen.getByLabelText('Empresa'), 'Empresa X');
    await userEvent.type(screen.getByLabelText(/CNPJ da empresa/), '11.222.333/0001-81');
    await userEvent.click(screen.getByRole('checkbox'));
    await userEvent.click(screen.getByRole('button', { name: 'Criar conta' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('E-mail já cadastrado');
    await userEvent.click(screen.getByRole('button', { name: 'Criar conta' }));
    expect(await screen.findByRole('heading', { name: 'Confira seu e-mail' })).toBeInTheDocument();
    expect(screen.getByText(/Sua conta começa no Sandbox/)).toHaveTextContent('Essencial Mais');
  });

  it('cadastro com ?plano grátis ou inválido não mostra o aviso de plano pago', async () => {
    mockApi((r) => {
      if (r.path === '/v1/conta/sessao') return unauth;
      if (r.path === '/v1/conta/planos') return { json: [{ codigo: 'GRATIS', nome: 'Grátis', preco_centavos: 0, contratacao_online: true, captura_dfe: false }] };
      return undefined;
    });
    const { unmount } = renderAt('/cadastro?plano=GRATIS');
    await screen.findByRole('heading', { name: 'Criar conta grátis' });
    await waitFor(() => expect(calls.some((c) => c.path === '/v1/conta/planos')).toBe(true));
    expect(screen.queryByText(/Você escolheu o plano/)).toBeNull();
    unmount();
    sessionStorage.clear();
    renderAt('/cadastro?plano=SANDBOX');
    await screen.findByRole('heading', { name: 'Criar conta grátis' });
    expect(sessionStorage.getItem(PLANO_ESCOLHIDO_KEY)).toBeNull();
  });
});

// ---------------------------------------------------------------------------------------------------------------------
// Backoffice: tenants, detalhe, faturamento e tokens (backoffice/BackofficeApp.tsx)
// ---------------------------------------------------------------------------------------------------------------------

describe('backoffice: tenants', () => {
  it('lista com paginação e busca', async () => {
    mockApi(chain((r) => {
      if (r.path === '/admin/v1/tenants') return { json: { tenants: r.query.get('busca') ? [tenantB] : [tenantA, tenantB], total: 30, pagina: Number(r.query.get('pagina')) } };
      return undefined;
    }, operador([])));
    renderAt('/backoffice');
    expect(await screen.findByText('Página 1 de 2 · 30 tenants')).toBeInTheDocument();
    expect(screen.getByText('auto CNPJ')).toBeInTheDocument();
    expect(screen.getByText('90 dias')).toBeInTheDocument();
    expect(screen.getByText('não guarda')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled();
    await userEvent.click(screen.getByRole('button', { name: 'Próxima' }));
    expect(await screen.findByText('Página 2 de 2 · 30 tenants')).toBeInTheDocument();
    expect(calls.filter((c) => c.path === '/admin/v1/tenants').at(-1)!.query.get('pagina')).toBe('2');
    expect(screen.getByRole('button', { name: 'Próxima' })).toBeDisabled();
    await userEvent.click(screen.getByRole('button', { name: 'Anterior' }));
    expect(await screen.findByText('Página 1 de 2 · 30 tenants')).toBeInTheDocument();

    await userEvent.type(screen.getByLabelText('Buscar'), 'Empresa Y');
    await userEvent.click(screen.getByRole('button', { name: 'Buscar' }));
    await waitFor(() => expect(calls.filter((c) => c.path === '/admin/v1/tenants').at(-1)!.query.get('busca')).toBe('Empresa Y'));
    await waitFor(() => expect(screen.queryByText('Empresa X')).toBeNull());
    // Sem a lista de planos, o seletor oferece STARTER.
    expect(screen.getByRole('option', { name: 'STARTER' })).toBeInTheDocument();
  });

  it('erro ao listar e lista vazia', async () => {
    let n = 0;
    mockApi(chain((r) => {
      if (r.path === '/admin/v1/tenants') return ++n === 1 ? problem(500, 'ERR_INTERNO', 'Falha interna') : { json: { tenants: [], total: 0, pagina: 1 } };
      return undefined;
    }, operador([]), tenantsBase));
    renderAt('/backoffice');
    expect(await screen.findByRole('alert')).toHaveTextContent('Falha interna');
    await userEvent.type(screen.getByLabelText('Buscar'), 'nada');
    await userEvent.click(screen.getByRole('button', { name: 'Buscar' }));
    expect(await screen.findByText('Nada por aqui ainda.')).toBeInTheDocument();
    expect(screen.queryByRole('navigation', { name: 'Páginas' })).toBeNull();
  });

  it('criar tenant: erro e depois a primeira API key exibida uma vez', async () => {
    let n = 0;
    mockApi(chain((r) => {
      if (r.path === '/admin/v1/tenants' && r.method === 'POST') return ++n === 1 ? problem(409, 'ERR_CONFLITO', 'CNPJ já cadastrado', 'Use outro') : { status: 201, json: { tenant_id: 't9', api_key: 'tg_live_sk_PRIMEIRA' } };
      return undefined;
    }, operador([]), tenantsBase));
    renderAt('/backoffice');
    await screen.findByText('Empresa X');
    await screen.findByRole('option', { name: 'PRO' });
    await userEvent.type(screen.getByLabelText('Nome'), 'Nova SA');
    await userEvent.type(screen.getByLabelText('CNPJ raiz (matriz)'), '99.888.777');
    await userEvent.selectOptions(screen.getByLabelText('Plano'), 'PRO');
    await userEvent.selectOptions(screen.getByLabelText('Retenção do XML (dias)'), '365');
    await userEvent.click(screen.getByLabelText(/Auto-provisionar CNPJs/));
    await userEvent.click(screen.getByRole('button', { name: 'Criar tenant' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('CNPJ já cadastrado. Use outro');
    await userEvent.click(screen.getByRole('button', { name: 'Criar tenant' }));
    const dialog = await screen.findByRole('dialog', { name: 'Primeira API key de Nova SA' });
    expect(dialog).toHaveTextContent('tg_live_sk_PRIMEIRA');
    expect(calls.filter((c) => c.path === '/admin/v1/tenants' && c.method === 'POST').at(-1)!.body).toEqual({ nome: 'Nova SA', cnpj_raiz: '99888777', plano: 'PRO', auto_provisionar_cnpj: true, retencao_xml_dias: 365 });
    expect(screen.getByLabelText('Nome')).toHaveValue('');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Já guardei' }));
    expect(screen.queryByRole('dialog', { name: 'Primeira API key de Nova SA' })).toBeNull();

    // Sem retenção: envia null.
    await userEvent.type(screen.getByLabelText('Nome'), 'Outra');
    await userEvent.type(screen.getByLabelText('CNPJ raiz (matriz)'), '11222333');
    await userEvent.selectOptions(screen.getByLabelText('Retenção do XML (dias)'), '');
    await userEvent.click(screen.getByRole('button', { name: 'Criar tenant' }));
    await screen.findByRole('dialog', { name: 'Primeira API key de Outra' });
    expect(calls.filter((c) => c.path === '/admin/v1/tenants' && c.method === 'POST').at(-1)!.body).toMatchObject({ retencao_xml_dias: null });
  });

  it('suspender exige digitar SUSPENDER; reativar; clientes OAuth: revogar e criar', async () => {
    mockApi(chain((r) => {
      if (r.path.endsWith('/suspend') || r.path.endsWith('/reactivate')) return { status: 204 };
      if (r.path === '/admin/v1/oauth-clients/c-ativo' && r.method === 'DELETE') return { status: 204 };
      if (r.path === '/admin/v1/tenants/t1/oauth-clients' && r.method === 'POST') {
        return (r.body as { nome: string }).nome === 'falha' ? problem(400, 'ERR_VALIDACAO', 'Nome inválido') : { status: 201, json: { client_id: 'c-novo', client_secret: 'segredo-oauth' } };
      }
      return undefined;
    }, operador([]), tenantsBase));
    renderAt('/backoffice');
    const rowX = (await screen.findByText('Empresa X')).closest('tr')!;
    await userEvent.click(within(rowX).getByRole('button', { name: 'Suspender' }));
    const dlg = await screen.findByRole('alertdialog', { name: 'Suspender Empresa X?' });
    const ok = within(dlg).getByRole('button', { name: 'Suspender cliente' });
    expect(ok).toBeDisabled();
    await userEvent.type(within(dlg).getByLabelText('Digite SUSPENDER para confirmar'), 'suspender');
    await userEvent.click(ok);
    await waitFor(() => expect(posted('/admin/v1/tenants/t1/suspend')).toBeDefined());

    const rowY = screen.getByText('Empresa Y').closest('tr')!;
    await userEvent.click(within(rowY).getByRole('button', { name: 'Reativar' }));
    await waitFor(() => expect(posted('/admin/v1/tenants/t2/reactivate')).toBeDefined());

    await userEvent.click(within(screen.getByText('Empresa X').closest('tr')!).getByRole('button', { name: 'OAuth (1)' }));
    expect(await screen.findByText('c-ativo')).toBeInTheDocument();
    expect(screen.getByText('revogado')).toBeInTheDocument();
    await userEvent.click(within(screen.getByText('c-ativo').closest('tr')!).getByRole('button', { name: 'Revogar' }));
    const rev = await screen.findByRole('alertdialog', { name: 'Revogar o cliente OAuth "erp"?' });
    await userEvent.click(within(rev).getByRole('button', { name: 'Revogar cliente' }));
    await waitFor(() => expect(posted('/admin/v1/oauth-clients/c-ativo', 'DELETE')).toBeDefined());

    await userEvent.type(screen.getByLabelText('Novo cliente OAuth'), 'falha');
    await userEvent.click(screen.getByRole('button', { name: 'Criar cliente' }));
    expect(await screen.findByText('Nome inválido')).toBeInTheDocument();

    await userEvent.clear(screen.getByLabelText('Novo cliente OAuth'));
    await userEvent.type(screen.getByLabelText('Novo cliente OAuth'), 'integracao');
    await userEvent.click(screen.getByRole('checkbox', { name: 'usage:read' }));
    await userEvent.click(screen.getByRole('checkbox', { name: 'dfe:manage' }));
    await userEvent.click(screen.getByRole('button', { name: 'Criar cliente' }));
    expect(await screen.findByRole('dialog', { name: 'client_secret de Empresa X' })).toHaveTextContent('segredo-oauth');
    expect(screen.getByText('Cliente c-novo criado.')).toBeInTheDocument();
    expect(calls.filter((c) => c.path === '/admin/v1/tenants/t1/oauth-clients').at(-1)!.body).toEqual({ nome: 'integracao', scopes: ['simulacao:write', 'auditoria:write', 'dfe:manage'] });

    // Fecha o painel OAuth.
    await userEvent.click(within(screen.getByText('Empresa X').closest('tr')!).getByRole('button', { name: /OAuth/ }));
    expect(screen.queryByLabelText('Novo cliente OAuth')).toBeNull();
    // OAuth de um tenant sem clientes mostra o estado vazio.
    await userEvent.click(within(screen.getByText('Empresa Y').closest('tr')!).getByRole('button', { name: 'OAuth (0)' }));
    expect(await screen.findByText('Nada por aqui ainda.')).toBeInTheDocument();
  });

  it('detalhe do tenant: teto, verificação manual, troca de plano e tabelas', async () => {
    const detalhe = {
      tenant: {
        tenant_id: 't1', nome: 'Empresa X', cnpj_raiz: '11222333', plano: 'STARTER', situacao: 'ACTIVE', criado_em: '2026-01-01T10:00:00Z',
        cobranca: { provedor: 'stripe', cliente: 'cus_1', assinatura: 'sub_1', situacao_assinatura: 'past_due', fim_do_periodo: '2026-12-01T10:00:00Z' },
        teto_centavos: 1250, verificada_em: null, metodo_verificacao: null, verificada_por: null,
        inadimplente_desde: '2026-11-10T10:00:00Z', motivo_suspensao: 'Fatura vencida', limites: { rpm: 900 },
      },
      pessoas: [
        { nome: 'Maria', email: 'maria@x.test', papel: 'owner', mfa: true, situacao: 'ACTIVE', ultimo_acesso: '2026-11-20T10:00:00Z' },
        { nome: 'João', email: 'joao@x.test', papel: 'fiscal', mfa: false, situacao: 'ACTIVE', ultimo_acesso: null },
      ],
      cnpjs: [{ cnpj: '11222333000181', ativo: true, criado_em: '2026-01-01T10:00:00Z' }, { cnpj: '11222333000262', ativo: false, criado_em: '2026-02-01T10:00:00Z' }],
      api_keys: [
        { nome: 'erp', prefixo: 'AAAAAAAA', scopes: ['simulacao:write', 'usage:read'], expira_em: null, revogada_em: null, ultimo_uso: null, criada_em: '2026-01-01T10:00:00Z' },
        { nome: 'antiga', prefixo: 'BBBBBBBB', scopes: [], expira_em: null, revogada_em: '2026-03-01T10:00:00Z', ultimo_uso: '2026-02-01T10:00:00Z', criada_em: '2026-01-01T10:00:00Z' },
      ],
      consumo: [{ competencia: '2026-11', documentos: 1234, simulacoes: null }],
      faturas: [{ numero: 'F-1', situacao: 'paid', valor_cents: 19900, pago_cents: 19900, inicio: '2026-10-01T03:00:00Z', fim: '2026-11-01T03:00:00Z' }, { numero: null, situacao: 'open', valor_cents: 500, pago_cents: 0, inicio: null, fim: null }],
    };
    mockApi(chain((r) => {
      if (r.path === '/admin/v1/tenants/t1' && r.method === 'GET') return { json: detalhe };
      if (r.path === '/admin/v1/tenants/t1/teto') return { json: { teto_centavos: 5050 } };
      if (r.path === '/admin/v1/tenants/t1/verificar') return { status: 204 };
      if (r.path === '/admin/v1/tenants/t1/plano') return { status: 204 };
      return undefined;
    }, operador(['admin:commercial', 'admin:security']), tenantsBase));
    renderAt('/backoffice');
    await screen.findByRole('option', { name: 'PRO' });
    await userEvent.click(within((await screen.findByText('Empresa X')).closest('tr')!).getByRole('button', { name: 'Detalhe' }));
    const drawer = await screen.findByRole('dialog', { name: 'Empresa X' });
    expect(await within(drawer).findByRole('heading', { name: 'Pessoas com acesso' })).toBeInTheDocument();
    expect(drawer).toHaveTextContent('past_due');
    expect(drawer).toHaveTextContent('não verificada');
    expect(drawer).toHaveTextContent('Fatura vencida');
    expect(drawer).toHaveTextContent('Inadimplente desde');
    expect(within(drawer).getByText('CNPJs (2)')).toBeInTheDocument();
    expect(within(drawer).getByText('inativo')).toBeInTheDocument();
    expect(within(drawer).getByText('revogada')).toBeInTheDocument();
    expect(within(drawer).getByText('pendente')).toBeInTheDocument();
    expect(within(drawer).getByText('F-1')).toBeInTheDocument();
    expect(within(drawer).getByText(/requisições por minuto: 900/)).toBeInTheDocument();

    // Teto de excedente.
    const teto = within(drawer).getByLabelText('Teto em reais');
    expect(teto).toHaveValue('12.5');
    expect(within(drawer).getByRole('button', { name: 'Salvar teto' })).toBeDisabled();
    await userEvent.clear(teto);
    await userEvent.type(teto, '50,5');
    await userEvent.click(within(drawer).getByRole('button', { name: 'Salvar teto' }));
    await waitFor(() => expect(posted('/admin/v1/tenants/t1/teto', 'PUT')!.body).toEqual({ teto_centavos: 5050 }));

    // Verificação manual.
    const marcar = within(drawer).getByRole('button', { name: 'Marcar verificada' });
    expect(marcar).toBeDisabled();
    await userEvent.type(within(drawer).getByLabelText('Documento conferido'), 'contrato social');
    await userEvent.click(marcar);
    const vdlg = await screen.findByRole('alertdialog', { name: 'Verificar a titularidade de Empresa X?' });
    await userEvent.click(within(vdlg).getByRole('button', { name: 'Marcar verificada' }));
    await waitFor(() => expect(posted('/admin/v1/tenants/t1/verificar')!.body).toEqual({ observacao: 'contrato social' }));
    await waitFor(() => expect(within(drawer).getByLabelText('Documento conferido')).toHaveValue(''));

    // Troca de plano.
    const aplicar = within(drawer).getByRole('button', { name: 'Aplicar' });
    expect(aplicar).toBeDisabled();
    await userEvent.selectOptions(within(drawer).getByLabelText('Novo plano'), 'PRO');
    await userEvent.click(aplicar);
    const pdlg = await screen.findByRole('alertdialog', { name: 'Trocar Empresa X para PRO?' });
    await userEvent.click(within(pdlg).getByRole('button', { name: 'Trocar plano' }));
    await waitFor(() => expect(posted('/admin/v1/tenants/t1/plano')!.body).toEqual({ plano: 'PRO' }));

    await userEvent.click(within(drawer).getByRole('button', { name: 'Fechar' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Empresa X' })).toBeNull());
  });

  it('detalhe sem escopos comerciais: verificada, teto zero, listas vazias e erro ao trocar plano', async () => {
    const detalhe = {
      tenant: {
        tenant_id: 't2', nome: 'Empresa Y', cnpj_raiz: '44555666', plano: 'STARTER', situacao: 'SUSPENDED', criado_em: '2026-01-01T10:00:00Z',
        cobranca: { provedor: null, cliente: null, assinatura: null, situacao_assinatura: null, fim_do_periodo: null },
        teto_centavos: 0, verificada_em: '2026-05-01T10:00:00Z', metodo_verificacao: 'MANUAL', verificada_por: null,
        inadimplente_desde: '2026-11-10T10:00:00Z', motivo_suspensao: null,
      },
      pessoas: [], cnpjs: [], api_keys: [], consumo: [], faturas: [],
    };
    mockApi(chain((r) => {
      if (r.path === '/admin/v1/tenants/t2' && r.method === 'GET') return { json: detalhe };
      if (r.path === '/admin/v1/tenants/t1' && r.method === 'GET') return problem(500, 'ERR_INTERNO', 'Detalhe indisponível');
      if (r.path === '/admin/v1/tenants/t2/plano') return problem(409, 'ERR_PLANO', 'Plano inativo');
      return undefined;
    }, operador(['admin:fiscal']), tenantsBase));
    renderAt('/backoffice');
    await screen.findByRole('option', { name: 'PRO' });
    await userEvent.click(within((await screen.findByText('Empresa Y')).closest('tr')!).getByRole('button', { name: 'Detalhe' }));
    const drawer = await screen.findByRole('dialog', { name: 'Empresa Y' });
    expect(await within(drawer).findByText('Nenhuma pessoa (cliente só por API).')).toBeInTheDocument();
    expect(drawer).toHaveTextContent('sem assinatura');
    expect(drawer).toHaveTextContent('verificada');
    expect(drawer).toHaveTextContent('MANUAL · —');
    expect(drawer).toHaveTextContent('zero (para na franquia)');
    expect(within(drawer).getByText('Nenhuma fatura ainda.')).toBeInTheDocument();
    expect(within(drawer).queryByLabelText('Teto em reais')).toBeNull();
    expect(within(drawer).queryByLabelText('Documento conferido')).toBeNull();
    expect(within(drawer).queryByText('Limites de contrato')).toBeNull();
    await userEvent.selectOptions(within(drawer).getByLabelText('Novo plano'), 'PRO');
    await userEvent.click(within(drawer).getByRole('button', { name: 'Aplicar' }));
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Trocar plano' }));
    expect(await within(drawer).findByRole('alert')).toHaveTextContent('Plano inativo');
    await userEvent.click(within(drawer).getByRole('button', { name: 'Fechar' }));

    await userEvent.click(within(screen.getByText('Empresa X').closest('tr')!).getByRole('button', { name: 'Detalhe' }));
    const d2 = await screen.findByRole('dialog', { name: 'Empresa X' });
    expect(await within(d2).findByRole('alert')).toHaveTextContent('Detalhe indisponível');
  });
});

describe('backoffice: faturamento e tokens', () => {
  it('fecha a competência, reconcilia com e sem divergências e registra ajuste', async () => {
    let rec = 0;
    mockApi(chain((r) => {
      if (r.path === '/admin/v1/billing/close') return { json: { periodos_fechados: 3, faturados: 2 } };
      if (r.path === '/admin/v1/billing/reconcile') return { json: ++rec === 1 ? { competencia: '2026-05', tenants: 4, divergencias: [{ tenant_id: 't1', motivo: 'valor' }] } : { competencia: '2026-05', tenants: 4, divergencias: [] } };
      if (r.path === '/admin/v1/billing/adjustments') return { status: 201, json: { id: 'a1' } };
      return undefined;
    }, operador([])));
    renderAt('/backoffice/faturamento');
    const mes = await screen.findByLabelText('Competência');
    expect((mes as HTMLInputElement).value).toMatch(/^\d{4}-\d{2}$/);
    fireEvent.change(mes, { target: { value: '2026-05' } });
    await userEvent.click(screen.getByRole('button', { name: 'Fechar 2026-05' }));
    const dlg = await screen.findByRole('alertdialog', { name: 'Fechar 2026-05?' });
    await userEvent.click(within(dlg).getByRole('button', { name: 'Fechar 2026-05' }));
    expect(await screen.findByText('3 período(s) fechado(s), 2 faturado(s).')).toBeInTheDocument();
    expect(posted('/admin/v1/billing/close')!.query.get('mes')).toBe('2026-05');

    await userEvent.click(screen.getByRole('button', { name: 'Reconciliar 2026-05' }));
    expect(await screen.findByText('1 divergência(s)')).toBeInTheDocument();
    expect(screen.getByText(/"motivo": "valor"/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Reconciliar 2026-05' }));
    expect(await screen.findByText('sem divergências')).toBeInTheDocument();

    await userEvent.type(screen.getByLabelText('Tenant (id)'), 't1');
    await userEvent.type(screen.getByLabelText('Valor (R$)'), '-10.5');
    await userEvent.type(screen.getByLabelText('Motivo'), 'crédito');
    await userEvent.click(screen.getByRole('button', { name: 'Registrar ajuste' }));
    expect(await screen.findByText(/Ajuste de .*10,50 registrado\./)).toBeInTheDocument();
    expect(posted('/admin/v1/billing/adjustments')!.body).toEqual({ tenant_id: 't1', competencia: '2026-05', valor_centavos: -1050, motivo: 'crédito' });
  });

  it('erro no fechamento aparece na tela', async () => {
    mockApi(chain((r) => (r.path === '/admin/v1/billing/close' ? problem(503, 'ERR_BILLING', 'Cobrança indisponível', 'Tente mais tarde') : undefined), operador([])));
    renderAt('/backoffice/faturamento');
    await userEvent.click(await screen.findByRole('button', { name: /^Fechar \d{4}-\d{2}$/ }));
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: /^Fechar/ }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Cobrança indisponível. Tente mais tarde');
  });

  it('revogar token: erro e sucesso', async () => {
    let n = 0;
    mockApi(chain((r) => (r.path === '/admin/v1/oauth/revoked-tokens' ? (++n === 1 ? problem(400, 'ERR_VALIDACAO', 'jti inválido') : { status: 204 }) : undefined), operador([])));
    renderAt('/backoffice/tokens');
    await userEvent.type(await screen.findByLabelText('jti'), 'abc-123');
    fireEvent.change(screen.getByLabelText('Expira em (exp do token)'), { target: { value: '2026-12-01T10:00' } });
    await userEvent.click(screen.getByRole('button', { name: 'Revogar' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('jti inválido');
    await userEvent.click(screen.getByRole('button', { name: 'Revogar' }));
    expect(await screen.findByText('Token abc-123 revogado.')).toBeInTheDocument();
    const body = posted('/admin/v1/oauth/revoked-tokens')!.body as { jti: string; expira_em: string };
    expect(body.jti).toBe('abc-123');
    expect(body.expira_em).toBe(new Date('2026-12-01T10:00').toISOString());
  });
});

// ---------------------------------------------------------------------------------------------------------------------
// Backoffice: planos, operadores, trilha e disputas
// ---------------------------------------------------------------------------------------------------------------------

const planoPro = {
  code: 'PRO', name: 'Profissional', description: 'Para empresas', features: ['Auditoria', 'Lotes'], price_cents: 19900, included_docs: 1000, included_simulations: 5000,
  included_cnpjs: 3, max_users: 10, overage_doc_millicents: 9000, overage_sim_millicents: 500, cnpj_price_cents: 2990,
  rpm: 600, burst: 60, per_cnpj_rpm: 5, concurrency: 20, batch_max_docs: 5000, batch_max_bytes: 10485760, queue_priority: 'normal', bill_schema_errors: true,
  hard_quota: false, dfe_enabled: true, is_public: true, self_service: true, active: true, sort_order: 20,
  updated_at: '2026-11-01T10:00:00Z', updated_by: 'ana', stripe_product_id: 'prod_1', stripe_price_id: 'price_1', stripe_synced_at: '2026-11-02T10:00:00Z',
};
const planoLegado = { ...planoPro, code: 'LEGADO', name: '', price_cents: 1000, overage_doc_millicents: 0, max_users: null, included_cnpjs: null, per_cnpj_rpm: null, hard_quota: true, is_public: true, self_service: false, active: false, stripe_synced_at: null };
const planoOculto = { ...planoPro, code: 'INTERNO', name: 'Interno', price_cents: 0, is_public: false };

describe('backoffice: planos', () => {
  it('lista, edita com histórico e salva; novo plano com erro', async () => {
    let lista = [planoPro, planoLegado, planoOculto];
    mockApi(chain((r) => {
      if (r.path === '/admin/v1/planos' && r.method === 'GET') return { json: lista };
      if (r.path === '/admin/v1/planos/PRO/historico') return { json: [{ em: '2026-10-01T10:00:00Z', por: 'ana', acao: 'create', dados: { price_cents: 10000 } }, { em: '2026-11-01T10:00:00Z', por: 'beto', acao: 'update', dados: {} }] };
      if (r.path === '/admin/v1/planos/PRO' && r.method === 'PUT') { lista = [{ ...planoPro, name: 'Pro Max' }, planoLegado, planoOculto]; return { json: { mensagem: 'Plano PRO salvo.' } }; }
      if (r.path === '/admin/v1/planos/NOVO_X' && r.method === 'PUT') return problem(400, 'ERR_VALIDACAO', 'Preço inválido');
      return undefined;
    }, operador(['admin:commercial'])));
    renderAt('/backoffice/planos');
    expect(await screen.findByText('Profissional')).toBeInTheDocument();
    expect(screen.getByText('legado')).toBeInTheDocument();
    expect(screen.getByText('rígida')).toBeInTheDocument();
    expect(screen.getByText('online')).toBeInTheDocument();
    expect(screen.getByText('sob consulta')).toBeInTheDocument();
    expect(screen.getByText('oculto')).toBeInTheDocument();
    expect(screen.getByText('sincronizado')).toBeInTheDocument();
    expect(screen.getAllByText('pendente').length).toBeGreaterThan(0);
    expect(screen.getByText('∞')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Novo plano' })).toBeInTheDocument();

    await userEvent.click(within(screen.getByText('Profissional').closest('tr')!).getByRole('button', { name: 'Editar' }));
    expect(await screen.findByRole('heading', { name: 'Editar PRO' })).toBeInTheDocument();
    expect(screen.getByLabelText('Código')).toBeDisabled();
    await waitFor(() => expect(screen.getByLabelText('Nome na vitrine')).toHaveValue('Profissional'));
    expect(screen.getByLabelText('Mensalidade (R$)')).toHaveValue('199.00');
    expect(screen.getByLabelText('Excedente por documento (R$)')).toHaveValue('0.0900');
    expect(await screen.findByText('criação')).toBeInTheDocument();
    expect(screen.getByText('alteração')).toBeInTheDocument();

    await userEvent.clear(screen.getByLabelText('Nome na vitrine'));
    await userEvent.type(screen.getByLabelText('Nome na vitrine'), 'Pro Max');
    await userEvent.click(screen.getByLabelText(/Franquia rígida/));
    await userEvent.selectOptions(screen.getByLabelText('Prioridade na fila'), 'high');
    await userEvent.clear(screen.getByLabelText('Usuários'));
    await userEvent.type(screen.getByLabelText('Recursos (um por linha, até 12)'), '\n  \nDF-e');
    await userEvent.click(screen.getByRole('button', { name: 'Salvar plano' }));
    expect(await screen.findByText('Plano PRO salvo.')).toBeInTheDocument();
    expect(posted('/admin/v1/planos/PRO', 'PUT')!.body).toMatchObject({
      name: 'Pro Max', hard_quota: true, queue_priority: 'high', max_users: null, included_cnpjs: 3, price_cents: 19900,
      overage_doc_millicents: 9000, cnpj_price_cents: 2990, per_cnpj_rpm: 5, features: ['Auditoria', 'Lotes', 'DF-e'],
    });
    expect(await screen.findByText('Pro Max')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Novo plano' }));
    expect(await screen.findByRole('heading', { name: 'Novo plano', level: 2 })).toBeInTheDocument();
    expect(screen.getByLabelText('Código')).toBeEnabled();
    expect(screen.getByLabelText('Código')).toHaveValue('');
    expect(screen.queryByRole('heading', { name: 'Histórico de mudanças' })).toBeNull();
    await userEvent.type(screen.getByLabelText('Código'), 'novo_x');
    await userEvent.type(screen.getByLabelText('Nome na vitrine'), 'Novo');
    await userEvent.click(screen.getByRole('button', { name: 'Salvar plano' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Preço inválido');
    expect(posted('/admin/v1/planos/NOVO_X', 'PUT')!.body).toMatchObject({ name: 'Novo', price_cents: 0, rpm: 120, dfe_enabled: true, included_cnpjs: null });
  });

  it('sem planos mostra o estado vazio', async () => {
    mockApi(chain((r) => (r.path === '/admin/v1/planos' ? { json: [] } : undefined), operador(['admin:commercial'])));
    renderAt('/backoffice/planos');
    expect(await screen.findByText('Nada por aqui ainda.')).toBeInTheDocument();
  });
});

describe('backoffice: operadores e trilha', () => {
  it('convida com os escopos marcados, desativa com confirmação e mostra erros', async () => {
    let convites = 0;
    mockApi(chain((r) => {
      if (r.path === '/admin/v1/operadores' && r.method === 'GET') {
        return { json: [
          { id: 'op1', email: 'ana@tg.test', nome: 'Ana', escopos: ['admin:fiscal'], mfa: true, situacao: 'ACTIVE', ultimo_acesso: '2026-11-20T10:00:00Z', criado_em: '2026-01-01T10:00:00Z' },
          { id: 'op2', email: 'beto@tg.test', nome: 'Beto', escopos: [], mfa: false, situacao: 'DISABLED', ultimo_acesso: null, criado_em: '2026-01-01T10:00:00Z' },
        ] };
      }
      if (r.path === '/admin/v1/operadores' && r.method === 'POST') return ++convites === 1 ? { status: 201, json: { id: 'op3' } } : problem(409, 'ERR_EXISTE', 'Operador já existe');
      if (r.path === '/admin/v1/operadores/op1/desativar') return { status: 204 };
      return undefined;
    }, operador(['admin:security'])));
    renderAt('/backoffice/operadores');
    expect(await screen.findByText('Ana')).toBeInTheDocument();
    expect(screen.getByText('desativado')).toBeInTheDocument();
    expect(screen.getByText('—')).toBeInTheDocument();
    expect(within(screen.getByText('Beto').closest('tr')!).queryByRole('button', { name: 'Desativar' })).toBeNull();

    await userEvent.type(screen.getByLabelText('Nome'), 'Carla');
    await userEvent.type(screen.getByLabelText('E-mail'), 'carla@tg.test');
    const enviar = screen.getByRole('button', { name: 'Enviar convite' });
    await userEvent.click(screen.getByLabelText(/Comercial/));
    expect(enviar).toBeDisabled();
    await userEvent.click(screen.getByLabelText(/Fiscal \(alíquotas\)/));
    expect(enviar).toBeEnabled();
    await userEvent.click(screen.getByLabelText(/Segurança/));
    await userEvent.click(enviar);
    expect(await screen.findByText('Convite enviado para carla@tg.test (vale 24 h).')).toBeInTheDocument();
    expect(posted('/admin/v1/operadores')!.body).toEqual({ email: 'carla@tg.test', nome: 'Carla', escopos: ['admin:fiscal', 'admin:security'] });
    expect(screen.getByLabelText('Nome')).toHaveValue('');

    await userEvent.type(screen.getByLabelText('Nome'), 'Ana');
    await userEvent.type(screen.getByLabelText('E-mail'), 'ana@tg.test');
    await userEvent.click(enviar);
    expect(await screen.findByRole('alert')).toHaveTextContent('Operador já existe');

    await userEvent.click(within(screen.getByText('ana@tg.test', { selector: 'td' }).closest('tr')!).getByRole('button', { name: 'Desativar' }));
    const dlg = await screen.findByRole('alertdialog', { name: 'Desativar Ana?' });
    expect(dlg).toHaveTextContent('ana@tg.test perde o acesso');
    await userEvent.click(within(dlg).getByRole('button', { name: 'Desativar operador' }));
    await waitFor(() => expect(posted('/admin/v1/operadores/op1/desativar')).toBeDefined());
  });

  it('erro ao listar operadores e lista vazia', async () => {
    mockApi(chain((r) => (r.path === '/admin/v1/operadores' ? problem(403, 'ERR_FORBIDDEN_SCOPE', 'Sem escopo') : undefined), operador(['admin:security'])));
    renderAt('/backoffice/operadores');
    expect(await screen.findByRole('alert')).toHaveTextContent('Seu acesso não permite esta ação');
    expect(screen.getByText('Nada por aqui ainda.')).toBeInTheDocument();
  });

  it('trilha: rótulos das ações, filtros por tenant e ator e erro', async () => {
    mockApi(chain((r) => {
      if (r.path === '/admin/v1/auditoria-acoes') {
        if (r.query.get('tenant')) return problem(500, 'ERR_INTERNO', 'Trilha indisponível');
        return { json: [
          { em: '2026-11-20T10:00:00Z', tipo_ator: 'usuario', ator: 'u-123', acao: 'login.mfa_ok', tenant_id: 't1', alvo: 't1', ip: '10.0.0.1', detalhe: {} },
          { em: '2026-11-20T11:00:00Z', tipo_ator: 'sistema', ator: null, acao: 'acao.desconhecida', tenant_id: null, alvo: null, ip: null, detalhe: {} },
        ] };
      }
      return undefined;
    }, operador(['admin:security'])));
    renderAt('/backoffice/trilha');
    expect(await screen.findByText('Entrou (MFA)')).toBeInTheDocument();
    expect(screen.getByText('acao.desconhecida')).toBeInTheDocument();
    expect(screen.getByText('usuario: u-123')).toBeInTheDocument();
    expect(screen.getByText('sistema')).toBeInTheDocument();
    expect(calls.find((c) => c.path === '/admin/v1/auditoria-acoes')!.url).toBe('/admin/v1/auditoria-acoes');

    await userEvent.type(screen.getByLabelText('Tenant (id)'), ' t1 ');
    await userEvent.type(screen.getByLabelText('Ator (id ou e-mail)'), 'ana@tg.test');
    await userEvent.click(screen.getByRole('button', { name: 'Filtrar' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Trilha indisponível');
    const ultima = calls.filter((c) => c.path === '/admin/v1/auditoria-acoes').at(-1)!;
    expect(ultima.query.get('tenant')).toBe('t1');
    expect(ultima.query.get('ator')).toBe('ana@tg.test');
  });

  it('menu de segurança aparece com admin:security', async () => {
    mockApi(chain((r) => (r.path === '/admin/v1/auditoria-acoes' ? { json: [] } : undefined), operador(['admin:security', 'admin:commercial'])));
    renderAt('/backoffice/trilha');
    await screen.findByRole('heading', { name: 'Trilha de auditoria', level: 1 });
    for (const name of ['Operadores', 'Disputas de CNPJ', 'Trilha de auditoria', 'Planos']) expect(screen.getByRole('link', { name })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Alíquotas' })).toBeNull();
    expect(await screen.findByText('fb@tg.test')).toBeInTheDocument();
  });
});

describe('backoffice: disputas', () => {
  it('rejeitar com erro do servidor; situação desconhecida e certificado ausente', async () => {
    mockApi(chain((r) => {
      if (r.path === '/admin/v1/disputas') {
        return { json: { disputas: [
          { id: 'd1', cnpj: '11222333', tenant_id: 't1', empresa: 'Empresa X', requerente: 'real@x.test', requerente_nome: 'Titular', certificado: null, situacao: 'ABERTA', criada_em: '2026-11-20T10:00:00Z', resolvida_por: null, resolvida_em: null },
          { id: 'd3', cnpj: '77888999', tenant_id: 't3', empresa: 'Empresa Z', requerente: 'z@x.test', requerente_nome: 'Z', certificado: 'XYZ', situacao: 'EM_ANALISE', criada_em: '2026-11-21T10:00:00Z', resolvida_por: null, resolvida_em: null },
        ] } };
      }
      if (r.path === '/admin/v1/disputas/d1/resolver') return problem(409, 'ERR_DISPUTA', 'Disputa já resolvida');
      return undefined;
    }, operador(['admin:security'])));
    renderAt('/backoffice/disputas');
    expect(await screen.findByText('EM_ANALISE')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Rejeitar' }));
    const dlg = await screen.findByRole('alertdialog', { name: 'Rejeitar a contestação?' });
    await userEvent.click(within(dlg).getByRole('button', { name: 'Rejeitar' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Disputa já resolvida');
    expect(posted('/admin/v1/disputas/d1/resolver')!.body).toEqual({ acao: 'rejeitar' });
  });

  it('sem disputas mostra o estado vazio', async () => {
    mockApi(chain((r) => (r.path === '/admin/v1/disputas' ? { json: { disputas: [] } } : undefined), operador(['admin:security'])));
    renderAt('/backoffice/disputas');
    expect(await screen.findByText('Nenhuma disputa.')).toBeInTheDocument();
  });
});
