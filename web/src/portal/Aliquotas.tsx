import { useState, type FormEvent } from 'react';
import { useSession } from '../auth/session';
import { AtualizacoesFiscais } from '../components/AtualizacoesFiscais';
import { ConfirmButton } from '../components/ConfirmDialog';
import { MunicipioPicker, type Municipio } from '../components/MunicipioPicker';
import { Alert, Badge, Card, Field, Loading, PageHeader, Table, useAction, useLoad } from '../components/ui';
import { downloadFile, portal } from '../lib/api';
import { dateTime, fileToBase64 } from '../lib/format';

export interface ConsultaAliquotas {
  municipio: { codigo: number; nome: string; uf: string };
  data: string;
  tributos: { tributo: string; nome: string; aliquota: number | null; origem?: string; forma_aplicacao?: string | null; referencia?: number;
    ato_legal?: string; fonte?: string; provisoria?: boolean; vigencia_inicio?: string; vigencia_fim?: string | null; mensagem?: string }[];
  total: number | null;
  observacao: string;
}

interface Excecao {
  id: string; cnpj: string | null; tributo: string; ente_codigo: string | null; produto: string | null; aliquota: number;
  vigencia_inicio: string; vigencia_fim: string | null; motivo: string; processo: string; documento: string; situacao: string;
  proposta_por: string | null; decidida_por: string | null; decidida_em: string | null; criada_em: string;
}

const MOTIVOS: Record<string, string> = {
  LIMINAR: 'Liminar', DECISAO_JUDICIAL: 'Decisão judicial', REGIME_ESPECIAL: 'Regime especial', BENEFICIO_FISCAL: 'Benefício fiscal', OUTRO: 'Outro',
};
const TRIBUTOS: Record<string, string> = { CBS: 'CBS', IBS_UF: 'IBS estadual', IBS_MUN: 'IBS municipal', IS: 'Imposto Seletivo' };
const SITUACAO: Record<string, { label: string; tone: 'ok' | 'warn' | 'bad' | 'neutral' }> = {
  PROPOSTA: { label: 'aguardando aprovação', tone: 'warn' }, ATIVA: { label: 'ativa', tone: 'ok' },
  RECUSADA: { label: 'recusada', tone: 'neutral' }, REVOGADA: { label: 'revogada', tone: 'neutral' },
};

const FORMA: Record<string, string> = { SUBSTITUICAO: 'substitui a referência', ACRESCIMO: 'soma à referência', DECRESCIMO: 'reduz a referência' };

/** "Própria do ente (soma à referência de 0,05%)" — só o que existir. */
const origemPropria = (forma?: string | null, referencia?: number) => {
  const partes = [forma ? FORMA[forma] ?? forma.toLowerCase() : null, referencia !== undefined && referencia !== null ? `referência ${pct(referencia)}` : null].filter(Boolean);
  return partes.length ? `Própria do ente (${partes.join('; ')})` : 'Própria do ente';
};

const pct = (v: number | null | undefined) => (v === null || v === undefined ? '—' : `${v.toLocaleString('pt-BR', { maximumFractionDigits: 4 })}%`);
const hoje = () => new Date(Date.now() - 3 * 3600_000).toISOString().slice(0, 10);

/** Tabela das alíquotas de um município numa data (mesma resolução do cálculo), com origem e ato legal (D-R21). */
export function TabelaAliquotas({ c }: { c: ConsultaAliquotas }) {
  return (
    <>
      <Table head={['Tributo', 'Alíquota', 'Origem', 'Ato legal', 'Vigência']}>
        {c.tributos.map((t) => (
          <tr key={t.tributo}>
            <td data-label="Tributo">{t.nome}</td>
            <td data-label="Alíquota" className="num">{t.aliquota === null ? <span className="muted">não publicada</span> : pct(t.aliquota)}{t.provisoria && <> <Badge tone="warn">provisória</Badge></>}</td>
            <td data-label="Origem">{t.origem === 'PROPRIA_DO_ENTE' ? origemPropria(t.forma_aplicacao, t.referencia) : t.origem ? 'Referência nacional' : t.mensagem}</td>
            <td data-label="Ato legal" className="small">{t.ato_legal ?? '—'}</td>
            <td data-label="Vigência" className="small">{t.vigencia_inicio ? `${t.vigencia_inicio.split('-').reverse().join('/')}${t.vigencia_fim ? ` a ${t.vigencia_fim.split('-').reverse().join('/')}` : ' em diante'}` : '—'}</td>
          </tr>
        ))}
        <tr><td><strong>Total</strong></td><td className="num"><strong>{pct(c.total)}</strong></td><td colSpan={3} /></tr>
      </Table>
      <p className="muted small">{c.observacao}</p>
    </>
  );
}

/**
 * Alíquotas e exceções (D-R20/D-R21): consulta por município e data e as exceções do contribuinte (liminar, regime
 * especial...), com documento e aprovação de outra pessoa. O cálculo oficial nunca muda: a exceção gera um cálculo
 * ajustado ao lado.
 */
export function Aliquotas() {
  const { can } = useSession();
  const [municipio, setMunicipio] = useState<Municipio | undefined>({ codigo: 3550308, nome: 'São Paulo', uf: 'SP' });
  const [data, setData] = useState(hoje());
  const consulta = useAction();
  const [res, setRes] = useState<ConsultaAliquotas>();
  const lista = useLoad(() => portal<{ excecoes: Excecao[] }>('/v1/excecoes-aliquota'));
  const acao = useAction();
  const [form, setForm] = useState({ tributo: 'CBS', aliquota: '', inicio: '2027-01-01', fim: '', motivo: 'LIMINAR', processo: '', cnpj: '', ente: '', produto: '' });
  const [pdf, setPdf] = useState<File>();
  const [ok, setOk] = useState<string>();

  const consultar = async (e?: FormEvent) => {
    e?.preventDefault();
    if (!municipio) return;
    setRes(await consulta.run(() => portal<ConsultaAliquotas>('/v1/publico/aliquotas', { query: { municipio: municipio.codigo, data } })));
  };

  const propor = async (e: FormEvent) => {
    e.preventDefault();
    setOk(undefined);
    if (!pdf) return;
    const r = await acao.run(async () => portal<{ mensagem: string }>('/v1/excecoes-aliquota', {
      method: 'POST',
      body: {
        tributo: form.tributo, aliquota: Number(form.aliquota.replace(',', '.')), vigencia_inicio: form.inicio, vigencia_fim: form.fim || undefined,
        motivo: form.motivo, processo: form.processo, cnpj: form.cnpj || undefined, ente_codigo: form.ente || undefined, produto: form.produto || undefined,
        documento_nome: pdf.name, documento_base64: await fileToBase64(pdf),
      },
    }));
    if (r) {
      setOk(r.mensagem);
      setForm({ ...form, aliquota: '', processo: '' });
      setPdf(undefined);
      await lista.reload();
    }
  };

  const decidir = async (id: string, a: 'aprovar' | 'recusar' | 'revogar') => {
    if (await acao.run(() => portal(`/v1/excecoes-aliquota/${id}/${a}`, { method: 'POST' })) !== undefined) await lista.reload();
  };

  return (
    <>
      <PageHeader title="Alíquotas e exceções" description="Quais alíquotas valem para cada município e data, de onde vêm (ato legal) e as exceções da sua empresa." />
      <Card title="Consultar alíquotas">
        <form className="row" style={{ gap: 12, alignItems: 'flex-end', flexWrap: 'wrap' }} onSubmit={(e) => void consultar(e)}>
          <MunicipioPicker value={municipio} onChange={setMunicipio} />
          <Field label="Data"><input type="date" min="2026-01-01" value={data} onChange={(e) => setData(e.target.value)} /></Field>
          <button type="submit" disabled={consulta.busy || !municipio}>Consultar</button>
        </form>
        <Alert>{consulta.error}</Alert>
        {res && <TabelaAliquotas c={res} />}
      </Card>

      <Card title="Atualizações fiscais" description="O que a Receita publicou e o TaxGateway já aplicou, com a data em que passa a valer.">
        <AtualizacoesFiscais limite={5} />
      </Card>

      <Card title="Exceções do contribuinte" description="Liminar, decisão judicial, regime especial ou benefício próprio da empresa. Não muda o cálculo oficial: a simulação mostra o cálculo ajustado ao lado e a auditoria avisa quando a exceção se aplica.">
        {lista.loading && <Loading />}
        <Alert>{lista.error ?? acao.error}</Alert>
        <Table head={['Tributo', 'Alíquota', 'Motivo', 'Vigência', 'Situação', '']} empty={!lista.data?.excecoes.length} emptyText="Nenhuma exceção cadastrada.">
          {lista.data?.excecoes.map((x) => (
            <tr key={x.id}>
              <td data-label="Tributo">{TRIBUTOS[x.tributo] ?? x.tributo}{x.ente_codigo && <span className="sub">ente {x.ente_codigo}</span>}{x.produto && <span className="sub">{x.produto}</span>}{x.cnpj && <span className="sub">CNPJ {x.cnpj}</span>}</td>
              <td data-label="Alíquota" className="num">{pct(x.aliquota)}</td>
              <td data-label="Motivo">{MOTIVOS[x.motivo] ?? x.motivo}<span className="sub">{x.processo}</span></td>
              <td data-label="Vigência" className="small">{x.vigencia_inicio.split('-').reverse().join('/')}{x.vigencia_fim ? ` a ${x.vigencia_fim.split('-').reverse().join('/')}` : ' em diante'}</td>
              <td data-label="Situação"><Badge tone={SITUACAO[x.situacao]?.tone ?? 'neutral'}>{SITUACAO[x.situacao]?.label ?? x.situacao}</Badge>
                <span className="sub">por {x.proposta_por ?? '—'}{x.decidida_por && ` · decidida por ${x.decidida_por} em ${dateTime(x.decidida_em)}`}</span></td>
              <td>
                <div className="row">
                  <button type="button" className="ghost small" onClick={() => void acao.run(() => downloadFile(`/v1/excecoes-aliquota/${x.id}/documento`, x.documento))}>Documento</button>
                  {can('cnpjs:manage') && x.situacao === 'PROPOSTA' && (
                    <>
                      <ConfirmButton label="Aprovar" tone="secondary" title="Aprovar a exceção?" confirmLabel="Aprovar"
                        impact="Passa a gerar o cálculo ajustado nas simulações e o aviso nas auditorias. Quem propôs não pode aprovar." onConfirm={() => decidir(x.id, 'aprovar')} />
                      <ConfirmButton label="Recusar" tone="secondary" title="Recusar a exceção?" confirmLabel="Recusar" impact="A proposta fica registrada como recusada." onConfirm={() => decidir(x.id, 'recusar')} />
                    </>
                  )}
                  {can('cnpjs:manage') && x.situacao === 'ATIVA' && (
                    <ConfirmButton label="Revogar" tone="secondary" title="Revogar a exceção?" confirmLabel="Revogar" impact="O cálculo ajustado deixa de aparecer a partir de agora." onConfirm={() => decidir(x.id, 'revogar')} />
                  )}
                </div>
              </td>
            </tr>
          ))}
        </Table>
        {can('cnpjs:manage') && (
          <form className="stack" style={{ marginTop: 16 }} onSubmit={(e) => void propor(e)}>
            <h3>Cadastrar exceção</h3>
            <div className="form-grid">
              <Field label="Tributo">
                <select value={form.tributo} onChange={(e) => setForm({ ...form, tributo: e.target.value })}>
                  {Object.entries(TRIBUTOS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
                </select>
              </Field>
              <Field label="Alíquota (%)"><input inputMode="decimal" required value={form.aliquota} onChange={(e) => setForm({ ...form, aliquota: e.target.value })} /></Field>
              <Field label="Motivo">
                <select value={form.motivo} onChange={(e) => setForm({ ...form, motivo: e.target.value })}>
                  {Object.entries(MOTIVOS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
                </select>
              </Field>
              <Field label="Processo ou ato" hint="Número do processo, do ato concessivo ou do termo."><input required value={form.processo} onChange={(e) => setForm({ ...form, processo: e.target.value })} /></Field>
              <Field label="Vale a partir de"><input type="date" required value={form.inicio} onChange={(e) => setForm({ ...form, inicio: e.target.value })} /></Field>
              <Field label="Vale até (opcional)"><input type="date" value={form.fim} onChange={(e) => setForm({ ...form, fim: e.target.value })} /></Field>
              <Field label="CNPJ (opcional)" hint="Vazio = todos os CNPJs da empresa."><input value={form.cnpj} onChange={(e) => setForm({ ...form, cnpj: e.target.value })} /></Field>
              {(form.tributo === 'IBS_UF' || form.tributo === 'IBS_MUN') && (
                <Field label={form.tributo === 'IBS_UF' ? 'Código da UF (opcional)' : 'Código do município (opcional)'}><input inputMode="numeric" value={form.ente} onChange={(e) => setForm({ ...form, ente: e.target.value.replace(/\D/g, '') })} /></Field>
              )}
              <Field label="Produto (opcional)" hint="NCM:2203 (prefixo) ou CCLASS:000001."><input value={form.produto} onChange={(e) => setForm({ ...form, produto: e.target.value.toUpperCase() })} /></Field>
              <Field label="Documento (PDF)" hint="Decisão, ato ou termo do regime, até 5 MB."><input type="file" accept="application/pdf,.pdf" onChange={(e) => setPdf(e.target.files?.[0])} /></Field>
            </div>
            {ok && <Alert kind="success">{ok}</Alert>}
            <div><button type="submit" disabled={acao.busy || !pdf || !form.aliquota || !form.processo}>Enviar para aprovação</button></div>
          </form>
        )}
      </Card>
    </>
  );
}
