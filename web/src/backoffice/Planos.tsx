import { useEffect, useState, type FormEvent } from 'react';
import { Alert, Badge, Card, Field, Loading, PageHeader, Table, useAction, useLoad } from '../components/ui';
import { backoffice } from '../lib/api';
import { dateTime, integer, money } from '../lib/format';

interface Plano {
  code: string; name: string; description: string; features: string[]; price_cents: number; included_docs: number; included_simulations: number;
  included_cnpjs: number | null; max_users: number | null; overage_doc_millicents: number; overage_sim_millicents: number; cnpj_price_cents: number;
  rpm: number; burst: number; per_cnpj_rpm?: number | null; concurrency: number; batch_max_docs: number; batch_max_bytes: number; queue_priority: string; bill_schema_errors: boolean;
  hard_quota: boolean; dfe_enabled: boolean; is_public: boolean; self_service: boolean; active: boolean; sort_order: number;
  updated_at: string; updated_by: string | null; stripe_product_id: string | null; stripe_price_id: string | null; stripe_synced_at: string | null;
}

type Form = Record<string, string | boolean>;

const reais = (cents: number) => (cents / 100).toFixed(2);
const toCents = (v: string) => Math.round(Number(v.replace(',', '.')) * 100);

function toForm(p?: Plano): Form {
  return {
    code: p?.code ?? '', name: p?.name ?? '', description: p?.description ?? '', features: (p?.features ?? []).join('\n'),
    price: p ? reais(p.price_cents) : '0', included_docs: String(p?.included_docs ?? 0), included_simulations: String(p?.included_simulations ?? 0),
    included_cnpjs: p?.included_cnpjs == null ? '' : String(p.included_cnpjs), max_users: p?.max_users == null ? '' : String(p.max_users),
    overage_doc: p ? (p.overage_doc_millicents / 100_000).toFixed(4) : '0', cnpj_price: p ? reais(p.cnpj_price_cents) : '0',
    rpm: String(p?.rpm ?? 120), burst: String(p?.burst ?? 30), per_cnpj_rpm: String(p?.per_cnpj_rpm ?? 0), concurrency: String(p?.concurrency ?? 6), batch_max_docs: String(p?.batch_max_docs ?? 1000),
    queue_priority: p?.queue_priority ?? 'normal', sort_order: String(p?.sort_order ?? 100),
    hard_quota: p?.hard_quota ?? false, dfe_enabled: p?.dfe_enabled ?? true, is_public: p?.is_public ?? false, self_service: p?.self_service ?? false,
    active: p?.active ?? true, bill_schema_errors: p?.bill_schema_errors ?? true,
  };
}

/** Cadastro de planos (D-C01): valores, franquias, limites e vitrine. Toda mudança fica no histórico e vai para a Stripe. */
export function Planos() {
  const list = useLoad(() => backoffice<Plano[]>('/admin/v1/planos'));
  const [sel, setSel] = useState<string>();
  const [form, setForm] = useState<Form>(toForm());
  const [msg, setMsg] = useState<string>();
  const { busy, error, run } = useAction();
  const history = useLoad(() => (sel ? backoffice<{ em: string; por: string; acao: string; dados: Record<string, unknown> }[]>(`/admin/v1/planos/${sel}/historico`) : Promise.resolve([])), [sel]);

  useEffect(() => { setForm(toForm(list.data?.find((p) => p.code === sel))); }, [sel, list.data]);

  const set = (k: string) => (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>) =>
    setForm({ ...form, [k]: e.target instanceof HTMLInputElement && e.target.type === 'checkbox' ? e.target.checked : e.target.value });
  const n = (k: string) => Number(form[k]);
  const opt = (k: string) => (form[k] === '' ? null : Number(form[k]));

  const save = async (e: FormEvent) => {
    e.preventDefault();
    setMsg(undefined);
    const code = String(form.code).trim().toUpperCase();
    const body = {
      name: form.name, description: form.description, features: String(form.features).split('\n').map((x) => x.trim()).filter(Boolean),
      price_cents: toCents(String(form.price)), included_docs: n('included_docs'), included_simulations: n('included_simulations'),
      included_cnpjs: opt('included_cnpjs'), max_users: opt('max_users'),
      overage_doc_millicents: Math.round(Number(String(form.overage_doc).replace(',', '.')) * 100_000), cnpj_price_cents: toCents(String(form.cnpj_price)),
      rpm: n('rpm'), burst: n('burst'), per_cnpj_rpm: n('per_cnpj_rpm'), concurrency: n('concurrency'), batch_max_docs: n('batch_max_docs'), queue_priority: form.queue_priority,
      sort_order: n('sort_order'), hard_quota: form.hard_quota, dfe_enabled: form.dfe_enabled, is_public: form.is_public, self_service: form.self_service,
      active: form.active, bill_schema_errors: form.bill_schema_errors,
    };
    const r = await run(() => backoffice<{ mensagem: string }>(`/admin/v1/planos/${code}`, { method: 'PUT', body }));
    if (r) { setMsg(r.mensagem); setSel(code); void list.reload(); void history.reload(); }
  };

  return (
    <>
      <PageHeader title="Planos" description="Preços, franquias e limites. Mudanças valem na hora para os limites; preços novos vão para a Stripe (assinaturas existentes seguem no preço antigo até trocarem de plano).">
        <button type="button" className="secondary" onClick={() => { setSel(undefined); setForm(toForm()); }}>Novo plano</button>
      </PageHeader>
      <Card flush>
        {list.loading ? <Loading /> : (
          <Table head={['Plano', 'Mensalidade', 'Documentos/mês', 'Excedente/doc', 'Usuários', 'Vitrine', 'Stripe', '']} empty={!list.data?.length}>
            {list.data?.map((p) => (
              <tr key={p.code} className={p.code === sel ? 'selected' : undefined}>
                <td><strong>{p.name || p.code}</strong> <span className="muted small mono">{p.code}</span>{!p.active && <> <Badge tone="neutral">legado</Badge></>}</td>
                <td className="num">{p.price_cents ? money(p.price_cents / 100) : '—'}</td>
                <td className="num">{integer(p.included_docs)}{p.hard_quota && <> <Badge tone="warn">rígida</Badge></>}</td>
                <td className="num">{p.overage_doc_millicents ? money(p.overage_doc_millicents / 100_000) : '—'}</td>
                <td className="num">{p.max_users ?? '∞'}</td>
                <td>{p.is_public ? <Badge tone="ok">{p.self_service ? 'online' : 'sob consulta'}</Badge> : <Badge tone="neutral">oculto</Badge>}</td>
                <td>{p.price_cents === 0 ? '—' : p.stripe_synced_at && p.stripe_synced_at >= p.updated_at ? <Badge tone="ok">sincronizado</Badge> : <Badge tone="warn">pendente</Badge>}</td>
                <td><button type="button" className="link" onClick={() => setSel(p.code)}>Editar</button></td>
              </tr>
            ))}
          </Table>
        )}
      </Card>
      <Card title={sel ? `Editar ${sel}` : 'Novo plano'}>
        <form onSubmit={save} className="stack">
          <div className="form-grid">
            <Field label="Código" hint="Maiúsculas, números e _. Não muda depois."><input value={String(form.code)} onChange={set('code')} disabled={!!sel} required pattern="[A-Za-z][A-Za-z0-9_]{1,29}" /></Field>
            <Field label="Nome na vitrine"><input value={String(form.name)} onChange={set('name')} required maxLength={60} /></Field>
            <Field label="Mensalidade (R$)"><input inputMode="decimal" value={String(form.price)} onChange={set('price')} required /></Field>
            <Field label="Ordem na vitrine"><input type="number" min={0} value={String(form.sort_order)} onChange={set('sort_order')} /></Field>
            <Field label="Documentos incluídos/mês"><input type="number" min={0} value={String(form.included_docs)} onChange={set('included_docs')} /></Field>
            <Field label="Simulações incluídas/mês"><input type="number" min={0} value={String(form.included_simulations)} onChange={set('included_simulations')} /></Field>
            <Field label="Excedente por documento (R$)"><input inputMode="decimal" value={String(form.overage_doc)} onChange={set('overage_doc')} /></Field>
            <Field label="CNPJs incluídos" hint="Vazio = ilimitado"><input type="number" min={0} value={String(form.included_cnpjs)} onChange={set('included_cnpjs')} /></Field>
            <Field label="CNPJ extra (R$/mês)"><input inputMode="decimal" value={String(form.cnpj_price)} onChange={set('cnpj_price')} /></Field>
            <Field label="Usuários" hint="Vazio = ilimitado"><input type="number" min={1} value={String(form.max_users)} onChange={set('max_users')} /></Field>
            <Field label="Requisições/min"><input type="number" min={1} value={String(form.rpm)} onChange={set('rpm')} /></Field>
            <Field label="Rajada"><input type="number" min={1} value={String(form.burst)} onChange={set('burst')} /></Field>
            <Field label="Sub-limite por CNPJ atendido (req/min)" hint="Teto de cada CNPJ atendido (OEM) dentro do limite do cliente. 0 = sem sub-limite."><input type="number" min={0} step={1} value={String(form.per_cnpj_rpm)} onChange={set('per_cnpj_rpm')} /></Field>
            <Field label="Simultâneas"><input type="number" min={1} value={String(form.concurrency)} onChange={set('concurrency')} /></Field>
            <Field label="Documentos por lote"><input type="number" min={1} value={String(form.batch_max_docs)} onChange={set('batch_max_docs')} /></Field>
            <Field label="Prioridade na fila">
              <select value={String(form.queue_priority)} onChange={set('queue_priority')}>
                <option value="low">baixa</option><option value="normal">normal</option><option value="high">alta</option><option value="realtime">máxima</option>
              </select>
            </Field>
          </div>
          <Field label="Descrição"><input value={String(form.description)} onChange={set('description')} maxLength={500} /></Field>
          <Field label="Recursos (um por linha, até 12)"><textarea rows={5} value={String(form.features)} onChange={set('features')} /></Field>
          <div className="row" style={{ flexWrap: 'wrap', gap: 16 }}>
            {([['is_public', 'Aparece na vitrine'], ['self_service', 'Contratação online'], ['active', 'Aceita novos clientes'], ['hard_quota', 'Franquia rígida (sem excedente)'],
              ['dfe_enabled', 'Captura de DF-e'], ['bill_schema_errors', 'Cobra XML fora do leiaute']] as const).map(([k, l]) => (
              <label key={k} className="checkbox small"><input type="checkbox" checked={Boolean(form[k])} onChange={set(k)} /> {l}</label>
            ))}
          </div>
          <Alert>{error}</Alert>
          {msg && <Alert kind="success">{msg}</Alert>}
          <div><button type="submit" disabled={busy}>{busy ? 'Salvando…' : 'Salvar plano'}</button></div>
        </form>
      </Card>
      {sel && (
        <Card title="Histórico de mudanças" flush>
          <Table head={['Quando', 'Quem', 'Ação', 'Mensalidade anterior']} empty={!history.data?.length}>
            {history.data?.map((h, i) => (
              <tr key={i}>
                <td>{dateTime(h.em)}</td><td>{h.por}</td><td>{h.acao === 'create' ? 'criação' : 'alteração'}</td>
                <td className="num">{typeof h.dados.price_cents === 'number' ? money(h.dados.price_cents / 100) : '—'}</td>
              </tr>
            ))}
          </Table>
        </Card>
      )}
    </>
  );
}
