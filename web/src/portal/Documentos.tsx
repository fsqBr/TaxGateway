import { useState, type FormEvent } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Download, Eye } from 'lucide-react';
import { Alert, Badge, Card, Field, Loading, PageHeader, StatusBadge, Table, useAction, useLoad } from '../components/ui';
import { CnpjInput, Drawer } from '../components/inputs';
import { downloadFile, portal } from '../lib/api';
import { cnpj, dateTime, integer, money } from '../lib/format';

export interface DocumentoResumo {
  id: string;
  chave_acesso: string | null;
  direcao: 'saida' | 'entrada';
  modelo: string;
  status: string;
  cnpj: string;
  cnpj_contraparte: string | null;
  diferenca: number;
  credito_potencial: number;
  ocorrencias: number;
  lote_id: string | null;
  competencia: string;
  criado_em: string;
  reprocessamento: boolean;
  xml_armazenado: boolean;
}

interface Ocorrencia {
  regra: string;
  versao?: number;
  natureza: string;
  severidade: string;
  numero_item?: number;
  campo_xml?: string;
  valor_declarado?: string;
  valor_referencia?: string;
  mensagem: string;
  fundamentacao: string;
  impacto_financeiro_estimado?: number;
}

interface ItemDeclarado {
  numero: number;
  ncm: string;
  cst: string;
  c_class_trib: string;
  vbc?: number;
  pcbs?: number; vcbs?: number;
  pibsuf?: number; vibsuf?: number;
  pibsmun?: number; vibsmun?: number;
  vis?: number;
}

interface Detalhe {
  documento: DocumentoResumo;
  itens_declarados: ItemDeclarado[] | null;
  data_emissao?: string;
  auditoria: {
    status: string;
    regras_versao: string;
    param_snapshot_id: number;
    rfb_engine_version?: { bundle?: string };
    resumo_financeiro: { tributo_declarado: { cbs: number; ibs: number; is: number }; tributo_recalculado?: { cbs: number; ibs: number; is: number }; diferenca: number; credito_potencial?: number; credito_status?: string };
    ocorrencias: Ocorrencia[];
    avisos: { codigo: string; mensagem: string }[];
    aviso_legal: string;
  } | null;
}

const STATUS = ['CONFORME', 'DIVERGENTE', 'INDICIO', 'SCHEMA_ERROR', 'ASSINATURA_INVALIDA', 'INDETERMINADO'];

function isoDaysAgo(days: number) {
  const d = new Date(Date.now() - days * 86_400_000);
  return d.toISOString().slice(0, 10);
}

/**
 * Documentos auditados (D-J01; revisão UX C1): antes, quem não tinha webhook nunca via o resultado do DF-e nem
 * consultava uma auditoria antiga. Filtros, "carregar mais" por cursor, detalhe com a comparação por item e CSV.
 */
export function Documentos() {
  // Filtros também pela URL (ex.: Lotes → /portal/documentos?lote=...), para links e favoritos.
  const [params] = useSearchParams();
  const [filtros, setFiltros] = useState({
    de: params.get('de') ?? isoDaysAgo(params.get('lote') ? 90 : 30), ate: params.get('ate') ?? isoDaysAgo(0), status: params.get('status') ?? '',
    direcao: params.get('direcao') ?? '', cnpj: params.get('cnpj') ?? '', chave: params.get('chave') ?? '', lote: params.get('lote') ?? '',
  });
  const [aplicados, setAplicados] = useState(filtros);
  const [extra, setExtra] = useState<DocumentoResumo[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [aberto, setAberto] = useState<string>();
  const more = useAction();
  const exportar = useAction();
  const query = { ...aplicados, chave: aplicados.chave.replace(/\D/g, '') || undefined, cnpj: aplicados.cnpj || undefined, lote: aplicados.lote || undefined };
  const list = useLoad(async () => {
    const r = await portal<{ documentos: DocumentoResumo[]; proximo_cursor: string | null }>('/v1/auditoria/documentos', { query });
    setExtra([]);
    setCursor(r.proximo_cursor);
    return r;
  }, [JSON.stringify(aplicados)]);
  const resumo = useLoad(() => portal<{ total: number; por_status: Record<string, number>; diferenca_total: number; credito_potencial_total: number }>('/v1/auditoria/resumo', { query }), [JSON.stringify(aplicados)]);
  const docs = [...(list.data?.documentos ?? []), ...extra];

  const aplicar = (e: FormEvent) => { e.preventDefault(); setAplicados(filtros); };
  const carregarMais = async () => {
    if (!cursor) return;
    const r = await more.run(() => portal<{ documentos: DocumentoResumo[]; proximo_cursor: string | null }>('/v1/auditoria/documentos', { query: { ...query, cursor } }));
    if (r) { setExtra((x) => [...x, ...r.documentos]); setCursor(r.proximo_cursor); }
  };
  const baixar = (nivel: 'documento' | 'ocorrencia') => exportar.run(() =>
    downloadFile('/v1/auditoria/documentos/exportar', `auditorias-${aplicados.de}-${aplicados.ate}${nivel === 'ocorrencia' ? '-ocorrencias' : ''}.csv`,
      { ...query, nivel } as Record<string, string | undefined>));

  return (
    <>
      <PageHeader title="Documentos auditados" description="Tudo o que foi auditado — pelo portal, pela API, em lote ou capturado do DF-e — com a comparação por item e o fundamento de cada achado.">
        <button className="secondary" disabled={exportar.busy} onClick={() => void baixar('documento')}><Download size={15} aria-hidden="true" /> CSV de documentos</button>
        <button className="secondary" disabled={exportar.busy} onClick={() => void baixar('ocorrencia')}><Download size={15} aria-hidden="true" /> CSV de ocorrências</button>
      </PageHeader>
      <Alert>{exportar.error}</Alert>
      <Card>
        <form className="filters" onSubmit={aplicar} aria-label="Filtros">
          <Field label="De"><input type="date" value={filtros.de} onChange={(e) => setFiltros({ ...filtros, de: e.target.value })} /></Field>
          <Field label="Até"><input type="date" value={filtros.ate} onChange={(e) => setFiltros({ ...filtros, ate: e.target.value })} /></Field>
          <Field label="Situação">
            <select value={filtros.status} onChange={(e) => setFiltros({ ...filtros, status: e.target.value })}>
              <option value="">Todas</option>
              <option value="DIVERGENTE,INDICIO">Com achados (divergente ou indício)</option>
              {STATUS.map((s) => <option key={s} value={s}>{s}</option>)}
            </select>
          </Field>
          <Field label="Direção">
            <select value={filtros.direcao} onChange={(e) => setFiltros({ ...filtros, direcao: e.target.value })}>
              <option value="">Saída e entrada</option>
              <option value="saida">Saída (emitidas)</option>
              <option value="entrada">Entrada (recebidas)</option>
            </select>
          </Field>
          <Field label="CNPJ (emitente ou contraparte)"><CnpjInput value={filtros.cnpj} onChange={(v) => setFiltros({ ...filtros, cnpj: v })} /></Field>
          <Field label="Chave de acesso"><input value={filtros.chave} inputMode="numeric" maxLength={54} onChange={(e) => setFiltros({ ...filtros, chave: e.target.value })} placeholder="44 dígitos" /></Field>
          {filtros.lote && <div className="field"><span className="field-label">Lote</span><span className="row"><code className="small">{filtros.lote.slice(0, 8)}…</code><button type="button" className="ghost small" onClick={() => setFiltros({ ...filtros, lote: '' })}>remover</button></span></div>}
          <div className="filters-actions"><button type="submit">Filtrar</button></div>
        </form>
      </Card>
      {resumo.data && (
        <div className="grid" style={{ margin: '16px 0' }}>
          <div className="card stat"><span className="muted small">Documentos no período</span><span className="stat-value">{integer(resumo.data.total)}</span></div>
          <div className="card stat"><span className="muted small">Com divergência ou indício</span><span className="stat-value">{integer((resumo.data.por_status.DIVERGENTE ?? 0) + (resumo.data.por_status.INDICIO ?? 0))}</span></div>
          <div className="card stat"><span className="muted small">Diferença apurada</span><span className="stat-value">{money(resumo.data.diferenca_total)}</span></div>
          <div className="card stat"><span className="muted small">Crédito potencial (entradas)</span><span className="stat-value">{money(resumo.data.credito_potencial_total)}</span></div>
        </div>
      )}
      <Card flush>
        {list.loading && <div style={{ padding: 16 }}><Loading /></div>}
        <Alert>{list.error}</Alert>
        {list.data && (
          <Table head={['Criado', 'Chave', 'Direção', 'Situação', 'Achados', 'Diferença', '']} empty={docs.length === 0}
            emptyText="Nenhum documento no período. Audite um XML em Auditar XML ou ajuste os filtros.">
            {docs.map((d) => (
              <tr key={d.id}>
                <td data-label="Criado">{dateTime(d.criado_em)}</td>
                <td data-label="Chave" className="mono small" title={d.chave_acesso ?? ''}>{d.chave_acesso ? `${d.chave_acesso.slice(0, 6)}…${d.chave_acesso.slice(-6)}` : '—'}<span className="sub">{cnpj(d.cnpj)}</span></td>
                <td data-label="Direção">{d.direcao === 'entrada' ? 'Entrada' : 'Saída'}{d.lote_id && <span className="sub">lote</span>}</td>
                <td data-label="Situação"><StatusBadge status={d.status} />{d.reprocessamento && <Badge>reprocessado</Badge>}</td>
                <td data-label="Achados" className="num">{integer(d.ocorrencias)}</td>
                <td data-label="Diferença" className="num">{money(d.diferenca)}</td>
                <td><button className="link" onClick={() => setAberto(d.id)} aria-label={`Ver detalhe do documento ${d.chave_acesso ?? d.id}`}><Eye size={15} aria-hidden="true" /> Detalhe</button></td>
              </tr>
            ))}
          </Table>
        )}
        {cursor && <div style={{ padding: 16 }}><button className="secondary" disabled={more.busy} onClick={() => void carregarMais()}>{more.busy ? 'Carregando…' : 'Carregar mais'}</button></div>}
        <Alert>{more.error}</Alert>
      </Card>
      {aberto && <DocumentoDrawer id={aberto} onClose={() => setAberto(undefined)} />}
    </>
  );
}

export function DocumentoDrawer({ id, onClose }: { id: string; onClose: () => void }) {
  const d = useLoad(() => portal<Detalhe>(`/v1/auditoria/documentos/${id}`), [id]);
  return (
    <Drawer title="Detalhe do documento" onClose={onClose} wide>
      {d.loading && <Loading />}
      <Alert>{d.error}</Alert>
      {d.data && <DocumentoDetalhe data={d.data} />}
    </Drawer>
  );
}

const pct = (v?: number) => (v === undefined || v === null ? '—' : `${v.toLocaleString('pt-BR', { maximumFractionDigits: 4 })}%`);

/** Comparação por item (UX C2): o que o XML declarou e, campo a campo, o que a regra esperava — com o fundamento. */
export function DocumentoDetalhe({ data }: { data: Detalhe }) {
  const a = data.auditoria;
  const doc = data.documento;
  const porItem = new Map<number | 'doc', Ocorrencia[]>();
  for (const o of a?.ocorrencias ?? []) {
    const k = o.numero_item ?? 'doc';
    porItem.set(k, [...(porItem.get(k) ?? []), o]);
  }
  return (
    <div className="stack">
      <div className="spread">
        <div>
          <div className="mono small">{doc.chave_acesso}</div>
          <div className="muted small">{doc.direcao === 'entrada' ? 'Entrada' : 'Saída'} · modelo {doc.modelo === 'NFCE65' ? '65 (NFC-e)' : '55 (NF-e)'} · emitida {data.data_emissao ? dateTime(data.data_emissao) : '—'} · auditada {dateTime(doc.criado_em)}</div>
        </div>
        <StatusBadge status={doc.status} />
      </div>
      {a && (
        <Table head={['', 'CBS', 'IBS', 'IS']}>
          <tr><td>Declarado</td><td className="num">{money(a.resumo_financeiro.tributo_declarado.cbs)}</td><td className="num">{money(a.resumo_financeiro.tributo_declarado.ibs)}</td><td className="num">{money(a.resumo_financeiro.tributo_declarado.is)}</td></tr>
          {a.resumo_financeiro.tributo_recalculado && (
            <tr><td>Recalculado (motor oficial)</td><td className="num">{money(a.resumo_financeiro.tributo_recalculado.cbs)}</td><td className="num">{money(a.resumo_financeiro.tributo_recalculado.ibs)}</td><td className="num">{money(a.resumo_financeiro.tributo_recalculado.is)}</td></tr>
          )}
          <tr><td><strong>Diferença</strong></td><td colSpan={3} className="num"><strong>{money(a.resumo_financeiro.diferenca)}</strong></td></tr>
        </Table>
      )}
      {(porItem.get('doc')?.length ?? 0) > 0 && (
        <Card title="Achados do documento"><Achados lista={porItem.get('doc')!} /></Card>
      )}
      <h3>Itens</h3>
      {(data.itens_declarados ?? []).length === 0 && <p className="muted small">Sem itens declarados (documento recusado antes do recálculo).</p>}
      {(data.itens_declarados ?? []).map((it) => {
        const achados = porItem.get(it.numero) ?? [];
        return (
          <section key={it.numero} className={`item-card${achados.length ? ' has-findings' : ''}`} aria-label={`Item ${it.numero}`}>
            <div className="spread">
              <strong>Item {it.numero}</strong>
              {achados.length ? <Badge tone="warn">{achados.length} achado(s)</Badge> : <Badge tone="ok">sem achados</Badge>}
            </div>
            <dl className="kv">
              <div><dt>NCM</dt><dd className="mono">{it.ncm || '—'}</dd></div>
              <div><dt>CST / cClassTrib</dt><dd className="mono">{it.cst} / {it.c_class_trib}</dd></div>
              <div><dt>Base de cálculo</dt><dd>{money(it.vbc)}</dd></div>
              <div><dt>CBS</dt><dd>{pct(it.pcbs)} · {money(it.vcbs)}</dd></div>
              <div><dt>IBS UF</dt><dd>{pct(it.pibsuf)} · {money(it.vibsuf)}</dd></div>
              <div><dt>IBS município</dt><dd>{pct(it.pibsmun)} · {money(it.vibsmun)}</dd></div>
              {it.vis !== undefined && <div><dt>IS</dt><dd>{money(it.vis)}</dd></div>}
            </dl>
            {achados.length > 0 && <Achados lista={achados} />}
          </section>
        );
      })}
      {a && a.avisos.length > 0 && <Alert kind="info">{a.avisos.map((x) => <div key={x.codigo}><code>{x.codigo}</code> {x.mensagem}</div>)}</Alert>}
      {a && <p className="muted small">Regras {a.regras_versao} · bundle {a.rfb_engine_version?.bundle ?? '—'} · parâmetros nº {a.param_snapshot_id}. {a.aviso_legal}</p>}
    </div>
  );
}

function Achados({ lista }: { lista: Ocorrencia[] }) {
  return (
    <Table head={['Regra', 'Campo', 'Declarado', 'Referência', 'Fundamento']}>
      {lista.map((o, i) => (
        <tr key={`${o.regra}-${i}`}>
          <td data-label="Regra"><code>{o.regra}</code> <Badge tone={o.natureza === 'DETERMINISTICA' ? 'bad' : o.natureza === 'INDICIO' ? 'warn' : 'info'}>{o.natureza === 'DETERMINISTICA' ? 'Divergência' : o.natureza === 'INDICIO' ? 'Indício' : 'Informativo'}</Badge><span className="sub">{o.mensagem}</span></td>
          <td data-label="Campo" className="mono small">{o.campo_xml ?? '—'}</td>
          <td data-label="Declarado" className="num">{o.valor_declarado ?? '—'}</td>
          <td data-label="Referência" className="num">{o.valor_referencia ?? '—'}</td>
          <td data-label="Fundamento" className="small">{o.fundamentacao}{o.impacto_financeiro_estimado ? <span className="sub">Impacto estimado {money(o.impacto_financeiro_estimado)}</span> : null}</td>
        </tr>
      ))}
    </Table>
  );
}
