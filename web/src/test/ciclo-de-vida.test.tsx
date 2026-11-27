import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AtualizacoesFiscais } from '../components/AtualizacoesFiscais';
import { Integracoes } from '../backoffice/Integracoes';
import { PlanosResumo } from '../pages/Landing';
import { PlanMatrix, linhasDaMatriz, type PlanoPublico } from '../pages/Planos';
import { PreferenciasEmail } from '../portal/Conta';

// D-R30 a D-R34: linha do tempo das atualizações fiscais, preferências de e-mail e matriz de comparação dos planos.

type Req = { method: string; url: string; body?: unknown };
let calls: Req[] = [];

function mockApi(handler: (req: Req) => { status?: number; json?: unknown } | undefined) {
  calls = [];
  vi.stubGlobal('fetch', vi.fn(async (input: string, init: RequestInit = {}) => {
    const req: Req = { method: init.method ?? 'GET', url: input, body: typeof init.body === 'string' ? JSON.parse(init.body) : undefined };
    calls.push(req);
    const r = handler(req) ?? { status: 404, json: { code: 'ERR_NOT_FOUND', title: 'Não encontrado' } };
    const status = r.status ?? 200;
    return new Response(r.json === undefined ? null : JSON.stringify(r.json), { status, headers: { 'content-type': status >= 400 ? 'application/problem+json' : 'application/json' } });
  }));
}

afterEach(() => vi.unstubAllGlobals());

const base = { descricao: 'd', recursos: [], cnpj_extra_centavos: 0, contratacao_online: true, excedente_simulacao_milicentavos: 0 };
const PLANOS: PlanoPublico[] = [
  { ...base, codigo: 'SANDBOX', nome: 'Sandbox', preco_centavos: 0, documentos_incluidos: 200, simulacoes_incluidas: 2000, cnpjs_incluidos: 1, usuarios: 2, excedente_documento_centavos: 0, requisicoes_por_minuto: 60, requisicoes_simultaneas: 2, lote_max_documentos: 100, captura_dfe: false, franquia_rigida: true },
  { ...base, codigo: 'PROFISSIONAL', nome: 'Profissional', preco_centavos: 49900, documentos_incluidos: 15000, simulacoes_incluidas: 150000, cnpjs_incluidos: 10, usuarios: 10, cnpj_extra_centavos: 2900, excedente_documento_centavos: 5, excedente_simulacao_milicentavos: 200, requisicoes_por_minuto: 1200, requisicoes_simultaneas: 8, lote_max_documentos: 25000, captura_dfe: true, franquia_rigida: false },
  { ...base, codigo: 'OEM', nome: 'Parceiro OEM', preco_centavos: 0, contratacao_online: false, documentos_incluidos: 0, simulacoes_incluidas: 0, cnpjs_incluidos: null, usuarios: null, excedente_documento_centavos: 0, requisicoes_por_minuto: 3000, requisicoes_por_minuto_por_cnpj: 120, captura_dfe: true, franquia_rigida: false },
];

describe('matriz de comparação dos planos', () => {
  it('monta as linhas a partir do cadastro e marca o plano atual', () => {
    const linhas = linhasDaMatriz(PLANOS);
    const v = (rotulo: string) => linhas.find((l) => l.rotulo === rotulo)!.valores;
    expect(v('Mensalidade')).toEqual(['Grátis', expect.stringContaining('499,00') as unknown as string, 'Sob consulta']);
    expect(v('Auditoria além da franquia')[0]).toBe('para no limite');
    expect(v('Auditoria além da franquia')[2]).toBe('sob contrato');
    expect(v('Auditorias por mês')[2]).toBe('sob contrato');
    expect(v('Simulação além da franquia')).toEqual(['para no limite', expect.stringContaining('0,002') as unknown as string, 'sob contrato']);
    expect(v('Teto de excedente definido por você')).toEqual([false, true, true]);
    expect(v('CNPJs incluídos')[2]).toBe('ilimitados');
    expect(v('CNPJ além dos incluídos')).toEqual([false, expect.stringContaining('29,00') as unknown as string, false]);
    expect(v('Captura de notas de fornecedores (DF-e)')).toEqual([false, true, true]);
    expect(v('Sub-limite por CNPJ atendido')).toEqual([false, false, expect.stringContaining('120') as unknown as string]);
    expect(v('Notas por lote')[2]).toBe(false);
    const porCnpj = linhasDaMatriz([{ ...PLANOS[2], cnpjs_incluidos: 0, cnpj_extra_centavos: 400 }]);
    expect(porCnpj.find((l) => l.rotulo === 'CNPJs incluídos')!.valores).toEqual(['cada CNPJ é cobrado']);

    render(<PlanMatrix planos={PLANOS} atual="PROFISSIONAL" />);
    const tabela = screen.getByRole('region', { name: 'Comparação dos planos' });
    expect(within(tabela).getByText('(atual)')).toBeInTheDocument();
    expect(within(tabela).getByRole('rowheader', { name: 'Captura de notas de fornecedores (DF-e)' })).toBeInTheDocument();
    expect(within(tabela).getAllByLabelText('incluído').length).toBeGreaterThan(3);
    expect(within(tabela).getAllByLabelText('não incluído').length).toBeGreaterThan(1);
    expect(within(tabela).getByText('Integração')).toBeInTheDocument();
  });

  it('sem os campos novos da API, omite as linhas que dependem deles', () => {
    const antigo = PLANOS.map(({ captura_dfe: _c, requisicoes_por_minuto: _r, requisicoes_simultaneas: _s, requisicoes_por_minuto_por_cnpj: _p, lote_max_documentos: _l, excedente_simulacao_milicentavos: _e, ...p }) => p as PlanoPublico);
    const rotulos = linhasDaMatriz(antigo).map((l) => l.rotulo);
    expect(rotulos).not.toContain('Captura de notas de fornecedores (DF-e)');
    expect(rotulos).not.toContain('Requisições por minuto (API)');
    expect(rotulos).not.toContain('Simulação além da franquia');
  });
});

describe('atualizações fiscais', () => {
  it('mostra alíquota futura, motor atualizado e a versão em uso', async () => {
    mockApi((r) => (r.url.includes('/v1/publico/atualizacoes-fiscais') ? {
      json: {
        bundle: '2026.11.2',
        atualizacoes: [
          { id: '1', tipo: 'ALIQUOTA', titulo: 'Alíquota atualizada pela Receita Federal', texto: 'IBS municipal: 0,5% a partir de 10/03/2099.', vigencia: '2099-03-10', origem: 'MOTOR_OFICIAL', publicado_em: '2026-11-27T10:00:00Z' },
          { id: '2', tipo: 'MOTOR', titulo: 'Motor oficial da Receita atualizado (2026.11.2)', texto: 'Nos 158 casos o cálculo não mudou.', vigencia: '2026-11-01', origem: 'MOTOR_OFICIAL', publicado_em: '2026-11-01T10:00:00Z' },
          { id: '3', tipo: 'ALIQUOTA', titulo: 'Mudança de alíquota publicada', texto: 'CBS provisória.', vigencia: null, origem: 'CATALOGO', publicado_em: '2026-10-01T10:00:00Z' },
        ],
      },
    } : undefined));
    render(<AtualizacoesFiscais />);
    expect(await screen.findByText('2026.11.2')).toBeInTheDocument();
    expect(screen.getByText('vale a partir de 10/03/2099')).toBeInTheDocument();
    expect(screen.getByText('motor oficial')).toBeInTheDocument();
    expect(screen.getByText(/vale desde 01\/11\/2026/)).toBeInTheDocument();
    expect(screen.getByText(/catálogo revisado pela equipe fiscal/)).toBeInTheDocument();
    expect(calls[0].url).toContain('limite=10');
  });

  it('lista vazia e erro', async () => {
    mockApi(() => ({ json: { bundle: 'b', atualizacoes: [] } }));
    const { unmount } = render(<AtualizacoesFiscais limite={5} />);
    expect(await screen.findByText('Nenhuma atualização publicada até agora.')).toBeInTheDocument();
    unmount();
    mockApi(() => ({ status: 503, json: { code: 'ERR', title: 'x' } }));
    render(<AtualizacoesFiscais />);
    expect(await screen.findByText('Não foi possível carregar as atualizações agora.')).toBeInTheDocument();
  });
});

describe('preferências de e-mail', () => {
  it('desliga e religa um informativo e mostra o erro do servidor', async () => {
    let desligados: string[] = [];
    let falhar = false;
    mockApi((r) => {
      if (!r.url.endsWith('/v1/conta/preferencias-email')) return undefined;
      if (r.method === 'PUT') {
        if (falhar) return { status: 400, json: { code: 'ERR_VALIDATION', title: 'Aviso que não pode ser desligado' } };
        desligados = (r.body as { desligados: string[] }).desligados;
      }
      return { json: { desligados, opcionais: [{ tipo: 'RESUMO_MENSAL', descricao: 'Resumo do mês' }, { tipo: 'MOTOR_ATUALIZADO', descricao: 'Nova versão do motor' }] } };
    });
    render(<MemoryRouter><PreferenciasEmail /></MemoryRouter>);
    const resumo = await screen.findByRole('checkbox', { name: 'Resumo do mês' });
    expect(resumo).toBeChecked();
    await userEvent.click(resumo);
    await waitFor(() => expect(screen.getByRole('checkbox', { name: 'Resumo do mês' })).not.toBeChecked());
    expect(calls.find((c) => c.method === 'PUT')!.body).toEqual({ desligados: ['RESUMO_MENSAL'] });
    expect(screen.getByText('Preferências salvas.')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('checkbox', { name: 'Resumo do mês' }));
    await waitFor(() => expect(screen.getByRole('checkbox', { name: 'Resumo do mês' })).toBeChecked());
    expect(desligados).toEqual([]);

    falhar = true;
    await userEvent.click(screen.getByRole('checkbox', { name: 'Nova versão do motor' }));
    expect(await screen.findByText(/não pode ser desligado/)).toBeInTheDocument();
  });
});

describe('planos na home', () => {
  it('mostra só os contratáveis online, com link para a comparação; sem planos, nada', async () => {
    mockApi((r) => (r.url.endsWith('/v1/conta/planos') ? { json: PLANOS } : undefined));
    const { unmount } = render(<MemoryRouter><PlanosResumo /></MemoryRouter>);
    expect(await screen.findByRole('article', { name: 'Plano Profissional' })).toHaveTextContent('notas de fornecedores');
    expect(screen.getByRole('article', { name: 'Plano Sandbox' })).toHaveTextContent('1 CNPJ');
    expect(screen.queryByRole('article', { name: 'Plano Parceiro OEM' })).toBeNull();
    expect(screen.getByRole('link', { name: /Comparar todos os planos/ })).toHaveAttribute('href', '/planos#comparar');
    unmount();
    mockApi(() => ({ status: 503, json: { code: 'ERR', title: 'x' } }));
    const { container } = render(<MemoryRouter><PlanosResumo /></MemoryRouter>);
    await waitFor(() => expect(calls.length).toBeGreaterThan(0));
    expect(container).toBeEmptyDOMElement();
  });
});

describe('backoffice: integrações', () => {
  const item = (id: string, nome: string, situacao: string) => ({ id, nome, situacao, resumo: `resumo ${id}`, detalhes: [{ rotulo: 'Último', valor: 'agora' }] });

  it('mostra a situação de cada integração e testa e-mail e Stripe', async () => {
    let geral = 'FALHA';
    mockApi((r) => {
      if (r.url.endsWith('/admin/v1/integracoes')) {
        return { json: { geral, verificado_em: '2026-11-27T15:00:00Z', integracoes: [item('email', 'E-mail (SMTP)', 'OK'), item('stripe', 'Pagamentos (Stripe)', 'FALHA'), item('dfe', 'Captura', 'DESLIGADO'), item('receita', 'Sincronização', 'ATENCAO')] } };
      }
      if (r.url.endsWith('/email-teste')) {
        const para = (r.body as { para?: string }).para;
        return para === 'x@y.z' ? { status: 403, json: { code: 'ERR_FORBIDDEN_SCOPE', title: 'Só operadores de segurança escolhem o destinatário' } } : { json: { enviado: true, mensagem: 'E-mail de teste enviado.' } };
      }
      if (r.url.endsWith('/stripe-teste')) return { json: { ok: false, mensagem: 'A Stripe recusou a chave.' } };
      return undefined;
    });
    render(<MemoryRouter><Integracoes /></MemoryRouter>);
    expect(await screen.findByText(/Há integração com falha/)).toBeInTheDocument();
    expect(screen.getByText('funcionando')).toBeInTheDocument();
    expect(screen.getByText('com falha')).toBeInTheDocument();
    expect(screen.getByText('não configurado')).toBeInTheDocument();
    expect(screen.getByText('atenção')).toBeInTheDocument();
    expect(screen.getByLabelText('Detalhes: E-mail (SMTP)')).toHaveTextContent('agora');

    await userEvent.click(screen.getByRole('button', { name: 'Enviar e-mail de teste' }));
    expect(await screen.findByText('E-mail de teste enviado.')).toBeInTheDocument();
    expect(calls.find((c) => c.url.endsWith('/email-teste'))!.body).toEqual({});
    await userEvent.type(screen.getByLabelText('Enviar para'), 'x@y.z');
    await userEvent.click(screen.getByRole('button', { name: 'Enviar e-mail de teste' }));
    expect(await screen.findByText(/Seu acesso não permite esta ação/)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Testar Stripe' }));
    expect(await screen.findByText('A Stripe recusou a chave.')).toBeInTheDocument();

    geral = 'OK';
    await userEvent.click(screen.getByRole('button', { name: /Verificar de novo/ }));
    expect(await screen.findByText('Tudo funcionando.')).toBeInTheDocument();
    geral = 'ATENCAO';
    await userEvent.click(screen.getByRole('button', { name: /Verificar de novo/ }));
    expect(await screen.findByText(/pontos de atenção/)).toBeInTheDocument();
  });
});
