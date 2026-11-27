import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import App from '../App';
import { lerCsv } from '../backoffice/aliquotasCsv';

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

const renderAt = (path: string) => render(<MemoryRouter initialEntries={[path]}><App /></MemoryRouter>);

const operador = (escopos: string[]): Handler => (r) => {
  if (r.url === '/admin/v1/conta/sessao') return { json: { usuario: { id: 'o1', nome: 'Fiscal B', email: 'fb@tg.test' }, mfa: 'ok', escopos } };
  if (r.url === '/admin/v1/me') return { json: { operador: 'fb@tg.test', scopes: escopos } };
  return undefined;
};

const proposta = { id: 'c1', dominio: 'RATE_CATALOG', status: 'PROPOSED', proposto_por: 'fiscal-a', aprovado_por: null, justificativa: 'Senado', vigente_em: '2027-01-01T03:00:00Z', criado_em: '2026-11-20T10:00:00Z', payload: { linhas: [{}] } };

/** Alíquotas: sessão de operador fiscal; cada teste completa com as rotas que usa. */
function mockAliquotas(extra: Handler = () => undefined, cobertura: unknown = { tributos: [] }) {
  const base = operador(['admin:fiscal']);
  mockApi((r) => extra(r) ?? base(r) ?? (
    r.url === '/admin/v1/params/rates' ? { json: [] }
      : r.url === '/admin/v1/params/cobertura' ? { json: cobertura }
        : r.url.startsWith('/admin/v1/params/changes') && r.method === 'GET' ? { json: [proposta] }
          : undefined));
}

const CSV = [
  'tributo;tipo;esfera;ente_codigo;forma_aplicacao;produto;aliquota;valid_from;valid_to;ato_legal;fonte;provisoria',
  'IBS_UF;PROPRIA_ENTE;UF;35;ACRESCIMO;;1,5;2027-01-01;;Lei SP 1/2026;LEI_ENTE;nao',
  'IS;REFERENCIA;UNIAO;;;NCM:2203;10;01/01/2027;;LC 214/2025;LEI_FEDERAL;sim',
  'XYZ;;;;;;1;2027-01-01;;ato;SENADO;',
].join('\r\n');

describe('backoffice: alíquotas', () => {
  it('lê CSV com vírgula como separador, decimal entre aspas e tipo/esfera deduzidos do tributo', () => {
    const r = lerCsv('tributo,ente_codigo,tipo,aliquota,valid_from,ato_legal,fonte\nIBS_MUN,3550308,PROPRIA_ENTE,"2,25",2027-01-01,Lei 1,LEI_ENTE\nCBS,,,"8,8",2027-01-01,LC 214,LEI_FEDERAL\n');
    expect(r.erros).toEqual([]);
    expect(r.linhas[0]).toMatchObject({ tributo: 'IBS_MUN', esfera: 'MUNICIPIO', ente_codigo: '3550308', forma_aplicacao: 'SUBSTITUICAO', aliquota: '2.25' });
    expect(r.linhas[1]).toMatchObject({ tributo: 'CBS', tipo: 'REFERENCIA', esfera: 'UNIAO', aliquota: '8.8' });
    expect(lerCsv('tributo;aliquota\nCBS;1').erros[0]).toContain('valid_from');
  });

  it('importa CSV, confere no servidor, marca a vigente a substituir e propõe todas as linhas', async () => {
    mockAliquotas((r) => {
      if (r.url === '/admin/v1/params/rates/changes/preview') {
        return { json: { linhas: 2, erros: [], conflitos_sem_substituicao: 1, itens: [
          { tributo: 'IBS_UF', ente_codigo: '35', produto: null, aliquota_nova: 1.5, valid_from: '2027-01-01',
            vigentes: [{ id: 'r-old', aliquota: 1, valid_from: '2026-01-01', valid_to: null, ato_legal: 'Lei SP 0/2025', provisoria: false }] },
          { tributo: 'IS', ente_codigo: null, produto: 'NCM:2203', aliquota_nova: 10, valid_from: '2027-01-01', vigentes: [] },
        ] } };
      }
      if (r.url === '/admin/v1/params/rates/changes' && r.method === 'POST') return { status: 201, json: { change_id: 'c9', status: 'PROPOSED' } };
      return undefined;
    });
    const { container } = renderAt('/backoffice/aliquotas');
    await screen.findByRole('heading', { name: 'Nova proposta' });
    expect(screen.queryByLabelText(/Aplicar a partir de/)).toBeNull();
    expect(screen.getByRole('button', { name: 'Propor' })).toBeDisabled();

    const input = container.querySelector('input[type=file]') as HTMLInputElement;
    fireEvent.change(input, { target: { files: [new File([CSV], 'aliquotas.csv', { type: 'text/csv' })] } });
    expect(await screen.findByText('2 linha(s) importada(s) de aliquotas.csv.')).toBeInTheDocument();
    expect(screen.getByText(/Linha 4: tributo "XYZ" desconhecido/)).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Linhas da proposta (2)' })).toBeInTheDocument();

    await userEvent.type(screen.getByLabelText('Justificativa'), 'Lei SP 1/2026 e LC 214/2025');
    await userEvent.click(screen.getByRole('button', { name: 'Conferir' }));
    const marcar = await screen.findByRole('checkbox', { name: /Substituir IBS estadual SP \(35\) 1,00% de 01\/01\/2026/ });
    expect(screen.getByText(/1 linha\(s\) se sobrepõem/)).toBeInTheDocument();
    await userEvent.click(marcar);
    expect(screen.queryByText(/se sobrepõem a uma alíquota vigente/)).toBeNull();

    await userEvent.click(screen.getByRole('button', { name: 'Propor' }));
    expect(await screen.findByText('Proposta c9 criada. Outra pessoa precisa aprová-la.')).toBeInTheDocument();
    const enviado = calls.find((c) => c.url === '/admin/v1/params/rates/changes' && c.method === 'POST')!.body as Record<string, unknown>;
    expect(enviado).toEqual({
      justificativa: 'Lei SP 1/2026 e LC 214/2025',
      substitui: ['r-old'],
      linhas: [
        { tributo: 'IBS_UF', tipo: 'PROPRIA_ENTE', esfera: 'UF', ente_codigo: '35', forma_aplicacao: 'ACRESCIMO', produto: null, aliquota: 1.5,
          valid_from: '2027-01-01', valid_to: null, ato_legal: 'Lei SP 1/2026', fonte: 'LEI_ENTE', provisoria: false },
        { tributo: 'IS', tipo: 'REFERENCIA', esfera: 'UNIAO', ente_codigo: null, forma_aplicacao: null, produto: 'NCM:2203', aliquota: 10,
          valid_from: '2027-01-01', valid_to: null, ato_legal: 'LC 214/2025', fonte: 'LEI_FEDERAL', provisoria: true },
      ],
    });
    // A conferência usa o mesmo corpo.
    expect(calls.find((c) => c.url.endsWith('/preview'))!.body).toMatchObject({ justificativa: 'Lei SP 1/2026 e LC 214/2025', linhas: enviado.linhas });
  });

  it('linha à mão: IS pede o produto e só mostra os campos que valem para o tributo', async () => {
    mockAliquotas();
    renderAt('/backoffice/aliquotas');
    const tributo = await screen.findByLabelText('Tributo');
    expect(screen.queryByLabelText('Forma de aplicação')).toBeNull();
    await userEvent.selectOptions(tributo, 'IBS_UF');
    await userEvent.selectOptions(screen.getByLabelText('Tipo'), 'PROPRIA_ENTE');
    expect(screen.getByLabelText('Forma de aplicação')).toBeInTheDocument();
    expect(screen.getByLabelText('UF')).toBeInTheDocument();
    await userEvent.selectOptions(tributo, 'IS');
    expect(screen.queryByLabelText('Tipo')).toBeNull();
    await userEvent.type(screen.getByLabelText('Alíquota (%)'), '10');
    fireEvent.change(screen.getByLabelText('Vigente de'), { target: { value: '2027-01-01' } });
    await userEvent.type(screen.getByLabelText('Ato legal'), 'LC 214/2025');
    await userEvent.click(screen.getByRole('button', { name: 'Adicionar linha' }));
    expect(await screen.findByText(/informe o produto/)).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText('Código NCM'), '2203');
    await userEvent.click(screen.getByRole('button', { name: 'Adicionar linha' }));
    expect(await screen.findByRole('heading', { name: 'Linhas da proposta (1)' })).toBeInTheDocument();
    expect(screen.getByText('NCM:2203')).toBeInTheDocument();
  });

  it('recusar exige motivo e envia {motivo}', async () => {
    mockAliquotas((r) => (r.url === '/admin/v1/params/changes/c1/reject' ? { json: { id: 'c1', status: 'REJECTED' } } : undefined));
    renderAt('/backoffice/aliquotas');
    await userEvent.click(await screen.findByRole('button', { name: 'Recusar' }));
    const dialog = await screen.findByRole('dialog', { name: 'Recusar a proposta?' });
    const confirmar = within(dialog).getByRole('button', { name: 'Recusar proposta' });
    expect(confirmar).toBeDisabled();
    const motivo = within(dialog).getByLabelText('Motivo da recusa');
    await userEvent.type(motivo, 'ato');
    expect(confirmar).toBeDisabled();
    await userEvent.type(motivo, ' ainda não publicado no DOU');
    expect(confirmar).toBeEnabled();
    await userEvent.click(confirmar);
    await waitFor(() => expect(calls.find((c) => c.url === '/admin/v1/params/changes/c1/reject')?.body).toEqual({ motivo: 'ato ainda não publicado no DOU' }));
    expect(await screen.findByText(/Proposta recusada/)).toBeInTheDocument();
  });

  it('avisa quando a alíquota de referência publicada cobre menos de 90 dias', async () => {
    mockAliquotas(undefined, { tributos: [
      { tributo: 'CBS', dias: 34, coberto_ate: '2026-12-31', alerta: true },
      { tributo: 'IBS_UF', dias: 400, coberto_ate: '2027-12-31', alerta: false },
    ] });
    renderAt('/backoffice/aliquotas');
    expect(await screen.findByText('CBS publicada só até 31/12/2026 — carregue o ato (Senado/CG-IBS).')).toBeInTheDocument();
    expect(screen.queryByText(/IBS estadual publicada/)).toBeNull();
  });
});

describe('backoffice: limites de contrato', () => {
  it('salva só os campos preenchidos e volta aos limites do plano com {}', async () => {
    const tenant = { tenant_id: 't1', nome: 'Empresa X', cnpj_raiz: '11222333', plano: 'STARTER', situacao: 'ACTIVE', auto_provisionar_cnpj: false, retencao_xml_dias: null, criado_em: '2026-01-01T00:00:00Z', clientes_oauth: [] };
    const detalhe = {
      tenant: { ...tenant, cobranca: { provedor: null, cliente: null, assinatura: null, situacao_assinatura: null, fim_do_periodo: null }, teto_centavos: 0,
        verificada_em: null, metodo_verificacao: null, verificada_por: null, inadimplente_desde: null, motivo_suspensao: null },
      pessoas: [], cnpjs: [], api_keys: [], consumo: [], faturas: [],
    };
    const base = operador(['admin:commercial']);
    mockApi((r) => {
      if (r.url.startsWith('/admin/v1/tenants?')) return { json: { tenants: [tenant], total: 1, pagina: 1 } };
      if (r.url === '/admin/v1/plans') return { json: { planos: [{ codigo: 'STARTER' }] } };
      if (r.url === '/admin/v1/tenants/t1') return { json: detalhe };
      if (r.url === '/admin/v1/tenants/t1/limites' && r.method === 'PUT') return { json: { tenant_id: 't1', limites: Object.keys(r.body as object).length ? r.body : null } };
      return base(r);
    });
    renderAt('/backoffice');
    await userEvent.click(await screen.findByRole('button', { name: 'Detalhe' }));
    const card = (await screen.findByRole('heading', { name: 'Limites de contrato' })).closest('section') as HTMLElement;
    expect(within(card).getByText('limites do plano')).toBeInTheDocument();
    await userEvent.type(within(card).getByLabelText('Requisições por minuto'), '1200');
    await userEvent.type(within(card).getByLabelText('Tamanho do lote (MB)'), '5');
    await userEvent.click(within(card).getByRole('button', { name: 'Salvar limites' }));
    await waitFor(() => expect(calls.find((c) => c.url === '/admin/v1/tenants/t1/limites')?.body).toEqual({ rpm: 1200, batch_max_bytes: 5 * 1024 * 1024 }));
    expect(await within(card).findByText(/requisições por minuto: 1\.200/)).toBeInTheDocument();

    await userEvent.click(within(card).getByRole('button', { name: 'Voltar aos limites do plano' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Voltar Empresa X aos limites do plano?' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Voltar ao plano' }));
    await waitFor(() => expect(calls.filter((c) => c.url === '/admin/v1/tenants/t1/limites').map((c) => c.body)).toEqual([{ rpm: 1200, batch_max_bytes: 5242880 }, {}]));
    expect(await within(card).findByText('O cliente voltou aos limites do plano.')).toBeInTheDocument();
  });
});
