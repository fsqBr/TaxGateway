import { useState, type FormEvent } from 'react';
import { Building2, Calculator, FileCheck2, Gauge } from 'lucide-react';
import { Alert, Badge, Card, Field, Json, Loading, PageHeader, Stat, StatusBadge, Table, statusLabel, useAction, useLoad } from '../components/ui';
import { getCredential, portal } from '../lib/api';
import { currentMonth, dateTime, integer, money } from '../lib/format';
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

export function Overview({ me }: { me?: Me }) {
  const [mes, setMes] = useState(currentMonth());
  const usage = useLoad(() => portal<Usage>('/v1/usage', { query: { mes } }), [mes]);
  const u = usage.data;
  const ratio = u && u.franquia_documentos > 0 ? u.documentos_auditados / u.franquia_documentos : undefined;
  return (
    <>
      <PageHeader eyebrow={me?.nome} title="Visão geral" description="Consumo da competência e prévia do que entra na fatura.">
        <input type="month" aria-label="Competência" value={mes} onChange={(e) => setMes(e.target.value)} style={{ width: 'auto' }} />
      </PageHeader>
      {me && me.situacao !== 'ACTIVE' && <Alert kind="warning">Este tenant está {statusLabel(me.situacao).toLowerCase()}: as chamadas à API são recusadas.</Alert>}
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
    </>
  );
}

const exemplo = {
  cnpj_emitente: '11222333000181',
  regime_tributario: 'REGIME_REGULAR',
  data_fato_gerador: '2027-06-15T14:00:00-03:00',
  municipio_ibge: 3550308,
  uf: 'SP',
  itens: [{ numero: 1, cst_ibs_cbs: '000', c_class_trib: '000001', valor_base_calculo: 1000, ncm: '84713012', quantidade: 1, unidade: 'UN' }],
};

interface Simulacao {
  simulation_id: string;
  regime_calculo: string;
  param_snapshot_id: number;
  totais: { valor_base_total: number; cbs_valor: number; ibs_uf_valor: number; ibs_mun_valor: number; ibs_total_valor: number; imposto_seletivo_valor: number };
  avisos: { codigo: string; mensagem: string }[];
}

export function Simular({ me }: { me: Me }) {
  // Exemplo já com o CNPJ raiz do próprio tenant (um CNPJ não cadastrado daria 403 ERR_CNPJ_NOT_PROVISIONED).
  const [text, setText] = useState(JSON.stringify({ ...exemplo, cnpj_emitente: me.cnpj_raiz || exemplo.cnpj_emitente }, null, 2));
  const [result, setResult] = useState<Simulacao>();
  const { busy, error, run, setError } = useAction();

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    let body: unknown;
    try {
      body = JSON.parse(text);
    } catch {
      setError('O corpo não é um JSON válido.');
      return;
    }
    setResult(await run(() => portal<Simulacao>('/v1/calculo/simular', { method: 'POST', body, idempotent: true })));
  };

  return (
    <>
      <PageHeader title="Simular" description="Calcule CBS, IBS e IS de uma operação com o motor oficial, sem emitir nota. O exemplo já usa o CNPJ raiz do seu tenant." />
      <div className="grid" style={{ alignItems: 'start' }}>
        <Card title="Requisição">
          <form className="stack" onSubmit={submit}>
            <Field label="Corpo (JSON)" hint="Mesmo formato de POST /v1/calculo/simular. Escopo simulacao:write.">
              <textarea value={text} onChange={(e) => setText(e.target.value)} spellCheck={false} rows={18} />
            </Field>
            <Alert>{error}</Alert>
            <button type="submit" disabled={busy}>{busy ? 'Calculando…' : 'Simular'}</button>
          </form>
        </Card>
        {result && (
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
            <details><summary>Resposta completa</summary><Json value={result} /></details>
          </Card>
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
}

export function Auditar() {
  const [file, setFile] = useState<File>();
  const [direction, setDirection] = useState('OUTBOUND');
  const [result, setResult] = useState<Auditoria>();
  const { busy, error, run } = useAction();

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!file) return;
    setResult(
      await run(async () =>
        portal<Auditoria>('/v1/auditoria/xml', {
          method: 'POST', rawBody: await file.text(), contentType: 'application/xml', query: { direction }, idempotent: true,
        })),
    );
  };

  return (
    <>
      <PageHeader title="Auditar XML" description="Envie uma NF-e ou NFC-e assinada para recalcular os tributos e ver cada divergência com a regra e o fundamento legal." />
      <Card>
        <form className="stack" onSubmit={submit}>
          <Field label="XML da NF-e/NFC-e" hint="NFe ou nfeProc assinado. Escopo auditoria:write.">
            <input type="file" accept=".xml,application/xml,text/xml" onChange={(e) => setFile(e.target.files?.[0])} />
          </Field>
          <Field label="Direção">
            <select value={direction} onChange={(e) => setDirection(e.target.value)}>
              <option value="OUTBOUND">Saída (sou o emitente)</option>
              <option value="INBOUND">Entrada (sou o destinatário)</option>
            </select>
          </Field>
          <Alert>{error}</Alert>
          <button type="submit" disabled={busy || !file}>{busy ? 'Auditando…' : 'Auditar'}</button>
        </form>
      </Card>
      {result && (
        <>
          <div style={{ height: 16 }} />
          <Card title={`Chave ${result.chave_acesso}`} actions={<StatusBadge status={result.status} />}>
            {result.replay && <Alert kind="info">Documento já auditado: resultado anterior devolvido (replay).</Alert>}
            <Table head={['', 'CBS', 'IBS']}>
              <tr><td>Declarado</td><td className="num">{money(result.resumo_financeiro.tributo_declarado.cbs)}</td><td className="num">{money(result.resumo_financeiro.tributo_declarado.ibs)}</td></tr>
              {result.resumo_financeiro.tributo_recalculado && (
                <tr><td>Recalculado</td><td className="num">{money(result.resumo_financeiro.tributo_recalculado.cbs)}</td><td className="num">{money(result.resumo_financeiro.tributo_recalculado.ibs)}</td></tr>
              )}
            </Table>
            <p>Diferença: <strong>{money(result.resumo_financeiro.diferenca)}</strong></p>
            <h2 style={{ margin: '12px 0 8px' }}>Ocorrências</h2>
            <Table head={['Regra', 'Natureza', 'Item', 'Mensagem']} empty={result.ocorrencias.length === 0}>
              {result.ocorrencias.map((o, i) => (
                <tr key={i}>
                  <td><code>{o.regra}</code></td>
                  <td><Badge tone={o.severidade === 'ALTA' ? 'bad' : 'warn'}>{o.natureza}</Badge></td>
                  <td>{o.numero_item ?? '—'}</td>
                  <td>{o.mensagem}<div className="muted">{o.fundamentacao}</div></td>
                </tr>
              ))}
            </Table>
          </Card>
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

export function Lotes() {
  const list = useLoad(() => portal<{ lotes: Lote[] }>('/v1/auditoria/lotes'));
  const [file, setFile] = useState<File>();
  const [direction, setDirection] = useState('OUTBOUND');
  const [sent, setSent] = useState<string>();
  const { busy, error, run } = useAction();

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!file) return;
    const r = await run(() =>
      portal<{ batch_id: string; documentos: number }>('/v1/auditoria/lotes', {
        method: 'POST', rawBody: file, contentType: 'application/zip', query: { direction }, idempotent: true,
      }));
    if (r) {
      setSent(`Lote ${r.batch_id} aceito com ${r.documentos} documentos.`);
      void list.reload();
    }
  };

  const download = async (id: string) => {
    const resp = await fetch(`/v1/auditoria/lotes/${id}/relatorio`, { headers: { 'X-Api-Key': getCredential('portal') ?? '' } });
    if (!resp.ok) return;
    const url = URL.createObjectURL(await resp.blob());
    const a = document.createElement('a');
    a.href = url;
    a.download = `lote-${id}.csv`;
    a.click();
    URL.revokeObjectURL(url);
  };

  return (
    <>
      <PageHeader title="Lotes" description="Auditoria assíncrona de muitos documentos. O resultado também chega pelo webhook batch.audit.completed."><button className="secondary" onClick={() => void list.reload()}>Atualizar</button></PageHeader>
      <Card title="Enviar lote">
        <form className="stack" onSubmit={submit}>
          <Field label="Arquivo ZIP" hint="Só arquivos .xml, sem ZIP dentro de ZIP, até 100 MB. O resultado chega por webhook batch.audit.completed.">
            <input type="file" accept=".zip,application/zip" onChange={(e) => setFile(e.target.files?.[0])} />
          </Field>
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
      <Card title="Últimos lotes">
        {list.loading && <Loading />}
        <Alert>{list.error}</Alert>
        {list.data && (
          <Table head={['Lote', 'Situação', 'Direção', 'Documentos', 'Conformes', 'Falhas', 'Criado', 'Relatório']} empty={list.data.lotes.length === 0}>
            {list.data.lotes.map((l) => (
              <tr key={l.batch_id}>
                <td className="mono">{l.batch_id.slice(0, 8)}…</td>
                <td><StatusBadge status={l.status} /></td>
                <td>{l.direction}</td>
                <td className="num">{integer(l.processados)} / {integer(l.total)}</td>
                <td className="num">{integer(l.conformes)}</td>
                <td className="num">{integer(l.falhas)}</td>
                <td>{dateTime(l.criado_em)}</td>
                <td>{l.status === 'COMPLETED' ? <button className="link" onClick={() => void download(l.batch_id)}>Baixar CSV</button> : '—'}</td>
              </tr>
            ))}
          </Table>
        )}
      </Card>
    </>
  );
}
