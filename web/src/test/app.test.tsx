import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import App from '../App';
import { ApiError, CSRF_HEADER, describeError, portal } from '../lib/api';
import { cnpj, currentMonth } from '../lib/format';
import { safeReturnPath } from '../components/Shell';

type Req = { method: string; url: string; headers: Record<string, string>; body?: unknown; credentials?: RequestCredentials };
type Handler = (req: Req) => { status?: number; json?: unknown } | undefined;

let calls: Req[] = [];

function mockApi(handler: Handler) {
  calls = [];
  vi.stubGlobal('fetch', vi.fn(async (input: string, init: RequestInit = {}) => {
    const req: Req = {
      method: init.method ?? 'GET',
      url: input,
      headers: (init.headers ?? {}) as Record<string, string>,
      body: typeof init.body === 'string' ? JSON.parse(init.body) : undefined,
      credentials: init.credentials,
    };
    calls.push(req);
    const r = handler(req) ?? { status: 404, json: { code: 'ERR_NOT_FOUND', title: 'Não encontrado' } };
    const status = r.status ?? 200;
    const text = r.json === undefined ? '' : JSON.stringify(r.json);
    return new Response(text || null, { status, headers: { 'content-type': status >= 400 ? 'application/problem+json' : 'application/json' } });
  }));
}

const me = { tenant_id: 't1', nome: 'Empresa X', cnpj_raiz: '11222333000181', plano: 'PROFISSIONAL', situacao: 'ACTIVE', credencial: 'sessao', auto_provisionar_cnpj: false, scopes: ['keys:manage', 'simulacao:write', 'usage:read'], limites: { rpm: 600, concorrencia: 20 } };
const usage = { competencia: '2026-11', plano: 'PROFISSIONAL', documentos_auditados: 1234, simulacoes: 10, cnpjs_ativos: 2, franquia_documentos: 1000, excedente_documentos: 234, atualizado_em: '2026-11-25T12:00:00Z' };
const empresa = { tenant_id: 't1', nome: 'Empresa X', cnpj_raiz: '11222333000181', papel: 'owner', plano: 'PROFISSIONAL', situacao: 'ACTIVE' };
const sessaoOk = {
  usuario: { id: 'u1', nome: 'Maria Fiscal', email: 'maria@x.test' }, mfa: 'ok', empresas: [empresa], empresa_ativa: 't1', papel: 'owner',
  escopos: ['simulacao:write', 'auditoria:write', 'keys:manage', 'webhooks:manage', 'usage:read', 'membros:manage'], plano: 'PROFISSIONAL', max_usuarios: 10,
};

const renderAt = (path: string) => render(<MemoryRouter initialEntries={[path]}><App /></MemoryRouter>);

describe('cliente HTTP', () => {
  it('usa só o cookie da sessão (sem credencial no JavaScript), manda o header anti-CSRF e traduz problem+json', async () => {
    mockApi((r) => (r.url.startsWith('/v1/calculo') ? { status: 403, json: { code: 'ERR_FORBIDDEN_SCOPE', title: 'Escopo insuficiente', detail: 'Exige simulacao:write' } } : undefined));
    const err = await portal('/v1/calculo/simular', { method: 'POST', body: {}, idempotent: true }).catch((e) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).code).toBe('ERR_FORBIDDEN_SCOPE');
    expect(calls[0].headers[CSRF_HEADER]).toBe('1');
    expect(calls[0].credentials).toBe('same-origin');
    expect(calls[0].headers['X-Api-Key']).toBeUndefined();
    expect(calls[0].headers['Idempotency-Key']).toMatch(/^[0-9a-f-]{36}$/);
    expect(describeError(new ApiError(401, 'ERR_UNAUTHENTICATED', 'x'))).toContain('Entre novamente');
    expect(describeError(new ApiError(403, 'ERR_CNPJ_NOT_PROVISIONED', 'x'))).toContain('Cadastre-o em CNPJs');
  });

  it('depois do login só volta para caminho interno (sem open redirect)', () => {
    expect(safeReturnPath('/portal/api-keys', '/portal')).toBe('/portal/api-keys');
    for (const bad of ['//evil.com', '/\evil.com', 'https://evil.com', '/portal/../x', '/portal?x=//evil', 'javascript:alert(1)', undefined, '/portal/entrar']) {
      expect(safeReturnPath(bad, '/portal')).toBe('/portal');
    }
  });

  it('formata CNPJ e competência', () => {
    expect(cnpj('11222333000181')).toBe('11.222.333/0001-81');
    expect(currentMonth(new Date('2026-12-01T01:00:00Z'))).toBe('2026-11'); // ainda 30/11 em Brasília
  });
});

describe('portal do cliente', () => {
  it('sem sessão vai para o login; senha certa leva ao MFA; código certo abre a visão geral', async () => {
    let state: 'anon' | 'mfa' | 'ok' = 'anon';
    mockApi((r) => {
      if (r.url === '/v1/conta/sessao') {
        if (state === 'anon') return { status: 401, json: { code: 'ERR_UNAUTHENTICATED', title: 'Sessão expirada' } };
        return { json: state === 'mfa' ? { ...sessaoOk, mfa: 'verificar', empresa_ativa: undefined } : sessaoOk };
      }
      if (r.url === '/v1/conta/entrar') {
        if ((r.body as { senha: string }).senha !== 'certa e longa 123') return { status: 401, json: { code: 'ERR_CREDENCIAIS', title: 'E-mail ou senha incorretos' } };
        state = 'mfa';
        return { json: { mfa: 'verificar', nome: 'Maria' } };
      }
      if (r.url === '/v1/conta/mfa/verificar') {
        if ((r.body as { codigo: string }).codigo !== '123456') return { status: 400, json: { code: 'ERR_CODIGO_INVALIDO', title: 'Código inválido' } };
        state = 'ok';
        return { status: 204 };
      }
      if (r.url === '/v1/me') return { json: me };
      if (r.url.startsWith('/v1/usage')) return { json: usage };
      if (r.url === '/v1/primeiros-passos') return { json: { passos: [{ id: 'simular', titulo: 'Simule um cálculo', descricao: 'd', link: '/portal/simular', feito: true }, { id: 'auditar', titulo: 'Audite uma nota', descricao: 'd', link: '/portal/auditoria', feito: false }], concluidos: 1, total: 2 } };
      if (r.url.startsWith('/v1/avisos')) return { json: { avisos: [], nao_lidos: 2 } };
      return undefined;
    });
    renderAt('/portal');
    await userEvent.type(await screen.findByLabelText('E-mail'), 'maria@x.test');
    await userEvent.type(screen.getByLabelText('Senha'), 'errada');
    await userEvent.click(screen.getByRole('button', { name: 'Entrar' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('E-mail ou senha incorretos');

    await userEvent.clear(screen.getByLabelText('Senha'));
    await userEvent.type(screen.getByLabelText('Senha'), 'certa e longa 123');
    await userEvent.click(screen.getByRole('button', { name: 'Entrar' }));
    const code = await screen.findByLabelText('Código de 6 dígitos');
    await userEvent.type(code, '123456');
    await userEvent.click(screen.getByRole('button', { name: 'Entrar' }));
    expect(await screen.findByRole('heading', { name: 'Visão geral' })).toBeInTheDocument();
    expect((await screen.findAllByText('1.234')).length).toBeGreaterThan(0);
    // Primeiros passos (D-J04) e sino de avisos (D-J03).
    expect(await screen.findByText('Comece por aqui')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Audite uma nota' })).toHaveAttribute('href', '/portal/auditoria');
    expect((await screen.findAllByRole('link', { name: 'Avisos: 2 não lido(s)' })).length).toBeGreaterThan(0);
    // Nenhuma credencial guardada no navegador.
    expect(sessionStorage.length).toBe(0);
    expect(localStorage.getItem('tg.portal.key')).toBeNull();
  });

  it('menu mostra só o que o papel permite', async () => {
    mockApi((r) => {
      if (r.url === '/v1/conta/sessao') return { json: { ...sessaoOk, papel: 'finance', escopos: ['usage:read', 'billing:manage'] } };
      if (r.url === '/v1/me') return { json: me };
      if (r.url.startsWith('/v1/usage')) return { json: usage };
      return undefined;
    });
    renderAt('/portal');
    await screen.findByRole('heading', { name: 'Visão geral' });
    expect(screen.queryByRole('link', { name: 'API keys' })).toBeNull();
    expect(screen.queryByRole('link', { name: 'Equipe' })).toBeNull();
    expect(screen.queryByRole('link', { name: 'Simular' })).toBeNull();
  });

  it('nova API key mostra o segredo uma vez e revogar pede confirmação com o nome da chave', async () => {
    const keys = [
      { id: 'k2', nome: 'erp-antigo', prefixo: 'BBBBBBBB', scopes: ['simulacao:write'], expira_em: null, revogada_em: null, ultimo_uso: null, criada_em: '2026-10-01T00:00:00Z', atual: false },
    ];
    mockApi((r) => {
      if (r.url === '/v1/conta/sessao') return { json: sessaoOk };
      if (r.url === '/v1/me') return { json: me };
      if (r.url === '/v1/api-keys' && r.method === 'GET') return { json: { api_keys: keys } };
      if (r.url === '/v1/api-keys' && r.method === 'POST') return { status: 201, json: { id: 'k3', api_key: 'tg_live_sk_NEWKEY00_secret', prefixo: 'NEWKEY00', scopes: ['simulacao:write'] } };
      return undefined;
    });
    renderAt('/portal/api-keys');
    const rows = await screen.findAllByRole('row');
    const antiga = rows.find((row) => within(row).queryByText('erp-antigo'))!;
    await userEvent.click(within(antiga).getByRole('button', { name: 'Revogar' }));
    const confirm = await screen.findByRole('alertdialog', { name: 'Revogar a chave "erp-antigo"?' });
    expect(confirm).toHaveTextContent('401 imediatamente');
    expect(within(confirm).getByRole('button', { name: 'Cancelar' })).toHaveFocus();
    await userEvent.keyboard('{Escape}');
    expect(screen.queryByRole('alertdialog')).toBeNull();

    // Escopo que a sessão não tem fica desabilitado.
    expect(screen.getByRole('checkbox', { name: /dfe:manage/ })).toBeDisabled();
    await userEvent.click(screen.getByRole('button', { name: 'Criar' }));
    const dialog = await screen.findByRole('dialog', { name: 'Nova API key' });
    expect(dialog).toHaveTextContent('tg_live_sk_NEWKEY00_secret');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Já guardei' }));
    await waitFor(() => expect(screen.queryByText('tg_live_sk_NEWKEY00_secret')).toBeNull());
  });

  it('sair encerra a sessão no servidor e volta ao login', async () => {
    let loggedIn = true;
    mockApi((r) => {
      if (r.url === '/v1/conta/sessao') return loggedIn ? { json: sessaoOk } : { status: 401, json: { code: 'ERR_UNAUTHENTICATED', title: 'x' } };
      if (r.url === '/v1/conta/sair') { loggedIn = false; return { status: 204 }; }
      if (r.url === '/v1/me') return { json: me };
      if (r.url.startsWith('/v1/usage')) return { json: usage };
      return undefined;
    });
    renderAt('/portal');
    await userEvent.click(await screen.findByRole('button', { name: 'Sair' }));
    expect(calls.some((c) => c.url === '/v1/conta/sair' && c.method === 'POST')).toBe(true);
    expect(await screen.findByLabelText('E-mail')).toBeInTheDocument();
  });

  it('cadastro envia os dados e pede para confirmar o e-mail', async () => {
    mockApi((r) => (r.url === '/v1/conta/cadastro' ? { status: 202, json: { mensagem: 'ok' } } : r.url === '/v1/conta/sessao' ? { status: 401, json: { code: 'ERR_UNAUTHENTICATED', title: 'x' } } : undefined));
    renderAt('/cadastro');
    await userEvent.type(await screen.findByLabelText('Seu nome'), 'Maria Fiscal');
    await userEvent.type(screen.getByLabelText('E-mail de trabalho'), 'maria@x.test');
    await userEvent.type(screen.getByLabelText('Senha'), 'uma frase longa e boa');
    await userEvent.type(screen.getByLabelText('Empresa'), 'Empresa X');
    await userEvent.type(screen.getByLabelText(/CNPJ da empresa/), '11.222.333/0001-81');
    await userEvent.click(screen.getByRole('checkbox'));
    await userEvent.click(screen.getByRole('button', { name: 'Criar conta' }));
    expect(await screen.findByRole('heading', { name: 'Confira seu e-mail' })).toBeInTheDocument();
    expect(calls.find((c) => c.url === '/v1/conta/cadastro')!.body).toMatchObject({ empresa: { cnpj: '11222333000181' }, aceite_termos: true });
  });
});

describe('backoffice', () => {
  it('só propostas pendentes podem ser aprovadas; tudo vai pela sessão do operador', async () => {
    mockApi((r) => {
      if (r.url === '/admin/v1/conta/sessao') return { json: { usuario: { id: 'o1', nome: 'Fiscal B', email: 'fb@tg.test' }, mfa: 'ok', escopos: ['admin:fiscal'] } };
      if (r.url === '/admin/v1/me') return { json: { operador: 'fb@tg.test', scopes: ['admin:fiscal'] } };
      if (r.url === '/admin/v1/params/rates') return { json: [] };
      if (r.url.startsWith('/admin/v1/params/changes')) {
        return { json: [
          { id: 'c1', dominio: 'RATE_CATALOG', status: 'PROPOSED', proposto_por: 'fiscal-a', aprovado_por: null, justificativa: 'Senado', vigente_em: '2027-01-01T03:00:00Z', criado_em: '2026-11-20T10:00:00Z', payload: {} },
          { id: 'c2', dominio: 'RATE_CATALOG', status: 'APPLIED', proposto_por: 'fiscal-a', aprovado_por: 'fiscal-b', justificativa: 'TCU', vigente_em: '2026-11-01T03:00:00Z', criado_em: '2026-11-01T10:00:00Z', payload: {} },
        ] };
      }
      return undefined;
    });
    renderAt('/backoffice/aliquotas');
    expect(await screen.findAllByRole('button', { name: 'Aprovar e aplicar' })).toHaveLength(1);
    const api = calls.filter((c) => !c.url.startsWith('/version.json'));
    expect(api.every((c) => !('X-Admin-Key' in c.headers) && !('X-Api-Key' in c.headers) && c.headers[CSRF_HEADER] === '1')).toBe(true);
    // Sem admin:commercial, o menu esconde as áreas comerciais.
    expect(screen.queryByRole('link', { name: 'Operadores' })).toBeNull();
  });

  it('sem sessão de operador vai para o login do backoffice', async () => {
    mockApi((r) => (r.url === '/admin/v1/conta/sessao' ? { status: 401, json: { code: 'ERR_UNAUTHENTICATED', title: 'x' } } : undefined));
    renderAt('/backoffice');
    expect(await screen.findByText('Conta de operador (e-mail, senha e MFA).')).toBeInTheDocument();
  });
});
