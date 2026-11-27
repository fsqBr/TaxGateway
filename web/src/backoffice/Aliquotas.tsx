import { useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Badge, Card, Confirm, Field, Json, Loading, PageHeader, StatusBadge, Table, useAction, useLoad } from '../components/ui';
import { Dropzone, UFS } from '../components/inputs';
import { MunicipioPicker, type Municipio } from '../components/MunicipioPicker';
import { backoffice } from '../lib/api';
import { dateTime } from '../lib/format';
import {
  aceitaPropria, coerente, CSV_CABECALHO, dataBr, FONTE_LABEL, FONTES, FORMA_LABEL, FORMAS, lerArquivo, lerCsv, type Linha, linhaVazia, MAX_LINHAS,
  nomeEnte, paraApi, pct, problemas, TRIBUTO_LABEL, TRIBUTOS, UF_IBGE,
} from './aliquotasCsv';

interface Rate {
  id: string; tributo: string; tipo: string; esfera: string; ente_codigo: string | null; forma_aplicacao: string | null; aliquota: number;
  valid_from: string; valid_to: string | null; ato_legal: string; fonte: string; provisoria: boolean; change_id: string | null; produto: string | null;
}
interface Change { id: string; dominio: string; status: string; proposto_por: string; aprovado_por: string | null; justificativa: string; vigente_em: string; criado_em: string; payload: unknown }
interface Vigente { id: string; aliquota: number; valid_from: string; valid_to: string | null; ato_legal: string; provisoria: boolean }
interface PreviewItem { tributo: string; ente_codigo: string | null; produto: string | null; aliquota_nova: number; valid_from: string; vigentes: Vigente[] }
interface Preview { linhas: number; erros: string[]; conflitos_sem_substituicao: number; itens: PreviewItem[] }
interface Cobertura { tributos: { tributo: string; dias: number | null; coberto_ate: string | null; alerta: boolean }[] }

const POR_PAGINA = 50;
const MOSTRAR_LINHAS = 200;
const STATUS_OPCOES: [string, string][] = [['PROPOSED', 'Aguardando aprovação'], ['APPLIED', 'Aplicadas'], ['REJECTED', 'Recusadas'], ['', 'Todas']];

/**
 * Alíquotas (catálogo de referência e próprias de UF/município). Toda mudança é uma proposta com uma ou várias linhas
 * (formulário ou CSV), conferida no servidor antes de propor e aprovada por outra pessoa.
 */
export function Aliquotas() {
  const rates = useLoad(() => backoffice<Rate[]>('/admin/v1/params/rates'));
  const cobertura = useLoad(() => backoffice<Cobertura>('/admin/v1/params/cobertura').catch(() => undefined));
  const [status, setStatus] = useState('PROPOSED');
  const changes = useLoad(() => backoffice<Change[]>('/admin/v1/params/changes', { query: { status } }), [status]);
  const [msg, setMsg] = useState<string>();
  const { error, run } = useAction();

  const decidir = async (id: string, acao: 'approve' | 'reject', motivo?: string) => {
    setMsg(undefined);
    const r = await run(() => backoffice(`/admin/v1/params/changes/${id}/${acao}`, { method: 'POST', body: acao === 'reject' ? { motivo } : undefined }));
    if (r === undefined) return;
    setMsg(acao === 'approve' ? 'Mudança aplicada; passa a valer em todas as instâncias em até 5 segundos.' : 'Proposta recusada. O motivo fica registrado na trilha.');
    void changes.reload();
    if (acao === 'approve') { void rates.reload(); void cobertura.reload(); }
  };

  const alertas = (cobertura.data?.tributos ?? []).filter((t) => t.alerta);

  return (
    <>
      <PageHeader title="Alíquotas" description="Alíquotas de referência (União) e próprias de cada UF e município. Toda mudança é proposta por uma pessoa, citando o ato legal, e aprovada por outra." />
      {alertas.map((t) => (
        <Alert key={t.tributo} kind="warning">
          {t.coberto_ate
            ? <>{TRIBUTO_LABEL[t.tributo] ?? t.tributo} publicada só até {dataBr(t.coberto_ate)} — carregue o ato (Senado/CG-IBS).</>
            : <>{TRIBUTO_LABEL[t.tributo] ?? t.tributo} sem alíquota de referência publicada — carregue o ato (Senado/CG-IBS).</>}
        </Alert>
      ))}
      <Alert>{error}</Alert>
      <Alert kind="success">{msg}</Alert>
      <Vigentes rates={rates.data} loading={rates.loading} error={rates.error} />
      <div style={{ height: 16 }} />
      <NovaProposta onProposta={(id) => { setMsg(`Proposta ${id} criada. Outra pessoa precisa aprová-la.`); void changes.reload(); }} />
      <div style={{ height: 16 }} />
      <Card
        title="Propostas"
        actions={
          <select aria-label="Situação" value={status} onChange={(e) => setStatus(e.target.value)} style={{ width: 'auto' }}>
            {STATUS_OPCOES.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
          </select>
        }
      >
        {changes.loading && <Loading />}
        <Alert>{changes.error}</Alert>
        {changes.data && (
          <Table head={['Criada', 'Situação', 'Proposta por', 'Decidida por', 'Justificativa', '']} empty={changes.data.length === 0}>
            {changes.data.map((c) => {
              const linhas = (c.payload as { linhas?: unknown[] } | null)?.linhas;
              return (
                <tr key={c.id}>
                  <td data-label="Criada" className="nowrap">{dateTime(c.criado_em)}</td>
                  <td data-label="Situação"><StatusBadge status={c.status} /></td>
                  <td data-label="Proposta por">{c.proposto_por}</td>
                  <td data-label="Decidida por">{c.aprovado_por ?? '—'}</td>
                  <td data-label="Justificativa">{c.justificativa}
                    <details><summary className="muted small">{Array.isArray(linhas) ? `${linhas.length} linha(s)` : 'conteúdo'}</summary><Json value={c.payload} /></details>
                  </td>
                  <td className="actions">
                    {c.status === 'PROPOSED' && (
                      <div className="row">
                        <Confirm tone="secondary" label="Aprovar e aplicar" impact="As linhas passam a valer no cálculo e na auditoria a partir da vigência de cada uma. Quem propôs não pode aprovar." onConfirm={() => void decidir(c.id, 'approve')} />
                        <RecusarButton onConfirm={(motivo) => decidir(c.id, 'reject', motivo)} />
                      </div>
                    )}
                  </td>
                </tr>
              );
            })}
          </Table>
        )}
      </Card>
    </>
  );
}

function Vigentes({ rates, loading, error }: { rates?: Rate[]; loading: boolean; error?: string }) {
  const [tributo, setTributo] = useState('');
  const [ente, setEnte] = useState('');
  const [soProvisorias, setSoProvisorias] = useState(false);
  const [pagina, setPagina] = useState(1);

  const filtradas = useMemo(() => {
    const e = ente.trim();
    const codigo = UF_IBGE[e.toUpperCase()] ?? e.replace(/\D/g, '');
    return (rates ?? []).filter((r) =>
      (!tributo || r.tributo === tributo)
      && (!e || (e.toLowerCase() === 'uniao' || e.toLowerCase() === 'união' ? !r.ente_codigo : !!codigo && (r.ente_codigo ?? '').startsWith(codigo)))
      && (!soProvisorias || r.provisoria));
  }, [rates, tributo, ente, soProvisorias]);
  useEffect(() => setPagina(1), [tributo, ente, soProvisorias]);
  const paginas = Math.max(1, Math.ceil(filtradas.length / POR_PAGINA));
  const visiveis = filtradas.slice((pagina - 1) * POR_PAGINA, pagina * POR_PAGINA);

  return (
    <Card
      title="Alíquotas vigentes"
      description="UF pela sigla (SP mostra o estado e os municípios dele) ou código IBGE; “União” mostra só as de referência."
      actions={
        <>
          <select aria-label="Filtrar por tributo" value={tributo} onChange={(e) => setTributo(e.target.value)} style={{ width: 'auto' }}>
            <option value="">Todos os tributos</option>
            {TRIBUTOS.map((t) => <option key={t} value={t}>{TRIBUTO_LABEL[t]}</option>)}
          </select>
          <input aria-label="Filtrar por ente" placeholder="SP, 3550308 ou União" value={ente} onChange={(e) => setEnte(e.target.value)} style={{ width: 180 }} />
          <label className="checkbox small"><input type="checkbox" checked={soProvisorias} onChange={(e) => setSoProvisorias(e.target.checked)} /> Só provisórias</label>
        </>
      }
    >
      {loading && <Loading />}
      <Alert>{error}</Alert>
      {rates && (
        <Table head={['Tributo', 'Ente', 'Produto', 'Aplicação', 'Alíquota', 'Vigência', 'Fonte e ato legal']} empty={filtradas.length === 0}
          emptyText={rates.length === 0 ? 'Nenhuma alíquota cadastrada.' : 'Nenhuma alíquota com estes filtros.'}>
          {visiveis.map((r) => (
            <tr key={r.id}>
              <td data-label="Tributo"><strong>{TRIBUTO_LABEL[r.tributo] ?? r.tributo}</strong>{r.provisoria && <> <Badge tone="warn">provisória</Badge></>}</td>
              <td data-label="Ente" className="nowrap">{nomeEnte(r.esfera, r.ente_codigo)}</td>
              <td data-label="Produto" className="mono">{r.produto ?? '—'}</td>
              <td data-label="Aplicação">{r.tipo === 'PROPRIA_ENTE' ? FORMA_LABEL[r.forma_aplicacao ?? ''] ?? r.forma_aplicacao ?? 'própria' : 'Referência'}</td>
              <td data-label="Alíquota" className="num">{pct(r.aliquota)}</td>
              <td data-label="Vigência" className="nowrap">{dataBr(r.valid_from)} → {r.valid_to ? dataBr(r.valid_to) : 'sem fim'}</td>
              <td data-label="Fonte e ato legal">{FONTE_LABEL[r.fonte] ?? r.fonte}<span className="sub">{r.ato_legal}</span></td>
            </tr>
          ))}
        </Table>
      )}
      {filtradas.length > POR_PAGINA && (
        <nav className="pager" aria-label="Páginas">
          <button className="secondary" disabled={pagina <= 1} onClick={() => setPagina(pagina - 1)}>Anterior</button>
          <span className="muted small">Página {pagina} de {paginas} · {filtradas.length} alíquotas</span>
          <button className="secondary" disabled={pagina >= paginas} onClick={() => setPagina(pagina + 1)}>Próxima</button>
        </nav>
      )}
    </Card>
  );
}

function NovaProposta({ onProposta }: { onProposta: (id: string) => void }) {
  const [justificativa, setJustificativa] = useState('');
  const [linhas, setLinhas] = useState<Linha[]>([]);
  const [versao, setVersao] = useState(0);
  const [arquivo, setArquivo] = useState<File>();
  const [importacao, setImportacao] = useState<{ ok: string; erros: string[] }>();
  const [preview, setPreview] = useState<{ versao: number; dados: Preview }>();
  const [substitui, setSubstitui] = useState<string[]>([]);
  const { busy, error, run, setError } = useAction();

  const mudar = (novas: Linha[]) => { setLinhas(novas); setVersao((v) => v + 1); };
  const corpo = () => ({ justificativa: justificativa.trim(), linhas: linhas.map(paraApi), substitui });

  const importar = async (f?: File) => {
    setArquivo(f);
    setImportacao(undefined);
    if (!f) return;
    const r = lerCsv(await lerArquivo(f));
    const total = linhas.length + r.linhas.length;
    if (total > MAX_LINHAS) { setImportacao({ ok: '', erros: [`A proposta passaria de ${MAX_LINHAS} linhas (${total}). Divida em mais de uma proposta.`] }); return; }
    if (r.linhas.length) mudar([...linhas, ...r.linhas]);
    setImportacao({ ok: r.linhas.length ? `${r.linhas.length} linha(s) importada(s) de ${f.name}.` : '', erros: r.erros });
  };

  const conferir = async () => {
    setError(undefined);
    if (!justificativa.trim()) { setError('Escreva a justificativa (cite o ato publicado).'); return; }
    const r = await run(() => backoffice<Preview>('/admin/v1/params/rates/changes/preview', { method: 'POST', body: corpo() }));
    if (r) {
      setPreview({ versao, dados: r });
      const existentes = new Set(r.itens.flatMap((i) => i.vigentes.map((v) => v.id)));
      setSubstitui((s) => s.filter((id) => existentes.has(id)));
    }
  };

  const propor = async () => {
    const r = await run(() => backoffice<{ change_id: string; status: string }>('/admin/v1/params/rates/changes', { method: 'POST', body: corpo() }));
    if (r) {
      onProposta(r.change_id);
      setLinhas([]); setVersao((v) => v + 1); setPreview(undefined); setSubstitui([]); setJustificativa(''); setArquivo(undefined); setImportacao(undefined);
    }
  };

  const atual = preview && preview.versao === versao ? preview.dados : undefined;
  const podePropor = !!atual && atual.erros.length === 0 && linhas.length > 0 && !!justificativa.trim();

  return (
    <Card title="Nova proposta" description="Monte uma ou várias linhas (à mão ou por planilha), confira o que está vigente e proponha. Outra pessoa aprova.">
      <div className="stack">
        <Field label="Justificativa" hint="Cite o ato publicado (DOU, resolução, lei).">
          <input value={justificativa} onChange={(e) => { setJustificativa(e.target.value); setVersao((v) => v + 1); }} maxLength={1000} />
        </Field>

        <LinhaForm onAdd={(l) => mudar([...linhas, l])} cheia={linhas.length >= MAX_LINHAS} />

        <Dropzone accept=".csv,text/csv,text/plain" label="Importar planilha (CSV)" file={arquivo} onFile={(f) => void importar(f)}
          hint={`Uma linha por alíquota, com o cabeçalho ${CSV_CABECALHO}. Separador ; ou , e vírgula decimal aceitos. As linhas são somadas às da proposta.`} />
        {importacao?.ok && <Alert kind="success">{importacao.ok}</Alert>}
        {importacao && importacao.erros.length > 0 && (
          <Alert kind="warning">
            {importacao.erros.length} linha(s) do arquivo ficaram de fora:
            <ul>{importacao.erros.slice(0, 20).map((e) => <li key={e}>{e}</li>)}</ul>
            {importacao.erros.length > 20 && <span>…e mais {importacao.erros.length - 20}.</span>}
          </Alert>
        )}

        <h3>Linhas da proposta ({linhas.length})</h3>
        <Table head={['Tributo', 'Ente', 'Produto', 'Aplicação', 'Alíquota', 'Vigência', 'Fonte e ato legal', '']} empty={linhas.length === 0} emptyText="Nenhuma linha ainda: adicione pelo formulário ou importe uma planilha.">
          {linhas.slice(0, MOSTRAR_LINHAS).map((l, i) => (
            <tr key={i}>
              <td data-label="Tributo"><strong>{TRIBUTO_LABEL[l.tributo] ?? l.tributo}</strong>{l.provisoria && <> <Badge tone="warn">provisória</Badge></>}</td>
              <td data-label="Ente" className="nowrap">{nomeEnte(l.esfera, l.ente_codigo)}</td>
              <td data-label="Produto" className="mono">{l.produto || '—'}</td>
              <td data-label="Aplicação">{l.tipo === 'PROPRIA_ENTE' ? FORMA_LABEL[l.forma_aplicacao] ?? l.forma_aplicacao : 'Referência'}</td>
              <td data-label="Alíquota" className="num">{pct(Number(l.aliquota))}</td>
              <td data-label="Vigência" className="nowrap">{dataBr(l.valid_from)} → {l.valid_to ? dataBr(l.valid_to) : 'sem fim'}</td>
              <td data-label="Fonte e ato legal">{FONTE_LABEL[l.fonte] ?? l.fonte}<span className="sub">{l.ato_legal}</span></td>
              <td className="actions"><button type="button" className="ghost" onClick={() => mudar(linhas.filter((_, j) => j !== i))} aria-label={`Remover linha ${i + 1}`}>Remover</button></td>
            </tr>
          ))}
        </Table>
        {linhas.length > MOSTRAR_LINHAS && <p className="muted small">Mostrando {MOSTRAR_LINHAS} de {linhas.length} linhas; todas vão na proposta.</p>}

        <Alert>{error}</Alert>
        <div className="row">
          <button type="button" className="secondary" disabled={busy || linhas.length === 0} onClick={() => void conferir()}>Conferir</button>
          <button type="button" disabled={busy || !podePropor} onClick={() => void propor()}>Propor</button>
          {linhas.length > 0 && <button type="button" className="ghost" disabled={busy} onClick={() => { mudar([]); setPreview(undefined); setSubstitui([]); }}>Limpar linhas</button>}
        </div>
        {!atual && linhas.length > 0 && <p className="muted small">Confira antes de propor: o servidor valida cada linha e mostra o que está vigente.</p>}
        {preview && !atual && <p className="muted small">As linhas mudaram desde a última conferência — confira de novo.</p>}
        {atual && <Conferencia dados={atual} substitui={substitui} onSubstitui={setSubstitui} />}
      </div>
    </Card>
  );
}

/** Formulário de UMA linha: mostra só os campos que valem para o tributo e o tipo escolhidos. */
function LinhaForm({ onAdd, cheia }: { onAdd: (l: Linha) => void; cheia: boolean }) {
  const [l, setL] = useState<Linha>(linhaVazia());
  const [muni, setMuni] = useState<Municipio>();
  const [prodTipo, setProdTipo] = useState('NCM');
  const [prodCod, setProdCod] = useState('');
  const [erros, setErros] = useState<string[]>([]);
  const set = (p: Partial<Linha>) => setL((x) => coerente({ ...x, ...p }));
  const propria = l.tipo === 'PROPRIA_ENTE';

  const adicionar = () => {
    const linha = coerente({ ...l, produto: l.tributo === 'IS' ? (prodCod.trim() ? `${prodTipo}:${prodCod.trim()}` : '') : '' });
    const p = problemas(linha);
    setErros(p);
    if (p.length) return;
    onAdd(linha);
    setL((x) => ({ ...x, aliquota: '', ente_codigo: '' }));
    setMuni(undefined);
    setProdCod('');
  };

  return (
    <fieldset className="stack" style={{ border: '1px solid var(--border, #ddd)', borderRadius: 8, padding: 12 }}>
      <legend className="field-label">Adicionar linha</legend>
      <div className="form-grid">
        <Field label="Tributo">
          <select value={l.tributo} onChange={(e) => { set({ tributo: e.target.value, ente_codigo: '' }); setMuni(undefined); }}>
            {TRIBUTOS.map((t) => <option key={t} value={t}>{TRIBUTO_LABEL[t]}</option>)}
          </select>
        </Field>
        {aceitaPropria(l.tributo) && (
          <Field label="Tipo">
            <select value={l.tipo} onChange={(e) => set({ tipo: e.target.value })}>
              <option value="REFERENCIA">Referência nacional</option>
              <option value="PROPRIA_ENTE">{l.tributo === 'IBS_UF' ? 'Própria do estado' : 'Própria do município'}</option>
            </select>
          </Field>
        )}
        {propria && l.esfera === 'UF' && (
          <Field label="UF">
            <select value={l.ente_codigo} onChange={(e) => set({ ente_codigo: e.target.value })}>
              <option value="">Escolha…</option>
              {UFS.map((u) => <option key={u} value={UF_IBGE[u]}>{u}</option>)}
            </select>
          </Field>
        )}
        {propria && l.esfera === 'MUNICIPIO' && (
          <MunicipioPicker value={muni} onChange={(m) => { setMuni(m); set({ ente_codigo: m ? String(m.codigo) : '' }); }} />
        )}
        {propria && (
          <Field label="Forma de aplicação">
            <select value={l.forma_aplicacao} onChange={(e) => set({ forma_aplicacao: e.target.value })}>
              {FORMAS.map((f) => <option key={f} value={f}>{FORMA_LABEL[f]}</option>)}
            </select>
          </Field>
        )}
        {l.tributo === 'IS' && (
          <>
            <Field label="Produto por">
              <select value={prodTipo} onChange={(e) => setProdTipo(e.target.value)}>
                <option value="NCM">NCM</option>
                <option value="CCLASS">Classificação tributária (cClassTrib)</option>
              </select>
            </Field>
            <Field label={prodTipo === 'NCM' ? 'Código NCM' : 'Código cClassTrib'}>
              <input value={prodCod} onChange={(e) => setProdCod(e.target.value)} placeholder={prodTipo === 'NCM' ? '2203' : '000001'} />
            </Field>
          </>
        )}
        <Field label="Alíquota (%)"><input inputMode="decimal" value={l.aliquota} onChange={(e) => set({ aliquota: e.target.value })} placeholder="0,9" /></Field>
        <Field label="Vigente de"><input type="date" value={l.valid_from} onChange={(e) => set({ valid_from: e.target.value })} /></Field>
        <Field label="Vigente até" hint="Em branco = sem data de fim. O dia informado já não vale."><input type="date" value={l.valid_to} onChange={(e) => set({ valid_to: e.target.value })} /></Field>
        <Field label="Fonte">
          <select value={l.fonte} onChange={(e) => set({ fonte: e.target.value })}>
            {FONTES.map((f) => <option key={f} value={f}>{FONTE_LABEL[f]}</option>)}
          </select>
        </Field>
      </div>
      <Field label="Ato legal" hint="Ex.: Resolução do Senado nº 1/2026, art. 2º."><input value={l.ato_legal} onChange={(e) => set({ ato_legal: e.target.value })} /></Field>
      <label className="checkbox"><input type="checkbox" checked={l.provisoria} onChange={(e) => set({ provisoria: e.target.checked })} /> Provisória (ainda sem ato definitivo)</label>
      {erros.length > 0 && <Alert>Corrija: {erros.join('; ')}.</Alert>}
      <div><button type="button" className="secondary" disabled={cheia} onClick={adicionar}>Adicionar linha</button></div>
    </fieldset>
  );
}

/** Resultado do /preview: erros por linha e, para cada linha nova, o que está vigente (marcar = vai em "substitui"). */
function Conferencia({ dados, substitui, onSubstitui }: { dados: Preview; substitui: string[]; onSubstitui: (ids: string[]) => void }) {
  const comVigente = dados.itens.filter((i) => i.vigentes.length > 0);
  const marcado = new Set(substitui);
  const semMarcar = comVigente.filter((i) => !i.vigentes.some((v) => marcado.has(v.id))).length;
  const todos = comVigente.flatMap((i) => i.vigentes.map((v) => v.id));
  const toggle = (id: string, on: boolean) => onSubstitui(on ? [...substitui, id] : substitui.filter((x) => x !== id));

  return (
    <div className="stack">
      <h3>Conferência</h3>
      {dados.erros.length > 0 ? (
        <Alert>
          {dados.erros.length} problema(s) — corrija antes de propor:
          <ul>{dados.erros.slice(0, 50).map((e) => <li key={e}>{e}</li>)}</ul>
          {dados.erros.length > 50 && <span>…e mais {dados.erros.length - 50}.</span>}
        </Alert>
      ) : (
        <Alert kind="success">{dados.linhas} linha(s) sem erros. {dados.itens.length - comVigente.length} não têm nada vigente no período (entram como novas).</Alert>
      )}
      {comVigente.length > 0 && (
        <>
          <p className="muted small">
            Estas linhas se sobrepõem a alíquotas vigentes. Marque as vigentes que a proposta substitui (elas deixam de valer quando a proposta for aprovada).
          </p>
          {semMarcar > 0 && <Alert kind="warning">{semMarcar} linha(s) se sobrepõem a uma alíquota vigente sem nada marcado para substituir.</Alert>}
          <div className="row">
            <button type="button" className="ghost" onClick={() => onSubstitui(todos)}>Marcar todas</button>
            <button type="button" className="ghost" onClick={() => onSubstitui([])}>Desmarcar todas</button>
          </div>
          <Table head={['Linha nova', 'Vigente hoje', 'Substituir?']}>
            {comVigente.slice(0, MOSTRAR_LINHAS).flatMap((i, n) => i.vigentes.map((v, k) => (
              <tr key={`${n}-${v.id}`}>
                {k === 0 && (
                  <td rowSpan={i.vigentes.length} data-label="Linha nova">
                    <strong>{TRIBUTO_LABEL[i.tributo] ?? i.tributo}</strong> · {nomeEnte('', i.ente_codigo)}{i.produto && <> · <span className="mono">{i.produto}</span></>}
                    <span className="sub">{pct(i.aliquota_nova)} a partir de {dataBr(i.valid_from)}</span>
                  </td>
                )}
                <td data-label="Vigente hoje">{pct(v.aliquota)} · {dataBr(v.valid_from)} → {v.valid_to ? dataBr(v.valid_to) : 'sem fim'}{v.provisoria && <> <Badge tone="warn">provisória</Badge></>}<span className="sub">{v.ato_legal}</span></td>
                <td data-label="Substituir?">
                  <label className="checkbox small">
                    <input type="checkbox" checked={marcado.has(v.id)} onChange={(e) => toggle(v.id, e.target.checked)}
                      aria-label={`Substituir ${TRIBUTO_LABEL[i.tributo] ?? i.tributo} ${nomeEnte('', i.ente_codigo)} ${pct(v.aliquota)} de ${dataBr(v.valid_from)}`} />
                    substituir
                  </label>
                </td>
              </tr>
            )))}
          </Table>
          {comVigente.length > MOSTRAR_LINHAS && <p className="muted small">Mostrando {MOSTRAR_LINHAS} de {comVigente.length}; “Marcar todas” vale para todas.</p>}
        </>
      )}
      {substitui.length > 0 && <p className="muted small">{substitui.length} alíquota(s) vigente(s) marcada(s) para substituir.</p>}
    </div>
  );
}

/** Recusar uma proposta pede o motivo (5 a 500 caracteres), que fica registrado. */
function RecusarButton({ onConfirm }: { onConfirm: (motivo: string) => unknown }) {
  const [open, setOpen] = useState(false);
  const [motivo, setMotivo] = useState('');
  const campo = useRef<HTMLTextAreaElement>(null);
  useEffect(() => {
    if (!open) return;
    campo.current?.focus();
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open]);
  const t = motivo.trim();
  const ok = t.length >= 5 && t.length <= 500;
  return (
    <>
      <button type="button" className="danger" onClick={() => { setMotivo(''); setOpen(true); }}>Recusar</button>
      {open && (
        <div className="modal-backdrop" role="presentation" onClick={() => setOpen(false)}>
          <div className="modal" role="dialog" aria-modal="true" aria-labelledby="recusar-title" onClick={(e) => e.stopPropagation()}>
            <header className="modal-header"><div><h2 id="recusar-title">Recusar a proposta?</h2></div></header>
            <div className="modal-body stack">
              <p>A proposta não será aplicada. Diga o motivo para quem propôs corrigir — fica registrado na trilha.</p>
              <Field label="Motivo da recusa" hint={`${t.length}/500 caracteres (mínimo 5).`}>
                <textarea ref={campo} rows={3} maxLength={500} value={motivo} onChange={(e) => setMotivo(e.target.value)} />
              </Field>
            </div>
            <footer className="modal-footer">
              <button type="button" className="secondary" onClick={() => setOpen(false)}>Cancelar</button>
              <button type="button" className="danger" disabled={!ok} onClick={async () => { setOpen(false); await onConfirm(t); }}>Recusar proposta</button>
            </footer>
          </div>
        </div>
      )}
    </>
  );
}
