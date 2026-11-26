import { Fragment, useState, type FormEvent } from 'react';
import { Route, Routes } from 'react-router-dom';
import { Ban, Building2, Percent, Receipt } from 'lucide-react';
import { Shell, type NavItem } from '../components/Shell';
import { Alert, Badge, Card, Confirm, Field, Json, Loading, PageHeader, SecretOnce, StatusBadge, Table, useAction, useLoad } from '../components/ui';
import { backoffice } from '../lib/api';
import { cnpj, currentMonth, dateTime, money, onlyCnpjChars } from '../lib/format';
import { ALL_SCOPES } from '../lib/scopes';

interface Operador { operador: string; scopes: string[] }

const nav: NavItem[] = [
  { to: '/backoffice', label: 'Tenants', icon: Building2, section: 'Clientes' },
  { to: '/backoffice/tokens', label: 'Revogar token', icon: Ban, section: 'Clientes' },
  { to: '/backoffice/aliquotas', label: 'Alíquotas', icon: Percent, section: 'Fiscal' },
  { to: '/backoffice/faturamento', label: 'Faturamento', icon: Receipt, section: 'Financeiro' },
];

export default function BackofficeApp() {
  const me = useLoad(() => backoffice<Operador>('/admin/v1/me'));
  return (
    <Shell
      realm="backoffice"
      nav={nav}
      workspace={me.data && (
        <>
          <span className="muted small">Operador</span>
          <strong>{me.data.operador}</strong>
          <span>{me.data.scopes.map((x) => <Badge key={x} tone="neutral">{x}</Badge>)}</span>
        </>
      )}
    >
      <Routes>
        <Route index element={<Tenants />} />
        <Route path="aliquotas" element={<Aliquotas />} />
        <Route path="faturamento" element={<Faturamento />} />
        <Route path="tokens" element={<Tokens />} />
      </Routes>
    </Shell>
  );
}

interface Tenant {
  tenant_id: string; nome: string; cnpj_raiz: string; plano: string; situacao: string; auto_provisionar_cnpj: boolean;
  retencao_xml_dias: number | null; criado_em: string; clientes_oauth: { client_id: string; nome: string; revogado_em: string | null; criado_em: string }[];
}

export function Tenants() {
  const [busca, setBusca] = useState('');
  const [q, setQ] = useState('');
  const list = useLoad(() => backoffice<{ tenants: Tenant[] }>('/admin/v1/tenants', { query: { busca: q } }), [q]);
  const plans = useLoad(() => backoffice<{ planos: { codigo: string }[] }>('/admin/v1/plans'));
  const [secret, setSecret] = useState<{ label: string; value: string }>();
  const [open, setOpen] = useState<string>();
  const { busy, error, run } = useAction();
  const [form, setForm] = useState({ nome: '', cnpj_raiz: '', plano: 'STARTER', auto_provisionar_cnpj: false, retencao_xml_dias: '' });

  const create = async (e: FormEvent) => {
    e.preventDefault();
    const r = await run(() =>
      backoffice<{ tenant_id: string; api_key: string }>('/admin/v1/tenants', {
        method: 'POST',
        body: { ...form, cnpj_raiz: onlyCnpjChars(form.cnpj_raiz), retencao_xml_dias: form.retencao_xml_dias ? Number(form.retencao_xml_dias) : null },
      }));
    if (r) {
      setSecret({ label: `Primeira API key de ${form.nome}`, value: r.api_key });
      setForm({ ...form, nome: '', cnpj_raiz: '' });
      void list.reload();
    }
  };

  const act = async (fn: () => Promise<unknown>) => {
    if (await run(fn) !== undefined) void list.reload();
  };

  return (
    <>
      <PageHeader title="Tenants" description="Clientes da plataforma: crie, acompanhe a situação, suspenda e gerencie os clientes OAuth." />
      {secret && <SecretOnce label={secret.label} value={secret.value} onClose={() => setSecret(undefined)} />}
      <Alert>{error}</Alert>
      <Card title="Novo tenant">
        <form className="stack" onSubmit={create}>
          <div className="grid">
            <Field label="Nome"><input value={form.nome} onChange={(e) => setForm({ ...form, nome: e.target.value })} required /></Field>
            <Field label="CNPJ raiz (matriz)"><input value={form.cnpj_raiz} onChange={(e) => setForm({ ...form, cnpj_raiz: e.target.value })} required /></Field>
            <Field label="Plano">
              <select value={form.plano} onChange={(e) => setForm({ ...form, plano: e.target.value })}>
                {(plans.data?.planos ?? [{ codigo: 'STARTER' }]).map((p) => <option key={p.codigo}>{p.codigo}</option>)}
              </select>
            </Field>
            <Field label="Retenção do XML (dias)" hint="Vazio = não guardar o XML (zero_retention).">
              <select value={form.retencao_xml_dias} onChange={(e) => setForm({ ...form, retencao_xml_dias: e.target.value })}>
                <option value="">Não guardar</option>
                {[30, 90, 365, 1825].map((d) => <option key={d} value={d}>{d}</option>)}
              </select>
            </Field>
          </div>
          <label className="checkbox"><input type="checkbox" checked={form.auto_provisionar_cnpj} onChange={(e) => setForm({ ...form, auto_provisionar_cnpj: e.target.checked })} /> Auto-provisionar CNPJs (OEM)</label>
          <button type="submit" disabled={busy}>Criar tenant</button>
        </form>
      </Card>
      <div style={{ height: 16 }} />
      <Card
        title="Tenants"
        actions={
          <form className="row" onSubmit={(e) => { e.preventDefault(); setQ(busca); }}>
            <input aria-label="Buscar" placeholder="Nome ou CNPJ" value={busca} onChange={(e) => setBusca(e.target.value)} style={{ width: 220 }} />
            <button type="submit" className="secondary">Buscar</button>
          </form>
        }
      >
        {list.loading && <Loading />}
        <Alert>{list.error}</Alert>
        {list.data && (
          <Table head={['Nome', 'CNPJ raiz', 'Plano', 'Situação', 'XML', 'Criado', '']} empty={list.data.tenants.length === 0}>
            {list.data.tenants.map((t) => (
              <Fragment key={t.tenant_id}>
                <tr>
                  <td><strong>{t.nome}</strong><span className="sub mono">{t.tenant_id}</span></td>
                  <td className="mono nowrap">{cnpj(t.cnpj_raiz)}</td>
                  <td>{t.plano}{t.auto_provisionar_cnpj && <> <Badge>auto CNPJ</Badge></>}</td>
                  <td><StatusBadge status={t.situacao} /></td>
                  <td className="nowrap">{t.retencao_xml_dias ? `${t.retencao_xml_dias} dias` : 'não guarda'}</td>
                  <td className="nowrap">{dateTime(t.criado_em)}</td>
                  <td className="actions"><div className="row">
                    {t.situacao === 'ACTIVE' && <Confirm label="Suspender" onConfirm={() => void act(() => backoffice(`/admin/v1/tenants/${t.tenant_id}/suspend`, { method: 'POST' }))} />}
                    {t.situacao === 'SUSPENDED' && <button className="secondary" onClick={() => void act(() => backoffice(`/admin/v1/tenants/${t.tenant_id}/reactivate`, { method: 'POST' }))}>Reativar</button>}
                    <button className="secondary" onClick={() => setOpen(open === t.tenant_id ? undefined : t.tenant_id)}>OAuth ({t.clientes_oauth.filter((c) => !c.revogado_em).length})</button>
                  </div></td>
                </tr>
                {open === t.tenant_id && (
                  <tr>
                    <td colSpan={7}>
                      <OAuthClients tenant={t} onSecret={(v) => setSecret({ label: `client_secret de ${t.nome}`, value: v })} onChange={() => void list.reload()} />
                    </td>
                  </tr>
                )}
              </Fragment>
            ))}
          </Table>
        )}
      </Card>
    </>
  );
}

function OAuthClients({ tenant, onSecret, onChange }: { tenant: Tenant; onSecret: (s: string) => void; onChange: () => void }) {
  const [nome, setNome] = useState('');
  const [scopes, setScopes] = useState<string[]>(['simulacao:write', 'auditoria:write', 'usage:read']);
  const [created, setCreated] = useState<string>();
  const { busy, error, run } = useAction();
  return (
    <div className="stack">
      <Table head={['client_id', 'Nome', 'Criado', 'Situação', '']} empty={tenant.clientes_oauth.length === 0}>
        {tenant.clientes_oauth.map((c) => (
          <tr key={c.client_id}>
            <td className="mono">{c.client_id}</td>
            <td>{c.nome}</td>
            <td>{dateTime(c.criado_em)}</td>
            <td>{c.revogado_em ? <Badge tone="bad">revogado</Badge> : <Badge tone="ok">ativo</Badge>}</td>
            <td>{!c.revogado_em && <Confirm label="Revogar" onConfirm={() => void run(() => backoffice(`/admin/v1/oauth-clients/${c.client_id}`, { method: 'DELETE' })).then(onChange)} />}</td>
          </tr>
        ))}
      </Table>
      <form
        className="stack"
        onSubmit={async (e) => {
          e.preventDefault();
          const r = await run(() => backoffice<{ client_id: string; client_secret: string }>(`/admin/v1/tenants/${tenant.tenant_id}/oauth-clients`, { method: 'POST', body: { nome, scopes } }));
          if (r) {
            setCreated(r.client_id);
            onSecret(r.client_secret);
            onChange();
          }
        }}
      >
        <Field label="Novo cliente OAuth"><input value={nome} onChange={(e) => setNome(e.target.value)} placeholder="integracao-erp" required /></Field>
        <div className="grid" style={{ gap: 4 }}>
          {ALL_SCOPES.map((s) => (
            <label className="checkbox" key={s.id}>
              <input type="checkbox" checked={scopes.includes(s.id)} onChange={(e) => setScopes(e.target.checked ? [...scopes, s.id] : scopes.filter((x) => x !== s.id))} />
              <code>{s.id}</code>
            </label>
          ))}
        </div>
        <Alert>{error}</Alert>
        {created && <Alert kind="success">Cliente {created} criado.</Alert>}
        <button type="submit" disabled={busy || scopes.length === 0}>Criar cliente</button>
      </form>
    </div>
  );
}

interface Rate { id: string; tributo: string; tipo: string; esfera: string; ente_codigo: string | null; aliquota: number; valid_from: string; valid_to: string | null; ato_legal: string; fonte: string; provisoria: boolean }
interface Change { id: string; dominio: string; status: string; proposto_por: string; aprovado_por: string | null; justificativa: string; vigente_em: string; criado_em: string; payload: unknown }

const emptyLine = { tributo: 'CBS', tipo: 'REFERENCIA', esfera: 'UNIAO', ente_codigo: '', aliquota: '', valid_from: '', valid_to: '', ato_legal: '', fonte: 'SENADO', provisoria: false };

export function Aliquotas() {
  const rates = useLoad(() => backoffice<Rate[]>('/admin/v1/params/rates'));
  const [status, setStatus] = useState('PROPOSED');
  const changes = useLoad(() => backoffice<Change[]>('/admin/v1/params/changes', { query: { status } }), [status]);
  const [justificativa, setJustificativa] = useState('');
  const [vigente, setVigente] = useState('');
  const [linha, setLinha] = useState(emptyLine);
  const [substitui, setSubstitui] = useState<string[]>([]);
  const [msg, setMsg] = useState<string>();
  const { busy, error, run } = useAction();

  const propose = async (e: FormEvent) => {
    e.preventDefault();
    const r = await run(() =>
      backoffice<{ change_id: string }>('/admin/v1/params/rates/changes', {
        method: 'POST',
        body: {
          justificativa,
          vigente_em: vigente ? new Date(`${vigente}T00:00:00-03:00`).toISOString() : undefined,
          linhas: [{ ...linha, ente_codigo: linha.ente_codigo || null, aliquota: Number(linha.aliquota), valid_to: linha.valid_to || null }],
          substitui,
        },
      }));
    if (r) {
      setMsg(`Proposta ${r.change_id} criada. Outra pessoa precisa aprová-la.`);
      setLinha(emptyLine);
      setSubstitui([]);
      void changes.reload();
    }
  };

  const approve = async (id: string) => {
    if (await run(() => backoffice(`/admin/v1/params/changes/${id}/approve`, { method: 'POST' })) !== undefined) {
      setMsg('Mudança aplicada; propaga para todas as instâncias em até 5 s.');
      void changes.reload();
      void rates.reload();
    }
  };

  return (
    <>
      <PageHeader title="Alíquotas" description="Catálogo de alíquotas de referência. Toda mudança precisa de uma pessoa que propõe e outra que aprova, citando o ato legal." />
      <Alert>{error}</Alert>
      <Alert kind="success">{msg}</Alert>
      <Card title="Catálogo vigente">
        {rates.loading && <Loading />}
        <Alert>{rates.error}</Alert>
        {rates.data && (
          <Table head={['Substituir', 'Tributo', 'Tipo/Esfera', 'Ente', 'Alíquota', 'Vigência', 'Fonte', 'Ato legal']} empty={rates.data.length === 0}>
            {rates.data.map((r) => (
              <tr key={r.id}>
                <td><input type="checkbox" aria-label={`Substituir ${r.tributo} ${r.valid_from}`} checked={substitui.includes(r.id)} onChange={(e) => setSubstitui(e.target.checked ? [...substitui, r.id] : substitui.filter((x) => x !== r.id))} /></td>
                <td><strong>{r.tributo}</strong>{r.provisoria && <> <Badge tone="warn">provisória</Badge></>}</td>
                <td>{r.tipo} · {r.esfera}</td>
                <td>{r.ente_codigo ?? '—'}</td>
                <td className="num">{r.aliquota.toLocaleString('pt-BR', { minimumFractionDigits: 2, maximumFractionDigits: 4 })}%</td>
                <td>{r.valid_from} → {r.valid_to ?? 'indefinido'}</td>
                <td>{r.fonte}</td>
                <td>{r.ato_legal}</td>
              </tr>
            ))}
          </Table>
        )}
      </Card>
      <div style={{ height: 16 }} />
      <Card title="Propor mudança (fluxo de duas pessoas)">
        <form className="stack" onSubmit={propose}>
          <Field label="Justificativa" hint="Cite o ato publicado (DOU, resolução, lei)."><input value={justificativa} onChange={(e) => setJustificativa(e.target.value)} required /></Field>
          <div className="grid">
            <Field label="Tributo"><select value={linha.tributo} onChange={(e) => setLinha({ ...linha, tributo: e.target.value })}>{['CBS', 'IBS_UF', 'IBS_MUN', 'IS'].map((x) => <option key={x}>{x}</option>)}</select></Field>
            <Field label="Tipo"><select value={linha.tipo} onChange={(e) => setLinha({ ...linha, tipo: e.target.value })}>{['REFERENCIA', 'PROPRIA_ENTE'].map((x) => <option key={x}>{x}</option>)}</select></Field>
            <Field label="Esfera"><select value={linha.esfera} onChange={(e) => setLinha({ ...linha, esfera: e.target.value })}>{['UNIAO', 'UF', 'MUNICIPIO'].map((x) => <option key={x}>{x}</option>)}</select></Field>
            <Field label="Código do ente" hint="Só para alíquota própria de UF/município."><input value={linha.ente_codigo} onChange={(e) => setLinha({ ...linha, ente_codigo: e.target.value })} /></Field>
            <Field label="Alíquota (%)"><input type="number" step="0.0001" min="0" value={linha.aliquota} onChange={(e) => setLinha({ ...linha, aliquota: e.target.value })} required /></Field>
            <Field label="Vigente de"><input type="date" value={linha.valid_from} onChange={(e) => setLinha({ ...linha, valid_from: e.target.value })} required /></Field>
            <Field label="Vigente até (exclusive)"><input type="date" value={linha.valid_to} onChange={(e) => setLinha({ ...linha, valid_to: e.target.value })} /></Field>
            <Field label="Fonte"><select value={linha.fonte} onChange={(e) => setLinha({ ...linha, fonte: e.target.value })}>{['LEI_FEDERAL', 'SENADO', 'TCU', 'CG_IBS', 'LEI_ENTE', 'BUNDLE_RFB'].map((x) => <option key={x}>{x}</option>)}</select></Field>
            <Field label="Aplicar a partir de" hint="Momento em que a mudança passa a valer no sistema."><input type="date" value={vigente} onChange={(e) => setVigente(e.target.value)} /></Field>
          </div>
          <Field label="Ato legal"><input value={linha.ato_legal} onChange={(e) => setLinha({ ...linha, ato_legal: e.target.value })} required /></Field>
          <label className="checkbox"><input type="checkbox" checked={linha.provisoria} onChange={(e) => setLinha({ ...linha, provisoria: e.target.checked })} /> Provisória</label>
          {substitui.length > 0 && <p className="muted">Substitui {substitui.length} linha(s) marcada(s) no catálogo.</p>}
          <button type="submit" disabled={busy}>Propor</button>
        </form>
      </Card>
      <div style={{ height: 16 }} />
      <Card
        title="Mudanças"
        actions={
          <select aria-label="Situação" value={status} onChange={(e) => setStatus(e.target.value)} style={{ width: 'auto' }}>
            {['PROPOSED', 'APPLIED', 'REJECTED', ''].map((s) => <option key={s} value={s}>{s || 'Todas'}</option>)}
          </select>
        }
      >
        {changes.loading && <Loading />}
        <Alert>{changes.error}</Alert>
        {changes.data && (
          <Table head={['Criada', 'Situação', 'Proposta por', 'Aprovada por', 'Justificativa', '']} empty={changes.data.length === 0}>
            {changes.data.map((c) => (
              <tr key={c.id}>
                <td>{dateTime(c.criado_em)}</td>
                <td><StatusBadge status={c.status} /></td>
                <td>{c.proposto_por}</td>
                <td>{c.aprovado_por ?? '—'}</td>
                <td>{c.justificativa}<details><summary className="muted">linhas</summary><Json value={c.payload} /></details></td>
                <td>{c.status === 'PROPOSED' && <Confirm tone="secondary" label="Aprovar e aplicar" onConfirm={() => void approve(c.id)} />}</td>
              </tr>
            ))}
          </Table>
        )}
      </Card>
    </>
  );
}

interface Reconcile { competencia: string; tenants: number; divergencias: Record<string, unknown>[] }

export function Faturamento() {
  const [mes, setMes] = useState(previousMonth());
  const [rec, setRec] = useState<Reconcile>();
  const [closed, setClosed] = useState<string>();
  const [adj, setAdj] = useState({ tenant_id: '', valor: '', motivo: '' });
  const { busy, error, run } = useAction();
  const notConfigured = error?.includes('HTTP_404');

  return (
    <>
      <PageHeader title="Faturamento" description="Fechamento mensal, reconciliação com o provedor de cobrança e notas de ajuste.">
        <input type="month" aria-label="Competência" value={mes} onChange={(e) => setMes(e.target.value)} style={{ width: 'auto' }} />
      </PageHeader>
      {notConfigured ? <Alert kind="warning">Faturamento não está habilitado nesta instância (ConnectionStrings:Billing).</Alert> : <Alert>{error}</Alert>}
      <div className="grid" style={{ alignItems: 'start' }}>
        <Card title="Fechar e faturar">
          <p className="muted">Fecha os períodos abertos da competência e envia ao provedor. Repetir não duplica cobrança.</p>
          <Confirm tone="secondary" label={`Fechar ${mes}`} disabled={busy} onConfirm={async () => {
            const r = await run(() => backoffice<{ periodos_fechados: number; faturados: number }>('/admin/v1/billing/close', { method: 'POST', query: { mes } }));
            if (r) setClosed(`${r.periodos_fechados} período(s) fechado(s), ${r.faturados} faturado(s).`);
          }} />
          <Alert kind="success">{closed}</Alert>
        </Card>
        <Card title="Reconciliação">
          <button type="button" disabled={busy} onClick={async () => setRec(await run(() => backoffice<Reconcile>('/admin/v1/billing/reconcile', { query: { mes } })))}>Reconciliar {mes}</button>
          {rec && (
            <div style={{ marginTop: 12 }}>
              <p>{rec.tenants} tenant(s) · {rec.divergencias.length === 0 ? <Badge tone="ok">sem divergências</Badge> : <Badge tone="bad">{rec.divergencias.length} divergência(s)</Badge>}</p>
              {rec.divergencias.length > 0 && <Json value={rec.divergencias} />}
            </div>
          )}
        </Card>
      </div>
      <div style={{ height: 16 }} />
      <Card title="Nota de ajuste (v2, v3…)">
        <form className="stack" onSubmit={async (e) => {
          e.preventDefault();
          const cents = Math.round(Number(adj.valor) * 100);
          const r = await run(() => backoffice('/admin/v1/billing/adjustments', { method: 'POST', body: { tenant_id: adj.tenant_id, competencia: mes, valor_centavos: cents, motivo: adj.motivo } }));
          if (r) setClosed(`Ajuste de ${money(cents / 100)} registrado.`);
        }}>
          <div className="grid">
            <Field label="Tenant (id)"><input value={adj.tenant_id} onChange={(e) => setAdj({ ...adj, tenant_id: e.target.value })} required /></Field>
            <Field label="Valor (R$)" hint="Negativo = crédito a abater."><input type="number" step="0.01" value={adj.valor} onChange={(e) => setAdj({ ...adj, valor: e.target.value })} required /></Field>
          </div>
          <Field label="Motivo"><input value={adj.motivo} onChange={(e) => setAdj({ ...adj, motivo: e.target.value })} required /></Field>
          <button type="submit" disabled={busy}>Registrar ajuste</button>
        </form>
      </Card>
    </>
  );
}

export function Tokens() {
  const [jti, setJti] = useState('');
  const [exp, setExp] = useState('');
  const [ok, setOk] = useState<string>();
  const { busy, error, run } = useAction();
  return (
    <>
      <PageHeader title="Revogar token OAuth" description="Corte imediato de um access token específico, por exemplo em caso de vazamento." />
      <Card>
        <p className="muted">Revoga um access token específico pelo <code>jti</code> (efeito imediato em todas as instâncias). Para cortar um cliente inteiro, revogue o cliente na tela de tenants.</p>
        <form className="stack" onSubmit={async (e) => {
          e.preventDefault();
          const r = await run(() => backoffice('/admin/v1/oauth/revoked-tokens', { method: 'POST', body: { jti, expira_em: new Date(exp).toISOString() } }));
          if (r !== undefined) setOk(`Token ${jti} revogado.`);
        }}>
          <Field label="jti"><input value={jti} onChange={(e) => setJti(e.target.value)} required /></Field>
          <Field label="Expira em (exp do token)"><input type="datetime-local" value={exp} onChange={(e) => setExp(e.target.value)} required /></Field>
          <Alert>{error}</Alert>
          <Alert kind="success">{ok}</Alert>
          <button type="submit" disabled={busy}>Revogar</button>
        </form>
      </Card>
    </>
  );
}

function previousMonth(): string {
  const [y, m] = currentMonth().split('-').map(Number);
  return m === 1 ? `${y - 1}-12` : `${y}-${String(m - 1).padStart(2, '0')}`;
}

