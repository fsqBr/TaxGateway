import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App from '../App';

// Site público: alíquotas por município, vitrine de planos e cadastro com plano escolhido.

type Req = { method: string; url: string; headers: Record<string, string>; body?: unknown };
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
    };
    calls.push(req);
    const r = handler(req) ?? { status: 404, json: { code: 'ERR_NOT_FOUND', title: 'Não encontrado' } };
    const status = r.status ?? 200;
    const text = r.json === undefined ? '' : JSON.stringify(r.json);
    return new Response(text || null, { status, headers: { 'content-type': status >= 400 ? 'application/problem+json' : 'application/json' } });
  }));
}

afterEach(() => vi.unstubAllGlobals());

const renderAt = (path: string) => render(<MemoryRouter initialEntries={[path]}><App /></MemoryRouter>);
const semSessao = { status: 401, json: { code: 'ERR_UNAUTHENTICATED', title: 'x' } };

const base = { descricao: 'd', recursos: [], cnpj_extra_centavos: 0, contratacao_online: true, requisicoes_simultaneas: 3, requisicoes_por_minuto_por_cnpj: null };
const PLANOS = [
  { ...base, codigo: 'SANDBOX', nome: 'Sandbox', preco_centavos: 0, documentos_incluidos: 200, simulacoes_incluidas: 2000, cnpjs_incluidos: 1, usuarios: 2, excedente_documento_centavos: 0, excedente_simulacao_milicentavos: 0, requisicoes_por_minuto: 60, lote_max_documentos: 100, lote_max_bytes: 10485760, captura_dfe: false, franquia_rigida: true },
  { ...base, codigo: 'ESSENCIAL', nome: 'Essencial', preco_centavos: 19900, documentos_incluidos: 2000, simulacoes_incluidas: 20000, cnpjs_incluidos: 3, usuarios: 3, cnpj_extra_centavos: 2900, excedente_documento_centavos: 8, excedente_simulacao_milicentavos: 400, requisicoes_por_minuto: 300, lote_max_documentos: 2000, lote_max_bytes: 52428800, captura_dfe: false, franquia_rigida: false },
  { ...base, codigo: 'ESCRITORIO', nome: 'Escritório', preco_centavos: 99900, documentos_incluidos: 30000, simulacoes_incluidas: 100000, cnpjs_incluidos: 50, usuarios: null, excedente_documento_centavos: 4, excedente_simulacao_milicentavos: 0, requisicoes_por_minuto: 2000, lote_max_documentos: 25000, lote_max_bytes: 524288000, captura_dfe: true, franquia_rigida: false },
  { ...base, codigo: 'PROFISSIONAL', nome: 'Profissional', preco_centavos: 49900, documentos_incluidos: 15000, simulacoes_incluidas: 150000, cnpjs_incluidos: 10, usuarios: 10, excedente_documento_centavos: 5, excedente_simulacao_milicentavos: 200, requisicoes_por_minuto: 1200, lote_max_documentos: 25000, lote_max_bytes: 524288000, captura_dfe: true, franquia_rigida: false },
  { ...base, codigo: 'ENTERPRISE', nome: 'Enterprise', preco_centavos: 0, contratacao_online: false, documentos_incluidos: 0, simulacoes_incluidas: 0, cnpjs_incluidos: null, usuarios: null, excedente_documento_centavos: 0, excedente_simulacao_milicentavos: 0, requisicoes_por_minuto: 3000, lote_max_documentos: 25000, lote_max_bytes: 524288000, captura_dfe: true, franquia_rigida: false },
];

const TABELA = {
  referencias: [
    { tributo: 'CBS', aliquota: 0.9, produto: null, vigencia_inicio: '2026-01-01', vigencia_fim: '2026-12-31', ato_legal: 'LC 214/2025, art. 346', fonte: 'DOU', provisoria: false },
    { tributo: 'CBS', aliquota: 11.2, produto: null, vigencia_inicio: '2027-01-01', vigencia_fim: '2027-12-31', ato_legal: 'LC 214/2025, art. 353', fonte: 'DOU', provisoria: true },
    { tributo: 'IBS_UF', aliquota: 0.1, produto: null, vigencia_inicio: '2026-01-01', vigencia_fim: '2026-12-31', ato_legal: 'LC 214/2025, art. 343', fonte: 'DOU', provisoria: false },
    { tributo: 'IBS_UF', aliquota: 0.05, produto: null, vigencia_inicio: '2027-01-01', vigencia_fim: '2028-12-31', ato_legal: 'LC 214/2025, art. 344', fonte: 'DOU', provisoria: false },
    { tributo: 'IBS_MUN', aliquota: 0.05, produto: null, vigencia_inicio: '2027-01-01', vigencia_fim: '2028-12-31', ato_legal: 'LC 214/2025, art. 344', fonte: 'DOU', provisoria: false },
  ],
  imposto_seletivo: [],
  entes_com_aliquota_propria: { ufs: 1, municipios: 2 },
};

const linha = (tributo: string, nome: string, aliquota: number, provisoria: boolean) => ({
  tributo, nome, aliquota, origem: 'REFERENCIA', forma_aplicacao: null, referencia: aliquota, ato_legal: 'LC 214/2025', fonte: 'DOU',
  provisoria, vigencia_inicio: '2027-01-01', vigencia_fim: '2027-12-31', catalogo_id: 7,
});

describe('alíquotas por município (/aliquotas)', () => {
  it('busca o município pelo nome, consulta a data e mostra a CBS provisória e o total', async () => {
    mockApi((r) => {
      if (r.url.startsWith('/v1/publico/municipios?')) return { json: [{ codigo: 3550308, nome: 'São Paulo', uf: 'SP' }] };
      if (r.url === '/v1/publico/aliquotas/tabela') return { json: TABELA };
      if (r.url.startsWith('/v1/publico/aliquotas?')) {
        return { json: {
          municipio: { codigo: 3550308, nome: 'São Paulo', uf: 'SP' }, data: '2027-06-15',
          tributos: [linha('CBS', 'CBS', 11.2, true), linha('IBS_UF', 'IBS estadual', 0.05, false), linha('IBS_MUN', 'IBS municipal', 0.05, false)],
          total: 11.3, observacao: 'Alíquotas nominais; reduções por cClassTrib são aplicadas pelo motor oficial no cálculo.', parametros_snapshot: 11,
        } };
      }
      return undefined;
    });
    renderAt('/aliquotas');
    expect(screen.getByRole('heading', { level: 1, name: 'Alíquotas de CBS e IBS por município' })).toBeInTheDocument();

    // Linha do tempo: 2028 sem CBS publicada; contagem de entes com alíquota própria.
    const ano2028 = (await screen.findByText('2028')).closest('tr')!;
    expect(within(ano2028).getAllByText('ainda não publicada')).toHaveLength(1);
    expect(screen.getByText(/1 estado/)).toBeInTheDocument();

    await userEvent.type(screen.getByRole('combobox', { name: 'Município' }), 'sao paulo');
    await userEvent.click(await screen.findByRole('option', { name: /São Paulo/ }));
    fireEvent.change(screen.getByLabelText('Data da operação'), { target: { value: '2027-06-15' } });
    await userEvent.click(screen.getByRole('button', { name: 'Consultar' }));

    const resultado = await screen.findByRole('region', { name: 'Resultado da consulta' });
    const cbs = within(resultado).getByText('CBS').closest('tr')!;
    expect(cbs).toHaveTextContent('11,20%');
    expect(within(cbs).getByText('provisória')).toBeInTheDocument();
    expect(cbs).toHaveTextContent('Referência nacional');
    expect(cbs).toHaveTextContent('LC 214/2025');
    expect(within(resultado).getByText('Total (CBS + IBS)').closest('tr')).toHaveTextContent('11,30%');
    expect(within(resultado).getByText(/reduções por cClassTrib/)).toBeInTheDocument();
    expect(calls.some((c) => c.url === '/v1/publico/aliquotas?municipio=3550308&data=2027-06-15')).toBe(true);
  });
});

describe('planos e cadastro', () => {
  it('/planos mostra a ficha de cada plano, destaca o plano pago com DF-e mais barato e leva o escolhido ao cadastro', async () => {
    mockApi((r) => (r.url === '/v1/conta/planos' ? { json: PLANOS } : r.url === '/v1/conta/sessao' ? semSessao : undefined));
    renderAt('/planos');

    const prof = await screen.findByRole('article', { name: 'Plano Profissional' });
    expect(within(prof).getByText('Recomendado')).toBeInTheDocument();
    expect(within(screen.getByRole('article', { name: 'Plano Escritório' })).queryByText('Recomendado')).toBeNull();

    const essencial = screen.getByRole('article', { name: 'Plano Essencial' });
    expect(essencial).toHaveTextContent(/0,08 por auditoria e R\$\s0,004 por simulação, até o teto que você definir/);
    expect(essencial).toHaveTextContent(/Captura de DF-eNão|Captura de DF-enão/);
    expect(essencial).toHaveTextContent('até 2.000 notas (50 MB)');
    expect(essencial).toHaveTextContent('300 requisições/min');
    expect(screen.getByRole('article', { name: 'Plano Sandbox' })).toHaveTextContent('para até o mês seguinte');

    const escolher = within(prof).getByRole('link', { name: 'Escolher Profissional' });
    expect(escolher).toHaveAttribute('href', '/cadastro?plano=PROFISSIONAL');
    await userEvent.click(escolher);

    expect(await screen.findByText(/Você escolheu o plano Profissional/)).toBeInTheDocument();
    const aviso = screen.getByText(/Você escolheu o plano Profissional/).closest('.alert')!;
    expect(aviso).toHaveTextContent('começa no Sandbox');
    expect(aviso).toHaveTextContent('ligar a captura de notas de fornecedores (DF-e)');
    expect(aviso).toHaveTextContent('verificar a empresa');
    expect(sessionStorage.getItem('tg:plano-escolhido')).toBe('PROFISSIONAL');
  });

  it('cadastro com ?plano avisa antes, grava o plano para o portal e não muda o contrato do cadastro', async () => {
    mockApi((r) => {
      if (r.url === '/v1/conta/planos') return { json: PLANOS };
      if (r.url === '/v1/conta/sessao') return semSessao;
      if (r.url === '/v1/conta/cadastro') return { status: 202, json: { mensagem: 'ok' } };
      return undefined;
    });
    renderAt('/cadastro?plano=ESSENCIAL');
    const aviso = (await screen.findByText(/Você escolheu o plano Essencial/)).closest('.alert')!;
    expect(aviso).toHaveTextContent(/certificado\s+e-CNPJ/);
    expect(aviso).toHaveTextContent('e-mail do cadastro do CNPJ na Receita Federal');
    expect(aviso).not.toHaveTextContent('DF-e');   // o Essencial não tem captura de DF-e
    expect(sessionStorage.getItem('tg:plano-escolhido')).toBe('ESSENCIAL');

    await userEvent.type(screen.getByLabelText('Seu nome'), 'Maria Fiscal');
    await userEvent.type(screen.getByLabelText('E-mail de trabalho'), 'maria@x.test');
    await userEvent.type(screen.getByLabelText('Senha'), 'uma frase longa e boa');
    await userEvent.type(screen.getByLabelText('Empresa'), 'Empresa X');
    await userEvent.type(screen.getByLabelText(/CNPJ da empresa/), '11.222.333/0001-81');
    await userEvent.click(screen.getByRole('checkbox'));
    await userEvent.click(screen.getByRole('button', { name: 'Criar conta' }));
    expect(await screen.findByRole('heading', { name: 'Confira seu e-mail' })).toBeInTheDocument();
    expect(screen.getByText(/Para contratar o plano/)).toHaveTextContent('Essencial');
    await waitFor(() => expect(calls.find((c) => c.url === '/v1/conta/cadastro')).toBeDefined());
    expect(Object.keys(calls.find((c) => c.url === '/v1/conta/cadastro')!.body as object).sort()).toEqual(['aceite_termos', 'email', 'empresa', 'nome', 'senha']);
  });

  it('cadastro sem plano ou com código inválido não mostra aviso nem grava nada', async () => {
    mockApi((r) => (r.url === '/v1/conta/sessao' ? semSessao : undefined));
    renderAt('/cadastro?plano=%3Cscript%3E');
    expect(await screen.findByRole('heading', { name: 'Criar conta grátis' })).toBeInTheDocument();
    expect(screen.queryByText(/Você escolheu o plano/)).toBeNull();
    expect(sessionStorage.getItem('tg:plano-escolhido')).toBeNull();
    expect(calls.some((c) => c.url === '/v1/conta/planos')).toBe(false);
  });

  it('o menu do site é o mesmo na landing, em planos, em alíquotas e na ajuda', async () => {
    mockApi((r) => (r.url === '/v1/conta/planos' ? { json: PLANOS } : r.url === '/v1/publico/aliquotas/tabela' ? { json: TABELA } : undefined));
    const esperado = ['Produto', 'Planos', 'Alíquotas', 'Documentação da API', 'Ajuda', 'Entrar', 'Criar conta grátis'];
    for (const path of ['/', '/planos', '/aliquotas', '/ajuda', '/termos']) {
      const { unmount } = renderAt(path);
      const nav = screen.getByRole('navigation', { name: 'Principal' });
      expect(within(nav).getAllByRole('link').map((a) => a.textContent), path).toEqual(esperado);
      unmount();
    }
  });
});
