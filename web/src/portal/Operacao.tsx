import { useEffect, useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Building2, Calculator, CheckCircle2, Circle, FileCheck2, Gauge, Plus, Sparkles, Trash2 } from 'lucide-react';
import { Alert, Badge, Card, Field, Json, Loading, PageHeader, Stat, StatusBadge, Table, statusLabel, useAction, useLoad } from '../components/ui';
import { CnpjInput, Dropzone, UfSelect } from '../components/inputs';
import { ApiError, downloadFile, portal } from '../lib/api';
import { cnpj as formatCnpj, currentMonth, dateTime, integer, money } from '../lib/format';
import { buildExampleXml } from '../lib/nfeExemplo';
import { useSession } from '../auth/session';
import { DocumentoDrawer } from './Documentos';
import type { Me } from './PortalApp';

interface Usage {
  competencia: string;
  plano: string;
  documentos_auditados: number;
  simulacoes: number;
  cnpjs_ativos: number;
  franquia_documentos: number;
  excedente_documentos: number;
  atualizado_em: string;
}

interface Passos { passos: { id: string; titulo: string; descricao: string; link: string; feito: boolean }[]; concluidos: number; total: number }

function lastMonths(n: number): string[] {
  const [y, m] = currentMonth().split('-').map(Number);
  return Array.from({ length: n }, (_, i) => {
    const d = new Date(Date.UTC(y, m - 1 - i, 1));
    return `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, '0')}`;
  });
}

export function Overview({ me }: { me?: Me }) {
  const [mes, setMes] = useState(currentMonth());
  const usage = useLoad(() => portal<Usage>('/v1/usage', { query: { mes } }), [mes]);
  const passos = useLoad(() => portal<Passos>('/v1/primeiros-passos'));
  const historico = useLoad(() => Promise.all(lastMonths(6).map((m) => portal<Usage>('/v1/usage', { query: { mes: m } }).catch(() => undefined))));
  const u = usage.data;
  const ratio = u && u.franquia_documentos > 0 ? u.documentos_auditados / u.franquia_documentos : undefined;
  const hist = (historico.data ?? []).filter((x): x is Usage => !!x);
  const maxDocs = Math.max(1, ...hist.map((h) => h.documentos_auditados + h.simulacoes));
  return (
    <>
      <PageHeader eyebrow={me?.nome} title="Visão geral" description="Consumo da competência e prévia do que entra na fatura.">
        <input type="month" aria-label="Competência" value={mes} onChange={(e) => setMes(e.target.value)} style={{ width: 'auto' }} />
      </PageHeader>
      {me && me.situacao !== 'ACTIVE' && <Alert kind="warning">Este tenant está {statusLabel(me.situacao).toLowerCase()}: as chamadas à API são recusadas.</Alert>}
      {passos.data && passos.data.concluidos < passos.data.total && (
        <Card title="Comece por aqui" description={`${passos.data.concluidos} de ${passos.data.total} passos concluídos`}
          actions={<div className="meter" style={{ width: 160 }} role="progressbar" aria-label="Progresso dos primeiros passos" aria-valuenow={Math.round((passos.data.concluidos / passos.data.total) * 100)} aria-valuemin={0} aria-valuemax={100}><span style={{ width: `${(passos.data.concluidos / passos.data.total) * 100}%` }} /></div>}>
          <ol className="onboarding">
            {passos.data.passos.map((p) => (
              <li key={p.id} className={p.feito ? 'done' : ''}>
                {p.feito ? <CheckCircle2 size={18} aria-hidden="true" /> : <Circle size={18} aria-hidden="true" />}
                <div>
                  <Link to={p.link}><strong>{p.titulo}</strong></Link>
                  <span className="sub">{p.descricao}</span>
                </div>
                <span className="visually-hidden">{p.feito ? 'concluído' : 'pendente'}</span>
              </li>
            ))}
          </ol>
        </Card>
      )}
      {passos.data && <div style={{ height: 16 }} />}
      {usage.loading && <Loading />}
      <Alert>{usage.error}</Alert>
      {u && (
        <div className="grid">
          <Stat label="Documentos auditados" icon={FileCheck2} value={integer(u.documentos_auditados)} meter={ratio}
            hint={u.franquia_documentos > 0 ? <>Franquia de {integer(u.franquia_documentos)}{u.excedente_documentos > 0 && <> · <strong>{integer(u.excedente_documentos)} excedentes</strong></>}</> : 'Sem franquia definida no plano'} />
          <Stat label="Simulações" icon={Calculator} value={integer(u.simulacoes)} hint="Cobradas por chamada" />
          <Stat label="CNPJs ativos" icon={Building2} value={integer(u.cnpjs_ativos)} hint="Com uso na competência" />
          {me && <Stat label="Limite do plano" icon={Gauge} value={`${integer(me.limites.rpm)}/min`} hint={`${me.limites.concorrencia} requisições simultâneas · plano ${me.plano}`} />}
        </div>
      )}
      {u && <p className="muted small" style={{ marginTop: 12 }}>Competência {u.competencia} · atualizado em {dateTime(u.atualizado_em)} (cache de até 60 s).</p>}
      {hist.length > 0 && (
        <>
          <div style={{ height: 16 }} />
          <Card title="Consumo dos últimos meses" description="Documentos auditados e simulações por competência.">
            <Table head={['Competência', 'Documentos', 'Simulações', '']}>
              {hist.map((h) => (
                <tr key={h.competencia}>
                  <td data-label="Competência">{h.competencia}</td>
                  <td data-label="Documentos" className="num">{integer(h.documentos_auditados)}</td>
                  <td data-label="Simulações" className="num">{integer(h.simulacoes)}</td>
                  <td aria-hidden="true" style={{ width: '40%' }}>
                    <div className="bars"><span className="bar-docs" style={{ width: `${(h.documentos_auditados / maxDocs) * 100}%` }} /><span className="bar-sims" style={{ width: `${(h.simulacoes / maxDocs) * 100}%` }} /></div>
                  </td>
                </tr>
              ))}
            </Table>
          </Card>
        </>
      )}
    </>
  );
}

interface ItemForm { ncm: string; cst: string; cclass: string; valor: string; quantidade: string; unidade: string }

interface Simulacao {
  simulation_id: string;
  regime_calculo: string;
  param_snapshot_id: number;
  totais: { valor_base_total: number; cbs_valor: number; ibs_uf_valor: number; ibs_mun_valor: number; ibs_total_valor: number; imposto_seletivo_valor: number };
  avisos: { codigo: string; mensagem: string }[];
}

const itemVazio = (): ItemForm => ({ ncm: '84713012', cst: '000', cclass: '000001', valor: '1000', quantidade: '1', unidade: 'UN' });

/** Simulação por formulário (UX: antes só JSON). O JSON continua disponível para quem integra. */
export function Simular({ me }: { me: Me }) {
  const [modo, setModo] = useState<'form' | 'json'>('form');
  const [emitente, setEmitente] = useState(me.cnpj_raiz);
  const [regime, setRegime] = useState('REGIME_REGULAR');
  const [data, setData] = useState('2027-06-15T14:00');
  const [municipio, setMunicipio] = useState('3550308');
  const [uf, setUf] = useState('SP');
  const [itens, setItens] = useState<ItemForm[]>([itemVazio()]);
  const [text, setText] = useState('');
  const [result, setResult] = useState<Simulacao>();
  const { busy, error, run, setError } = useAction();

  const corpo = () => ({
    cnpj_emitente: emitente,
    regime_tributario: regime,
    data_fato_gerador: `${data}:00-03:00`,
    municipio_ibge: Number(municipio),
    uf,
    itens: itens.map((it, i) => ({
      numero: i + 1, ncm: it.ncm.replace(/\D/g, ''), cst_ibs_cbs: it.cst, c_class_trib: it.cclass,
      valor_base_calculo: Number(it.valor.replace(',', '.')), quantidade: Number(it.quantidade.replace(',', '.')), unidade: it.unidade,
    })),
  });

  const trocarModo = (m: 'form' | 'json') => {
    if (m === 'json') setText(JSON.stringify(corpo(), null, 2));
    setModo(m);
  };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    let body: unknown = corpo();
    if (modo === 'json') {
      try {
        body = JSON.parse(text);
      } catch {
        setError('O corpo não é um JSON válido.');
        return;
      }
    }
    setResult(await run(() => portal<Simulacao>('/v1/calculo/simular', { method: 'POST', body, idempotent: true })));
  };

  const setItem = (i: number, patch: Partial<ItemForm>) => setItens(itens.map((it, j) => (j === i ? { ...it, ...patch } : it)));

  return (
    <>
      <PageHeader title="Simular" description="Calcule CBS, IBS e IS de uma operação com o motor oficial, sem emitir nota.">
        <div className="segmented" role="tablist" aria-label="Modo">
          <button type="button" role="tab" aria-selected={modo === 'form'} className={modo === 'form' ? 'active' : ''} onClick={() => trocarModo('form')}>Formulário</button>
          <button type="button" role="tab" aria-selected={modo === 'json'} className={modo === 'json' ? 'active' : ''} onClick={() => trocarModo('json')}>JSON da API</button>
        </div>
      </PageHeader>
      <div className="grid-2">
        <Card title="Operação">
          <form className="stack" onSubmit={submit}>
            {modo === 'form' ? (
              <>
                <div className="form-grid">
                  <Field label="CNPJ do emitente" hint="Precisa estar entre os CNPJs da empresa."><CnpjInput value={emitente} onChange={setEmitente} required /></Field>
                  <Field label="Regime">
                    <select value={regime} onChange={(e) => setRegime(e.target.value)}>
                      <option value="REGIME_REGULAR">Regime regular</option>
                      <option value="SIMPLES_NACIONAL">Simples Nacional</option>
                    </select>
                  </Field>
                  <Field label="Data e hora do fato gerador" hint="Horário de Brasília."><input type="datetime-local" min="2026-01-01T00:00" value={data} onChange={(e) => setData(e.target.value)} required /></Field>
                  <Field label="Município (IBGE)" hint="7 dígitos. São Paulo: 3550308."><input value={municipio} inputMode="numeric" maxLength={7} onChange={(e) => setMunicipio(e.target.value.replace(/\D/g, ''))} required /></Field>
                  <Field label="UF"><UfSelect value={uf} onChange={setUf} /></Field>
                </div>
                {itens.map((it, i) => (
                  <fieldset key={i} className="item-card">
                    <legend>Item {i + 1}</legend>
                    <div className="form-grid">
                      <Field label="NCM"><input value={it.ncm} inputMode="numeric" maxLength={10} onChange={(e) => setItem(i, { ncm: e.target.value })} /></Field>
                      <Field label="CST"><input value={it.cst} inputMode="numeric" maxLength={3} onChange={(e) => setItem(i, { cst: e.target.value.replace(/\D/g, '') })} /></Field>
                      <Field label="cClassTrib"><input value={it.cclass} inputMode="numeric" maxLength={6} onChange={(e) => setItem(i, { cclass: e.target.value.replace(/\D/g, '') })} /></Field>
                      <Field label="Base de cálculo (R$)"><input value={it.valor} inputMode="decimal" onChange={(e) => setItem(i, { valor: e.target.value })} /></Field>
                      <Field label="Quantidade"><input value={it.quantidade} inputMode="decimal" onChange={(e) => setItem(i, { quantidade: e.target.value })} /></Field>
                      <Field label="Unidade"><input value={it.unidade} maxLength={6} onChange={(e) => setItem(i, { unidade: e.target.value.toUpperCase() })} /></Field>
                    </div>
                    <div className="row">
                      <Link to={`/portal/classificacao?ncm=${it.ncm.replace(/\D/g, '')}`} className="small">Não sabe o CST/cClassTrib? Consulte pelo NCM</Link>
                      {itens.length > 1 && <button type="button" className="ghost small" onClick={() => setItens(itens.filter((_, j) => j !== i))}><Trash2 size={14} aria-hidden="true" /> Remover item</button>}
                    </div>
                  </fieldset>
                ))}
                <button type="button" className="secondary" onClick={() => setItens([...itens, itemVazio()])} disabled={itens.length >= 50}><Plus size={15} aria-hidden="true" /> Adicionar item</button>
              </>
            ) : (
              <Field label="Corpo (JSON)" hint="Mesmo formato de POST /v1/calculo/simular. Campos desconhecidos são recusados.">
                <textarea value={text} onChange={(e) => setText(e.target.value)} spellCheck={false} rows={18} />
              </Field>
            )}
            <Alert>{error}</Alert>
            <button type="submit" disabled={busy}>{busy ? 'Calculando…' : 'Simular'}</button>
          </form>
        </Card>
        {result ? (
          <Card title="Resultado">
            <p className="muted">Regime {result.regime_calculo} · snapshot de parâmetros {result.param_snapshot_id}</p>
            <Table head={['Tributo', 'Valor']}>
              <tr><td>Base de cálculo</td><td className="num">{money(result.totais.valor_base_total)}</td></tr>
              <tr><td>CBS</td><td className="num">{money(result.totais.cbs_valor)}</td></tr>
              <tr><td>IBS estadual</td><td className="num">{money(result.totais.ibs_uf_valor)}</td></tr>
              <tr><td>IBS municipal</td><td className="num">{money(result.totais.ibs_mun_valor)}</td></tr>
              <tr><td>IBS total</td><td className="num">{money(result.totais.ibs_total_valor)}</td></tr>
              <tr><td>Imposto Seletivo</td><td className="num">{money(result.totais.imposto_seletivo_valor)}</td></tr>
            </Table>
            {result.avisos.length > 0 && (
              <Alert kind="warning">{result.avisos.map((a) => <div key={a.codigo}>{a.codigo}: {a.mensagem}</div>)}</Alert>
            )}
            <details><summary>Resposta completa (JSON)</summary><Json value={result} /></details>
          </Card>
        ) : (
          <Card title="Como funciona"><p className="muted">Preencha a operação e os itens. O cálculo usa o motor oficial da RFB e o catálogo de alíquotas vigente na data; nada é emitido nem enviado ao fisco. Para integrar, veja o mesmo pedido em "JSON da API".</p></Card>
        )}
      </div>
    </>
  );
}

interface Auditoria {
  audit_id: string;
  chave_acesso: string;
  direction: string;
  status: string;
  replay: boolean;
  resumo_financeiro: { tributo_declarado: { cbs: number; ibs: number }; tributo_recalculado?: { cbs: number; ibs: number }; diferenca: number; credito_potencial?: number };
  ocorrencias: { regra: string; natureza: string; severidade: string; numero_item?: number; mensagem: string; fundamentacao: string }[];
  avisos: { codigo: string; mensagem: string }[];
}

export function Auditar({ me }: { me?: Me }) {
  const { can } = useSession();
  const [file, setFile] = useState<File>();
  const [exemplo, setExemplo] = useState(false);
  const [direction, setDirection] = useState('OUTBOUND');
  const [result, setResult] = useState<Auditoria>();
  const [cnpjFaltando, setCnpjFaltando] = useState<string>();
  const [detalhe, setDetalhe] = useState(false);
  const { busy, error, run, setError } = useAction();

  const enviar = async (f: File, comExemplo: boolean) => {
    setCnpjFaltando(undefined);
    setResult(undefined);
    try {
      setResult(await portal<Auditoria>('/v1/auditoria/xml', {
        method: 'POST', rawBody: await f.text(), contentType: 'application/xml', idempotent: true,
        query: { direction, assinatura: comExemplo ? 'opcional' : undefined },
      }));
      setError(undefined);
    } catch (e) {
      // D-J06: CNPJ fora do cadastro vira uma ação, não um código de erro.
      if (e instanceof ApiError && e.code === 'ERR_CNPJ_NOT_PROVISIONED') {
        const m = /CNPJ ([0-9A-Z]{14})/.exec(e.detail ?? '');
        setCnpjFaltando(m?.[1] ?? '');
      }
      throw e;
    }
  };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (file) await run(() => enviar(file, exemplo));
  };

  const usarExemplo = async () => {
    const f = await run(() => buildExampleXml(me?.cnpj_raiz ?? ''));
    if (f) { setFile(f); setExemplo(true); setDirection('OUTBOUND'); }
  };

  const cadastrar = async () => {
    if (!cnpjFaltando || !file) return;
    const ok = await run(() => portal('/v1/oem/cnpjs', { method: 'POST', body: { cnpj: cnpjFaltando } }));
    if (ok !== undefined) await run(() => enviar(file, exemplo));
  };

  return (
    <>
      <PageHeader title="Auditar XML" description="Envie uma NF-e ou NFC-e para recalcular os tributos e ver cada divergência com a regra e o fundamento legal." />
      <Card>
        <form className="stack" onSubmit={submit}>
          <Dropzone accept=".xml,application/xml,text/xml" label="XML da NF-e/NFC-e" hint="NFe ou nfeProc assinado (até 1 MB)." file={file} onFile={(f) => { setFile(f); setExemplo(false); }} />
          <div className="row">
            <span className="muted small">Sem um XML à mão?</span>
            <button type="button" className="ghost small" onClick={() => void usarExemplo()}><Sparkles size={14} aria-hidden="true" /> Usar XML de exemplo</button>
          </div>
          {exemplo && <Alert kind="info">Exemplo de homologação com o CNPJ da sua empresa e sem assinatura digital: vai com assinatura opcional e o resultado traz o aviso ASSINATURA_AUSENTE. Conta na franquia como uma auditoria.</Alert>}
          <Field label="Direção">
            <select value={direction} onChange={(e) => setDirection(e.target.value)}>
              <option value="OUTBOUND">Saída (sou o emitente)</option>
              <option value="INBOUND">Entrada (sou o destinatário)</option>
            </select>
          </Field>
          {cnpjFaltando !== undefined ? (
            <Alert kind="warning">
              O CNPJ {cnpjFaltando ? <strong>{formatCnpj(cnpjFaltando)}</strong> : 'desta nota'} não está entre os CNPJs atendidos da sua empresa.
              {can('cnpjs:manage') && cnpjFaltando
                ? <div className="row" style={{ marginTop: 8 }}><button type="button" onClick={() => void cadastrar()} disabled={busy}>Cadastrar este CNPJ e auditar</button></div>
                : <div>Peça a quem administra a conta para cadastrá-lo em CNPJs.</div>}
            </Alert>
          ) : <Alert>{error}</Alert>}
          <button type="submit" disabled={busy || !file}>{busy ? 'Auditando…' : 'Auditar'}</button>
        </form>
      </Card>
      {result && (
        <>
          <div style={{ height: 16 }} />
          <Card title={`Chave ${result.chave_acesso}`} actions={<><StatusBadge status={result.status} /><button className="secondary" onClick={() => setDetalhe(true)}>Comparação por item</button></>}>
            {result.replay && <Alert kind="info">Documento já auditado: resultado anterior devolvido (replay, sem nova cobrança).</Alert>}
            <Table head={['', 'CBS', 'IBS']}>
              <tr><td>Declarado</td><td className="num">{money(result.resumo_financeiro.tributo_declarado.cbs)}</td><td className="num">{money(result.resumo_financeiro.tributo_declarado.ibs)}</td></tr>
              {result.resumo_financeiro.tributo_recalculado && (
                <tr><td>Recalculado</td><td className="num">{money(result.resumo_financeiro.tributo_recalculado.cbs)}</td><td className="num">{money(result.resumo_financeiro.tributo_recalculado.ibs)}</td></tr>
              )}
            </Table>
            <p>Diferença: <strong>{money(result.resumo_financeiro.diferenca)}</strong></p>
            {result.avisos.length > 0 && <Alert kind="info">{result.avisos.map((a) => <div key={a.codigo}><code>{a.codigo}</code> {a.mensagem}</div>)}</Alert>}
            <h2 style={{ margin: '12px 0 8px' }}>Ocorrências</h2>
            <Table head={['Regra', 'Natureza', 'Item', 'Mensagem']} empty={result.ocorrencias.length === 0} emptyText="Nenhuma ocorrência: o declarado confere com o recalculado.">
              {result.ocorrencias.map((o, i) => (
                <tr key={i}>
                  <td data-label="Regra"><code>{o.regra}</code></td>
                  <td data-label="Natureza"><Badge tone={o.natureza === 'DETERMINISTICA' ? 'bad' : 'warn'}>{o.natureza === 'DETERMINISTICA' ? 'Divergência' : o.natureza === 'INDICIO' ? 'Indício' : 'Informativo'}</Badge></td>
                  <td data-label="Item">{o.numero_item ?? '—'}</td>
                  <td data-label="Mensagem">{o.mensagem}<div className="muted">{o.fundamentacao}</div></td>
                </tr>
              ))}
            </Table>
          </Card>
          {detalhe && <DocumentoDrawer id={result.audit_id.replace(/^aud_/, '')} onClose={() => setDetalhe(false)} />}
        </>
      )}
    </>
  );
}

interface Lote {
  batch_id: string;
  status: string;
  direction: string;
  total: number | null;
  processados: number;
  falhas: number;
  conformes: number | null;
  criado_em: string;
  concluido_em: string | null;
}

const EM_ANDAMENTO = new Set(['QUEUED', 'RUNNING', 'COMPLETING']);

export function Lotes() {
  const list = useLoad(() => portal<{ lotes: Lote[] }>('/v1/auditoria/lotes'));
  const navigate = useNavigate();
  const [file, setFile] = useState<File>();
  const [direction, setDirection] = useState('OUTBOUND');
  const [sent, setSent] = useState<string>();
  const { busy, error, run } = useAction();
  const download = useAction();
  const andamento = (list.data?.lotes ?? []).some((l) => EM_ANDAMENTO.has(l.status));

  // Acompanha sozinho enquanto houver lote em processamento (antes era preciso clicar em "Atualizar").
  useEffect(() => {
    if (!andamento) return;
    const t = window.setInterval(() => void list.reload(), 5000);
    return () => window.clearInterval(t);
  }, [andamento, list]);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!file) return;
    const r = await run(() =>
      portal<{ batch_id: string; documentos: number }>('/v1/auditoria/lotes', {
        method: 'POST', rawBody: file, contentType: 'application/zip', query: { direction }, idempotent: true,
      }));
    if (r) {
      setSent(`Lote ${r.batch_id} aceito com ${r.documentos} documentos. Acompanhe abaixo.`);
      setFile(undefined);
      void list.reload();
    }
  };

  return (
    <>
      <PageHeader title="Lotes" description="Auditoria assíncrona de muitos documentos. O resultado também chega pelo webhook batch.audit.completed.">
        <button className="secondary" onClick={() => void list.reload()}>Atualizar</button>
      </PageHeader>
      <Card title="Enviar lote">
        <form className="stack" onSubmit={submit}>
          <Dropzone accept=".zip,application/zip" label="Arquivo ZIP" hint="Só arquivos .xml, sem ZIP dentro de ZIP, até 100 MB." file={file} onFile={setFile} />
          <Field label="Direção">
            <select value={direction} onChange={(e) => setDirection(e.target.value)}>
              <option value="OUTBOUND">Saída</option>
              <option value="INBOUND">Entrada</option>
            </select>
          </Field>
          <Alert>{error}</Alert>
          <Alert kind="success">{sent}</Alert>
          <button type="submit" disabled={busy || !file}>{busy ? 'Enviando…' : 'Enviar'}</button>
        </form>
      </Card>
      <div style={{ height: 16 }} />
      <Card title="Últimos lotes" actions={andamento ? <span className="muted small" aria-live="polite">Atualizando a cada 5 s…</span> : undefined}>
        {list.loading && !list.data && <Loading />}
        <Alert>{list.error ?? download.error}</Alert>
        {list.data && (
          <Table head={['Lote', 'Situação', 'Direção', 'Progresso', 'Conformes', 'Falhas', 'Criado', '']} empty={list.data.lotes.length === 0} emptyText="Nenhum lote enviado ainda.">
            {list.data.lotes.map((l) => (
              <tr key={l.batch_id}>
                <td data-label="Lote" className="mono">{l.batch_id.slice(0, 8)}…</td>
                <td data-label="Situação"><StatusBadge status={l.status} /></td>
                <td data-label="Direção">{l.direction === 'INBOUND' ? 'Entrada' : 'Saída'}</td>
                <td data-label="Progresso" className="num">
                  {integer(l.processados)} / {integer(l.total)}
                  {l.total ? <div className="meter" aria-hidden="true"><span style={{ width: `${Math.min(100, (l.processados / l.total) * 100)}%` }} /></div> : null}
                </td>
                <td data-label="Conformes" className="num">{integer(l.conformes)}</td>
                <td data-label="Falhas" className="num">{integer(l.falhas)}</td>
                <td data-label="Criado">{dateTime(l.criado_em)}</td>
                <td>
                  <div className="row">
                    <button className="link" onClick={() => navigate(`/portal/documentos?lote=${l.batch_id}`)}>Documentos</button>
                    {l.status === 'COMPLETED' && <button className="link" onClick={() => void download.run(() => downloadFile(`/v1/auditoria/lotes/${l.batch_id}/relatorio`, `lote-${l.batch_id}.csv`))}>CSV</button>}
                  </div>
                </td>
              </tr>
            ))}
          </Table>
        )}
      </Card>
    </>
  );
}
