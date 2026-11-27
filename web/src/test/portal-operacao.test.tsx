import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import App from '../App';
import { currentMonth } from '../lib/format';

// Telas do portal do cliente: operação (visão geral, simular, auditar, lotes), conta e equipe, integração
// (API keys e webhooks), credenciais (OAuth e log de chamadas) e cobrança.

type Req = { method: string; url: string; path: string; query: URLSearchParams; headers: Record<string, string>; body?: unknown; raw?: unknown };
type Resp = { status?: number; json?: unknown; text?: string; contentType?: string };
type Handler = (req: Req) => Resp | undefined;

let calls: Req[] = [];

function mockApi(handler: Handler) {
  calls = [];
  vi.stubGlobal('fetch', vi.fn(async (input: string, init: RequestInit = {}) => {
    const u = new URL(input, 'http://x');
    let body: unknown;
    if (typeof init.body === 'string') {
      try { body = JSON.parse(init.body); } catch { body = init.body; }
    }
    const req: Req = { method: init.method ?? 'GET', url: input, path: u.pathname, query: u.searchParams, headers: (init.headers ?? {}) as Record<string, string>, body, raw: init.body };
    calls.push(req);
    const r = handler(req) ?? { status: 404, json: { code: 'ERR_NOT_FOUND', title: 'Não encontrado' } };
    const status = r.status ?? 200;
    if (r.text !== undefined) return new Response(r.text, { status, headers: { 'content-type': r.contentType ?? 'text/plain' } });
    const text = r.json === undefined ? '' : JSON.stringify(r.json);
    return new Response(text || null, { status, headers: { 'content-type': status >= 400 ? 'application/problem+json' : 'application/json' } });
  }));
}

const problem = (status: number, code: string, title: string, detail?: string): Resp => ({ status, json: { code, title, detail } });

const me = {
  tenant_id: 't1', nome: 'Empresa X', cnpj_raiz: '11222333000181', plano: 'PROFISSIONAL', situacao: 'ACTIVE', credencial: 'sessao', auto_provisionar_cnpj: false,
  scopes: ['keys:manage', 'simulacao:write', 'auditoria:write', 'usage:read'], limites: { rpm: 600, concorrencia: 20 },
};
const empresa = { tenant_id: 't1', nome: 'Empresa X', cnpj_raiz: '11222333000181', papel: 'owner', plano: 'PROFISSIONAL', situacao: 'ACTIVE' };
const ESCOPOS = ['simulacao:write', 'auditoria:write', 'auditoria:read', 'keys:manage', 'webhooks:manage', 'usage:read', 'membros:manage', 'billing:manage', 'cnpjs:manage'];
const sessao = (extra: Record<string, unknown> = {}) => ({
  usuario: { id: 'u1', nome: 'Maria Fiscal', email: 'maria@x.test' }, mfa: 'ok', empresas: [empresa], empresa_ativa: 't1', papel: 'owner',
  escopos: ESCOPOS, plano: 'PROFISSIONAL', max_usuarios: 10, codigos_recuperacao_restantes: 7, ...extra,
});

/** Respostas comuns (sessão e /v1/me) + as da tela. */
const withBase = (h: Handler, s: Record<string, unknown> = {}, m: Record<string, unknown> = {}): Handler => (r) => {
  if (r.path === '/v1/conta/sessao') return { json: sessao(s) };
  if (r.path === '/v1/me') return { json: { ...me, ...m } };
  return h(r);
};

const renderAt = (path: string) => render(<MemoryRouter initialEntries={[path]}><App /></MemoryRouter>);

// jsdom 25 não implementa Blob.text() (os navegadores implementam): a auditoria lê o XML com file.text().
beforeAll(() => {
  if (typeof Blob.prototype.text !== 'function') {
    Object.defineProperty(Blob.prototype, 'text', {
      configurable: true,
      value(this: Blob) {
        return new Promise<string>((resolve, reject) => {
          const fr = new FileReader();
          fr.onload = () => resolve(fr.result as string);
          fr.onerror = () => reject(fr.error);
          fr.readAsText(this);
        });
      },
    });
  }
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const fileInput = (container: HTMLElement) => container.querySelector('input[type="file"]') as HTMLInputElement;

// ------------------------------------------------------------------------------------------------ visão geral

describe('visão geral', () => {
  const usage = (mes: string, docs = 1234) => ({ competencia: mes, plano: 'PROFISSIONAL', documentos_auditados: docs, simulacoes: 10, cnpjs_ativos: 2, franquia_documentos: 1000, excedente_documentos: Math.max(0, docs - 1000), atualizado_em: '2026-11-25T12:00:00Z' });

  it('mostra primeiros passos, consumo com excedente, histórico, aviso de tenant suspenso e troca a competência', async () => {
    mockApi(withBase((r) => {
      if (r.path === '/v1/usage') {
        const mes = r.query.get('mes')!;
        if (mes === '2020-01') return { json: { ...usage(mes, 5), franquia_documentos: 0, excedente_documentos: 0 } };
        if (mes === currentMonth()) return { json: usage(mes) };
        return { json: usage(mes, 0) };
      }
      if (r.path === '/v1/primeiros-passos') {
        return { json: { passos: [
          { id: 'simular', titulo: 'Simule um cálculo', descricao: 'd1', link: '/portal/simular', feito: true },
          { id: 'auditar', titulo: 'Audite uma nota', descricao: 'd2', link: '/portal/auditoria', feito: false },
        ], concluidos: 1, total: 2 } };
      }
      return undefined;
    }, {}, { situacao: 'SUSPENDED' }));
    renderAt('/portal');
    expect(await screen.findByText('Comece por aqui')).toBeInTheDocument();
    expect(screen.getByRole('progressbar', { name: 'Progresso dos primeiros passos' })).toHaveAttribute('aria-valuenow', '50');
    expect(screen.getByText('concluído')).toBeInTheDocument();
    expect(screen.getByText('pendente')).toBeInTheDocument();
    expect(await screen.findByText(/as chamadas à API são recusadas/)).toBeInTheDocument();
    expect(await screen.findByText('234 excedentes')).toBeInTheDocument();
    expect(screen.getByText('600/min')).toBeInTheDocument();
    expect(await screen.findByText('Consumo dos últimos meses')).toBeInTheDocument();
    expect(calls.filter((c) => c.path === '/v1/usage').length).toBeGreaterThanOrEqual(7);

    const competencia = screen.getByLabelText('Competência');
    await userEvent.clear(competencia);
    await userEvent.type(competencia, '2020-01');
    expect(await screen.findByText('Sem franquia definida no plano')).toBeInTheDocument();
  });

  it('sem passos pendentes não mostra o "Comece por aqui"; erro no consumo aparece', async () => {
    mockApi(withBase((r) => {
      if (r.path === '/v1/usage') return r.query.get('mes') === currentMonth() ? problem(500, 'ERR_INTERNAL', 'Falha ao ler o consumo') : problem(500, 'ERR_INTERNAL', 'x');
      if (r.path === '/v1/primeiros-passos') return { json: { passos: [], concluidos: 2, total: 2 } };
      return undefined;
    }));
    renderAt('/portal');
    expect(await screen.findByText('Falha ao ler o consumo')).toBeInTheDocument();
    expect(screen.queryByText('Comece por aqui')).toBeNull();
    expect(screen.queryByText('Consumo dos últimos meses')).toBeNull();
  });
});

// ------------------------------------------------------------------------------------------------ simular

const totais = (cbs: number) => ({ valor_base_total: 1000, cbs_valor: cbs, ibs_uf_valor: 1, ibs_mun_valor: 0, ibs_total_valor: 1, imposto_seletivo_valor: 0 });

describe('simular', () => {
  it('modo JSON: recusa JSON inválido e mostra NÃO OFICIAL com o cenário ajustado pelas exceções', async () => {
    mockApi(withBase((r) => {
      if (r.path === '/v1/calculo/simular') {
        return { json: { simulation_id: 's1', regime_calculo: 'XPTO', param_snapshot_id: 1, oficial: false, avisos: [],
          totais: totais(9), cenario_ajustado: { totais: totais(0), diferenca: -9, excecoes: [{ id: 'e1', tributo: 'CBS', aliquota: 0, motivo: 'DECISAO_JUDICIAL', processo: '123-45', itens: [1] }] } } };
      }
      return undefined;
    }));
    renderAt('/portal/simular');
    await userEvent.click(await screen.findByRole('tab', { name: 'JSON da API' }));
    const corpo = screen.getByLabelText(/Corpo \(JSON\)/) as HTMLTextAreaElement;
    expect(JSON.parse(corpo.value)).toMatchObject({ cnpj_emitente: '11222333000181', municipio_ibge: 3550308, itens: [{ numero: 1, ncm: '84713012' }] });

    await userEvent.clear(corpo);
    await userEvent.type(corpo, 'não é json');
    await userEvent.click(screen.getByRole('button', { name: 'Simular' }));
    expect(await screen.findByText('O corpo não é um JSON válido.')).toBeInTheDocument();
    expect(calls.some((c) => c.path === '/v1/calculo/simular')).toBe(false);

    await userEvent.clear(corpo);
    await userEvent.type(corpo, '{{"cnpj_emitente":"1"}');
    await userEvent.click(screen.getByRole('button', { name: 'Simular' }));
    expect(await screen.findByText('NÃO OFICIAL')).toBeInTheDocument();
    expect(calls.find((c) => c.path === '/v1/calculo/simular')!.body).toEqual({ cnpj_emitente: '1' });
    expect(screen.getByRole('columnheader', { name: 'Com exceções' })).toBeInTheDocument();
    expect(screen.getByText(/decisao judicial 123-45 \(CBS 0%\)/)).toBeInTheDocument();
    expect(screen.getByText(/^XPTO ·/)).toBeInTheDocument();
    // Em JSON não há comparação com 2027.
    expect(screen.queryByRole('button', { name: /Comparar com a mesma operação em 2027/ })).toBeNull();

    await userEvent.click(screen.getByRole('tab', { name: 'Formulário' }));
    expect(screen.getByRole('tab', { name: 'Formulário' })).toHaveAttribute('aria-selected', 'true');
  });

  it('formulário: itens adicionados, editados e removidos; sem município não envia', async () => {
    mockApi(withBase(() => undefined));
    const { container } = renderAt('/portal/simular');
    await screen.findByRole('tab', { name: 'Formulário' });
    await userEvent.click(screen.getByRole('button', { name: /Adicionar item/ }));
    expect(screen.getByText('Item 2')).toBeInTheDocument();
    const item2 = container.querySelectorAll('fieldset.item-card')[1] as HTMLElement;
    const ncm = within(item2).getByLabelText('NCM');
    await userEvent.clear(ncm);
    await userEvent.type(ncm, '2203.00.00');
    const cst = within(item2).getByLabelText('CST');
    await userEvent.clear(cst);
    await userEvent.type(cst, '2a00');
    const cclass = within(item2).getByLabelText('cClassTrib');
    await userEvent.clear(cclass);
    await userEvent.type(cclass, '000002');
    const valor = within(item2).getByLabelText('Base de cálculo (R$)');
    await userEvent.clear(valor);
    await userEvent.type(valor, '10,5');
    const qtd = within(item2).getByLabelText('Quantidade');
    await userEvent.clear(qtd);
    await userEvent.type(qtd, '2');
    const un = within(item2).getByLabelText('Unidade');
    await userEvent.clear(un);
    await userEvent.type(un, 'cx');
    expect(within(item2).getByRole('link', { name: /Consulte pelo NCM/ })).toHaveAttribute('href', '/portal/classificacao?ncm=22030000');

    // Remove o item 1: o item 2 passa a ser o 1.
    const item1 = container.querySelectorAll('fieldset.item-card')[0] as HTMLElement;
    await userEvent.click(within(item1).getByRole('button', { name: /Remover item/ }));
    expect(screen.queryByText('Item 2')).toBeNull();
    expect(screen.queryByRole('button', { name: /Remover item/ })).toBeNull();

    await userEvent.selectOptions(screen.getByLabelText(/^Regime/), 'SIMPLES_NACIONAL');
    const data = screen.getByLabelText(/Data e hora do fato gerador/);
    await userEvent.clear(data);
    await userEvent.type(data, '2026-08-10T10:00');

    // Sem município escolhido na lista.
    const mun = screen.getByLabelText('Município de incidência');
    await userEvent.clear(mun);
    await userEvent.type(mun, 'x');
    await userEvent.click(screen.getByRole('button', { name: 'Simular' }));
    expect(await screen.findByText('Escolha o município na lista.')).toBeInTheDocument();
    expect(calls.some((c) => c.path === '/v1/calculo/simular')).toBe(false);
  });

  it('formulário: envia itens convertidos, mostra erro da API e depois o resultado oficial', async () => {
    let falhar = true;
    mockApi(withBase((r) => {
      if (r.path === '/v1/calculo/simular') {
        if (falhar) return problem(422, 'ERR_VALIDATION', 'Dados inválidos', 'NCM inexistente');
        return { json: { simulation_id: 's2', regime_calculo: 'SN_PLENO', param_snapshot_id: 1, avisos: [{ codigo: 'A1', mensagem: 'Aviso um.' }], totais: totais(9) } };
      }
      return undefined;
    }));
    renderAt('/portal/simular');
    const valor = await screen.findByLabelText('Base de cálculo (R$)');
    await userEvent.clear(valor);
    await userEvent.type(valor, '10,5');
    const data = screen.getByLabelText(/Data e hora do fato gerador/);
    await userEvent.clear(data);
    await userEvent.type(data, '2026-08-10T10:00');
    await userEvent.click(screen.getByRole('button', { name: 'Simular' }));
    expect(await screen.findByText('Dados inválidos. NCM inexistente')).toBeInTheDocument();
    expect(calls.find((c) => c.path === '/v1/calculo/simular')!.body).toMatchObject({
      data_fato_gerador: '2026-08-10T10:00:00-03:00', municipio_ibge: 3550308, uf: 'SP',
      itens: [{ numero: 1, ncm: '84713012', cst_ibs_cbs: '000', c_class_trib: '000001', valor_base_calculo: 10.5, quantidade: 1, unidade: 'UN' }],
    });
    expect(calls.find((c) => c.path === '/v1/calculo/simular')!.headers['Idempotency-Key']).toBeTruthy();

    falhar = false;
    await userEvent.click(screen.getByRole('button', { name: 'Simular' }));
    expect(await screen.findByText('cálculo oficial')).toBeInTheDocument();
    expect(screen.getByText(/Simples Nacional ·/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'ver alíquotas usadas' })).toHaveAttribute('href', '/portal/aliquotas');
    expect(screen.getByText('Aviso um.')).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'Valor' })).toBeInTheDocument();
  });
});

// ------------------------------------------------------------------------------------------------ auditar

const XML = '<NFe><infNFe Id="NFe35261111222333000181550010000000011000000010"><ide><cDV>0</cDV><nNF>1</nNF></ide><emit><CNPJ>11222333000181</CNPJ></emit></infNFe></NFe>';

const auditoria = (extra: Record<string, unknown> = {}) => ({
  audit_id: 'aud_abc123', chave_acesso: '3526' + '1'.repeat(40), direction: 'OUTBOUND', status: 'DIVERGENT', replay: false,
  resumo_financeiro: { tributo_declarado: { cbs: 10, ibs: 1 }, tributo_recalculado: { cbs: 9, ibs: 1 }, diferenca: 1 },
  ocorrencias: [
    { regra: 'R001', natureza: 'DETERMINISTICA', severidade: 'ALTA', numero_item: 1, mensagem: 'CBS a maior', fundamentacao: 'LC 214, art. 1' },
    { regra: 'R002', natureza: 'INDICIO', severidade: 'MEDIA', mensagem: 'Pode ser cesta básica', fundamentacao: 'LC 214, anexo I' },
    { regra: 'R003', natureza: 'INFORMATIVA', severidade: 'BAIXA', mensagem: 'Só para saber', fundamentacao: '—' },
  ],
  avisos: [{ codigo: 'AV1', mensagem: 'Aviso da auditoria.' }],
  aliquotas_aplicadas: [
    { tributo: 'CBS', aliquota: 8.8, origem: 'REFERENCIA', ato_legal: 'Resolução do Senado 1/2026', provisoria: true },
    { tributo: 'IBS_UF', aliquota: 10.1234, origem: 'PROPRIA_DO_ENTE', ente_codigo: '35', ato_legal: 'Lei SP 1', provisoria: false },
    { tributo: 'IS', aliquota: 5, origem: 'PRODUTO', produto: 'Bebidas', item: 2, ato_legal: 'Lei IS', provisoria: false },
    { tributo: 'XYZ', aliquota: 1, origem: 'PRODUTO', ato_legal: 'Ato X', provisoria: false },
  ],
  ...extra,
});

describe('auditar', () => {
  it('envia o XML, mostra replay, ocorrências, alíquotas usadas e reaudita confirmando o enquadramento', async () => {
    mockApi(withBase((r) => {
      if (r.path === '/v1/auditoria/xml') return { json: auditoria({ replay: !r.query.get('confirmar') }) };
      if (r.path === '/v1/auditoria/documentos/abc123') return problem(404, 'ERR_NOT_FOUND', 'Documento não encontrado');
      return undefined;
    }));
    const { container } = renderAt('/portal/auditoria');
    await screen.findByRole('heading', { name: 'Auditar XML' });
    expect(screen.getByRole('button', { name: 'Auditar' })).toBeDisabled();
    await userEvent.upload(fileInput(container), new File([XML], 'nota.xml', { type: 'application/xml' }));
    expect(screen.getByText('nota.xml')).toBeInTheDocument();
    await userEvent.selectOptions(screen.getByLabelText(/^Direção/), 'INBOUND');
    await userEvent.click(screen.getByRole('button', { name: 'Auditar' }));

    expect(await screen.findByText(/Documento já auditado/)).toBeInTheDocument();
    const req = calls.find((c) => c.path === '/v1/auditoria/xml')!;
    expect(req.raw).toBe(XML);
    expect(req.headers['Content-Type']).toBe('application/xml');
    expect(req.query.get('direction')).toBe('INBOUND');
    expect(req.query.get('assinatura')).toBeNull();
    expect(screen.getByText('Recalculado')).toBeInTheDocument();
    expect(screen.getByText('Aviso da auditoria.')).toBeInTheDocument();
    expect(screen.getByText('Divergência')).toBeInTheDocument();
    expect(screen.getByText('Indício')).toBeInTheDocument();
    expect(screen.getByText('Informativo')).toBeInTheDocument();
    // Alíquotas usadas (D-R21).
    expect(screen.getByText('provisória')).toBeInTheDocument();
    expect(screen.getByText('Referência nacional')).toBeInTheDocument();
    expect(screen.getByText('Própria do ente 35')).toBeInTheDocument();
    expect(screen.getByText('Produto Bebidas')).toBeInTheDocument();
    expect(screen.getByText('Imposto Seletivo (item 2)')).toBeInTheDocument();
    expect(screen.getByText('IBS estadual')).toBeInTheDocument();
    expect(screen.getByText('XYZ')).toBeInTheDocument();

    // Só o indício oferece confirmar o enquadramento.
    const botoes = screen.getAllByRole('button', { name: 'Confirmar enquadramento e auditar de novo' });
    expect(botoes).toHaveLength(1);
    await userEvent.click(botoes[0]);
    await waitFor(() => expect(calls.filter((c) => c.path === '/v1/auditoria/xml')).toHaveLength(2));
    const re = calls.filter((c) => c.path === '/v1/auditoria/xml')[1];
    expect(re.query.get('confirmar')).toBe('R002');
    expect(re.query.get('reprocess')).toBe('true');
    await waitFor(() => expect(screen.queryByText(/Documento já auditado/)).toBeNull());

    // Detalhe por item abre o painel do documento.
    await userEvent.click(await screen.findByRole('button', { name: 'Comparação por item' }));
    const drawer = await screen.findByRole('dialog', { name: 'Detalhe do documento' });
    expect(await within(drawer).findByText('Documento não encontrado')).toBeInTheDocument();
    await userEvent.click(within(drawer).getByRole('button', { name: 'Fechar' }));
    expect(screen.queryByRole('dialog', { name: 'Detalhe do documento' })).toBeNull();
  });

  it('XML de exemplo com CNPJ fora do cadastro: cadastra o CNPJ e audita de novo', async () => {
    let cadastrado = false;
    mockApi(withBase((r) => {
      if (r.path === '/exemplos/nfe-exemplo.xml') return { text: XML, contentType: 'application/xml' };
      if (r.path === '/v1/auditoria/xml') {
        if (!cadastrado) return problem(403, 'ERR_CNPJ_NOT_PROVISIONED', 'CNPJ não provisionado', 'CNPJ 99888777000166 não está entre os CNPJs atendidos');
        return { json: auditoria({ ocorrencias: [], aliquotas_aplicadas: [], avisos: [], resumo_financeiro: { tributo_declarado: { cbs: 9, ibs: 1 }, diferenca: 0 } }) };
      }
      if (r.path === '/v1/oem/cnpjs' && r.method === 'POST') { cadastrado = true; return { status: 201, json: { cnpj: '99888777000166' } }; }
      return undefined;
    }));
    renderAt('/portal/auditoria');
    await userEvent.click(await screen.findByRole('button', { name: /Usar XML de exemplo/ }));
    expect(await screen.findByText(/Exemplo de homologação com o CNPJ da sua empresa/)).toBeInTheDocument();
    expect(screen.getByText(/^exemplo-35\d+\.xml$/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Auditar' }));

    expect(await screen.findByText(/não está entre os CNPJs atendidos da sua empresa/)).toBeInTheDocument();
    expect(screen.getByText('99.888.777/0001-66')).toBeInTheDocument();
    const primeira = calls.find((c) => c.path === '/v1/auditoria/xml')!;
    expect(primeira.query.get('assinatura')).toBe('opcional');
    expect(String(primeira.raw)).toContain('<CNPJ>11222333000181</CNPJ>');

    await userEvent.click(screen.getByRole('button', { name: 'Cadastrar este CNPJ e auditar' }));
    expect(await screen.findByText('Nenhuma ocorrência: o declarado confere com o recalculado.')).toBeInTheDocument();
    expect(calls.find((c) => c.path === '/v1/oem/cnpjs')!.body).toEqual({ cnpj: '99888777000166' });
    expect(screen.getByText(/Antes de 2027 o motor oficial usa as alíquotas da base dele/)).toBeInTheDocument();
    expect(screen.queryByText('Recalculado')).toBeNull();
    // Arquivo escolhido à mão sai do modo exemplo.
    await userEvent.click(screen.getByRole('button', { name: 'Remover arquivo' }));
    expect(screen.queryByText(/Exemplo de homologação/)).toBeNull();
    expect(screen.getByRole('button', { name: 'Auditar' })).toBeDisabled();
  });

  it('sem permissão de CNPJs pede ao administrador; cadastro recusado não reaudita; falha no exemplo aparece', async () => {
    let semDetalhe = false;
    mockApi(withBase((r) => {
      if (r.path === '/exemplos/nfe-exemplo.xml') return { status: 404, text: 'nada' };
      if (r.path === '/v1/auditoria/xml') return semDetalhe ? problem(403, 'ERR_CNPJ_NOT_PROVISIONED', 'CNPJ não provisionado') : problem(400, 'ERR_XML', 'XML inválido', 'Falta a tag infNFe');
      return undefined;
    }, { escopos: ESCOPOS.filter((e) => e !== 'cnpjs:manage') }));
    const { container } = renderAt('/portal/auditoria');
    await userEvent.click(await screen.findByRole('button', { name: /Usar XML de exemplo/ }));
    expect(await screen.findByText('Não foi possível carregar o XML de exemplo.')).toBeInTheDocument();

    await userEvent.upload(fileInput(container), new File(['<x/>'], 'ruim.xml', { type: 'application/xml' }));
    await userEvent.click(screen.getByRole('button', { name: 'Auditar' }));
    expect(await screen.findByText('XML inválido. Falta a tag infNFe')).toBeInTheDocument();

    semDetalhe = true;
    await userEvent.click(screen.getByRole('button', { name: 'Auditar' }));
    expect(await screen.findByText(/O CNPJ desta nota não está entre os CNPJs atendidos/)).toBeInTheDocument();
    expect(screen.getByText('Peça a quem administra a conta para cadastrá-lo em CNPJs.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Cadastrar este CNPJ e auditar' })).toBeNull();
  });

  it('cadastrar o CNPJ com erro mostra o erro e não reenvia a nota', async () => {
    mockApi(withBase((r) => {
      if (r.path === '/v1/auditoria/xml') return problem(403, 'ERR_CNPJ_NOT_PROVISIONED', 'CNPJ não provisionado', 'CNPJ 99888777000166 fora');
      if (r.path === '/v1/oem/cnpjs') return problem(402, 'ERR_LIMITE_CNPJS', 'Limite de CNPJs do plano');
      return undefined;
    }));
    const { container } = renderAt('/portal/auditoria');
    await screen.findByRole('heading', { name: 'Auditar XML' });
    await userEvent.upload(fileInput(container), new File([XML], 'nota.xml', { type: 'application/xml' }));
    await userEvent.click(screen.getByRole('button', { name: 'Auditar' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Cadastrar este CNPJ e auditar' }));
    await waitFor(() => expect(calls.some((c) => c.path === '/v1/oem/cnpjs')).toBe(true));
    expect(calls.filter((c) => c.path === '/v1/auditoria/xml')).toHaveLength(1);
  });
});

// ------------------------------------------------------------------------------------------------ lotes

describe('lotes', () => {
  const lote = (id: string, status: string, extra: Record<string, unknown> = {}) => ({
    batch_id: id, status, direction: 'OUTBOUND', total: 10, processados: 4, falhas: 1, conformes: 3, criado_em: '2026-11-20T10:00:00Z', concluido_em: null, ...extra,
  });

  it('envia ZIP, acompanha os lotes, abre documentos e baixa o relatório (com e sem erro)', async () => {
    let lotes = [lote('aaaaaaaa-1111', 'COMPLETED', { direction: 'INBOUND', processados: 10 }), lote('bbbbbbbb-2222', 'RUNNING', { total: null, conformes: null })];
    let falharCsv = true;
    const createObjectURL = vi.fn(() => 'blob:relatorio');
    const revokeObjectURL = vi.fn();
    vi.stubGlobal('URL', Object.assign(URL, { createObjectURL, revokeObjectURL }));
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    mockApi(withBase((r) => {
      if (r.path === '/v1/auditoria/lotes' && r.method === 'GET') return { json: { lotes } };
      if (r.path === '/v1/auditoria/lotes' && r.method === 'POST') {
        lotes = [lote('cccccccc-3333', 'QUEUED', { processados: 0 }), ...lotes];
        return { status: 202, json: { batch_id: 'cccccccc-3333', documentos: 42 } };
      }
      if (r.path === '/v1/auditoria/lotes/aaaaaaaa-1111/relatorio') {
        if (falharCsv) return problem(410, 'ERR_EXPIRADO', 'Relatório expirado', 'Gere de novo');
        return { text: 'chave;status\n1;OK\n', contentType: 'text/csv' };
      }
      return undefined;
    }));
    const { container } = renderAt('/portal/lotes');
    expect(await screen.findByText('aaaaaaaa…')).toBeInTheDocument();
    expect(screen.getByRole('cell', { name: 'Entrada' })).toBeInTheDocument();
    expect(screen.getByText('Atualizando a cada 5 s…')).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'CSV' })).toHaveLength(1);

    // Relatório: erro do servidor e depois download.
    await userEvent.click(screen.getByRole('button', { name: 'CSV' }));
    expect(await screen.findByText('Relatório expirado. Gere de novo')).toBeInTheDocument();
    falharCsv = false;
    await userEvent.click(screen.getByRole('button', { name: 'CSV' }));
    await waitFor(() => expect(click).toHaveBeenCalled());
    expect(createObjectURL).toHaveBeenCalled();
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:relatorio');

    // Envio do ZIP.
    expect(screen.getByRole('button', { name: 'Enviar' })).toBeDisabled();
    const zip = new File([new Uint8Array([80, 75, 3, 4])], 'notas.zip', { type: 'application/zip' });
    await userEvent.upload(fileInput(container), zip);
    await userEvent.selectOptions(screen.getByLabelText(/^Direção/), 'INBOUND');
    await userEvent.click(screen.getByRole('button', { name: 'Enviar' }));
    expect(await screen.findByText('Lote cccccccc-3333 aceito com 42 documentos. Acompanhe abaixo.')).toBeInTheDocument();
    const post = calls.find((c) => c.path === '/v1/auditoria/lotes' && c.method === 'POST')!;
    expect(post.raw).toBe(zip);
    expect(post.headers['Content-Type']).toBe('application/zip');
    expect(post.query.get('direction')).toBe('INBOUND');
    expect(await screen.findByText('cccccccc…')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Enviar' })).toBeDisabled();

    // Atualizar manualmente.
    const antes = calls.filter((c) => c.path === '/v1/auditoria/lotes' && c.method === 'GET').length;
    await userEvent.click(screen.getByRole('button', { name: 'Atualizar' }));
    await waitFor(() => expect(calls.filter((c) => c.path === '/v1/auditoria/lotes' && c.method === 'GET').length).toBeGreaterThan(antes));

    // Documentos do lote.
    await userEvent.click(screen.getAllByRole('button', { name: 'Documentos' })[0]);
    await waitFor(() => expect(screen.queryByRole('heading', { name: 'Lotes' })).toBeNull());
  });

  it('acompanha sozinho a cada 5 s enquanto houver lote em andamento', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      let n = 0;
      mockApi(withBase((r) => {
        if (r.path === '/v1/auditoria/lotes') { n++; return { json: { lotes: [lote('dddddddd-4444', n > 2 ? 'COMPLETED' : 'RUNNING')] } }; }
        return undefined;
      }));
      renderAt('/portal/lotes');
      expect(await screen.findByText('Atualizando a cada 5 s…')).toBeInTheDocument();
      await act(() => vi.advanceTimersByTimeAsync(5100));
      await act(() => vi.advanceTimersByTimeAsync(5100));
      await waitFor(() => expect(screen.queryByText('Atualizando a cada 5 s…')).toBeNull());
      expect(n).toBeGreaterThanOrEqual(3);
    } finally {
      vi.useRealTimers();
    }
  });

  it('lista vazia, erro ao listar e erro ao enviar', async () => {
    let vazio = true;
    mockApi(withBase((r) => {
      if (r.path === '/v1/auditoria/lotes' && r.method === 'GET') return vazio ? { json: { lotes: [] } } : problem(503, 'ERR_INDISPONIVEL', 'Fila indisponível');
      if (r.path === '/v1/auditoria/lotes' && r.method === 'POST') return problem(413, 'ERR_LOTE_GRANDE', 'Lote grande demais', 'Máximo de 100 MB');
      return undefined;
    }));
    const { container } = renderAt('/portal/lotes');
    expect(await screen.findByText('Nenhum lote enviado ainda.')).toBeInTheDocument();
    await userEvent.upload(fileInput(container), new File(['zz'], 'grande.zip', { type: 'application/zip' }));
    await userEvent.click(screen.getByRole('button', { name: 'Enviar' }));
    expect(await screen.findByText('Lote grande demais. Máximo de 100 MB')).toBeInTheDocument();
    expect(screen.getByText('grande.zip')).toBeInTheDocument();
    vazio = false;
    await userEvent.click(screen.getByRole('button', { name: 'Atualizar' }));
    expect(await screen.findByText('Fila indisponível')).toBeInTheDocument();
  });
});

// ------------------------------------------------------------------------------------------------ minha conta e equipe

describe('minha conta', () => {
  const sessoes = [
    { id: 's1', ip: '10.0.0.1', navegador: 'Mozilla/5.0 (Windows NT 10.0) Chrome/120 Edg/120', criada_em: '2026-11-20T10:00:00Z', ultimo_uso: '2026-11-26T10:00:00Z', atual: true },
    { id: 's2', ip: null, navegador: 'Mozilla/5.0 (Macintosh; Mac OS X) Chrome/120 Safari/537', criada_em: '2026-11-20T10:00:00Z', ultimo_uso: '2026-11-26T10:00:00Z', atual: false },
    { id: 's3', ip: '10.0.0.3', navegador: 'Mozilla/5.0 (Android 14) Firefox/120', criada_em: '2026-11-20T10:00:00Z', ultimo_uso: '2026-11-26T10:00:00Z', atual: false },
    { id: 's4', ip: '10.0.0.4', navegador: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17) Safari/604', criada_em: '2026-11-20T10:00:00Z', ultimo_uso: '2026-11-26T10:00:00Z', atual: false },
    { id: 's5', ip: '10.0.0.5', navegador: 'curl/8.0 (Linux)', criada_em: '2026-11-20T10:00:00Z', ultimo_uso: '2026-11-26T10:00:00Z', atual: false },
    { id: 's6', ip: '10.0.0.6', navegador: 'robo', criada_em: '2026-11-20T10:00:00Z', ultimo_uso: '2026-11-26T10:00:00Z', atual: false },
    { id: 's7', ip: '10.0.0.7', navegador: null, criada_em: '2026-11-20T10:00:00Z', ultimo_uso: '2026-11-26T10:00:00Z', atual: false },
  ];

  it('troca a senha, gera novos códigos de recuperação e sai das outras sessões', async () => {
    let senhaErrada = true;
    let sairFalha = true;
    mockApi(withBase((r) => {
      if (r.path === '/v1/conta/sessoes') return { json: sessoes };
      if (r.path === '/v1/conta/senha/alterar') return senhaErrada ? problem(400, 'ERR_SENHA_ATUAL', 'Senha atual incorreta') : { status: 204 };
      if (r.path === '/v1/conta/mfa/novos-codigos') {
        return (r.body as { codigo: string }).codigo === '123456' ? { json: { codigos_recuperacao: ['AAAA-1111', 'BBBB-2222'] } } : problem(400, 'ERR_CODIGO_INVALIDO', 'Código inválido');
      }
      if (r.path === '/v1/conta/sair-outras') return sairFalha ? problem(500, 'ERR_INTERNAL', 'Não deu para encerrar') : { status: 204 };
      return undefined;
    }));
    renderAt('/portal/conta');
    expect(await screen.findByRole('heading', { name: 'Minha conta' })).toBeInTheDocument();
    expect(screen.getByText('Maria Fiscal · maria@x.test')).toBeInTheDocument();
    expect(await screen.findByText('Edge · Windows')).toBeInTheDocument();
    expect(screen.getByText('Chrome · macOS')).toBeInTheDocument();
    expect(screen.getByText('Firefox · Android')).toBeInTheDocument();
    expect(screen.getByText('Safari · iOS')).toBeInTheDocument();
    expect(screen.getByText('Navegador · Linux')).toBeInTheDocument();
    expect(screen.getByText('Navegador')).toBeInTheDocument();
    expect(screen.getByText('Desconhecido')).toBeInTheDocument();
    expect(screen.getByText('esta sessão')).toBeInTheDocument();
    expect(screen.getByText('7')).toBeInTheDocument();

    // Senha: erro e depois sucesso (limpa o formulário e recarrega as sessões).
    await userEvent.type(screen.getByLabelText('Senha atual'), 'velha senha 123');
    await userEvent.type(screen.getByLabelText('Nova senha'), 'nova senha bem longa');
    await userEvent.click(screen.getByRole('button', { name: 'Alterar senha' }));
    expect(await screen.findByText('Senha atual incorreta')).toBeInTheDocument();
    expect(calls.find((c) => c.path === '/v1/conta/senha/alterar')!.body).toEqual({ atual: 'velha senha 123', nova: 'nova senha bem longa' });
    senhaErrada = false;
    const antes = calls.filter((c) => c.path === '/v1/conta/sessoes').length;
    await userEvent.click(screen.getByRole('button', { name: 'Alterar senha' }));
    expect(await screen.findByText('Senha alterada.')).toBeInTheDocument();
    expect(screen.getByLabelText('Senha atual')).toHaveValue('');
    await waitFor(() => expect(calls.filter((c) => c.path === '/v1/conta/sessoes').length).toBeGreaterThan(antes));

    // MFA: só com 6 dígitos; código errado e depois certo.
    const codigo = screen.getByLabelText('Código do app');
    const gerar = screen.getByRole('button', { name: 'Gerar novos códigos' });
    await userEvent.type(codigo, '12ab');
    expect(codigo).toHaveValue('12');
    expect(gerar).toBeDisabled();
    await userEvent.type(codigo, '0000');
    await userEvent.click(gerar);
    expect(await screen.findByText('Código inválido')).toBeInTheDocument();
    await userEvent.clear(codigo);
    await userEvent.type(codigo, '123456');
    await userEvent.click(gerar);
    expect(await screen.findByText('AAAA-1111')).toBeInTheDocument();
    expect(screen.getByText('Códigos de recuperação')).toBeInTheDocument();
    expect(screen.queryByLabelText('Código do app')).toBeNull();

    // Sair das outras sessões: erro aparece; depois funciona.
    await userEvent.click(screen.getByRole('button', { name: 'Sair das outras sessões' }));
    expect(await screen.findByText('Não deu para encerrar')).toBeInTheDocument();
    sairFalha = false;
    await userEvent.click(screen.getByRole('button', { name: 'Sair das outras sessões' }));
    await waitFor(() => expect(screen.queryByText('Não deu para encerrar')).toBeNull());
    expect(calls.filter((c) => c.path === '/v1/conta/sair-outras' && c.method === 'POST')).toHaveLength(2);
  });

  it('sem sessões listadas mostra vazio; erro ao listar e contestação recusada aparecem', async () => {
    let erroSessoes = false;
    mockApi(withBase((r) => {
      if (r.path === '/v1/conta/sessoes') return erroSessoes ? problem(500, 'ERR_INTERNAL', 'Sessões indisponíveis') : { json: [] };
      if (r.path === '/v1/conta/sair-outras') { erroSessoes = true; return { status: 204 }; }
      if (r.path === '/v1/conta/disputa-cnpj') return problem(409, 'ERR_EMPRESA_VERIFICADA', 'Empresa já verificada', 'Não pode ser contestada');
      return undefined;
    }));
    renderAt('/portal/conta');
    expect(await screen.findByText('Nada por aqui ainda.')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Sair das outras sessões' }));
    expect(await screen.findByText('Sessões indisponíveis')).toBeInTheDocument();

    const enviar = screen.getByRole('button', { name: 'Enviar contestação' });
    expect(enviar).toBeDisabled();
    await userEvent.upload(screen.getByLabelText('Certificado e-CNPJ (.pfx)'), new File([new Uint8Array([1, 2, 3])], 'a1.pfx'));
    expect(enviar).toBeDisabled();
    await userEvent.type(screen.getByLabelText('Senha do certificado'), 'x');
    await userEvent.click(enviar);
    expect(await screen.findByText('Empresa já verificada. Não pode ser contestada')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Enviar contestação' })).toBeInTheDocument();
  });
});

describe('equipe', () => {
  const membros = () => ({
    membros: [
      { user_id: 'u1', nome: 'Maria Fiscal', email: 'maria@x.test', papel: 'admin', desde: '2026-01-01T00:00:00Z', mfa: true, ultimo_acesso: '2026-11-26T10:00:00Z' },
      { user_id: 'u0', nome: 'Dona Ana', email: 'ana@x.test', papel: 'owner', desde: '2026-01-01T00:00:00Z', mfa: true, ultimo_acesso: null },
      { user_id: 'u2', nome: 'João Dev', email: 'joao@x.test', papel: 'developer', desde: '2026-01-01T00:00:00Z', mfa: false, ultimo_acesso: null },
    ],
    convites: [{ email: 'novo@x.test', papel: 'finance', expira_em: '2026-12-01T00:00:00Z', criado_em: '2026-11-24T00:00:00Z' }],
  });

  it('convida, troca papel, remove com confirmação e cancela convite (admin não mexe no dono)', async () => {
    let conviteFalha = true;
    mockApi(withBase((r) => {
      if (r.path === '/v1/conta/membros') return { json: membros() };
      if (r.path === '/v1/conta/membros/convites' && r.method === 'POST') return conviteFalha ? problem(409, 'ERR_LIMITE_USUARIOS', 'Limite de usuários do plano') : { status: 201, json: { email: 'x' } };
      if (r.path === '/v1/conta/membros/convites' && r.method === 'DELETE') return { status: 204 };
      if (r.path === '/v1/conta/membros/u2') return { status: 204 };
      return undefined;
    }, { papel: 'admin', max_usuarios: 5 }));
    renderAt('/portal/equipe');
    expect(await screen.findByText('João Dev')).toBeInTheDocument();
    expect(screen.getByText('4 de 5 usuários do plano em uso (contando convites pendentes).')).toBeInTheDocument();
    expect(screen.getByText('você')).toBeInTheDocument();
    // Admin vê o dono só como rótulo; o próprio papel também não é editável.
    expect(screen.queryByLabelText('Papel de Dona Ana')).toBeNull();
    expect(screen.queryByLabelText('Papel de Maria Fiscal')).toBeNull();
    const papelJoao = screen.getByLabelText('Papel de João Dev');
    expect(within(papelJoao).queryByRole('option', { name: 'Dono' })).toBeNull();
    expect(screen.getByText('pendente')).toBeInTheDocument();

    // Convite: erro e depois sucesso.
    await userEvent.type(screen.getByLabelText('E-mail'), 'pessoa@x.test');
    await userEvent.selectOptions(screen.getByLabelText(/^Papel(?! de)/), 'viewer');
    await userEvent.click(screen.getByRole('button', { name: /Enviar convite/ }));
    expect(await screen.findByText('Limite de usuários do plano')).toBeInTheDocument();
    conviteFalha = false;
    await userEvent.click(screen.getByRole('button', { name: /Enviar convite/ }));
    expect(await screen.findByText('Convite enviado para pessoa@x.test. Ele vale por 7 dias.')).toBeInTheDocument();
    expect(screen.getByLabelText('E-mail')).toHaveValue('');
    expect(calls.filter((c) => c.path === '/v1/conta/membros/convites' && c.method === 'POST')[1].body).toEqual({ email: 'pessoa@x.test', papel: 'viewer' });

    // Troca de papel.
    await userEvent.selectOptions(screen.getByLabelText('Papel de João Dev'), 'fiscal');
    await waitFor(() => expect(calls.find((c) => c.path === '/v1/conta/membros/u2' && c.method === 'PATCH')?.body).toEqual({ papel: 'fiscal' }));

    // Remoção com confirmação.
    const linha = screen.getByText('João Dev').closest('tr')!;
    await userEvent.click(within(linha).getByRole('button', { name: 'Remover' }));
    const dlg = await screen.findByRole('alertdialog', { name: 'Remover João Dev?' });
    expect(dlg).toHaveTextContent('joao@x.test');
    await userEvent.click(within(dlg).getByRole('button', { name: 'Remover acesso' }));
    await waitFor(() => expect(calls.some((c) => c.path === '/v1/conta/membros/u2' && c.method === 'DELETE')).toBe(true));

    // Cancelar convite.
    const convite = screen.getByText('novo@x.test').closest('tr')!;
    expect(within(convite).getByText('Financeiro')).toBeInTheDocument();
    await userEvent.click(within(convite).getByRole('button', { name: 'Cancelar' }));
    await waitFor(() => expect(calls.find((c) => c.path === '/v1/conta/membros/convites' && c.method === 'DELETE')?.query.get('email')).toBe('novo@x.test'));
  });

  it('dono pode promover a dono; plano sem limite e sem convites pendentes', async () => {
    mockApi(withBase((r) => (r.path === '/v1/conta/membros' ? { json: { membros: [membros().membros[2]], convites: [] } } : undefined), { papel: 'owner', max_usuarios: null }));
    renderAt('/portal/equipe');
    expect(await screen.findByText('Usuários ilimitados no seu plano.')).toBeInTheDocument();
    const papel = await screen.findByLabelText('Papel de João Dev');
    expect(within(papel).getByRole('option', { name: 'Dono' })).toBeInTheDocument();
    expect(screen.queryByText('Convites pendentes')).toBeNull();
  });

  it('equipe vazia mostra o estado vazio e o erro da listagem', async () => {
    mockApi(withBase((r) => (r.path === '/v1/conta/membros' ? problem(500, 'ERR_INTERNAL', 'Falha ao listar a equipe') : undefined)));
    renderAt('/portal/equipe');
    expect(await screen.findByText('Falha ao listar a equipe')).toBeInTheDocument();
    expect(screen.getByText('Nada por aqui ainda.')).toBeInTheDocument();
  });
});

// ------------------------------------------------------------------------------------------------ integração e credenciais

describe('API keys e clientes OAuth', () => {
  it('cria chave com nome, escopos e validade; erros de criação e de listagem aparecem', async () => {
    let falhaCriar = true;
    mockApi(withBase((r) => {
      if (r.path === '/v1/api-keys' && r.method === 'GET') {
        return { json: { api_keys: [
          { id: 'k1', nome: 'esta', prefixo: 'AAAAAAAA', scopes: ['keys:manage'], expira_em: null, revogada_em: null, ultimo_uso: '2026-11-26T10:00:00Z', criada_em: '2026-10-01T00:00:00Z', atual: true },
          { id: 'k2', nome: 'velha', prefixo: 'BBBBBBBB', scopes: ['simulacao:write'], expira_em: null, revogada_em: '2026-11-01T00:00:00Z', ultimo_uso: null, criada_em: '2026-10-01T00:00:00Z', atual: false },
        ] } };
      }
      if (r.path === '/v1/api-keys' && r.method === 'POST') return falhaCriar ? problem(403, 'ERR_FORBIDDEN_SCOPE', 'x') : { status: 201, json: { id: 'k3', api_key: 'tg_live_sk_SEGREDO', prefixo: 'CCCC', scopes: [] } };
      if (r.path === '/v1/oauth-clients') return problem(500, 'ERR_INTERNAL', 'Clientes OAuth indisponíveis');
      return undefined;
    }));
    renderAt('/portal/api-keys');
    expect(await screen.findByText('revogada')).toBeInTheDocument();
    expect(screen.getByText('esta sessão')).toBeInTheDocument();
    // Nenhuma das duas pode ser revogada aqui (atual e já revogada).
    expect(screen.queryByRole('button', { name: 'Revogar' })).toBeNull();
    expect(await screen.findByText('Clientes OAuth indisponíveis')).toBeInTheDocument();

    const form = screen.getByRole('heading', { name: 'Criar API key' }).closest('section')!;
    await userEvent.type(within(form).getByLabelText('Nome'), 'erp-novo');
    await userEvent.click(within(form).getByRole('checkbox', { name: /auditoria:write/ }));
    await userEvent.click(within(form).getByRole('checkbox', { name: /simulacao:write/ }));
    expect(within(form).getByRole('button', { name: 'Criar' })).toBeDisabled();
    await userEvent.click(within(form).getByRole('checkbox', { name: /simulacao:write/ }));
    const expira = within(form).getByLabelText('Expira em (opcional)');
    await userEvent.type(expira, '2027-01-31');
    await userEvent.click(within(form).getByRole('button', { name: 'Criar' }));
    expect(await screen.findByText('Seu acesso não permite esta ação. Peça a quem administra a conta.')).toBeInTheDocument();
    expect(calls.find((c) => c.path === '/v1/api-keys' && c.method === 'POST')!.body).toEqual({ nome: 'erp-novo', scopes: ['simulacao:write'], expira_em: '2027-02-01T02:59:59.000Z' });

    falhaCriar = false;
    await userEvent.click(within(form).getByRole('button', { name: 'Criar' }));
    expect(await screen.findByRole('dialog', { name: 'Nova API key' })).toHaveTextContent('tg_live_sk_SEGREDO');
    expect(within(form).getByLabelText('Nome')).toHaveValue('');
  });

  it('chaves: erro ao listar e lista vazia', async () => {
    mockApi(withBase((r) => {
      if (r.path === '/v1/api-keys') return problem(500, 'ERR_INTERNAL', 'Chaves indisponíveis');
      if (r.path === '/v1/oauth-clients') return { json: { clientes: [] } };
      return undefined;
    }));
    renderAt('/portal/api-keys');
    expect(await screen.findByText('Chaves indisponíveis')).toBeInTheDocument();
    expect(await screen.findByText('Nenhum cliente OAuth.')).toBeInTheDocument();
  });

  it('cliente OAuth: cria com o segredo uma vez, revoga com confirmação e mostra erro', async () => {
    let clientes = [
      { client_id: 'tgc_1', nome: 'ERP produção', scopes: ['simulacao:write', 'auditoria:write'], criado_em: '2026-11-01T00:00:00Z', revogado_em: null as string | null },
      { client_id: 'tgc_0', nome: 'Antigo', scopes: ['simulacao:write'], criado_em: '2026-10-01T00:00:00Z', revogado_em: '2026-10-10T00:00:00Z' },
    ];
    let falhaCriar = true;
    mockApi(withBase((r) => {
      if (r.path === '/v1/api-keys') return { json: { api_keys: [] } };
      if (r.path === '/v1/oauth-clients' && r.method === 'GET') return { json: { clientes } };
      if (r.path === '/v1/oauth-clients' && r.method === 'POST') {
        if (falhaCriar) return problem(400, 'ERR_VALIDATION', 'Nome repetido');
        clientes = [{ client_id: 'tgc_2', nome: 'BI', scopes: ['usage:read'], criado_em: '2026-11-26T00:00:00Z', revogado_em: null }, ...clientes];
        return { status: 201, json: { client_id: 'tgc_2', client_secret: 'segredo-oauth-xyz' } };
      }
      if (r.path === '/v1/oauth-clients/tgc_1' && r.method === 'DELETE') {
        clientes = clientes.map((c) => (c.client_id === 'tgc_1' ? { ...c, revogado_em: '2026-11-26T00:00:00Z' } : c));
        return { status: 204 };
      }
      return undefined;
    }));
    renderAt('/portal/api-keys');
    const card = (await screen.findByRole('heading', { name: 'Clientes OAuth 2.0' })).closest('section')!;
    expect(await within(card).findByText('tgc_1')).toBeInTheDocument();
    expect(within(card).getByText('revogado')).toBeInTheDocument();

    const criar = within(card).getByRole('button', { name: 'Criar cliente OAuth' });
    expect(criar).toBeDisabled();
    await userEvent.type(within(card).getByLabelText('Nome do sistema'), 'BI');
    await userEvent.click(within(card).getByRole('checkbox', { name: /auditoria:write/ }));
    await userEvent.click(within(card).getByRole('checkbox', { name: /usage:read/ }));
    await userEvent.click(criar);
    expect(await within(card).findByText('Nome repetido')).toBeInTheDocument();
    falhaCriar = false;
    await userEvent.click(criar);
    const segredo = await within(card).findByRole('dialog', { name: 'client_secret de tgc_2' });
    expect(segredo).toHaveTextContent('segredo-oauth-xyz');
    expect(calls.filter((c) => c.path === '/v1/oauth-clients' && c.method === 'POST')[1].body).toEqual({ nome: 'BI', scopes: ['simulacao:write', 'usage:read'] });
    await userEvent.click(within(segredo).getByRole('button', { name: 'Já guardei' }));
    expect(within(card).queryByRole('dialog')).toBeNull();

    const linha = within(card).getByText('tgc_1').closest('tr')!;
    await userEvent.click(within(linha).getByRole('button', { name: 'Revogar' }));
    const dlg = await screen.findByRole('alertdialog', { name: 'Revogar o cliente "ERP produção"?' });
    await userEvent.click(within(dlg).getByRole('button', { name: 'Revogar' }));
    await waitFor(() => expect(within(linha).getByText('revogado')).toBeInTheDocument());
  });
});

describe('webhooks', () => {
  it('cadastra, mostra o segredo, gira o segredo, desativa, filtra entregas e reenvia', async () => {
    let hooks = [
      { id: 'w1', url: 'https://erp.test/hook', events: ['document.audited'], active: true, disabled_reason: null, created_at: '2026-11-01T00:00:00Z' },
      { id: 'w2', url: 'https://old.test/hook', events: ['batch.audit.completed'], active: false, disabled_reason: 'falhas seguidas', created_at: '2026-10-01T00:00:00Z' },
      { id: 'w3', url: 'https://off.test/hook', events: [], active: false, disabled_reason: null, created_at: '2026-10-01T00:00:00Z' },
    ];
    let falhaCriar = true;
    mockApi(withBase((r) => {
      if (r.path === '/v1/webhooks' && r.method === 'GET') return { json: hooks };
      if (r.path === '/v1/webhooks' && r.method === 'POST') {
        if (falhaCriar) return problem(400, 'ERR_URL_PRIVADA', 'URL recusada', 'Endereço interno');
        return { status: 201, json: { id: 'w4', secret: 'whsec_novo' } };
      }
      if (r.path === '/v1/webhooks/w1/rotate-secret') return { json: { secret: 'whsec_girado' } };
      if (r.path === '/v1/webhooks/w1' && r.method === 'DELETE') { hooks = hooks.map((h) => (h.id === 'w1' ? { ...h, active: false, disabled_reason: 'desativado' } : h)); return { status: 204 }; }
      if (r.path === '/v1/webhooks/deliveries') {
        const all = [
          { id: 'd1', webhook_id: 'w1', event: 'document.audited', status: 'DELIVERED', attempts: 1, next_attempt_at: null, last_status_code: 200, created_at: '2026-11-26T10:00:00Z' },
          { id: 'd2', webhook_id: 'w1', event: 'batch.audit.completed', status: 'DEAD', attempts: 8, next_attempt_at: null, last_status_code: null, created_at: '2026-11-26T10:00:00Z' },
        ];
        const st = r.query.get('status');
        return { json: st ? all.filter((d) => d.status === st) : all };
      }
      if (r.path === '/v1/webhooks/deliveries/d2/retry') return { status: 202, json: {} };
      return undefined;
    }));
    renderAt('/portal/webhooks');
    expect(await screen.findByText('https://erp.test/hook')).toBeInTheDocument();
    expect(screen.getByText('falhas seguidas')).toBeInTheDocument();
    expect(screen.getByText('inativo')).toBeInTheDocument();
    expect(await screen.findByText('200')).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Reenviar' })).toHaveLength(1);

    // Cadastro: sem eventos fica desabilitado; erro e depois sucesso com segredo.
    await userEvent.type(screen.getByLabelText(/URL \(HTTPS\)/), 'https://novo.test/h');
    const cadastrar = screen.getByRole('button', { name: 'Cadastrar' });
    await userEvent.click(screen.getByRole('checkbox', { name: 'document.audited' }));
    expect(cadastrar).toBeDisabled();
    await userEvent.click(screen.getByRole('checkbox', { name: 'certificate.expiring' }));
    await userEvent.click(cadastrar);
    expect(await screen.findByText('URL recusada. Endereço interno')).toBeInTheDocument();
    expect(calls.find((c) => c.path === '/v1/webhooks' && c.method === 'POST')!.body).toEqual({ url: 'https://novo.test/h', eventos: ['certificate.expiring'] });
    falhaCriar = false;
    await userEvent.click(cadastrar);
    expect(await screen.findByRole('dialog', { name: 'Segredo de assinatura do webhook' })).toHaveTextContent('whsec_novo');
    expect(screen.getByLabelText(/URL \(HTTPS\)/)).toHaveValue('');

    // Girar segredo.
    await userEvent.click(screen.getByRole('button', { name: 'Girar segredo' }));
    await waitFor(() => expect(screen.getByRole('dialog', { name: 'Segredo de assinatura do webhook' })).toHaveTextContent('whsec_girado'));
    await userEvent.click(screen.getByRole('button', { name: 'Já guardei' }));
    expect(screen.queryByRole('dialog', { name: 'Segredo de assinatura do webhook' })).toBeNull();

    // Reenviar entrega que morreu.
    await userEvent.click(screen.getByRole('button', { name: 'Reenviar' }));
    await waitFor(() => expect(calls.some((c) => c.path === '/v1/webhooks/deliveries/d2/retry' && c.method === 'POST')).toBe(true));

    // Filtro de entregas.
    await userEvent.selectOptions(screen.getByLabelText('Filtrar entregas'), 'DEAD');
    await waitFor(() => expect(calls.some((c) => c.path === '/v1/webhooks/deliveries' && c.query.get('status') === 'DEAD')).toBe(true));
    await waitFor(() => expect(screen.queryByText('200')).toBeNull());

    // Desativar com confirmação.
    await userEvent.click(screen.getByRole('button', { name: 'Desativar' }));
    const dlg = await screen.findByRole('alertdialog', { name: 'Desativar este webhook?' });
    expect(dlg).toHaveTextContent('https://erp.test/hook deixa de receber eventos agora');
    await userEvent.click(within(dlg).getByRole('button', { name: 'Desativar webhook' }));
    expect(await screen.findByText('desativado')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Girar segredo' })).toBeNull();
  });

  it('erros ao listar webhooks e entregas; listas vazias', async () => {
    let falhar = true;
    mockApi(withBase((r) => {
      if (r.path === '/v1/webhooks') return falhar ? problem(500, 'ERR_INTERNAL', 'Webhooks indisponíveis') : { json: [] };
      if (r.path === '/v1/webhooks/deliveries') return falhar ? problem(500, 'ERR_INTERNAL', 'Entregas indisponíveis') : { json: [] };
      return undefined;
    }));
    renderAt('/portal/webhooks');
    expect(await screen.findByText('Webhooks indisponíveis')).toBeInTheDocument();
    expect(await screen.findByText('Entregas indisponíveis')).toBeInTheDocument();
    falhar = false;
    await userEvent.selectOptions(screen.getByLabelText('Filtrar entregas'), 'PENDING');
    await waitFor(() => expect(screen.queryByText('Entregas indisponíveis')).toBeNull());
  });
});

describe('log de chamadas', () => {
  it('lista chamadas com resultado, erro e rastreio; filtra só erros; mostra vazio e erro', async () => {
    mockApi(withBase((r) => {
      if (r.path === '/v1/logs') {
        if (r.query.get('status') === 'erros') return { json: { chamadas: [] } };
        return { json: { chamadas: [
          { em: '2026-11-26T10:00:00Z', metodo: 'POST', rota: '/v1/calculo/simular', status: 200, duracao_ms: 35, credencial: 'chave erp', erro: null, trace_id: '0123456789abcdef0123' },
          { em: '2026-11-26T10:01:00Z', metodo: 'POST', rota: '/v1/auditoria/xml', status: 422, duracao_ms: 12, credencial: 'chave erp', erro: 'ERR_XML', trace_id: null },
          { em: '2026-11-26T10:02:00Z', metodo: 'GET', rota: '/v1/me', status: 503, duracao_ms: 1000, credencial: 'sessão', erro: 'ERR_INDISPONIVEL', trace_id: 'abc' },
        ] } };
      }
      return undefined;
    }));
    renderAt('/portal/logs');
    expect(await screen.findByText('POST /v1/calculo/simular')).toBeInTheDocument();
    expect(screen.getByText('0123456789abcdef')).toBeInTheDocument();
    expect(screen.getByText('ERR_XML')).toBeInTheDocument();
    expect(screen.getByText('503')).toBeInTheDocument();
    expect(screen.getByText('35 ms')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('checkbox', { name: /Só erros/ }));
    expect(await screen.findByText('Nenhuma chamada registrada ainda.')).toBeInTheDocument();
    expect(calls.some((c) => c.path === '/v1/logs' && c.query.get('status') === 'erros')).toBe(true);
  });

  it('erro ao carregar o log aparece', async () => {
    mockApi(withBase((r) => (r.path === '/v1/logs' ? problem(500, 'ERR_INTERNAL', 'Log indisponível') : undefined)));
    renderAt('/portal/logs');
    expect(await screen.findByText('Log indisponível')).toBeInTheDocument();
  });
});

// ------------------------------------------------------------------------------------------------ cobrança

const plano = (codigo: string, nome: string, preco: number, online: boolean) => ({
  codigo, nome, descricao: `Plano ${nome}`, recursos: ['r1'], preco_centavos: preco, documentos_incluidos: 100, simulacoes_incluidas: 100, cnpjs_incluidos: 1,
  usuarios: 3, excedente_documento_centavos: 9, cnpj_extra_centavos: 0, contratacao_online: online,
});
const PLANOS = [plano('SANDBOX', 'Sandbox', 0, true), plano('ESSENCIAL', 'Essencial', 9900, true), plano('PROFISSIONAL', 'Profissional', 49900, true), plano('ENTERPRISE', 'Enterprise', 0, false)];

const cobranca = (extra: Record<string, unknown> = {}) => ({
  plano: 'ESSENCIAL', assinatura: { situacao: 'active', renova_em: '2026-12-10T00:00:00Z' }, pagamento_online: true,
  excedente: { teto_centavos: 5000, usado_centavos: 1234, franquia_rigida: false, preco_documento_milicentavos: 9000, preco_simulacao_milicentavos: 500 },
  verificacao: { verificada: true, verificada_em: '2026-11-01T00:00:00Z', metodo: 'EMAIL_RECEITA' },
  inadimplencia: null,
  faturas: [
    { id: 'f1', numero: 'TG-0001', situacao: 'paid', valor_centavos: 9900, pago_centavos: 9900, link: 'https://pay.stripe.test/f1', pdf: null, criada_em: '2026-11-01T00:00:00Z' },
    { id: 'f2', numero: null, situacao: 'estranha', valor_centavos: 100, pago_centavos: 0, link: null, pdf: null, criada_em: '2026-10-01T00:00:00Z' },
  ],
  ...extra,
});

describe('plano e cobrança', () => {
  it('assinante troca de plano com confirmação, gerencia o pagamento na Stripe e vê faturas', async () => {
    const assign = vi.fn();
    vi.stubGlobal('location', { ...window.location, assign });
    let plano = 'ESSENCIAL';
    mockApi(withBase((r) => {
      if (r.path === '/v1/conta/planos') return { json: PLANOS };
      if (r.path === '/v1/cobranca') return { json: cobranca({ plano }) };
      if (r.path === '/v1/cobranca/checkout') { plano = (r.body as { plano: string }).plano; return { json: { acao: 'trocado' } }; }
      if (r.path === '/v1/cobranca/portal') return { json: { url: 'https://billing.stripe.test/sessao' } };
      return undefined;
    }));
    renderAt('/portal/cobranca?plano=PROFISSIONAL&contratado=1&cancelado=1');
    expect(await screen.findByText(/Pagamento recebido!/)).toBeInTheDocument();
    expect(screen.getByText('Contratação cancelada. Nada foi cobrado.')).toBeInTheDocument();
    expect(await screen.findByText('assinatura ativa')).toBeInTheDocument();
    expect(screen.getByText(/renova em/)).toBeInTheDocument();
    expect(screen.getByText(/Empresa verificada por código no e-mail do cadastro da Receita/)).toBeInTheDocument();
    expect(await screen.findByText(/Você escolheu o plano/)).toHaveTextContent('Profissional');
    expect(screen.getByText(/Usado neste mês/)).toHaveTextContent('R$ 12,34');
    // Faturas.
    expect(screen.getByText('TG-0001')).toBeInTheDocument();
    expect(screen.getByText('paga')).toBeInTheDocument();
    expect(screen.getByText('estranha')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Ver fatura/ })).toHaveAttribute('href', 'https://pay.stripe.test/f1');

    // Cartões de plano.
    expect(screen.getByRole('heading', { name: 'Trocar de plano' })).toBeInTheDocument();
    expect(within(screen.getByRole('article', { name: 'Plano Essencial' })).getByText('plano atual')).toBeInTheDocument();
    expect(within(screen.getByRole('article', { name: 'Plano Sandbox' })).getByText(/cancele a assinatura/)).toBeInTheDocument();
    expect(within(screen.getByRole('article', { name: 'Plano Enterprise' })).getByRole('link', { name: 'Falar com vendas' })).toHaveAttribute('href', 'mailto:vendas@taxgateway.com.br');

    await userEvent.click(screen.getByRole('button', { name: 'Trocar para Profissional' }));
    const dlg = await screen.findByRole('alertdialog', { name: 'Trocar para o plano Profissional?' });
    expect(dlg).toHaveTextContent('R$ 499,00');
    await userEvent.click(within(dlg).getByRole('button', { name: 'Trocar de plano' }));
    await waitFor(() => expect(calls.find((c) => c.path === '/v1/cobranca/checkout')?.body).toEqual({ plano: 'PROFISSIONAL' }));
    await waitFor(() => expect(within(screen.getByRole('article', { name: 'Plano Profissional' })).getByText('plano atual')).toBeInTheDocument());
    expect(screen.queryByText(/Você escolheu o plano/)).toBeNull();
    expect(assign).not.toHaveBeenCalled();

    await userEvent.click(screen.getByRole('button', { name: /Gerenciar pagamento/ }));
    await waitFor(() => expect(assign).toHaveBeenCalledWith('https://billing.stripe.test/sessao'));
  });

  it('sem assinatura: contrata o plano escolhido no site indo para o Checkout da Stripe', async () => {
    const assign = vi.fn();
    vi.stubGlobal('location', { ...window.location, assign });
    sessionStorage.setItem('tg:plano-escolhido', 'NAOEXISTE');
    let falhar = true;
    mockApi(withBase((r) => {
      if (r.path === '/v1/conta/planos') return { json: PLANOS };
      if (r.path === '/v1/cobranca') {
        return { json: cobranca({ plano: 'SANDBOX', assinatura: null, faturas: [], inadimplencia: { desde: '2026-11-10T10:00:00Z', situacao: 'WARNING', restringe_em: '2026-11-20T10:00:00Z', suspende_em: '2026-11-25T10:00:00Z' },
          excedente: { teto_centavos: 0, usado_centavos: 0, franquia_rigida: false, preco_documento_milicentavos: 0, preco_simulacao_milicentavos: 0 } }) };
      }
      if (r.path === '/v1/cobranca/checkout') return falhar ? problem(409, 'ERR_EMPRESA_NAO_VERIFICADA', 'x') : { json: { acao: 'checkout', url: 'https://checkout.stripe.test/c1' } };
      return undefined;
    }));
    renderAt('/portal/cobranca');
    expect(await screen.findByRole('heading', { name: 'Contratar um plano' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Gerenciar pagamento/ })).toBeNull();
    expect(screen.getByText(/A cobrança de .* falhou/)).toBeInTheDocument();
    expect(screen.getByText('Este plano não cobra excedente.')).toBeInTheDocument();
    expect(screen.getByText('Nenhuma fatura ainda.')).toBeInTheDocument();
    expect(screen.getByText(/Você escolheu o plano/)).toHaveTextContent('NAOEXISTE');

    await userEvent.click(screen.getByRole('button', { name: 'Contratar Profissional' }));
    expect(await screen.findByText(/Verifique a empresa em Plano e cobrança/)).toBeInTheDocument();
    falhar = false;
    await userEvent.click(screen.getByRole('button', { name: 'Contratar Profissional' }));
    await waitFor(() => expect(assign).toHaveBeenCalledWith('https://checkout.stripe.test/c1'));
  });

  it('franquia rígida, pagamento offline, conta suspensa e empresa não verificada', async () => {
    mockApi(withBase((r) => {
      if (r.path === '/v1/conta/planos') return { json: PLANOS };
      if (r.path === '/v1/cobranca') {
        return { json: cobranca({ plano: 'SANDBOX', assinatura: { situacao: 'unpaid', renova_em: null }, pagamento_online: false,
          verificacao: { verificada: false, verificada_em: null, metodo: null },
          inadimplencia: { desde: '2026-11-10T10:00:00Z', situacao: 'SUSPENDED', restringe_em: '2026-11-20T10:00:00Z', suspende_em: '2026-11-25T10:00:00Z' },
          excedente: { teto_centavos: 0, usado_centavos: 0, franquia_rigida: true, preco_documento_milicentavos: 0, preco_simulacao_milicentavos: 0 } }) };
      }
      return undefined;
    }));
    renderAt('/portal/cobranca');
    expect(await screen.findByText(/Conta suspensa por falta de pagamento/)).toBeInTheDocument();
    expect(screen.getByText(/Pagamento online ainda não está habilitado/)).toBeInTheDocument();
    expect(screen.getByText(/Este plano tem franquia rígida/)).toBeInTheDocument();
    expect(screen.getByText('assinatura não paga')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Verificar a empresa' })).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Trocar para Profissional' })).toBeDisabled();
  });

  it('pagamento pendente sem régua, método de verificação desconhecido, erro ao salvar teto e sessionStorage indisponível', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('bloqueado'); });
    mockApi(withBase((r) => {
      if (r.path === '/v1/conta/planos') return { json: PLANOS };
      if (r.path === '/v1/cobranca') {
        return { json: cobranca({ assinatura: { situacao: 'past_due', renova_em: null }, verificacao: { verificada: true, verificada_em: '2026-11-01T00:00:00Z', metodo: 'OUTRO' } }) };
      }
      if (r.path === '/v1/cobranca/teto') return problem(400, 'ERR_VALIDATION', 'teto_centavos de 0 a 100.000.000');
      return undefined;
    }));
    renderAt('/portal/cobranca');
    expect(await screen.findByText(/Há um pagamento pendente. Atualize o cartão/)).toBeInTheDocument();
    expect(screen.getByText(/Empresa verificada por OUTRO/)).toBeInTheDocument();
    expect(screen.queryByText(/Você escolheu o plano/)).toBeNull();
    const teto = screen.getByLabelText(/Teto mensal de excedente/);
    await userEvent.clear(teto);
    await userEvent.type(teto, '999999999');
    await userEvent.click(screen.getByRole('button', { name: 'Salvar teto' }));
    expect(await screen.findByText('teto_centavos de 0 a 100.000.000')).toBeInTheDocument();
    expect(screen.queryByText(/Teto atualizado/)).toBeNull();
  });

  it('teto salvo mostra confirmação e recarrega a cobrança; erro ao carregar a cobrança aparece', async () => {
    let n = 0;
    mockApi(withBase((r) => {
      if (r.path === '/v1/conta/planos') return { status: 500, json: { code: 'ERR_INTERNAL', title: 'x' } };
      if (r.path === '/v1/cobranca') { n++; return { json: cobranca() }; }
      if (r.path === '/v1/cobranca/teto') return { json: { teto_centavos: 7550 } };
      return undefined;
    }));
    renderAt('/portal/cobranca');
    const teto = await screen.findByLabelText(/Teto mensal de excedente/);
    expect(teto).toHaveValue('50');
    await userEvent.clear(teto);
    await userEvent.type(teto, '75,50');
    await userEvent.click(screen.getByRole('button', { name: 'Salvar teto' }));
    expect(await screen.findByText(/Teto atualizado/)).toBeInTheDocument();
    expect(calls.find((c) => c.path === '/v1/cobranca/teto')!.body).toEqual({ teto_centavos: 7550 });
    expect(n).toBe(2);
    // Sem planos (falha ao carregar), não há cartões.
    expect(screen.queryByRole('heading', { name: 'Trocar de plano' })).toBeNull();
  });

  it('erro ao carregar a cobrança', async () => {
    mockApi(withBase((r) => (r.path === '/v1/cobranca' ? problem(500, 'ERR_INTERNAL', 'Cobrança indisponível') : undefined)));
    renderAt('/portal/cobranca');
    expect(await screen.findByText('Cobrança indisponível')).toBeInTheDocument();
  });
});
