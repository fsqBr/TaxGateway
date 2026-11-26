import { useState, type FormEvent } from 'react';
import { Alert, Badge, Card, Field, Loading, PageHeader, Table, useAction, useLoad } from '../components/ui';
import { ConfirmButton } from '../components/ConfirmDialog';
import { backoffice } from '../lib/api';
import { dateTime } from '../lib/format';

interface Operador { id: string; email: string; nome: string; escopos: string[]; mfa: boolean; situacao: string; ultimo_acesso: string | null; criado_em: string }

/** Operadores do backoffice com conta e MFA (D-U08). O convite chega por e-mail (definir senha + MFA). */
export function Operadores() {
  const list = useLoad(() => backoffice<Operador[]>('/admin/v1/operadores'));
  const { busy, error, run } = useAction();
  const [form, setForm] = useState({ email: '', nome: '', commercial: true, fiscal: false });
  const [sent, setSent] = useState<string>();

  const create = async (e: FormEvent) => {
    e.preventDefault();
    const escopos = [form.commercial && 'admin:commercial', form.fiscal && 'admin:fiscal'].filter(Boolean);
    if (await run(() => backoffice('/admin/v1/operadores', { method: 'POST', body: { email: form.email, nome: form.nome, escopos } })) !== undefined) {
      setSent(form.email);
      setForm({ ...form, email: '', nome: '' });
      void list.reload();
    }
  };

  return (
    <>
      <PageHeader title="Operadores" description="Pessoas do time TaxGateway com acesso ao backoffice. Todas entram com senha e verificação em duas etapas." />
      <Card title="Convidar operador">
        <form className="row" onSubmit={create} style={{ alignItems: 'flex-end', flexWrap: 'wrap' }}>
          <Field label="Nome"><input value={form.nome} onChange={(e) => setForm({ ...form, nome: e.target.value })} required /></Field>
          <Field label="E-mail"><input type="email" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} required /></Field>
          <label className="checkbox small"><input type="checkbox" checked={form.commercial} onChange={(e) => setForm({ ...form, commercial: e.target.checked })} /> Comercial (clientes, planos, faturamento)</label>
          <label className="checkbox small"><input type="checkbox" checked={form.fiscal} onChange={(e) => setForm({ ...form, fiscal: e.target.checked })} /> Fiscal (alíquotas)</label>
          <button type="submit" disabled={busy || (!form.commercial && !form.fiscal)}>Enviar convite</button>
        </form>
        {sent && <Alert kind="success">Convite enviado para {sent} (vale 24 h).</Alert>}
        <Alert>{error ?? list.error}</Alert>
      </Card>
      <Card title="Operadores" flush>
        {list.loading ? <Loading /> : (
          <Table head={['Nome', 'E-mail', 'Escopos', 'MFA', 'Situação', 'Último acesso', '']} empty={!list.data?.length}>
            {list.data?.map((o) => (
              <tr key={o.id}>
                <td>{o.nome}</td>
                <td className="small">{o.email}</td>
                <td>{o.escopos.map((s) => <Badge key={s} tone="neutral">{s}</Badge>)}</td>
                <td>{o.mfa ? <Badge tone="ok">ativo</Badge> : <Badge tone="warn">pendente</Badge>}</td>
                <td>{o.situacao === 'ACTIVE' ? <Badge tone="ok">ativo</Badge> : <Badge tone="bad">desativado</Badge>}</td>
                <td>{o.ultimo_acesso ? dateTime(o.ultimo_acesso) : '—'}</td>
                <td>{o.situacao === 'ACTIVE' && (
                  <ConfirmButton label="Desativar" title={`Desativar ${o.nome}?`} confirmLabel="Desativar operador"
                    impact={`${o.email} perde o acesso ao backoffice na próxima requisição. A ação fica na trilha de auditoria.`}
                    onConfirm={() => run(() => backoffice(`/admin/v1/operadores/${o.id}/desativar`, { method: 'POST' })).then(() => list.reload())} />
                )}</td>
              </tr>
            ))}
          </Table>
        )}
      </Card>
    </>
  );
}

interface Evento { em: string; tipo_ator: string; ator: string | null; acao: string; tenant_id: string | null; alvo: string | null; ip: string | null; detalhe: Record<string, unknown> }

const ACAO: Record<string, string> = {
  'login.senha_ok': 'Senha correta', 'login.mfa_ok': 'Entrou (MFA)', 'login.falhou': 'Falha de login', logout: 'Saiu',
  signup: 'Cadastro', 'email.confirmado': 'E-mail confirmado', 'mfa.ativado': 'MFA ativado', 'senha.redefinida': 'Senha redefinida',
  'senha.alterada': 'Senha alterada', 'membro.convidado': 'Convidou pessoa', 'membro.entrou': 'Pessoa entrou', 'membro.removido': 'Removeu pessoa',
  'membro.papel_alterado': 'Mudou papel', 'operador.criado': 'Operador criado', 'operador.desativado': 'Operador desativado',
};

/** Trilha de auditoria (D-U07): acessos, MFA, senha, equipe e ações de operador. */
export function Trilha() {
  const [filtro, setFiltro] = useState({ tenant: '', ator: '' });
  const [q, setQ] = useState(filtro);
  const list = useLoad(() => backoffice<Evento[]>('/admin/v1/auditoria-acoes', { query: { tenant: q.tenant || undefined, ator: q.ator || undefined } }), [q]);
  return (
    <>
      <PageHeader title="Trilha de auditoria" description="Últimos 200 eventos de segurança e de administração. Somente leitura." />
      <Card>
        <form className="row" onSubmit={(e) => { e.preventDefault(); setQ(filtro); }} style={{ alignItems: 'flex-end', flexWrap: 'wrap' }}>
          <Field label="Tenant (id)"><input value={filtro.tenant} onChange={(e) => setFiltro({ ...filtro, tenant: e.target.value.trim() })} placeholder="opcional" /></Field>
          <Field label="Ator (id ou e-mail)"><input value={filtro.ator} onChange={(e) => setFiltro({ ...filtro, ator: e.target.value.trim() })} placeholder="opcional" /></Field>
          <button type="submit" className="secondary">Filtrar</button>
        </form>
      </Card>
      <Card flush>
        {list.loading ? <Loading /> : (
          <Table head={['Quando', 'Ação', 'Ator', 'Alvo', 'IP']} empty={!list.data?.length}>
            {list.data?.map((e, i) => (
              <tr key={i}>
                <td>{dateTime(e.em)}</td>
                <td>{ACAO[e.acao] ?? e.acao}</td>
                <td className="small mono">{e.tipo_ator}{e.ator ? `: ${e.ator.slice(0, 36)}` : ''}</td>
                <td className="small mono">{e.alvo ?? '—'}</td>
                <td className="small mono">{e.ip ?? '—'}</td>
              </tr>
            ))}
          </Table>
        )}
        <Alert>{list.error}</Alert>
      </Card>
    </>
  );
}
