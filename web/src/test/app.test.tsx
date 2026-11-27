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
/** Consumo por competência (respeita ?mes=): só o mês corrente tem uso, como na tela de histórico. */
const usageFor = (url: string) => {
  const mes = new URL(url, 'http://x').searchParams.get('mes') ?? currentMonth();
  return mes === currentMonth() ? { ...usage, competencia: mes } : { ...usage, competencia: mes, documentos_auditados: 0, simulacoes: 0, excedente_documentos: 0 };
};
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
      if (r.url.startsWith('/v1/usage')) return { json: usageFor(r.url) };
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
      if (r.url.startsWith('/v1/usage')) return { json: usageFor(r.url) };
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
      if (r.url === '/v1/api-keys/k2' && r.method === 'DELETE') return { status: 204 };
      if (r.url === '/v1/oauth-clients') return { json: { clientes: [] } };
      return undefined;
    });
    renderAt('/portal/api-keys');
    const antiga = (await screen.findByText('erp-antigo')).closest('tr')!;
    await userEvent.click(within(antiga).getByRole('button', { name: 'Revogar' }));
    const confirm = await screen.findByRole('alertdialog', { name: 'Revogar a chave "erp-antigo"?' });
    expect(confirm).toHaveTextContent('401 imediatamente');
    expect(within(confirm).getByRole('button', { name: 'Cancelar' })).toHaveFocus();
    await userEvent.keyboard('{Escape}');
    expect(screen.queryByRole('alertdialog')).toBeNull();

    // Confirmando de verdade: a revogação é enviada para a chave certa.
    await userEvent.click(within(antiga).getByRole('button', { name: 'Revogar' }));
    const again = await screen.findByRole('alertdialog', { name: 'Revogar a chave "erp-antigo"?' });
    await userEvent.click(within(again).getByRole('button', { name: /Revogar/ }));
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.url === '/v1/api-keys/k2')).toBe(true));

    // Escopo que a sessão não tem fica desabilitado.
    screen.getAllByRole('checkbox', { name: /dfe:manage/ }).forEach((c) => expect(c).toBeDisabled());   // API key e cliente OAuth
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
      if (r.url.startsWith('/v1/usage')) return { json: usageFor(r.url) };
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

  it('cobrança: teto de excedente e régua de inadimplência; a verificação fica em Empresa (D-R08/D-R09/D-R25)', async () => {
    const cobranca = {
      plano: 'ESSENCIAL', assinatura: { situacao: 'past_due', renova_em: null }, pagamento_online: true, faturas: [],
      excedente: { teto_centavos: 0, usado_centavos: 0, franquia_rigida: false, preco_documento_milicentavos: 9000, preco_simulacao_milicentavos: 500 },
      verificacao: { verificada: false, verificada_em: null, metodo: null },
      inadimplencia: { desde: '2026-11-10T10:00:00Z', situacao: 'RESTRICTED', restringe_em: '2026-11-20T10:00:00Z', suspende_em: '2026-11-25T10:00:00Z' },
    };
    mockApi((r) => {
      if (r.url === '/v1/conta/sessao') return { json: { ...sessaoOk, escopos: [...sessaoOk.escopos, 'billing:manage'] } };
      if (r.url === '/v1/me') return { json: me };
      if (r.url === '/v1/conta/planos') return { json: [] };
      if (r.url === '/v1/cobranca') return { json: cobranca };
      if (r.url === '/v1/cobranca/teto') return { json: { teto_centavos: 5000 } };
      return undefined;
    });
    renderAt('/portal/cobranca');
    expect(await screen.findByText(/cálculos e auditorias estão bloqueados/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Verificar a empresa' })).toHaveAttribute('href', '/portal/empresa');
    const teto = screen.getByLabelText(/Teto mensal de excedente/);
    await userEvent.clear(teto);
    await userEvent.type(teto, '50,00');
    await userEvent.click(screen.getByRole('button', { name: 'Salvar teto' }));
    await waitFor(() => expect(calls.find((c) => c.url === '/v1/cobranca/teto')?.body).toEqual({ teto_centavos: 5000 }));
  });

  it('empresa: o fiscal verifica pelo código no e-mail do cadastro da Receita (D-R10/D-R25)', async () => {
    let verificada = false;
    mockApi((r) => {
      if (r.url === '/v1/conta/sessao') return { json: { ...sessaoOk, papel: 'fiscal', escopos: ['cnpjs:manage', 'dfe:manage', 'simulacao:write', 'auditoria:write'] } };
      if (r.url === '/v1/me') return { json: me };
      if (r.url === '/v1/verificacao-cnpj') return { json: { cnpj: '11222333000181', verificada, verificada_em: verificada ? '2026-11-20T10:00:00Z' : null, metodo: verificada ? 'EMAIL_RECEITA' : null, metodos_disponiveis: ['CERTIFICADO', 'EMAIL_RECEITA', 'MANUAL'] } };
      if (r.url === '/v1/verificacao-cnpj/email') return { status: 202, json: { enviado_para: 'ti***@empresa.test', expira_em_minutos: 30 } };
      if (r.url === '/v1/verificacao-cnpj/email/confirmar') { verificada = true; return { json: { verificada: true, metodo: 'EMAIL_RECEITA' } }; }
      return undefined;
    });
    renderAt('/portal/empresa');
    expect(await screen.findByRole('link', { name: 'Dados e verificação' })).toBeInTheDocument();
    await userEvent.click(await screen.findByRole('button', { name: 'Enviar código' }));
    await userEvent.type(await screen.findByLabelText(/Código enviado para ti\*\*\*@empresa.test/), '123456');
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar código' }));
    expect(await screen.findByText('verificada')).toBeInTheDocument();
    expect(calls.find((c) => c.url === '/v1/verificacao-cnpj/email/confirmar')!.body).toEqual({ codigo: '123456' });
    expect(screen.queryByRole('link', { name: 'Plano e cobrança' })).toBeNull();
  });

  it('rota fora do papel explica em vez de abrir a tela', async () => {
    mockApi((r) => {
      if (r.url === '/v1/conta/sessao') return { json: { ...sessaoOk, papel: 'finance', escopos: ['usage:read', 'billing:manage'] } };
      if (r.url === '/v1/me') return { json: me };
      return undefined;
    });
    renderAt('/portal/api-keys');
    expect(await screen.findByRole('heading', { name: 'Sem acesso a esta área' })).toBeInTheDocument();
    expect(calls.some((c) => c.url === '/v1/api-keys')).toBe(false);
  });

  it('simulação: município pelo nome, data de hoje, comparação com 2027 e selo de não oficial', async () => {
    mockApi((r) => {
      if (r.url === '/v1/conta/sessao') return { json: sessaoOk };
      if (r.url === '/v1/me') return { json: me };
      if (r.url.startsWith('/v1/publico/municipios')) return { json: [{ codigo: 3304557, nome: 'Rio de Janeiro', uf: 'RJ' }] };
      if (r.url === '/v1/calculo/simular') {
        const d = (r.body as { data_fato_gerador: string }).data_fato_gerador;
        const cbs = d.startsWith('2027') ? 112 : 9;
        return { json: { simulation_id: 's', regime_calculo: 'REGULAR', param_snapshot_id: 1, oficial: true, avisos: [{ codigo: 'ALIQUOTA_PROVISORIA', mensagem: 'CBS provisória.' }],
          totais: { valor_base_total: 1000, cbs_valor: cbs, ibs_uf_valor: 1, ibs_mun_valor: 0, ibs_total_valor: 1, imposto_seletivo_valor: 0 } } };
      }
      return undefined;
    });
    renderAt('/portal/simular');
    const campo = await screen.findByLabelText('Município de incidência');
    await userEvent.clear(campo);
    await userEvent.type(campo, 'rio de');
    await userEvent.click(await screen.findByRole('option', { name: /Rio de Janeiro/ }));
    const data = screen.getByLabelText(/Data e hora do fato gerador/) as HTMLInputElement;
    expect(data.value.slice(0, 4)).toBe(String(new Date().getFullYear()));
    await userEvent.clear(data);
    await userEvent.type(data, '2026-08-10T10:00');
    await userEvent.click(screen.getByRole('button', { name: 'Simular' }));
    expect(await screen.findByText('cálculo oficial')).toBeInTheDocument();
    expect(calls.find((c) => c.url === '/v1/calculo/simular')!.body).toMatchObject({ municipio_ibge: 3304557, uf: 'RJ' });
    await userEvent.click(screen.getByRole('button', { name: /Comparar com a mesma operação em 2027/ }));
    expect(await screen.findByRole('columnheader', { name: 'Em 2027' })).toBeInTheDocument();
    expect(screen.getAllByText(/CBS provisória\./).length).toBeGreaterThan(0);
  });

  it('conta sem empresa vê como pedir convite ou contestar a titularidade com o e-CNPJ', async () => {
    mockApi((r) => {
      if (r.url === '/v1/conta/sessao') return { json: { ...sessaoOk, empresas: [], empresa_ativa: undefined, escopos: [] } };
      if (r.url === '/v1/conta/disputa-cnpj') return { status: 202, json: { mensagem: 'Pedido registrado. A conta atual foi avisada.' } };
      return undefined;
    });
    renderAt('/portal');
    expect(await screen.findByRole('heading', { name: 'Sua conta ainda não tem empresa' })).toBeInTheDocument();
    expect(calls.some((c) => c.url === '/v1/me')).toBe(false);
    await userEvent.upload(screen.getByLabelText('Certificado e-CNPJ (.pfx)'), new File([new Uint8Array([1, 2, 3])], 'a1.pfx'));
    await userEvent.type(screen.getByLabelText('Senha do certificado'), 'senha-a1');
    await userEvent.click(screen.getByRole('button', { name: 'Enviar contestação' }));
    expect(await screen.findByText(/Pedido registrado/)).toBeInTheDocument();
    expect(calls.find((c) => c.url === '/v1/conta/disputa-cnpj')!.body).toEqual({ pfx_base64: 'AQID', senha: 'senha-a1' });
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

  it('disputas de CNPJ: só as abertas podem ser decididas e transferir pede confirmação (D-R10)', async () => {
    mockApi((r) => {
      if (r.url === '/admin/v1/conta/sessao') return { json: { usuario: { id: 'o1', nome: 'Seg', email: 'seg@tg.test' }, mfa: 'ok', escopos: ['admin:security'] } };
      if (r.url === '/admin/v1/me') return { json: { operador: 'seg@tg.test', scopes: ['admin:security'] } };
      if (r.url === '/admin/v1/disputas') {
        return { json: { disputas: [
          { id: 'd1', cnpj: '11222333', tenant_id: 't1', empresa: 'Empresa X', requerente: 'real@x.test', requerente_nome: 'Titular', certificado: 'ABC', situacao: 'ABERTA', criada_em: '2026-11-20T10:00:00Z', resolvida_por: null, resolvida_em: null },
          { id: 'd2', cnpj: '44555666', tenant_id: 't2', empresa: 'Empresa Y', requerente: 'y@x.test', requerente_nome: 'Outro', certificado: 'DEF', situacao: 'REJEITADA', criada_em: '2026-11-01T10:00:00Z', resolvida_por: 'seg@tg.test', resolvida_em: '2026-11-02T10:00:00Z' },
        ] } };
      }
      if (r.url === '/admin/v1/disputas/d1/resolver') return { json: { disputa: 'd1', acao: 'transferir', tenant_id: 't1' } };
      return undefined;
    });
    renderAt('/backoffice/disputas');
    expect(await screen.findAllByRole('button', { name: 'Transferir' })).toHaveLength(1);
    await userEvent.click(screen.getByRole('button', { name: 'Transferir' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Transferir Empresa X para real@x.test?' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Transferir empresa' }));
    await waitFor(() => expect(calls.find((c) => c.url === '/admin/v1/disputas/d1/resolver')?.body).toEqual({ acao: 'transferir' }));
  });

  it('sem sessão de operador vai para o login do backoffice', async () => {
    mockApi((r) => (r.url === '/admin/v1/conta/sessao' ? { status: 401, json: { code: 'ERR_UNAUTHENTICATED', title: 'x' } } : undefined));
    renderAt('/backoffice');
    expect(await screen.findByText('Conta de operador (e-mail, senha e MFA).')).toBeInTheDocument();
  });
});
