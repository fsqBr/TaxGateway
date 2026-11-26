import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import App from '../App';
import { ApiError, describeError, getCredential, portal, setCredential } from '../lib/api';
import { cnpj, currentMonth } from '../lib/format';
import { safeReturnPath } from '../components/Shell';

type Handler = (req: { method: string; url: string; headers: Record<string, string>; body?: unknown }) => { status?: number; json?: unknown } | undefined;

let calls: { method: string; url: string; headers: Record<string, string>; body?: unknown }[] = [];

function mockApi(handler: Handler) {
  calls = [];
  vi.stubGlobal('fetch', vi.fn(async (input: string, init: RequestInit = {}) => {
    const req = {
      method: init.method ?? 'GET',
      url: input,
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

const me = { tenant_id: 't1', nome: 'Empresa X', plano: 'GROWTH', situacao: 'ACTIVE', credencial: 'api_key', auto_provisionar_cnpj: false, scopes: ['keys:manage', 'simulacao:write', 'usage:read'], limites: { rpm: 600, concorrencia: 20 } };
const usage = { competencia: '2026-11', plano: 'GROWTH', documentos_auditados: 1234, simulacoes: 10, cnpjs_ativos: 2, franquia_documentos: 1000, excedente_documentos: 234, atualizado_em: '2026-11-25T12:00:00Z' };

const renderAt = (path: string) => render(<MemoryRouter initialEntries={[path]}><App /></MemoryRouter>);

describe('cliente HTTP', () => {
  beforeEach(() => sessionStorage.clear());

  it('envia a credencial da aba só no header e traduz problem+json', async () => {
    setCredential('portal', 'tg_test_sk_AAAAAAAA_x');
    mockApi((r) => (r.url.startsWith('/v1/calculo') ? { status: 403, json: { code: 'ERR_FORBIDDEN_SCOPE', title: 'Escopo insuficiente', detail: 'Exige simulacao:write' } } : undefined));
    const err = await portal('/v1/calculo/simular', { method: 'POST', body: {}, idempotent: true }).catch((e) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).code).toBe('ERR_FORBIDDEN_SCOPE');
    expect(calls[0].headers['X-Api-Key']).toBe('tg_test_sk_AAAAAAAA_x');
    expect(calls[0].headers['Idempotency-Key']).toMatch(/^[0-9a-f-]{36}$/);
    expect(calls[0].url).not.toContain('tg_test');
    expect(describeError(new ApiError(401, 'ERR_UNAUTHENTICATED', 'x'))).toContain('Entre novamente');
  });

  it('depois do login só volta para caminho interno (sem open redirect)', () => {
    expect(safeReturnPath('/portal/api-keys', '/portal')).toBe('/portal/api-keys');
    for (const bad of ['//evil.com', '/\evil.com', 'https://evil.com', '/portal/../x', '/portal?x=//evil', 'javascript:alert(1)', undefined]) {
      expect(safeReturnPath(bad, '/portal')).toBe('/portal');
    }
  });

  it('formata CNPJ e competência', () => {
    expect(cnpj('11222333000181')).toBe('11.222.333/0001-81');
    expect(currentMonth(new Date('2026-12-01T01:00:00Z'))).toBe('2026-11'); // ainda 30/11 em Brasília
  });
});

describe('portal do cliente', () => {
  beforeEach(() => sessionStorage.clear());

  it('sem credencial vai para o login; login válido guarda a chave na aba e abre a visão geral', async () => {
    mockApi((r) => {
      if (r.url === '/v1/me') return r.headers['X-Api-Key'] === 'tg_live_sk_OK' ? { json: me } : { status: 401, json: { code: 'ERR_UNAUTHENTICATED', title: 'API key inválida' } };
      if (r.url.startsWith('/v1/usage')) return { json: usage };
      return undefined;
    });
    renderAt('/portal');
    const input = await screen.findByLabelText('API key');

    await userEvent.type(input, 'errada');
    await userEvent.click(screen.getByRole('button', { name: 'Entrar' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Entre novamente');
    expect(getCredential('portal')).toBeNull();

    await userEvent.clear(input);
    await userEvent.type(input, 'tg_live_sk_OK');
    await userEvent.click(screen.getByRole('button', { name: 'Entrar' }));
    expect(await screen.findByRole('heading', { name: 'Visão geral' })).toBeInTheDocument();
    expect(await screen.findByText('1.234')).toBeInTheDocument();
    expect(getCredential('portal')).toBe('tg_live_sk_OK');
  });

  it('nova API key mostra o segredo uma vez e não oferece revogar a chave da sessão', async () => {
    setCredential('portal', 'tg_live_sk_OK');
    const keys = [
      { id: 'k1', nome: 'sessao', prefixo: 'AAAAAAAA', scopes: ['keys:manage'], expira_em: null, revogada_em: null, ultimo_uso: null, criada_em: '2026-11-01T00:00:00Z', atual: true },
      { id: 'k2', nome: 'antiga', prefixo: 'BBBBBBBB', scopes: ['simulacao:write'], expira_em: null, revogada_em: null, ultimo_uso: null, criada_em: '2026-10-01T00:00:00Z', atual: false },
    ];
    mockApi((r) => {
      if (r.url === '/v1/me') return { json: me };
      if (r.url === '/v1/api-keys' && r.method === 'GET') return { json: { api_keys: keys } };
      if (r.url === '/v1/api-keys' && r.method === 'POST') return { status: 201, json: { id: 'k3', api_key: 'tg_live_sk_NEWKEY00_secret', prefixo: 'NEWKEY00', scopes: ['simulacao:write'] } };
      return undefined;
    });
    renderAt('/portal/api-keys');
    const rows = await screen.findAllByRole('row');
    const sessao = rows.find((row) => within(row).queryByText('sessao'))!;
    const antiga = rows.find((row) => within(row).queryByText('antiga'))!;
    expect(within(sessao).queryByRole('button', { name: 'Revogar' })).toBeNull();
    expect(within(antiga).getByRole('button', { name: 'Revogar' })).toBeInTheDocument();

    // Escopo que a chave atual não tem fica desabilitado.
    expect(screen.getByRole('checkbox', { name: /dfe:manage/ })).toBeDisabled();

    await userEvent.click(screen.getByRole('button', { name: 'Criar' }));
    const dialog = await screen.findByRole('dialog', { name: 'Nova API key' });
    expect(dialog).toHaveTextContent('tg_live_sk_NEWKEY00_secret');
    expect(calls.find((c) => c.method === 'POST')!.body).toMatchObject({ scopes: ['simulacao:write'] });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Já guardei' }));
    await waitFor(() => expect(screen.queryByText('tg_live_sk_NEWKEY00_secret')).toBeNull());
  });

  it('sair apaga a credencial da aba', async () => {
    setCredential('portal', 'tg_live_sk_OK');
    mockApi((r) => (r.url === '/v1/me' ? { json: me } : r.url.startsWith('/v1/usage') ? { json: usage } : undefined));
    renderAt('/portal');
    await userEvent.click(await screen.findByRole('button', { name: 'Sair' }));
    expect(getCredential('portal')).toBeNull();
    expect(await screen.findByLabelText('API key')).toBeInTheDocument();
  });
});

describe('backoffice', () => {
  beforeEach(() => sessionStorage.clear());

  it('só propostas pendentes podem ser aprovadas', async () => {
    setCredential('backoffice', 'op-key');
    mockApi((r) => {
      if (r.url === '/admin/v1/me') return { json: { operador: 'fiscal-b', scopes: ['admin:fiscal'] } };
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
    expect(calls.every((c) => c.headers['X-Admin-Key'] === 'op-key' && !('X-Api-Key' in c.headers))).toBe(true);
  });

  it('credencial do portal não abre o backoffice', async () => {
    setCredential('portal', 'tg_live_sk_OK');
    mockApi(() => undefined);
    renderAt('/backoffice');
    expect(await screen.findByLabelText('Chave de operador')).toBeInTheDocument();
  });
});
