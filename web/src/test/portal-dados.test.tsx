import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import App from '../App';
import { lerProdutosCsv } from '../portal/Classificacao';
import { currentMonth } from '../lib/format';

// Telas de dados do portal do cliente: cadastros, documentos, classificação, alíquotas, apuração e avisos.

type Req = { method: string; url: string; headers: Record<string, string>; body?: unknown };
type Resp = { status?: number; json?: unknown };
type Handler = (req: Req) => Resp | undefined;

let calls: Req[] = [];

const problem = (status: number, code: string, title: string, detail?: string): Resp => ({ status, json: { code, title, detail } });

const empresa = { tenant_id: 't1', nome: 'Empresa X', cnpj_raiz: '11222333000181', papel: 'owner', plano: 'PROFISSIONAL', situacao: 'ACTIVE' };
const TODOS = ['cnpjs:manage', 'dfe:manage', 'titulares:manage', 'auditoria:read', 'auditoria:write', 'simulacao:write'];
const sessao = (escopos: string[] = TODOS) => ({
  usuario: { id: 'u1', nome: 'Maria Fiscal', email: 'maria@x.test' }, mfa: 'ok', empresas: [empresa], empresa_ativa: 't1', papel: 'owner',
  escopos, plano: 'PROFISSIONAL', max_usuarios: 10,
});
const me = { tenant_id: 't1', nome: 'Empresa X', cnpj_raiz: '11222333000181', plano: 'PROFISSIONAL', situacao: 'ACTIVE', credencial: 'sessao', auto_provisionar_cnpj: false, scopes: TODOS, limites: { rpm: 600, concorrencia: 20 } };

/** Substitui o fetch: sessão e /v1/me de cliente por padrão; o handler da tela responde o resto. */
function mockApi(handler: Handler, escopos: string[] = TODOS) {
  calls = [];
  vi.stubGlobal('fetch', vi.fn(async (input: string, init: RequestInit = {}) => {
    const req: Req = {
      method: init.method ?? 'GET',
      url: input,
      headers: (init.headers ?? {}) as Record<string, string>,
      body: typeof init.body === 'string' ? JSON.parse(init.body) : undefined,
    };
    calls.push(req);
    const r = handler(req)
      ?? (req.url === '/v1/conta/sessao' ? { json: sessao(escopos) } : undefined)
      ?? (req.url === '/v1/me' ? { json: me } : undefined)
      ?? (req.url.startsWith('/v1/avisos') ? { json: { avisos: [], nao_lidos: 0 } } : undefined)
      ?? { status: 404, json: { code: 'ERR_NOT_FOUND', title: 'Não encontrado' } };
    const status = r.status ?? 200;
    const text = r.json === undefined ? '' : JSON.stringify(r.json);
    return new Response(text || null, { status, headers: { 'content-type': status >= 400 ? 'application/problem+json' : 'application/json' } });
  }));
}

const renderAt = (path: string) => render(<MemoryRouter initialEntries={[path]}><App /></MemoryRouter>);
const path = (url: string) => url.split('?')[0];
const qs = (url: string) => new URL(url, 'http://x').searchParams;
const has = (pred: (c: Req) => boolean) => calls.some(pred);

const createObjectURL = vi.fn(() => 'blob:x');
const revokeObjectURL = vi.fn();
beforeEach(() => {
  createObjectURL.mockClear();
  Object.defineProperty(URL, 'createObjectURL', { value: createObjectURL, configurable: true, writable: true });
  Object.defineProperty(URL, 'revokeObjectURL', { value: revokeObjectURL, configurable: true, writable: true });
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
});
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

/** jsdom não implementa Blob.text(): o arquivo de teste traz o próprio. */
function csvFile(conteudo: string, nome: string) {
  const f = new File([conteudo], nome, { type: 'text/csv' });
  Object.defineProperty(f, 'text', { value: async () => conteudo });
  return f;
}

/** Confirma o ConfirmDialog aberto (digitando a palavra, se pedida). */
async function confirmar(titulo: string, botao: string, palavra?: string) {
  const dialog = await screen.findByRole('alertdialog', { name: titulo });
  if (palavra) {
    expect(within(dialog).getByRole('button', { name: botao })).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText(`Digite ${palavra} para confirmar`), palavra.toLowerCase());
  }
  await userEvent.click(within(dialog).getByRole('button', { name: botao }));
  await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull());
}

// ---------------------------------------------------------------------------------------------------------------------
describe('CNPJs atendidos', () => {
  it('lista, cadastra (com e sem erro), desativa com confirmação e mostra o uso por competência', async () => {
    let cnpjs = [
      { cnpj: '11222333000181', ativo: true, cobranca_encerrada_a_partir_de: null, criado_em: '2026-10-01T10:00:00Z' },
      { cnpj: '44555666000199', ativo: false, cobranca_encerrada_a_partir_de: '2026-11', criado_em: '2026-09-01T10:00:00Z' },
    ];
    mockApi((r) => {
      if (r.url === '/v1/oem/cnpjs' && r.method === 'GET') return { json: { cnpjs } };
      if (r.url === '/v1/oem/cnpjs' && r.method === 'POST') {
        const c = (r.body as { cnpj: string }).cnpj;
        if (c === '00000000000000') return problem(400, 'ERR_CNPJ_INVALIDO', 'CNPJ inválido', 'Dígito verificador não confere');
        cnpjs = [...cnpjs, { cnpj: c, ativo: true, cobranca_encerrada_a_partir_de: null, criado_em: '2026-11-20T10:00:00Z' }];
        return { status: 201, json: { cnpj: c } };
      }
      if (r.url.startsWith('/v1/oem/cnpjs/') && r.method === 'DELETE') {
        cnpjs = cnpjs.map((c) => (c.cnpj === r.url.split('/').pop() ? { ...c, ativo: false } : c));
        return { status: 204 };
      }
      if (path(r.url) === '/v1/oem/usage') {
        const month = qs(r.url).get('month');
        return month === '2026-01'
          ? { json: { competencia: month, cnpjs_ativos: 0, por_cnpj: [] } }
          : { json: { competencia: month, cnpjs_ativos: 1, por_cnpj: [{ cnpj: '11222333000181', documentos_auditados: 1500, simulacoes: 7 }] } };
      }
      return undefined;
    });
    renderAt('/portal/cnpjs');
    expect(await screen.findByRole('heading', { name: 'CNPJs atendidos' })).toBeInTheDocument();
    const ativo = (await screen.findByText('ativo')).closest('tr')!;
    expect(ativo).toHaveTextContent('11.222.333/0001-81');
    const inativo = screen.getByText('44.555.666/0001-99').closest('tr')!;
    expect(within(inativo).getByText('desativado')).toBeInTheDocument();
    expect(within(inativo).queryByRole('button', { name: 'Desativar' })).toBeNull();
    expect(await screen.findByText('1.500')).toBeInTheDocument();
    expect(calls.find((c) => path(c.url) === '/v1/oem/usage')!.url).toContain(`month=${currentMonth()}`);

    // Erro da API aparece e o campo não é limpo.
    const campo = screen.getAllByLabelText('CNPJ')[0] as HTMLInputElement;
    await userEvent.type(campo, '00.000.000/0000-00');
    await userEvent.click(screen.getByRole('button', { name: 'Cadastrar' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('CNPJ inválido. Dígito verificador não confere');
    expect(campo.value).toBe('00.000.000/0000-00');

    // Sucesso: envia só os caracteres do CNPJ, limpa o campo e recarrega a lista.
    await userEvent.clear(campo);
    await userEvent.type(campo, '12.345.678/0001-95');
    await userEvent.click(screen.getByRole('button', { name: 'Cadastrar' }));
    expect(await screen.findByText('12.345.678/0001-95')).toBeInTheDocument();
    expect(calls.filter((c) => c.method === 'POST').pop()!.body).toEqual({ cnpj: '12345678000195' });
    expect(campo.value).toBe('');

    // Desativar pede confirmação com o CNPJ; Cancelar não chama nada.
    await userEvent.click(within(ativo).getByRole('button', { name: 'Desativar' }));
    const dlg = await screen.findByRole('alertdialog', { name: 'Desativar o CNPJ 11.222.333/0001-81?' });
    expect(dlg).toHaveTextContent('recusadas na hora (403)');
    await userEvent.click(within(dlg).getByRole('button', { name: 'Cancelar' }));
    expect(has((c) => c.method === 'DELETE')).toBe(false);
    await userEvent.click(within(ativo).getByRole('button', { name: 'Desativar' }));
    await confirmar('Desativar o CNPJ 11.222.333/0001-81?', 'Desativar CNPJ');
    await waitFor(() => expect(has((c) => c.method === 'DELETE' && c.url === '/v1/oem/cnpjs/11222333000181')).toBe(true));
    await waitFor(() => expect(screen.getAllByText('desativado')).toHaveLength(2));

    // Outra competência sem uso: tabela vazia.
    fireEvent.change(screen.getByLabelText('Competência'), { target: { value: '2026-01' } });
    await waitFor(() => expect(has((c) => c.url === '/v1/oem/usage?month=2026-01')).toBe(true));
    expect(await screen.findByText('Nada por aqui ainda.')).toBeInTheDocument();
  });

  it('mostra erro ao carregar a lista e o uso', async () => {
    mockApi((r) => {
      if (r.url === '/v1/oem/cnpjs') return problem(500, 'ERR_INTERNAL', 'Falha interna');
      if (path(r.url) === '/v1/oem/usage') return problem(403, 'ERR_FORBIDDEN_SCOPE', 'Escopo');
      return undefined;
    });
    renderAt('/portal/cnpjs');
    expect(await screen.findByText('Falha interna')).toBeInTheDocument();
    expect(await screen.findByText(/Seu acesso não permite esta ação/)).toBeInTheDocument();
  });

  it('sem o escopo cnpjs:manage a rota explica em vez de abrir', async () => {
    mockApi(() => undefined, ['auditoria:read']);
    renderAt('/portal/cnpjs');
    expect(await screen.findByRole('heading', { name: 'Sem acesso a esta área' })).toBeInTheDocument();
    expect(has((c) => c.url === '/v1/oem/cnpjs')).toBe(false);
  });
});

// ---------------------------------------------------------------------------------------------------------------------
describe('DF-e e certificado A1', () => {
  const C = '11222333000181';
  it('sem certificado nem captura: envia o PFX, liga a captura e remove o certificado com confirmação', async () => {
    let cert: unknown = null;
    let status: unknown = null;
    mockApi((r) => {
      if (r.url === `/v1/cnpjs/${C}/certificado` && r.method === 'GET') return cert ? { json: cert } : problem(404, 'ERR_NOT_FOUND', 'Sem certificado');
      if (r.url === `/v1/cnpjs/${C}/certificado` && r.method === 'PUT') {
        if ((r.body as { senha: string }).senha === 'errada') return problem(400, 'ERR_PFX_SENHA', 'Senha do certificado incorreta');
        cert = { cnpj: C, thumbprint: 'ABCDEF0123', titular: 'EMPRESA X LTDA', vence_em: '2027-06-01T00:00:00Z' };
        return { json: cert };
      }
      if (r.url === `/v1/cnpjs/${C}/certificado` && r.method === 'DELETE') { cert = null; return { status: 204 }; }
      if (r.url === `/v1/cnpjs/${C}/dfe/status`) return status ? { json: status } : problem(404, 'ERR_NOT_FOUND', 'Sem captura');
      if (r.url === `/v1/cnpjs/${C}/dfe/sync`) {
        status = { cnpj: C, ativo: true, ult_nsu: 10, max_nsu: null, proxima_consulta: '2026-11-27T12:00:00Z', ultimo_cstat: '656', ultima_consulta: '2026-11-27T11:00:00Z', ultimo_656: '2026-11-27T11:00:00Z', manifestar_ciencia: false, documentos: { AUDITADO: 3, CIENCIA_FALHOU: 1 } };
        return { status: 202, json: { ok: true } };
      }
      return undefined;
    });
    renderAt('/portal/dfe');
    await userEvent.type(await screen.findByLabelText('CNPJ'), '11.222.333/0001-81');
    await userEvent.click(screen.getByRole('button', { name: 'Abrir' }));
    expect(await screen.findByText('Nenhum certificado enviado.')).toBeInTheDocument();
    expect(await screen.findByText('Captura ainda não configurada.')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Certificado de 11.222.333/0001-81' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Ligar captura' })).toBeDisabled();
    expect(screen.getByText('Envie o certificado antes de ligar a captura.')).toBeInTheDocument();
    const enviar = screen.getByRole('button', { name: 'Enviar' });
    expect(enviar).toBeDisabled();

    // Senha errada: erro e a senha é apagada do campo.
    await userEvent.upload(screen.getByLabelText('Arquivo .pfx'), new File([new Uint8Array([1, 2, 3])], 'a1.pfx'));
    await userEvent.type(screen.getByLabelText('Senha do certificado'), 'errada');
    await userEvent.click(enviar);
    expect(await screen.findByText('Senha do certificado incorreta')).toBeInTheDocument();
    expect((screen.getByLabelText('Senha do certificado') as HTMLInputElement).value).toBe('');

    await userEvent.type(screen.getByLabelText('Senha do certificado'), 'senha-a1');
    await userEvent.click(screen.getByRole('button', { name: 'Enviar' }));
    expect(await screen.findByText('EMPRESA X LTDA')).toBeInTheDocument();
    expect(calls.filter((c) => c.method === 'PUT').pop()!.body).toEqual({ pfx_base64: 'AQID', senha: 'senha-a1' });
    expect(screen.getByText('ABCDEF0123')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Substituir' })).toBeDisabled();

    // Liga a captura no RJ, sem ciência automática.
    await userEvent.selectOptions(screen.getByLabelText('UF da empresa'), 'RJ');
    await userEvent.click(screen.getByRole('checkbox', { name: /Manifestar Ciência/ }));
    await userEvent.click(screen.getByRole('button', { name: 'Ligar captura' }));
    await waitFor(() => expect(calls.find((c) => c.url.endsWith('/dfe/sync'))?.body).toEqual({ c_uf_autor: 33, manifestar_ciencia: false }));
    expect(await screen.findByText('ligada')).toBeInTheDocument();
    expect(screen.getByText('SEFAZ bloqueou por 1 h (consumo indevido)')).toBeInTheDocument();
    expect(screen.getByText('10 de —')).toBeInTheDocument();
    expect(screen.getByText('Auditado: 3')).toBeInTheDocument();
    expect(screen.getByText('Ciência falhou: 1')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Salvar configuração' })).toBeEnabled();

    // Remover exige digitar REMOVER.
    await userEvent.click(screen.getByRole('button', { name: 'Remover certificado' }));
    await confirmar('Remover o certificado A1?', 'Remover certificado', 'REMOVER');
    await waitFor(() => expect(has((c) => c.method === 'DELETE' && c.url === `/v1/cnpjs/${C}/certificado`)).toBe(true));
    expect(await screen.findByText('Nenhum certificado enviado.')).toBeInTheDocument();
  });

  it('captura existente com retorno desconhecido; erro diferente de 404 aparece; falha ao ligar a captura', async () => {
    mockApi((r) => {
      if (r.url === `/v1/cnpjs/${C}/certificado`) return problem(500, 'ERR_INTERNAL', 'Cofre indisponível');
      if (r.url === `/v1/cnpjs/${C}/dfe/status`) {
        return { json: { cnpj: C, ativo: false, ult_nsu: 5, max_nsu: 9, proxima_consulta: '2026-11-27T12:00:00Z', ultimo_cstat: '999', ultima_consulta: null, ultimo_656: null, manifestar_ciencia: true, documentos: {} } };
      }
      return undefined;
    });
    renderAt('/portal/dfe');
    await userEvent.type(await screen.findByLabelText('CNPJ'), C);
    await userEvent.click(screen.getByRole('button', { name: 'Abrir' }));
    expect(await screen.findByText('Cofre indisponível')).toBeInTheDocument();
    expect(await screen.findByText('retorno 999 da SEFAZ')).toBeInTheDocument();
    expect(screen.getByText('desligada')).toBeInTheDocument();
    expect(screen.getByText('5 de 9')).toBeInTheDocument();
  });

  it('mostra a saúde da captura e testa a conexão (ok, falha e erro)', async () => {
    let teste = 0;
    mockApi((r) => {
      if (r.url === `/v1/cnpjs/${C}/certificado`) return { json: { cnpj: C, thumbprint: 'T', titular: 'TIT', vence_em: '2027-01-01T00:00:00Z' } };
      if (r.url === `/v1/cnpjs/${C}/dfe/status`) {
        return { json: { cnpj: C, ativo: true, ult_nsu: 5, max_nsu: 5, proxima_consulta: '2026-11-27T12:00:00Z', ultimo_cstat: '137', ultima_consulta: '2026-11-27T11:00:00Z', ultimo_656: null, manifestar_ciencia: true, documentos: {},
          saude: { situacao: 'PARADO', ok: false, mensagem: 'Nenhuma consulta à SEFAZ desde 26/11/2026 08:00.', certificado_vence_em_dias: 35 } } };
      }
      if (r.url === `/v1/cnpjs/${C}/certificado/testar`) {
        teste++;
        if (teste === 1) return { json: { resultado: 'OK', testado_em: 'x', verificacoes: [{ item: 'Conexão com a SEFAZ', situacao: 'OK', detalhe: 'A SEFAZ aceitou o certificado.' }] } };
        if (teste === 2) return { json: { resultado: 'FALHA', testado_em: 'x', verificacoes: [{ item: 'Prazo de validade', situacao: 'ATENCAO', detalhe: 'Vence em 10 dias.' }, { item: 'Conexão com a SEFAZ', situacao: 'FALHA', detalhe: 'A SEFAZ recusou o certificado.' }] } };
        return problem(503, 'ERR_INDISPONIVEL', 'Fora do ar');
      }
      return undefined;
    });
    renderAt('/portal/dfe');
    await userEvent.type(await screen.findByLabelText('CNPJ'), C);
    await userEvent.click(screen.getByRole('button', { name: 'Abrir' }));
    expect(await screen.findByText('Captura parada.')).toBeInTheDocument();
    expect(screen.getByText(/Nenhuma consulta à SEFAZ desde/)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Testar conexão' }));
    const ok = await screen.findByLabelText('Resultado do teste de conexão');
    expect(ok).toHaveTextContent('tudo certo');
    expect(ok).toHaveTextContent('A SEFAZ aceitou o certificado.');

    await userEvent.click(screen.getByRole('button', { name: 'Testar conexão' }));
    await waitFor(() => expect(screen.getByLabelText('Resultado do teste de conexão')).toHaveTextContent('não está funcionando'));
    expect(screen.getByLabelText('Resultado do teste de conexão')).toHaveTextContent('atenção');
    expect(screen.getByLabelText('Resultado do teste de conexão')).toHaveTextContent('falha');

    await userEvent.click(screen.getByRole('button', { name: 'Testar conexão' }));
    expect(await screen.findByText('Fora do ar')).toBeInTheDocument();
  });

  it('erro ao ligar a captura aparece e não recarrega o status', async () => {
    mockApi((r) => {
      if (r.url === `/v1/cnpjs/${C}/certificado`) return { json: { cnpj: C, thumbprint: 'T', titular: 'TIT', vence_em: '2027-01-01T00:00:00Z' } };
      if (r.url === `/v1/cnpjs/${C}/dfe/status`) return problem(404, 'ERR_NOT_FOUND', 'x');
      if (r.url === `/v1/cnpjs/${C}/dfe/sync`) return problem(409, 'ERR_DFE_BLOQUEADO', 'SEFAZ bloqueou', 'Tente em 1 h');
      return undefined;
    });
    renderAt('/portal/dfe');
    await userEvent.type(await screen.findByLabelText('CNPJ'), C);
    await userEvent.click(screen.getByRole('button', { name: 'Abrir' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Ligar captura' }));
    expect(await screen.findByText('SEFAZ bloqueou. Tente em 1 h')).toBeInTheDocument();
    expect(calls.filter((c) => c.url.endsWith('/dfe/status'))).toHaveLength(1);
  });
});

// ---------------------------------------------------------------------------------------------------------------------
describe('LGPD: titulares', () => {
  it('localiza pelo CPF (só dígitos) e expurga com confirmação digitada', async () => {
    mockApi((r) => {
      if (r.url === '/v1/titulares/localizar') {
        return { json: { documentos: [
          { id: 'd1', direcao: 'saida', chave_acesso: '35261111222333000181550010000000011000000011', xml_armazenado: true, criado_em: '2026-11-01T10:00:00Z' },
          { id: 'd2', direcao: 'entrada', chave_acesso: null, xml_armazenado: false, criado_em: '2026-11-02T10:00:00Z' },
        ] } };
      }
      if (r.url === '/v1/titulares/expurgar') return { json: { documentos: [], documentos_expurgados: 2, xmls_apagados: 1 } };
      return undefined;
    });
    renderAt('/portal/titulares');
    await userEvent.type(await screen.findByLabelText('CPF'), '123.456.789-09');
    await userEvent.click(screen.getByRole('button', { name: 'Localizar' }));
    expect(await screen.findByRole('heading', { name: '2 documento(s)' })).toBeInTheDocument();
    expect(calls.find((c) => c.url === '/v1/titulares/localizar')!.body).toEqual({ cpf: '12345678909' });
    expect(screen.getByText('sim')).toBeInTheDocument();
    expect(screen.getByText('não')).toBeInTheDocument();
    expect(screen.getByText('—')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Expurgar dados deste titular' }));
    const dlg = await screen.findByRole('alertdialog', { name: 'Expurgar os dados deste titular?' });
    expect(dlg).toHaveTextContent('Apaga definitivamente 1 XML(s) guardado(s) e o vínculo com o CPF em 2 documento(s)');
    await confirmar('Expurgar os dados deste titular?', 'Expurgar definitivamente', 'EXPURGAR');
    expect(await screen.findByText('2 documento(s) expurgado(s); 1 XML(s) apagado(s).')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: '2 documento(s)' })).toBeNull();
    expect(calls.find((c) => c.url === '/v1/titulares/expurgar')!.body).toEqual({ cpf: '12345678909' });
  });

  it('nenhum documento: sem botão de expurgo; erros de localizar e de expurgar aparecem', async () => {
    let n = 0;
    mockApi((r) => {
      if (r.url === '/v1/titulares/localizar') {
        n += 1;
        if (n === 1) return { json: { documentos: [] } };
        if (n === 2) return problem(400, 'ERR_CPF_INVALIDO', 'CPF inválido');
        return { json: { documentos: [{ id: 'd1', direcao: 'saida', chave_acesso: 'K', xml_armazenado: true, criado_em: '2026-11-01T10:00:00Z' }] } };
      }
      if (r.url === '/v1/titulares/expurgar') return problem(500, 'ERR_INTERNAL', 'Falha no expurgo');
      return undefined;
    });
    renderAt('/portal/titulares');
    await userEvent.type(await screen.findByLabelText('CPF'), '12345678909');
    await userEvent.click(screen.getByRole('button', { name: 'Localizar' }));
    expect(await screen.findByRole('heading', { name: '0 documento(s)' })).toBeInTheDocument();
    expect(screen.getByText('Nada por aqui ainda.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Expurgar dados deste titular' })).toBeNull();

    await userEvent.click(screen.getByRole('button', { name: 'Localizar' }));
    expect(await screen.findByText('CPF inválido')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Localizar' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Expurgar dados deste titular' }));
    await confirmar('Expurgar os dados deste titular?', 'Expurgar definitivamente', 'EXPURGAR');
    expect(await screen.findByText('Falha no expurgo')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '1 documento(s)' })).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------------------------------------------------
const CHAVE = '35261111222333000181550010000000011000000019';
const doc = (id: string, extra: Record<string, unknown> = {}) => ({
  id, chave_acesso: CHAVE.slice(0, 40) + id.padStart(4, '0'), direcao: 'saida', modelo: 'NFE55', status: 'DIVERGENTE', cnpj: '11222333000181', cnpj_contraparte: null,
  diferenca: 12.5, credito_potencial: 0, ocorrencias: 2, lote_id: null, competencia: '2026-11', criado_em: '2026-11-20T10:00:00Z', reprocessamento: false, xml_armazenado: true, ...extra,
});
const detalhe = {
  documento: doc('d1', { chave_acesso: CHAVE }),
  data_emissao: '2026-11-19T10:00:00Z',
  itens_declarados: [
    { numero: 1, ncm: '22021000', cst: '000', c_class_trib: '000001', vbc: 100, pcbs: 0.9, vcbs: 0.9, pibsuf: 0.1, vibsuf: 0.1, pibsmun: 0, vibsmun: 0, vis: 2 },
    { numero: 2, ncm: '', cst: '200', c_class_trib: '200001', vbc: 50 },
  ],
  auditoria: {
    status: 'DIVERGENTE', regras_versao: 'r-2026.11', param_snapshot_id: 42, rfb_engine_version: { bundle: 'rfb-1.2.3' },
    resumo_financeiro: { tributo_declarado: { cbs: 0.9, ibs: 0.1, is: 2 }, tributo_recalculado: { cbs: 1, ibs: 0.2, is: 2 }, diferenca: 0.2 },
    ocorrencias: [
      { regra: 'R-DOC-01', natureza: 'INFORMATIVA', severidade: 'BAIXA', mensagem: 'Documento sem destinatário', fundamentacao: 'LC 214, art. 1' },
      { regra: 'R-ITEM-07', natureza: 'DETERMINISTICA', severidade: 'ALTA', numero_item: 1, campo_xml: 'vCBS', valor_declarado: '0,90', valor_referencia: '1,00', mensagem: 'CBS a menor', fundamentacao: 'LC 214, art. 12', impacto_financeiro_estimado: 0.1 },
      { regra: 'R-ITEM-08', natureza: 'INDICIO', severidade: 'MEDIA', numero_item: 1, mensagem: 'cClassTrib incomum', fundamentacao: 'Tabela cClassTrib' },
    ],
    avisos: [{ codigo: 'ALIQUOTA_PROVISORIA', mensagem: 'Alíquota de CBS provisória.' }],
    aliquotas_aplicadas: [{ tributo: 'CBS', aliquota: 0.9, origem: 'NACIONAL', ato_legal: 'LC 214/2025', provisoria: true }],
    aviso_legal: 'Resultado informativo, não substitui parecer.',
  },
};

describe('documentos auditados', () => {
  function api(extra?: Handler) {
    mockApi((r) => {
      const e = extra?.(r);
      if (e) return e;
      const p = path(r.url);
      if (p === '/v1/auditoria/documentos') {
        if (qs(r.url).get('cursor') === 'c2') return { json: { documentos: [doc('d3', { direcao: 'entrada', lote_id: 'L1', reprocessamento: true, chave_acesso: null, status: 'CONFORME' })], proximo_cursor: null } };
        return { json: { documentos: [doc('d1', { chave_acesso: CHAVE }), doc('d2')], proximo_cursor: 'c2' } };
      }
      if (p === '/v1/auditoria/resumo') return { json: { total: 3, por_status: { DIVERGENTE: 2, INDICIO: 1 }, diferenca_total: 25, credito_potencial_total: 7 } };
      if (p === '/v1/auditoria/documentos/d1') return { json: detalhe };
      if (p === '/v1/auditoria/documentos/d2') return { json: { documento: doc('d2', { direcao: 'entrada', modelo: 'NFCE65' }), itens_declarados: null, auditoria: null } };
      if (p === '/v1/auditoria/documentos/exportar') return { json: 'csv' };
      return undefined;
    });
  }

  it('lista com resumo, carrega mais pelo cursor, filtra e exporta os dois CSVs', async () => {
    api();
    renderAt('/portal/documentos');
    expect(await screen.findByRole('heading', { name: 'Documentos auditados' })).toBeInTheDocument();
    expect(await screen.findByText('Documentos no período')).toBeInTheDocument();
    const cards = screen.getByText('Com divergência ou indício').parentElement!;
    expect(cards).toHaveTextContent('3');
    expect(await screen.findByText(`${CHAVE.slice(0, 6)}…${CHAVE.slice(-6)}`)).toBeInTheDocument();
    const first = calls.find((c) => path(c.url) === '/v1/auditoria/documentos')!;
    expect(qs(first.url).get('de')).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(qs(first.url).has('cnpj')).toBe(false);

    await userEvent.click(screen.getByRole('button', { name: 'Carregar mais' }));
    expect(await screen.findByText('reprocessado')).toBeInTheDocument();
    expect(screen.getByText('lote')).toBeInTheDocument();
    expect(screen.getByText('Entrada')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Carregar mais' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Ver detalhe do documento d3' })).toBeInTheDocument();

    // Filtros.
    const form = screen.getByRole('form', { name: 'Filtros' });
    fireEvent.change(within(form).getByLabelText('De'), { target: { value: '2026-11-01' } });
    fireEvent.change(within(form).getByLabelText('Até'), { target: { value: '2026-11-30' } });
    await userEvent.selectOptions(within(form).getByLabelText('Situação'), 'DIVERGENTE,INDICIO');
    await userEvent.selectOptions(within(form).getByLabelText('Direção'), 'entrada');
    await userEvent.type(within(form).getByLabelText('CNPJ (emitente ou contraparte)'), '11222333000181');
    await userEvent.type(within(form).getByLabelText('Chave de acesso'), '3526 1111');
    await userEvent.click(within(form).getByRole('button', { name: 'Filtrar' }));
    await waitFor(() => expect(calls.some((c) => path(c.url) === '/v1/auditoria/documentos' && qs(c.url).get('chave') === '35261111')).toBe(true));
    const filtrada = calls.filter((c) => path(c.url) === '/v1/auditoria/documentos').pop()!;
    expect(Object.fromEntries(qs(filtrada.url))).toMatchObject({ de: '2026-11-01', ate: '2026-11-30', status: 'DIVERGENTE,INDICIO', direcao: 'entrada', cnpj: '11222333000181', chave: '35261111' });
    await waitFor(() => expect(calls.filter((c) => path(c.url) === '/v1/auditoria/resumo').pop()!.url).toContain('direcao=entrada'));

    // Exportação: fetch direto com o filtro e o nível; o arquivo é baixado.
    await userEvent.click(screen.getByRole('button', { name: 'CSV de documentos' }));
    await waitFor(() => expect(createObjectURL).toHaveBeenCalledTimes(1));
    const exp1 = calls.filter((c) => path(c.url) === '/v1/auditoria/documentos/exportar').pop()!;
    expect(Object.fromEntries(qs(exp1.url))).toMatchObject({ nivel: 'documento', de: '2026-11-01', status: 'DIVERGENTE,INDICIO' });
    expect(exp1.headers['X-TaxGateway-Csrf']).toBe('1');
    await userEvent.click(screen.getByRole('button', { name: 'CSV de ocorrências' }));
    await waitFor(() => expect(createObjectURL).toHaveBeenCalledTimes(2));
    expect(qs(calls.filter((c) => path(c.url) === '/v1/auditoria/documentos/exportar').pop()!.url).get('nivel')).toBe('ocorrencia');
  });

  it('detalhe no painel: comparação por item, achados com fundamento, avisos, alíquotas e link do dossiê', async () => {
    api();
    renderAt('/portal/documentos');
    await userEvent.click(await screen.findByRole('button', { name: `Ver detalhe do documento ${CHAVE}` }));
    const drawer = await screen.findByRole('dialog', { name: 'Detalhe do documento' });
    expect(await within(drawer).findByRole('link', { name: /Dossiê para fiscalização/ })).toHaveAttribute('href', '/portal/documentos/d1/dossie');
    expect(within(drawer).getByText('Recalculado (motor oficial)')).toBeInTheDocument();
    expect(within(drawer).getByRole('heading', { name: 'Achados do documento' })).toBeInTheDocument();
    expect(within(drawer).getByText('Documento sem destinatário')).toBeInTheDocument();
    const item1 = within(drawer).getByRole('region', { name: 'Item 1' });
    expect(item1).toHaveTextContent('2 achado(s)');
    expect(item1).toHaveTextContent('Divergência');
    expect(item1).toHaveTextContent('Indício');
    expect(item1).toHaveTextContent('Impacto estimado');
    expect(item1).toHaveTextContent('0,9%');
    const item2 = within(drawer).getByRole('region', { name: 'Item 2' });
    expect(item2).toHaveTextContent('sem achados');
    expect(item2).not.toHaveTextContent('IS');
    expect(within(drawer).getByText('Alíquota de CBS provisória.')).toBeInTheDocument();
    expect(within(drawer).getByText('LC 214/2025')).toBeInTheDocument();
    expect(within(drawer).getByText(/Regras r-2026.11 · bundle rfb-1.2.3 · parâmetros nº 42/)).toBeInTheDocument();
    expect(within(drawer).getByText(/modelo 55 \(NF-e\)/)).toBeInTheDocument();
    await userEvent.click(within(drawer).getByRole('button', { name: 'Fechar' }));
    expect(screen.queryByRole('dialog', { name: 'Detalhe do documento' })).toBeNull();

    // Documento recusado antes do recálculo: sem auditoria e sem itens.
    await userEvent.click(screen.getByRole('button', { name: /Ver detalhe do documento .*0d2$/ }));
    const d2 = await screen.findByRole('dialog', { name: 'Detalhe do documento' });
    expect(await within(d2).findByText('Sem itens declarados (documento recusado antes do recálculo).')).toBeInTheDocument();
    expect(within(d2).getByText(/Entrada · modelo 65 \(NFC-e\) · emitida —/)).toBeInTheDocument();
    expect(within(d2).queryByText('Alíquotas usadas')).toBeNull();
    await userEvent.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Detalhe do documento' })).toBeNull());
  });

  it('erros: lista, carregar mais, exportação e detalhe', async () => {
    api((r) => {
      const p = path(r.url);
      if (p === '/v1/auditoria/documentos' && qs(r.url).get('cursor')) return problem(500, 'ERR_INTERNAL', 'Falha ao paginar');
      if (p === '/v1/auditoria/documentos/exportar') return problem(429, 'ERR_RATE', 'Muitas');
      if (p === '/v1/auditoria/documentos/d1') return problem(404, 'ERR_NOT_FOUND', 'Documento não encontrado');
      return undefined;
    });
    renderAt('/portal/documentos');
    await userEvent.click(await screen.findByRole('button', { name: 'Carregar mais' }));
    expect(await screen.findByText('Falha ao paginar')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Carregar mais' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'CSV de documentos' }));
    expect(await screen.findByText(/Muitas requisições/)).toBeInTheDocument();
    expect(createObjectURL).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole('button', { name: `Ver detalhe do documento ${CHAVE}` }));
    const drawer = await screen.findByRole('dialog', { name: 'Detalhe do documento' });
    expect(await within(drawer).findByText('Documento não encontrado')).toBeInTheDocument();
    expect(within(drawer).queryByRole('link')).toBeNull();
  });

  it('lista vazia e erro da lista; filtro de lote vindo da URL pode ser removido', async () => {
    mockApi((r) => {
      const p = path(r.url);
      if (p === '/v1/auditoria/documentos') {
        return qs(r.url).get('lote') ? { json: { documentos: [], proximo_cursor: null } } : problem(500, 'ERR_INTERNAL', 'Banco indisponível');
      }
      if (p === '/v1/auditoria/resumo') return { json: { total: 0, por_status: {}, diferenca_total: 0, credito_potencial_total: 0 } };
      return undefined;
    });
    renderAt('/portal/documentos?lote=abcdef1234567890&status=CONFORME');
    expect(await screen.findByText('Nenhum documento no período. Audite um XML em Auditar XML ou ajuste os filtros.')).toBeInTheDocument();
    const first = calls.find((c) => path(c.url) === '/v1/auditoria/documentos')!;
    expect(qs(first.url).get('lote')).toBe('abcdef1234567890');
    expect(qs(first.url).get('status')).toBe('CONFORME');
    // Com lote, o período padrão é de 90 dias.
    const de = new Date(qs(first.url).get('de')!);
    expect(Math.round((Date.now() - de.getTime()) / 86_400_000)).toBeGreaterThanOrEqual(89);
    expect(screen.getByText('abcdef12…')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'remover' }));
    expect(screen.queryByText('abcdef12…')).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Filtrar' }));
    expect(await screen.findByText('Banco indisponível')).toBeInTheDocument();
  });

  it('dossiê para fiscalização: versões do motor, das regras e dos parâmetros e botão de imprimir', async () => {
    const print = vi.fn();
    vi.stubGlobal('print', print);
    window.print = print;
    api();
    renderAt('/portal/documentos/d1/dossie');
    expect(await screen.findByRole('heading', { name: 'Dossiê da auditoria' })).toBeInTheDocument();
    expect(await screen.findByText('rfb-1.2.3')).toBeInTheDocument();
    expect(screen.getByText(/^Empresa X · gerado em/)).toBeInTheDocument();
    expect(screen.getByText('r-2026.11')).toBeInTheDocument();
    expect(screen.getByText('42')).toBeInTheDocument();
    expect(screen.getAllByText('Resultado informativo, não substitui parecer.', { exact: false }).length).toBeGreaterThan(0);
    await userEvent.click(screen.getByRole('button', { name: 'Imprimir / salvar PDF' }));
    expect(print).toHaveBeenCalled();
  });

  it('dossiê de documento sem auditoria mostra traços', async () => {
    api();
    renderAt('/portal/documentos/d2/dossie');
    expect(await screen.findByText('Sem itens declarados (documento recusado antes do recálculo).')).toBeInTheDocument();
    const kv = screen.getByText('Motor oficial (bundle)').closest('div')!;
    expect(kv).toHaveTextContent('—');
    expect(screen.getByText('Situação').closest('div')!).toHaveTextContent('DIVERGENTE');
  });
});

// ---------------------------------------------------------------------------------------------------------------------
describe('classificação tributária', () => {
  const resultado = {
    consulta: { tipo: 'NCM', codigo: '22021000', data: '2027-01-01', modelo: 55 },
    item: { descricao: 'Águas minerais com açúcar', imposto_seletivo: true, is_aliquota_ad_valorem: true, is_aliquota_ad_rem: false },
    sugestoes: [
      { c_class_trib: '000001', cst: '000', descricao: 'Tributação integral', tratamento: 'INTEGRAL', tipo_aliquota: 'PADRAO', reducao_cbs_percentual: 0, reducao_ibs_uf_percentual: 0, reducao_ibs_mun_percentual: 0, credito_adquirente: true, motivo: 'Regra geral' },
      { c_class_trib: '200003', cst: '200', descricao: 'Redução de 60%', tratamento: 'REDUCAO', tipo_aliquota: 'REDUZIDA', reducao_cbs_percentual: 60.5, reducao_ibs_uf_percentual: 60, reducao_ibs_mun_percentual: 60, credito_adquirente: false, motivo: 'Anexo I' },
    ],
    aviso: 'Sugestão, não parecer.',
  };

  it('consulta por NCM vindo da URL, valida NBS e mostra vazio e erro', async () => {
    mockApi((r) => {
      if (path(r.url) !== '/v1/classificacao') return undefined;
      const q = qs(r.url);
      if (q.get('nbs') === '101011100') return problem(400, 'ERR_NBS', 'NBS desconhecida', 'Confira o código');
      if (q.get('q') === 'nada') return { json: { ...resultado, item: { ...resultado.item, imposto_seletivo: false }, sugestoes: [] } };
      return { json: resultado };
    });
    renderAt('/portal/classificacao?ncm=2202.10.00');
    const consultar = await screen.findByRole('button', { name: 'Consultar' });
    expect(consultar).toBeEnabled();
    await userEvent.selectOptions(screen.getByLabelText('Documento'), '65');
    fireEvent.change(screen.getByLabelText('Data da operação'), { target: { value: '2027-02-01' } });
    await userEvent.click(consultar);
    expect(await screen.findByRole('heading', { name: 'NCM 22021000' })).toBeInTheDocument();
    expect(Object.fromEntries(qs(calls.find((c) => path(c.url) === '/v1/classificacao')!.url))).toEqual({ ncm: '22021000', data: '2027-02-01', modelo: '65' });
    expect(screen.getByText('Sujeito ao Imposto Seletivo')).toBeInTheDocument();
    expect(screen.getByText('Águas minerais com açúcar')).toBeInTheDocument();
    expect(screen.getByText('60,5% / 60%')).toBeInTheDocument();
    expect(screen.getByText('Sim')).toBeInTheDocument();
    expect(screen.getByText('Não')).toBeInTheDocument();
    expect(screen.getByText('Sugestão, não parecer.')).toBeInTheDocument();

    // Palavras que não casam: tabela vazia e fora do IS.
    await userEvent.type(screen.getByLabelText('Palavras do produto (opcional)'), 'nada');
    await userEvent.click(screen.getByRole('button', { name: 'Consultar' }));
    expect(await screen.findByText('Nenhuma classificação compatível com o filtro. Tente sem as palavras.')).toBeInTheDocument();
    expect(screen.getByText('Fora do Imposto Seletivo')).toBeInTheDocument();
    expect(calls.filter((c) => path(c.url) === '/v1/classificacao').pop()!.url).toContain('q=nada');

    // NBS: 8 dígitos não bastam.
    await userEvent.selectOptions(screen.getByLabelText('Tipo'), 'nbs');
    const campo = screen.getByLabelText('NBS (9 dígitos)');
    expect(campo).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByRole('button', { name: 'Consultar' })).toBeDisabled();
    await userEvent.clear(campo);
    await userEvent.type(campo, '1.0101.11.00');
    expect(campo).not.toHaveAttribute('aria-invalid');
    await userEvent.clear(screen.getByLabelText('Palavras do produto (opcional)'));
    await userEvent.click(screen.getByRole('button', { name: 'Consultar' }));
    expect(await screen.findByText('NBS desconhecida. Confira o código')).toBeInTheDocument();
  });

  it('NBS pela URL abre no tipo serviço', async () => {
    mockApi(() => undefined);
    renderAt('/portal/classificacao?nbs=101011100');
    expect(await screen.findByLabelText('NBS (9 dígitos)')).toHaveValue('101011100');
    expect(screen.getByRole('button', { name: 'Consultar' })).toBeEnabled();
  });

  it('lê o CSV de produtos: cabeçalho, vírgula, aspas, linhas vazias e NCM inválido', () => {
    const r = lerProdutosCsv('codigo;descricao;ncm\r\nP1;"Refri";2202.10.00\n\nP2,Agua,22011000\nP3;Ruim;123\n');
    expect(r.linhas).toEqual([{ codigo: 'P1', descricao: 'Refri', ncm: '22021000' }, { codigo: 'P2', descricao: 'Agua', ncm: '22011000' }]);
    expect(r.erros).toEqual(['linha 5: NCM "123" precisa ter 8 dígitos']);
    expect(lerProdutosCsv('a;b').erros).toEqual(['linha 1: NCM "" precisa ter 8 dígitos']);
  });

  it('classificação em massa: consulta cada NCM uma vez, repete em 429, gera a planilha e avisa linhas inválidas', async () => {
    let tentativas429 = 0;
    mockApi((r) => {
      if (path(r.url) !== '/v1/classificacao') return undefined;
      const ncm = qs(r.url).get('ncm');
      if (ncm === '22011000' && tentativas429++ === 0) return problem(429, 'ERR_RATE', 'Muitas');
      if (ncm === '99999999') return problem(400, 'ERR_NCM', 'NCM inexistente');
      if (ncm === '22030000') return { json: { ...resultado, sugestoes: [] } };
      return { json: resultado };
    });
    renderAt('/portal/classificacao');
    const botao = await screen.findByRole('button', { name: 'Classificar' });
    expect(botao).toBeDisabled();
    const csv = 'codigo;descricao;ncm\nP1;Refri;2202.10.00\nP2;Agua;22011000\nP3;Refri 2;22021000\nP4;X;99999999\nP5;Cerveja;22030000\nP6;Ruim;12\n';
    await userEvent.upload(screen.getByLabelText('Arquivo CSV'), csvFile(csv, 'produtos.csv'));
    await userEvent.click(botao);
    expect(await screen.findByText('5 produto(s) classificados.', {}, { timeout: 6000 })).toBeInTheDocument();
    expect(screen.getByText('linha 7: NCM "12" precisa ter 8 dígitos')).toBeInTheDocument();
    const consultas = calls.filter((c) => path(c.url) === '/v1/classificacao').map((c) => qs(c.url).get('ncm'));
    expect(consultas).toEqual(['22021000', '22011000', '22011000', '99999999', '22030000']);
    const link = screen.getByRole('link', { name: 'Baixar planilha' });
    expect(link).toHaveAttribute('download', 'produtos-classificados.csv');
    const conteudo = decodeURIComponent(link.getAttribute('href')!.replace('data:text/csv;charset=utf-8,', ''));
    expect(conteudo.split('\r\n')).toEqual([
      '﻿codigo;descricao;ncm;cst_sugerido;c_class_trib_sugerido;tratamento;reducao_cbs_percentual',
      'P1;Refri;22021000;000;000001;INTEGRAL;0',
      'P2;Agua;22011000;000;000001;INTEGRAL;0',
      'P3;Refri 2;22021000;000;000001;INTEGRAL;0',
      'P4;X;99999999;;;sem sugestão;',
      'P5;Cerveja;22030000;;;sem sugestão;',
    ]);
    // Trocar o arquivo apaga a planilha anterior.
    await userEvent.upload(screen.getByLabelText('Arquivo CSV'), csvFile('x', 'outro.csv'));
    expect(screen.queryByRole('link', { name: 'Baixar planilha' })).toBeNull();
    expect(screen.queryByText('5 produto(s) classificados.')).toBeNull();
  }, 15_000);

  it('classificação em massa recusa arquivo com mais de 300 NCMs distintos', async () => {
    mockApi(() => undefined);
    renderAt('/portal/classificacao');
    const linhas = Array.from({ length: 301 }, (_, i) => `P${i};Prod;${String(10000000 + i)}`).join('\n') + '\nPX;Ruim;1';
    await userEvent.upload(await screen.findByLabelText('Arquivo CSV'), csvFile(linhas, 'grande.csv'));
    await userEvent.click(screen.getByRole('button', { name: 'Classificar' }));
    expect(await screen.findByText('O arquivo tem 301 NCMs distintos; envie até 300 por vez.')).toBeInTheDocument();
    expect(screen.getByText('linha 302: NCM "1" precisa ter 8 dígitos')).toBeInTheDocument();
    expect(has((c) => path(c.url) === '/v1/classificacao')).toBe(false);
  });
});

// ---------------------------------------------------------------------------------------------------------------------
describe('alíquotas e exceções', () => {
  const excecoes = [
    { id: 'e1', cnpj: '11222333000181', tributo: 'IBS_MUN', ente_codigo: '3550308', produto: 'NCM:2203', aliquota: 1.5, vigencia_inicio: '2027-01-01', vigencia_fim: '2027-12-31', motivo: 'LIMINAR', processo: 'MS 123', documento: 'liminar.pdf', situacao: 'PROPOSTA', proposta_por: 'joao@x.test', decidida_por: null, decidida_em: null, criada_em: '2026-11-20T10:00:00Z' },
    { id: 'e2', cnpj: null, tributo: 'CBS', ente_codigo: null, produto: null, aliquota: 0, vigencia_inicio: '2027-01-01', vigencia_fim: null, motivo: 'REGIME_ESPECIAL', processo: 'TA 9', documento: 'termo.pdf', situacao: 'ATIVA', proposta_por: null, decidida_por: 'ana@x.test', decidida_em: '2026-11-21T10:00:00Z', criada_em: '2026-11-19T10:00:00Z' },
    { id: 'e3', cnpj: null, tributo: 'XYZ', ente_codigo: null, produto: null, aliquota: 2, vigencia_inicio: '2027-01-01', vigencia_fim: null, motivo: 'NOVO', processo: 'P', documento: 'x.pdf', situacao: 'ESTRANHA', proposta_por: 'a', decidida_por: null, decidida_em: null, criada_em: '2026-11-19T10:00:00Z' },
  ];
  const consulta = {
    municipio: { codigo: 3550308, nome: 'São Paulo', uf: 'SP' }, data: '2027-01-01',
    tributos: [
      { tributo: 'CBS', nome: 'CBS', aliquota: 8.8, origem: 'REFERENCIA', ato_legal: 'Resolução do Senado', provisoria: true, vigencia_inicio: '2027-01-01', vigencia_fim: '2027-12-31' },
      { tributo: 'IBS_MUN', nome: 'IBS municipal', aliquota: 10, origem: 'PROPRIA_DO_ENTE', forma_aplicacao: 'SUBSTITUI', referencia: 9.5, ato_legal: 'Lei municipal 1', vigencia_inicio: '2027-01-01', vigencia_fim: null },
      { tributo: 'IBS_UF', nome: 'IBS estadual', aliquota: null, mensagem: 'Aguardando publicação do Senado' },
      { tributo: 'IS', nome: 'Imposto Seletivo', aliquota: 0, origem: 'PROPRIA_DO_ENTE', forma_aplicacao: null },
    ],
    total: 18.8, observacao: 'Mesma resolução do cálculo.',
  };

  it('consulta alíquotas, decide exceções com confirmação, baixa o documento e propõe nova exceção com PDF', async () => {
    let lista = excecoes;
    mockApi((r) => {
      const p = path(r.url);
      if (p === '/v1/publico/aliquotas') return { json: consulta };
      if (r.url === '/v1/excecoes-aliquota' && r.method === 'GET') return { json: { excecoes: lista } };
      if (r.url === '/v1/excecoes-aliquota' && r.method === 'POST') { lista = [...lista]; return { status: 201, json: { mensagem: 'Exceção enviada para aprovação.' } } ; }
      if (/^\/v1\/excecoes-aliquota\/e\d\/(aprovar|recusar)$/.test(r.url)) return { json: {} };
      if (r.url === '/v1/excecoes-aliquota/e2/revogar') return problem(409, 'ERR_JA_REVOGADA', 'Exceção já revogada');
      if (r.url === '/v1/excecoes-aliquota/e1/documento') return { json: 'pdf' };
      return undefined;
    });
    renderAt('/portal/aliquotas');
    expect(await screen.findByRole('heading', { name: 'Alíquotas e exceções' })).toBeInTheDocument();

    // Consulta (município padrão São Paulo).
    await userEvent.click(screen.getByRole('button', { name: 'Consultar' }));
    expect(await screen.findByText('Mesma resolução do cálculo.')).toBeInTheDocument();
    const q = qs(calls.find((c) => path(c.url) === '/v1/publico/aliquotas')!.url);
    expect(q.get('municipio')).toBe('3550308');
    expect(q.get('data')).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(screen.getByText('Referência nacional')).toBeInTheDocument();
    expect(screen.getByText('Própria do ente (substitui; referência 9,5%)')).toBeInTheDocument();
    expect(screen.getByText('Própria do ente')).toBeInTheDocument();
    expect(screen.getByText('não publicada')).toBeInTheDocument();
    expect(screen.getByText('Aguardando publicação do Senado')).toBeInTheDocument();
    expect(screen.getAllByText('01/01/2027 a 31/12/2027').length).toBeGreaterThan(1);
    expect(screen.getAllByText('01/01/2027 em diante').length).toBeGreaterThan(0);
    expect(screen.getByText('18,8%')).toBeInTheDocument();

    // Lista de exceções.
    const e1 = (await screen.findByText('MS 123')).closest('tr')!;
    expect(e1).toHaveTextContent('IBS municipal');
    expect(e1).toHaveTextContent('ente 3550308');
    expect(e1).toHaveTextContent('NCM:2203');
    expect(e1).toHaveTextContent('CNPJ 11222333000181');
    expect(e1).toHaveTextContent('Liminar');
    expect(e1).toHaveTextContent('aguardando aprovação');
    expect(e1).toHaveTextContent('por joao@x.test');
    const e2 = screen.getByText('TA 9').closest('tr')!;
    expect(e2).toHaveTextContent('Regime especial');
    expect(e2).toHaveTextContent('por — · decidida por ana@x.test em');
    expect(within(e2).queryByRole('button', { name: 'Aprovar' })).toBeNull();
    const e3 = screen.getByText('XYZ').closest('tr')!;
    expect(e3).toHaveTextContent('NOVO');
    expect(e3).toHaveTextContent('ESTRANHA');
    expect(within(e3).queryByRole('button', { name: 'Revogar' })).toBeNull();

    // Documento: download direto.
    await userEvent.click(within(e1).getByRole('button', { name: 'Documento' }));
    await waitFor(() => expect(createObjectURL).toHaveBeenCalled());
    expect(has((c) => c.url === '/v1/excecoes-aliquota/e1/documento')).toBe(true);

    // Aprovar e recusar pedem confirmação.
    await userEvent.click(within(e1).getByRole('button', { name: 'Aprovar' }));
    await confirmar('Aprovar a exceção?', 'Aprovar');
    await waitFor(() => expect(has((c) => c.method === 'POST' && c.url === '/v1/excecoes-aliquota/e1/aprovar')).toBe(true));
    const antes = calls.filter((c) => c.url === '/v1/excecoes-aliquota' && c.method === 'GET').length;
    await userEvent.click(within(screen.getByText('MS 123').closest('tr')!).getByRole('button', { name: 'Recusar' }));
    await confirmar('Recusar a exceção?', 'Recusar');
    await waitFor(() => expect(has((c) => c.url === '/v1/excecoes-aliquota/e1/recusar')).toBe(true));
    await waitFor(() => expect(calls.filter((c) => c.url === '/v1/excecoes-aliquota' && c.method === 'GET').length).toBeGreaterThan(antes));

    // Revogar com erro: mensagem e sem recarregar.
    await userEvent.click(within(screen.getByText('TA 9').closest('tr')!).getByRole('button', { name: 'Revogar' }));
    await confirmar('Revogar a exceção?', 'Revogar');
    expect(await screen.findByText('Exceção já revogada')).toBeInTheDocument();

    // Nova exceção: IBS municipal mostra o código do município; produto vira maiúsculo; PDF vai em base64.
    const enviar = screen.getByRole('button', { name: 'Enviar para aprovação' });
    expect(enviar).toBeDisabled();
    expect(screen.queryByLabelText(/Código d/)).toBeNull();
    await userEvent.selectOptions(screen.getByLabelText('Tributo'), 'IBS_UF');
    expect(screen.getByLabelText('Código da UF (opcional)')).toBeInTheDocument();
    await userEvent.selectOptions(screen.getByLabelText('Tributo'), 'IBS_MUN');
    await userEvent.type(screen.getByLabelText('Código do município (opcional)'), '35a50308');
    expect(screen.getByLabelText('Código do município (opcional)')).toHaveValue('3550308');
    await userEvent.type(screen.getByLabelText('Alíquota (%)'), '1,25');
    await userEvent.selectOptions(screen.getByLabelText('Motivo'), 'DECISAO_JUDICIAL');
    await userEvent.type(screen.getByLabelText('Processo ou ato'), 'Proc 777');
    fireEvent.change(screen.getByLabelText('Vale até (opcional)'), { target: { value: '2027-06-30' } });
    fireEvent.change(screen.getByLabelText('Vale a partir de'), { target: { value: '2027-02-01' } });
    await userEvent.type(screen.getByLabelText('CNPJ (opcional)'), '11222333000181');
    await userEvent.type(screen.getByLabelText('Produto (opcional)'), 'ncm:2203');
    expect(screen.getByLabelText('Produto (opcional)')).toHaveValue('NCM:2203');
    expect(enviar).toBeDisabled();
    await userEvent.upload(screen.getByLabelText('Documento (PDF)'), new File([new Uint8Array([37, 80, 68, 70])], 'decisao.pdf', { type: 'application/pdf' }));
    expect(enviar).toBeEnabled();
    await userEvent.click(enviar);
    expect(await screen.findByText('Exceção enviada para aprovação.')).toBeInTheDocument();
    expect(calls.find((c) => c.url === '/v1/excecoes-aliquota' && c.method === 'POST')!.body).toEqual({
      tributo: 'IBS_MUN', aliquota: 1.25, vigencia_inicio: '2027-02-01', vigencia_fim: '2027-06-30', motivo: 'DECISAO_JUDICIAL', processo: 'Proc 777',
      cnpj: '11222333000181', ente_codigo: '3550308', produto: 'NCM:2203', documento_nome: 'decisao.pdf', documento_base64: 'JVBERg==',
    });
    expect(screen.getByLabelText('Alíquota (%)')).toHaveValue('');
    expect(screen.getByLabelText('Processo ou ato')).toHaveValue('');
  });

  it('proposta recusada pela API mostra o erro e mantém o formulário; opcionais vazios não vão no corpo', async () => {
    mockApi((r) => {
      if (r.url === '/v1/excecoes-aliquota' && r.method === 'GET') return { json: { excecoes: [] } };
      if (r.url === '/v1/excecoes-aliquota' && r.method === 'POST') return problem(400, 'ERR_PDF', 'Documento inválido', 'Envie um PDF de até 5 MB');
      if (path(r.url) === '/v1/publico/aliquotas') return problem(400, 'ERR_DATA', 'Data fora da vigência');
      return undefined;
    });
    renderAt('/portal/aliquotas');
    expect(await screen.findByText('Nenhuma exceção cadastrada.')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Consultar' }));
    expect(await screen.findByText('Data fora da vigência')).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText('Alíquota (%)'), '2');
    await userEvent.type(screen.getByLabelText('Processo ou ato'), 'P1');
    await userEvent.upload(screen.getByLabelText('Documento (PDF)'), new File(['%PDF'], 'a.pdf', { type: 'application/pdf' }));
    await userEvent.click(screen.getByRole('button', { name: 'Enviar para aprovação' }));
    expect(await screen.findByText('Documento inválido. Envie um PDF de até 5 MB')).toBeInTheDocument();
    const body = calls.find((c) => c.method === 'POST')!.body as Record<string, unknown>;
    expect(body).toMatchObject({ tributo: 'CBS', aliquota: 2, motivo: 'LIMINAR', vigencia_inicio: '2027-01-01' });
    expect(body).not.toHaveProperty('cnpj');
    expect(body).not.toHaveProperty('vigencia_fim');
    expect(screen.getByLabelText('Processo ou ato')).toHaveValue('P1');
    // Sem município escolhido não dá para consultar.
    await userEvent.clear(screen.getByLabelText('Município'));
    expect(screen.getByRole('button', { name: 'Consultar' })).toBeDisabled();
  });

  it('sem cnpjs:manage só consulta: sem aprovar e sem formulário; erro da lista aparece', async () => {
    mockApi((r) => {
      if (r.url === '/v1/excecoes-aliquota') return problem(500, 'ERR_INTERNAL', 'Lista indisponível');
      return undefined;
    }, ['auditoria:read']);
    renderAt('/portal/aliquotas');
    expect(await screen.findByText('Lista indisponível')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Enviar para aprovação' })).toBeNull();
    expect(screen.queryByRole('heading', { name: 'Cadastrar exceção' })).toBeNull();
  });

  it('sem cnpjs:manage a lista não mostra os botões de decisão', async () => {
    mockApi((r) => (r.url === '/v1/excecoes-aliquota' ? { json: { excecoes } } : undefined), ['simulacao:write']);
    renderAt('/portal/aliquotas');
    const e1 = (await screen.findByText('MS 123')).closest('tr')!;
    expect(within(e1).getByRole('button', { name: 'Documento' })).toBeInTheDocument();
    expect(within(e1).queryByRole('button', { name: 'Aprovar' })).toBeNull();
    expect(within(screen.getByText('TA 9').closest('tr')!).queryByRole('button', { name: 'Revogar' })).toBeNull();
  });
});

// ---------------------------------------------------------------------------------------------------------------------
describe('apuração do mês', () => {
  const resumo = (competencia: string, credor: number) => ({
    competencia,
    saidas: { documentos: 10, cbs: 100, ibs: 200, is: 0, declarado: { cbs: 100, ibs: 200, is: 0 }, diferenca: 5, divergentes: 2 },
    entradas: { documentos: 4, cbs: 40, ibs: 60, credito_potencial: 100, credito_em_risco: 15, divergentes: 1 },
    saldo: { debitos: 300, creditos_aproveitaveis: 85, a_recolher_estimado: credor ? 0 : 215, credor_estimado: credor },
    por_municipio: credor ? [] : [{ codigo: 3550308, nome: 'São Paulo', uf: 'SP', documentos: 7, cbs: 70, ibs: 140 }, { codigo: 9999999, nome: null, uf: null, documentos: 2, cbs: 20, ibs: 40 }, { codigo: null, nome: null, uf: null, documentos: 1, cbs: 10, ibs: 20 }],
    por_classificacao: credor ? [] : [{ cst: '000', c_class_trib: '000001', itens: 12, base: 1000, valor: 265 }, { cst: null, c_class_trib: null, itens: 1, base: 10, valor: 2 }],
    observacao: `Estimativa da competência ${competencia}.`,
  });

  it('mostra saldo, quebras por município e classificação, fornecedores e exporta CSV; troca de competência', async () => {
    const mes = currentMonth();
    mockApi((r) => {
      const p = path(r.url);
      const c = qs(r.url).get('competencia');
      if (p === '/v1/apuracao') return c === '2026-01' ? { json: resumo(c, 50) } : c === '2026-02' ? problem(500, 'ERR_INTERNAL', 'Apuração indisponível') : { json: resumo(c!, 0) };
      if (p === '/v1/apuracao/fornecedores') {
        if (c === '2026-01') return { json: { fornecedores: [] } };
        if (c === '2026-02') return problem(500, 'ERR_INTERNAL', 'Fornecedores indisponíveis');
        return { json: { fornecedores: [{ cnpj: '44555666000199', documentos: 3, divergentes: 1, indicios: 2, credito_potencial: 30, credito_em_risco: 12.34, diferenca: 1 }] } };
      }
      if (p === '/v1/apuracao/exportar') return { json: 'csv' };
      return undefined;
    });
    renderAt('/portal/apuracao');
    expect(await screen.findByText('A recolher (estimado)')).toBeInTheDocument();
    expect(screen.getByText(`Estimativa da competência ${mes}.`)).toBeInTheDocument();
    expect(screen.getByText('São Paulo / SP')).toBeInTheDocument();
    expect(screen.getByText('9999999')).toBeInTheDocument();
    expect(screen.getByText('000001')).toBeInTheDocument();
    expect(screen.getByText('Notas com divergência').parentElement!.parentElement!).toHaveTextContent('3');
    expect(await screen.findByText('44.555.666/0001-99')).toBeInTheDocument();
    expect(screen.getByText(/12,34/)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: /Exportar para o contador/ }));
    await waitFor(() => expect(createObjectURL).toHaveBeenCalled());
    expect(calls.find((c) => path(c.url) === '/v1/apuracao/exportar')!.url).toBe(`/v1/apuracao/exportar?competencia=${mes}`);

    // Competência com saldo credor e sem notas.
    const comp = screen.getByLabelText('Competência');
    fireEvent.change(comp, { target: { value: '2026-01' } });
    expect(await screen.findByText('Saldo credor estimado')).toBeInTheDocument();
    expect(screen.getByText('Nenhuma nota de saída auditada nesta competência.')).toBeInTheDocument();
    expect(screen.getByText('Sem itens.')).toBeInTheDocument();
    expect(await screen.findByText('Nenhuma nota de entrada auditada nesta competência.')).toBeInTheDocument();

    // Apagar o campo não muda a competência.
    const antes = calls.length;
    fireEvent.change(comp, { target: { value: '' } });
    expect(calls.length).toBe(antes);

    fireEvent.change(comp, { target: { value: '2026-02' } });
    expect(await screen.findByText('Apuração indisponível')).toBeInTheDocument();
    expect(await screen.findByText('Fornecedores indisponíveis')).toBeInTheDocument();
  });
});

describe('preço e margem', () => {
  it('calcula preço com IBS+CBS por fora, crédito, margem e preço-alvo', async () => {
    mockApi(() => undefined, ['usage:read']);
    renderAt('/portal/preco-margem');
    expect(await screen.findByRole('heading', { name: 'Preço e margem' })).toBeInTheDocument();
    const linha = (t: string) => screen.getByText(t).closest('tr')!;
    // Padrão: 100, 60, 26,5%, 26,5%, 20%.
    expect(linha('Preço ao cliente (com IBS+CBS)')).toHaveTextContent('126,50');
    expect(linha('IBS+CBS destacado na venda')).toHaveTextContent('26,50');
    expect(linha('Crédito das compras')).toHaveTextContent('15,90');
    expect(linha('IBS+CBS a recolher')).toHaveTextContent('10,60');
    expect(linha('Margem')).toHaveTextContent('40,00 (40%)');
    expect(linha('Preço sem tributo para 20% de margem')).toHaveTextContent('75,00');

    // Crédito maior que o débito: a recolher não fica negativo; margem 100% não tem preço; preço zero.
    const set = async (label: string | RegExp, v: string) => { const el = screen.getByLabelText(label); await userEvent.clear(el); if (v) await userEvent.type(el, v); };
    await set(/paga nas compras/, '80');
    expect(linha('IBS+CBS a recolher')).toHaveTextContent('0,00');
    await set('Margem desejada (%)', '100');
    expect(linha('Preço sem tributo para 100% de margem')).toHaveTextContent('—');
    await set('Preço de venda sem tributo (R$)', '');
    expect(linha('Margem')).toHaveTextContent('(0%)');
    await set('Custo do produto sem tributo (R$)', 'abc');
    await set(/na venda/, '10');
    expect(linha('Crédito das compras')).toHaveTextContent('0,00');
  });
});

// ---------------------------------------------------------------------------------------------------------------------
describe('avisos', () => {
  const avisos = () => [
    { id: 'a1', tipo: 'FRANQUIA', severidade: 'aviso', titulo: 'Franquia em 80%', mensagem: 'Você usou 80% da franquia.', link: '/portal/cobranca', criado_em: '2026-11-20T10:00:00Z', lido: false },
    { id: 'a2', tipo: 'EMPRESA_VERIFICADA', severidade: 'info', titulo: 'Empresa verificada', mensagem: 'Pronto.', link: '/portal/cobranca', criado_em: '2026-11-19T10:00:00Z', lido: true },
    { id: 'a3', tipo: 'CERTIFICADO', severidade: 'critico', titulo: 'Certificado vencendo', mensagem: 'Vence em 5 dias.', link: null, criado_em: '2026-11-18T10:00:00Z', lido: false },
    { id: 'a4', tipo: 'DISPUTA_CNPJ', severidade: 'aviso', titulo: 'Disputa aberta', mensagem: 'Alguém contestou.', link: null, criado_em: '2026-11-17T10:00:00Z', lido: true },
  ];

  it('lista com severidade, abre no destino certo, marca um e todos como lidos e atualiza o sino', async () => {
    let lidos = new Set<string>();
    const naoLidos = () => avisos().filter((a) => !a.lido && !lidos.has(a.id)).length;
    mockApi((r) => {
      if (r.url === '/v1/avisos?nao_lidos=true') return { json: { avisos: [], nao_lidos: naoLidos() } };
      if (r.url === '/v1/avisos') return { json: { avisos: avisos().map((a) => ({ ...a, lido: a.lido || lidos.has(a.id) })), nao_lidos: naoLidos() } };
      if (r.url === '/v1/avisos/lidos') { lidos = new Set(avisos().map((a) => a.id)); return { status: 204 }; }
      const m = /^\/v1\/avisos\/(a\d)\/lido$/.exec(r.url);
      if (m) { lidos.add(m[1]); return { status: 204 }; }
      return undefined;
    });
    renderAt('/portal/avisos');
    const franquia = await screen.findByRole('article', { name: 'Franquia em 80%' });
    expect(franquia).toHaveTextContent('Atenção');
    expect(within(franquia).getByRole('link', { name: 'Abrir' })).toHaveAttribute('href', '/portal/cobranca');
    const verificada = screen.getByRole('article', { name: 'Empresa verificada' });
    expect(verificada).toHaveTextContent('Informativo');
    expect(verificada).toHaveClass('read');
    expect(within(verificada).getByRole('link', { name: 'Abrir' })).toHaveAttribute('href', '/portal/empresa');
    expect(within(verificada).queryByRole('button', { name: 'Marcar como lido' })).toBeNull();
    const cert = screen.getByRole('article', { name: 'Certificado vencendo' });
    expect(cert).toHaveTextContent('Crítico');
    expect(within(cert).queryByRole('link', { name: 'Abrir' })).toBeNull();
    expect((await screen.findAllByRole('link', { name: 'Avisos: 2 não lido(s)' })).length).toBeGreaterThan(0);

    await userEvent.click(within(franquia).getByRole('button', { name: 'Marcar como lido' }));
    await waitFor(() => expect(has((c) => c.method === 'POST' && c.url === '/v1/avisos/a1/lido')).toBe(true));
    expect((await screen.findAllByRole('link', { name: 'Avisos: 1 não lido(s)' })).length).toBeGreaterThan(0);
    await waitFor(() => expect(screen.getByRole('article', { name: 'Franquia em 80%' })).toHaveClass('read'));

    await userEvent.click(screen.getByRole('button', { name: 'Marcar todos como lidos' }));
    await waitFor(() => expect(has((c) => c.method === 'POST' && c.url === '/v1/avisos/lidos')).toBe(true));
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Marcar todos como lidos' })).toBeNull());
    expect((await screen.findAllByRole('link', { name: 'Avisos' })).length).toBeGreaterThan(0);
    expect(screen.queryAllByRole('button', { name: 'Marcar como lido' })).toHaveLength(0);
  });

  it('estado vazio, sino com 99+ e erro ao marcar', async () => {
    mockApi((r) => {
      if (r.url === '/v1/avisos?nao_lidos=true') return { json: { avisos: [], nao_lidos: 150 } };
      if (r.url === '/v1/avisos') return { json: { avisos: [], nao_lidos: 3 } };
      if (r.url === '/v1/avisos/lidos') return problem(500, 'ERR_INTERNAL', 'Falha ao marcar');
      return undefined;
    });
    renderAt('/portal/avisos');
    expect(await screen.findByText(/Nenhum aviso por enquanto/)).toBeInTheDocument();
    expect((await screen.findAllByText('99+')).length).toBeGreaterThan(0);
    await userEvent.click(screen.getByRole('button', { name: 'Marcar todos como lidos' }));
    expect(await screen.findByText('Falha ao marcar')).toBeInTheDocument();
  });

  it('erro ao carregar a lista; falha do sino é silenciosa; o sino recarrega a cada 2 minutos', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      let bell = 0;
      mockApi((r) => {
        if (r.url === '/v1/avisos?nao_lidos=true') { bell += 1; return bell === 1 ? problem(500, 'ERR_INTERNAL', 'x') : { json: { avisos: [], nao_lidos: 4 } }; }
        if (r.url === '/v1/avisos') return problem(503, 'ERR_UNAVAILABLE', 'Avisos indisponíveis');
        return undefined;
      });
      renderAt('/portal/avisos');
      expect(await screen.findByText('Avisos indisponíveis')).toBeInTheDocument();
      expect(screen.getAllByRole('link', { name: 'Avisos' }).length).toBeGreaterThan(0);
      await act(() => vi.advanceTimersByTimeAsync(120_000));
      expect((await screen.findAllByRole('link', { name: 'Avisos: 4 não lido(s)' })).length).toBeGreaterThan(0);
    } finally {
      vi.useRealTimers();
    }
  });
});
