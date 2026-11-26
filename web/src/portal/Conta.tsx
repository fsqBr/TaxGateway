import { useState, type FormEvent } from 'react';
import { MonitorSmartphone, ShieldCheck, UserPlus } from 'lucide-react';
import { RecoveryCodes } from '../auth/AuthPages';
import { useSession } from '../auth/session';
import { Alert, Badge, Card, Field, Loading, PageHeader, Table, useAction, useLoad } from '../components/ui';
import { ConfirmButton } from '../components/ConfirmDialog';
import { account } from '../lib/api';
import { dateTime } from '../lib/format';

export const ROLE_LABEL: Record<string, string> = {
  owner: 'Dono', admin: 'Administrador', fiscal: 'Fiscal', developer: 'Desenvolvedor', finance: 'Financeiro', viewer: 'Leitura',
};

const ROLE_HELP: Record<string, string> = {
  admin: 'Tudo, inclusive equipe e cobrança',
  fiscal: 'Simular, auditar, DF-e, CNPJs e LGPD',
  developer: 'Simular, auditar, chaves e webhooks',
  finance: 'Consumo, plano, faturas e consulta de documentos',
  viewer: 'Consulta de consumo e documentos auditados (sem enviar nem alterar)',
};

/** Minha conta: senha, verificação em duas etapas e sessões abertas (D-U01). */
export function MinhaConta() {
  const { sessao, reload } = useSession();
  const sessions = useLoad(() => account<{ id: string; ip: string | null; navegador: string | null; criada_em: string; ultimo_uso: string; atual: boolean }[]>('portal', '/sessoes'));
  const pw = useAction();
  const mfa = useAction();
  const out = useAction();
  const [form, setForm] = useState({ atual: '', nova: '' });
  const [pwOk, setPwOk] = useState(false);
  const [code, setCode] = useState('');
  const [codes, setCodes] = useState<string[]>();

  const changePassword = async (e: FormEvent) => {
    e.preventDefault();
    setPwOk(false);
    if (await pw.run(() => account('portal', '/senha/alterar', { method: 'POST', body: form })) !== undefined) {
      setForm({ atual: '', nova: '' });
      setPwOk(true);
      void sessions.reload();
    }
  };

  return (
    <>
      <PageHeader title="Minha conta" description={sessao ? `${sessao.usuario.nome} · ${sessao.usuario.email}` : undefined} />
      <div className="grid-2">
        <Card title="Senha" description="Trocar a senha encerra as outras sessões abertas.">
          <form onSubmit={changePassword} className="stack">
            <Field label="Senha atual"><input type="password" autoComplete="current-password" value={form.atual} onChange={(e) => setForm({ ...form, atual: e.target.value })} required /></Field>
            <Field label="Nova senha" hint="Mínimo de 12 caracteres."><input type="password" autoComplete="new-password" minLength={12} value={form.nova} onChange={(e) => setForm({ ...form, nova: e.target.value })} required /></Field>
            <Alert>{pw.error}</Alert>
            {pwOk && <Alert kind="success">Senha alterada.</Alert>}
            <div><button type="submit" disabled={pw.busy}>Alterar senha</button></div>
          </form>
        </Card>
        <Card title="Verificação em duas etapas" description="Obrigatória para todas as contas.">
          <div className="stack">
            <p className="row"><ShieldCheck size={18} aria-hidden="true" color="var(--ok)" /> Ativa com app autenticador.</p>
            <p className="muted small">Códigos de recuperação restantes: <strong>{sessao?.codigos_recuperacao_restantes ?? '—'}</strong> de 10.</p>
            {codes ? <RecoveryCodes codes={codes} /> : (
              <form className="row" onSubmit={async (e) => {
                e.preventDefault();
                const r = await mfa.run(() => account<{ codigos_recuperacao: string[] }>('portal', '/mfa/novos-codigos', { method: 'POST', body: { codigo: code } }));
                if (r) { setCodes(r.codigos_recuperacao); setCode(''); void reload(); }
              }}>
                <input inputMode="numeric" aria-label="Código do app" placeholder="Código do app" maxLength={6} value={code} onChange={(e) => setCode(e.target.value.replace(/\D/g, ''))} style={{ maxWidth: 160 }} />
                <button type="submit" className="secondary" disabled={mfa.busy || code.length !== 6}>Gerar novos códigos</button>
              </form>
            )}
            <Alert>{mfa.error}</Alert>
          </div>
        </Card>
      </div>
      <Card title="Sessões abertas" description="Onde sua conta está conectada. Saia das que você não reconhece."
        actions={<button type="button" className="secondary" disabled={out.busy} onClick={async () => { await out.run(() => account('portal', '/sair-outras', { method: 'POST' })); void sessions.reload(); }}>Sair das outras sessões</button>}>
        {sessions.loading ? <Loading /> : (
          <Table head={['Dispositivo', 'IP', 'Entrou em', 'Último uso', '']} empty={!sessions.data?.length}>
            {sessions.data?.map((s) => (
              <tr key={s.id}>
                <td><MonitorSmartphone size={14} aria-hidden="true" /> <span className="small">{browserLabel(s.navegador)}</span></td>
                <td className="mono small">{s.ip ?? '—'}</td>
                <td>{dateTime(s.criada_em)}</td>
                <td>{dateTime(s.ultimo_uso)}</td>
                <td>{s.atual && <Badge tone="ok">esta sessão</Badge>}</td>
              </tr>
            ))}
          </Table>
        )}
        <Alert>{out.error ?? sessions.error}</Alert>
      </Card>
    </>
  );
}

function browserLabel(ua: string | null): string {
  if (!ua) return 'Desconhecido';
  const b = /Edg\//.test(ua) ? 'Edge' : /Chrome\//.test(ua) ? 'Chrome' : /Firefox\//.test(ua) ? 'Firefox' : /Safari\//.test(ua) ? 'Safari' : 'Navegador';
  const o = /Windows/.test(ua) ? 'Windows' : /Mac OS/.test(ua) ? 'macOS' : /Android/.test(ua) ? 'Android' : /iPhone|iPad/.test(ua) ? 'iOS' : /Linux/.test(ua) ? 'Linux' : '';
  return o ? `${b} · ${o}` : b;
}

interface Membros {
  membros: { user_id: string; nome: string; email: string; papel: string; desde: string; mfa: boolean; ultimo_acesso: string | null }[];
  convites: { email: string; papel: string; expira_em: string; criado_em: string }[];
}

/** Equipe: pessoas com acesso, papéis e convites (D-U05). Só dono e administrador. */
export function Equipe() {
  const { sessao } = useSession();
  const list = useLoad(() => account<Membros>('portal', '/membros'));
  const { busy, error, run } = useAction();
  const [form, setForm] = useState({ email: '', papel: 'fiscal' });
  const [sent, setSent] = useState<string>();
  const used = (list.data?.membros.length ?? 0) + (list.data?.convites.length ?? 0);
  const max = sessao?.max_usuarios;

  const invite = async (e: FormEvent) => {
    e.preventDefault();
    setSent(undefined);
    if (await run(() => account('portal', '/membros/convites', { method: 'POST', body: form })) !== undefined) {
      setSent(form.email);
      setForm({ ...form, email: '' });
      void list.reload();
    }
  };
  const act = async (fn: () => Promise<unknown>) => { if (await run(fn) !== undefined) void list.reload(); };

  return (
    <>
      <PageHeader title="Equipe" description="Quem acessa esta empresa e o que cada pessoa pode fazer. Todos entram com senha e verificação em duas etapas." />
      <Card title="Convidar pessoa" description={max ? `${used} de ${max} usuários do plano em uso (contando convites pendentes).` : 'Usuários ilimitados no seu plano.'}>
        <form className="row" onSubmit={invite} style={{ alignItems: 'flex-end', flexWrap: 'wrap' }}>
          <Field label="E-mail"><input type="email" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} required style={{ minWidth: 260 }} /></Field>
          <Field label="Papel">
            <select value={form.papel} onChange={(e) => setForm({ ...form, papel: e.target.value })}>
              {Object.entries(ROLE_HELP).map(([k, v]) => <option key={k} value={k}>{ROLE_LABEL[k]} — {v}</option>)}
            </select>
          </Field>
          <button type="submit" disabled={busy}><UserPlus size={15} aria-hidden="true" /> Enviar convite</button>
        </form>
        {sent && <Alert kind="success">Convite enviado para {sent}. Ele vale por 7 dias.</Alert>}
        <Alert>{error ?? list.error}</Alert>
      </Card>
      <Card title="Pessoas com acesso" flush>
        {list.loading ? <Loading /> : (
          <Table head={['Nome', 'E-mail', 'Papel', 'MFA', 'Último acesso', '']} empty={!list.data?.membros.length}>
            {list.data?.membros.map((m) => {
              const self = m.user_id === sessao?.usuario.id;
              return (
                <tr key={m.user_id}>
                  <td>{m.nome}{self && <> <Badge tone="info">você</Badge></>}</td>
                  <td className="small">{m.email}</td>
                  <td>
                    {self || (m.papel === 'owner' && sessao?.papel !== 'owner') ? ROLE_LABEL[m.papel] : (
                      <select aria-label={`Papel de ${m.nome}`} value={m.papel} disabled={busy}
                        onChange={(e) => void act(() => account('portal', `/membros/${m.user_id}`, { method: 'PATCH', body: { papel: e.target.value } }))}>
                        {Object.keys(ROLE_LABEL).filter((r) => r !== 'owner' || sessao?.papel === 'owner').map((r) => <option key={r} value={r}>{ROLE_LABEL[r]}</option>)}
                      </select>
                    )}
                  </td>
                  <td>{m.mfa ? <Badge tone="ok">ativo</Badge> : <Badge tone="warn">pendente</Badge>}</td>
                  <td>{m.ultimo_acesso ? dateTime(m.ultimo_acesso) : '—'}</td>
                  <td>{!self && (
                    <ConfirmButton label="Remover" title={`Remover ${m.nome}?`}
                      impact={`${m.nome} (${m.email}) perde o acesso a esta empresa na hora. As chaves de API criadas por essa pessoa continuam valendo — revogue em API keys se for o caso.`}
                      confirmLabel="Remover acesso" onConfirm={() => act(() => account('portal', `/membros/${m.user_id}`, { method: 'DELETE' }))} />
                  )}</td>
                </tr>
              );
            })}
          </Table>
        )}
      </Card>
      {!!list.data?.convites.length && (
        <Card title="Convites pendentes" flush>
          <Table head={['E-mail', 'Papel', 'Enviado em', 'Expira em', '']}>
            {list.data.convites.map((c) => (
              <tr key={c.email}>
                <td>{c.email}</td>
                <td>{ROLE_LABEL[c.papel]}</td>
                <td>{dateTime(c.criado_em)}</td>
                <td>{dateTime(c.expira_em)}</td>
                <td><button type="button" className="link" onClick={() => void act(() => account('portal', '/membros/convites', { method: 'DELETE', query: { email: c.email } }))}>Cancelar</button></td>
              </tr>
            ))}
          </Table>
        </Card>
      )}
    </>
  );
}
