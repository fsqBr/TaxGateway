import { Fragment, useState, type FormEvent } from 'react';
import { Route, Routes } from 'react-router-dom';
import { Ban, Building2, History, Layers3, Percent, Receipt, Scale, UserCog } from 'lucide-react';
import { Operadores, Trilha } from './Operadores';
import { Planos } from './Planos';
import { Disputas } from './Disputas';
import { Aliquotas } from './Aliquotas';
import { LimitesContrato, type Limites } from './LimitesContrato';
import { useSession } from '../auth/session';
import { Shell, type NavItem } from '../components/Shell';
import { ConfirmButton } from '../components/ConfirmDialog';
import { Alert, Badge, Card, Confirm, Field, Json, Loading, PageHeader, SecretOnce, StatusBadge, Table, useAction, useLoad } from '../components/ui';
import { backoffice } from '../lib/api';
import { cnpj, currentMonth, dateTime, integer, money, onlyCnpjChars } from '../lib/format';
import { Drawer } from '../components/inputs';
import { ALL_SCOPES } from '../lib/scopes';

interface Operador { operador: string; scopes: string[] }

const nav: NavItem[] = [
  { to: '/backoffice', label: 'Tenants', icon: Building2, section: 'Clientes' },
  { to: '/backoffice/tokens', label: 'Revogar token', icon: Ban, section: 'Clientes' },
  { to: '/backoffice/aliquotas', label: 'Alíquotas', icon: Percent, section: 'Fiscal', scope: 'admin:fiscal' },
  { to: '/backoffice/planos', label: 'Planos', icon: Layers3, section: 'Financeiro', scope: 'admin:commercial' },
  { to: '/backoffice/faturamento', label: 'Faturamento', icon: Receipt, section: 'Financeiro' },
  { to: '/backoffice/operadores', label: 'Operadores', icon: UserCog, section: 'Segurança', scope: 'admin:security' },
  { to: '/backoffice/disputas', label: 'Disputas de CNPJ', icon: Scale, section: 'Segurança', scope: 'admin:security' },
  { to: '/backoffice/trilha', label: 'Trilha de auditoria', icon: History, section: 'Segurança', scope: 'admin:security' },
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
        <Route path="operadores" element={<Operadores />} />
        <Route path="planos" element={<Planos />} />
        <Route path="disputas" element={<Disputas />} />
        <Route path="trilha" element={<Trilha />} />
      </Routes>
    </Shell>
  );
}

interface Tenant {
  tenant_id: string; nome: string; cnpj_raiz: string; plano: string; situacao: string; auto_provisionar_cnpj: boolean;
  retencao_xml_dias: number | null; criado_em: string; clientes_oauth: { client_id: string; nome: string; revogado_em: string | null; criado_em: string }[];
}

const PAGE = 25;

export function Tenants() {
  const [busca, setBusca] = useState('');
  const [q, setQ] = useState('');
  const [pagina, setPagina] = useState(1);
  const [detalhe, setDetalhe] = useState<Tenant>();
  const list = useLoad(() => backoffice<{ tenants: Tenant[]; total: number; pagina: number }>('/admin/v1/tenants', { query: { busca: q, limite: PAGE, pagina } }), [q, pagina]);
  const paginas = Math.max(1, Math.ceil((list.data?.total ?? 0) / PAGE));
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
          <form className="row" onSubmit={(e) => { e.preventDefault(); setPagina(1); setQ(busca); }}>
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
                    {t.situacao === 'ACTIVE' && (
                      <ConfirmButton label="Suspender" title={`Suspender ${t.nome}?`} confirmLabel="Suspender cliente" typeToConfirm="SUSPENDER"
                        impact={`Todas as chamadas de ${t.nome} (${cnpj(t.cnpj_raiz)}) — API keys, tokens OAuth e portal — passam a ser recusadas com 403 em até 5 segundos. A ação fica registrada na trilha de auditoria.`}
                        onConfirm={() => void act(() => backoffice(`/admin/v1/tenants/${t.tenant_id}/suspend`, { method: 'POST' }))} />
                    )}
                    {t.situacao === 'SUSPENDED' && <button className="secondary" onClick={() => void act(() => backoffice(`/admin/v1/tenants/${t.tenant_id}/reactivate`, { method: 'POST' }))}>Reativar</button>}
                    <button className="secondary" onClick={() => setDetalhe(t)}>Detalhe</button>
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
        {list.data && list.data.total > PAGE && (
          <nav className="pager" aria-label="Páginas">
            <button className="secondary" disabled={pagina <= 1} onClick={() => setPagina(pagina - 1)}>Anterior</button>
            <span className="muted small">Página {pagina} de {paginas} · {list.data.total} tenants</span>
            <button className="secondary" disabled={pagina >= paginas} onClick={() => setPagina(pagina + 1)}>Próxima</button>
          </nav>
        )}
      </Card>
      {detalhe && <TenantDetalhe tenant={detalhe} planos={(plans.data?.planos ?? []).map((p) => p.codigo)} onClose={() => setDetalhe(undefined)} onChange={() => void list.reload()} />}
    </>
  );
}

interface TenantDetail {
  tenant: { tenant_id: string; nome: string; cnpj_raiz: string; plano: string; situacao: string; criado_em: string;
    cobranca: { provedor: string | null; cliente: string | null; assinatura: string | null; situacao_assinatura: string | null; fim_do_periodo: string | null };
    teto_centavos: number; verificada_em: string | null; metodo_verificacao: string | null; verificada_por: string | null;
    inadimplente_desde: string | null; motivo_suspensao: string | null; limites?: Limites | null };
  pessoas: { nome: string; email: string; papel: string; mfa: boolean; situacao: string; ultimo_acesso: string | null }[];
  cnpjs: { cnpj: string; ativo: boolean; criado_em: string }[];
  api_keys: { nome: string; prefixo: string; scopes: string[]; expira_em: string | null; revogada_em: string | null; ultimo_uso: string | null; criada_em: string }[];
  consumo: { competencia: string; documentos: number | null; simulacoes: number | null }[];
  faturas: { numero: string | null; situacao: string; valor_cents: number; pago_cents: number; inicio: string | null; fim: string | null }[];
}

/** Detalhe do cliente para o atendimento (D-J07): quem acessa, CNPJs, chaves, consumo, faturas e troca de plano. */
function TenantDetalhe({ tenant, planos, onClose, onChange }: { tenant: Tenant; planos: string[]; onClose: () => void; onChange: () => void }) {
  const d = useLoad(() => backoffice<TenantDetail>(`/admin/v1/tenants/${tenant.tenant_id}`), [tenant.tenant_id]);
  const [plano, setPlano] = useState(tenant.plano);
  const { busy, error, run } = useAction();
  const trocar = async () => {
    if (await run(() => backoffice(`/admin/v1/tenants/${tenant.tenant_id}/plano`, { method: 'POST', body: { plano } })) !== undefined) {
      onChange();
      await d.reload();
    }
  };
  const t = d.data?.tenant;
  const { can } = useSession();
  const [teto, setTeto] = useState<string>();
  const [obs, setObs] = useState('');
  const salvarTeto = async () => {
    const centavos = Math.round(Number((teto ?? '0').replace(',', '.')) * 100);
    if (await run(() => backoffice(`/admin/v1/tenants/${tenant.tenant_id}/teto`, { method: 'PUT', body: { teto_centavos: centavos } })) !== undefined) {
      setTeto(undefined);
      await d.reload();
    }
  };
  const verificar = async () => {
    if (await run(() => backoffice(`/admin/v1/tenants/${tenant.tenant_id}/verificar`, { method: 'POST', body: { observacao: obs } })) !== undefined) {
      setObs('');
      await d.reload();
    }
  };
  return (
    <Drawer title={tenant.nome} onClose={onClose} wide>
      {d.loading && <Loading />}
      <Alert>{d.error ?? error}</Alert>
      {d.data && t && (
        <div className="stack">
          <dl className="kv">
            <div><dt>CNPJ raiz</dt><dd className="mono">{cnpj(t.cnpj_raiz)}</dd></div>
            <div><dt>Situação</dt><dd><StatusBadge status={t.situacao} /></dd></div>
            <div><dt>Plano</dt><dd>{t.plano}</dd></div>
            <div><dt>Assinatura</dt><dd>{t.cobranca.situacao_assinatura ?? 'sem assinatura'}{t.cobranca.fim_do_periodo && <span className="sub">até {dateTime(t.cobranca.fim_do_periodo)}</span>}</dd></div>
            <div><dt>Cliente desde</dt><dd>{dateTime(t.criado_em)}</dd></div>
            <div><dt>Titularidade</dt><dd>{t.verificada_em ? <><Badge tone="ok">verificada</Badge><span className="sub">{t.metodo_verificacao} · {t.verificada_por ?? '—'} · {dateTime(t.verificada_em)}</span></> : <Badge tone="warn">não verificada</Badge>}</dd></div>
            <div><dt>Teto de excedente</dt><dd>{t.teto_centavos === 0 ? 'zero (para na franquia)' : money(t.teto_centavos / 100)}</dd></div>
            {t.inadimplente_desde && <div><dt>Inadimplente desde</dt><dd><Badge tone="bad">{dateTime(t.inadimplente_desde)}</Badge>{t.motivo_suspensao && <span className="sub">{t.motivo_suspensao}</span>}</dd></div>}
          </dl>
          {can('admin:commercial') && (
            <Card title="Teto de excedente" description="Quanto o cliente autoriza pagar além da franquia por mês. 0 = o consumo para ao esgotar a franquia. Fica na trilha.">
              <div className="row">
                <input aria-label="Teto em reais" inputMode="decimal" value={teto ?? String(t.teto_centavos / 100)} onChange={(e) => setTeto(e.target.value)} style={{ maxWidth: 160 }} />
                <button type="button" className="secondary" disabled={busy || teto === undefined} onClick={() => void salvarTeto()}>Salvar teto</button>
              </div>
            </Card>
          )}
          {can('admin:commercial') && <LimitesContrato tenantId={t.tenant_id} nome={t.nome} atuais={t.limites} />}
          {can('admin:security') && !t.verificada_em && (
            <Card title="Verificar titularidade manualmente" description="Só depois de conferir contrato social ou documento equivalente. Registre o que foi conferido — fica na trilha.">
              <div className="row">
                <input aria-label="Documento conferido" placeholder="Ex.: contrato social 3ª alteração, JUCESP 2025" maxLength={500} value={obs} onChange={(e) => setObs(e.target.value)} />
                <ConfirmButton label="Marcar verificada" tone="secondary" disabled={busy || obs.trim().length < 5} title={`Verificar a titularidade de ${t.nome}?`}
                  impact="Libera plano pago e captura de DF-e e impede contestações futuras deste CNPJ." confirmLabel="Marcar verificada" onConfirm={verificar} />
              </div>
            </Card>
          )}
          <Card title="Trocar plano" description="Vale na hora para limites e franquia; a cobrança da Stripe segue a assinatura do cliente. Fica na trilha de auditoria.">
            <div className="row">
              <select aria-label="Novo plano" value={plano} onChange={(e) => setPlano(e.target.value)} style={{ width: 'auto' }}>
                {planos.map((p) => <option key={p}>{p}</option>)}
              </select>
              <ConfirmButton label="Aplicar" tone="secondary" disabled={busy || plano === t.plano} title={`Trocar ${t.nome} para ${plano}?`}
                impact={`Limites, franquia e excedente de ${plano} passam a valer imediatamente para ${t.nome}.`} confirmLabel="Trocar plano" onConfirm={trocar} />
            </div>
          </Card>
          <h3>Pessoas com acesso</h3>
          <Table head={['Nome', 'E-mail', 'Papel', 'MFA', 'Último acesso']} empty={d.data.pessoas.length === 0} emptyText="Nenhuma pessoa (cliente só por API).">
            {d.data.pessoas.map((p) => (
              <tr key={p.email}><td data-label="Nome">{p.nome}</td><td data-label="E-mail">{p.email}</td><td data-label="Papel">{p.papel}</td>
                <td data-label="MFA">{p.mfa ? <Badge tone="ok">ativo</Badge> : <Badge tone="warn">pendente</Badge>}</td><td data-label="Último acesso">{dateTime(p.ultimo_acesso)}</td></tr>
            ))}
          </Table>
          <h3>Consumo (últimos 3 meses)</h3>
          <Table head={['Competência', 'Documentos', 'Simulações']} empty={d.data.consumo.length === 0}>
            {d.data.consumo.map((c) => <tr key={c.competencia}><td data-label="Competência">{c.competencia}</td><td data-label="Documentos" className="num">{integer(c.documentos ?? 0)}</td><td data-label="Simulações" className="num">{integer(c.simulacoes ?? 0)}</td></tr>)}
          </Table>
          <h3>CNPJs ({d.data.cnpjs.length})</h3>
          <Table head={['CNPJ', 'Situação', 'Desde']} empty={d.data.cnpjs.length === 0}>
            {d.data.cnpjs.slice(0, 50).map((c) => <tr key={c.cnpj}><td data-label="CNPJ" className="mono">{cnpj(c.cnpj)}</td><td data-label="Situação">{c.ativo ? 'ativo' : 'inativo'}</td><td data-label="Desde">{dateTime(c.criado_em)}</td></tr>)}
          </Table>
          <h3>API keys</h3>
          <Table head={['Nome', 'Prefixo', 'Escopos', 'Último uso', 'Situação']} empty={d.data.api_keys.length === 0}>
            {d.data.api_keys.map((k) => <tr key={k.prefixo}><td data-label="Nome">{k.nome}</td><td data-label="Prefixo" className="mono">{k.prefixo}</td><td data-label="Escopos" className="small">{k.scopes.join(', ')}</td><td data-label="Último uso">{dateTime(k.ultimo_uso)}</td><td data-label="Situação">{k.revogada_em ? <Badge tone="bad">revogada</Badge> : <Badge tone="ok">ativa</Badge>}</td></tr>)}
          </Table>
          <h3>Faturas</h3>
          <Table head={['Número', 'Situação', 'Valor', 'Período']} empty={d.data.faturas.length === 0} emptyText="Nenhuma fatura ainda.">
            {d.data.faturas.map((f, i) => <tr key={f.numero ?? i}><td data-label="Número">{f.numero ?? '—'}</td><td data-label="Situação">{f.situacao}</td><td data-label="Valor" className="num">{money(f.valor_cents / 100)}</td><td data-label="Período">{dateTime(f.inicio)} – {dateTime(f.fim)}</td></tr>)}
          </Table>
        </div>
      )}
    </Drawer>
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
            <td>{!c.revogado_em && <ConfirmButton label="Revogar" title={`Revogar o cliente OAuth "${c.nome}"?`} confirmLabel="Revogar cliente"
              impact="Tokens já emitidos para este cliente deixam de valer na hora e ele não consegue emitir novos."
              onConfirm={() => void run(() => backoffice(`/admin/v1/oauth-clients/${c.client_id}`, { method: 'DELETE' })).then(onChange)} />}</td>
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

interface Reconcile { competencia: string; tenants: number; divergencias: Record<string, unknown>[] }

export function Faturamento() {
  const [mes, setMes] = useState(previousMonth());
  const [rec, setRec] = useState<Reconcile>();
  const [closed, setClosed] = useState<string>();
  const [adj, setAdj] = useState({ tenant_id: '', valor: '', motivo: '' });
  const { busy, error, errorCode, run } = useAction();
  const notConfigured = errorCode === 'HTTP_404';   // rotas de faturamento só existem com ConnectionStrings:Billing

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

