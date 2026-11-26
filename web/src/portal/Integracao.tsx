import { useState, type FormEvent } from 'react';
import { ConfirmButton } from '../components/ConfirmDialog';
import { Alert, Badge, Card, Field, Loading, PageHeader, SecretOnce, StatusBadge, Table, useAction, useLoad } from '../components/ui';
import { portal } from '../lib/api';
import { dateTime } from '../lib/format';
import type { Me } from './PortalApp';
import { ALL_SCOPES } from '../lib/scopes';

interface ApiKey {
  id: string;
  nome: string;
  prefixo: string;
  scopes: string[];
  expira_em: string | null;
  revogada_em: string | null;
  ultimo_uso: string | null;
  criada_em: string;
  atual: boolean;
}

export function ApiKeys({ me }: { me?: Me }) {
  const list = useLoad(() => portal<{ api_keys: ApiKey[] }>('/v1/api-keys'));
  const [nome, setNome] = useState('');
  const [expira, setExpira] = useState('');
  const [scopes, setScopes] = useState<string[]>(['simulacao:write', 'auditoria:write']);
  const [secret, setSecret] = useState<string>();
  const { busy, error, run } = useAction();
  const allowed = new Set(me?.scopes ?? []);
  // Só escopos que a chave atual tem (a API recusaria os demais com 403 ERR_FORBIDDEN_SCOPE).
  const effective = scopes.filter((s) => allowed.has(s));

  const create = async (e: FormEvent) => {
    e.preventDefault();
    const r = await run(() =>
      portal<{ api_key: string }>('/v1/api-keys', {
        method: 'POST',
        body: { nome: nome || undefined, scopes: effective, expira_em: expira ? new Date(`${expira}T23:59:59-03:00`).toISOString() : undefined },
      }));
    if (r) {
      setSecret(r.api_key);
      setNome('');
      void list.reload();
    }
  };

  const revoke = async (id: string) => {
    if (await run(() => portal(`/v1/api-keys/${id}`, { method: 'DELETE' })) !== undefined) void list.reload();
  };

  return (
    <>
      <PageHeader title="API keys" description="Credenciais de máquina para o seu ERP. Dê a cada sistema só os escopos de que ele precisa e revogue o que não usa mais." />
      {secret && <SecretOnce label="Nova API key" value={secret} onClose={() => setSecret(undefined)} />}
      <Card title="Criar API key">
        <form className="stack" onSubmit={create}>
          <Field label="Nome"><input value={nome} onChange={(e) => setNome(e.target.value)} placeholder="erp-producao" maxLength={80} /></Field>
          <fieldset className="field" style={{ border: 'none', padding: 0, margin: 0 }}>
            <legend className="field-label">Escopos</legend>
            <div className="grid" style={{ gap: 4 }}>
              {ALL_SCOPES.map((s) => (
                <label className="checkbox" key={s.id} title={allowed.has(s.id) ? '' : 'A sua chave atual não tem este escopo'}>
                  <input
                    type="checkbox"
                    disabled={!allowed.has(s.id)}
                    checked={allowed.has(s.id) && scopes.includes(s.id)}
                    onChange={(e) => setScopes(e.target.checked ? [...scopes, s.id] : scopes.filter((x) => x !== s.id))}
                  />
                  {s.label} <code className="muted">{s.id}</code>
                </label>
              ))}
            </div>
            <span className="field-hint">Uma chave só pode ter escopos que a sua chave atual também tem.</span>
          </fieldset>
          <Field label="Expira em (opcional)"><input type="date" value={expira} onChange={(e) => setExpira(e.target.value)} /></Field>
          <Alert>{error}</Alert>
          <button type="submit" disabled={busy || effective.length === 0}>Criar</button>
        </form>
      </Card>
      <div style={{ height: 16 }} />
      <Card title="Chaves">
        {list.loading && <Loading />}
        <Alert>{list.error}</Alert>
        {list.data && (
          <Table head={['Nome', 'Prefixo', 'Escopos', 'Último uso', 'Expira', 'Situação', '']} empty={list.data.api_keys.length === 0}>
            {list.data.api_keys.map((k) => (
              <tr key={k.id}>
                <td>{k.nome}{k.atual && <> <Badge tone="info">esta sessão</Badge></>}</td>
                <td className="mono">{k.prefixo}</td>
                <td>{k.scopes.map((s) => <Badge key={s}>{s}</Badge>)}</td>
                <td>{dateTime(k.ultimo_uso)}</td>
                <td>{dateTime(k.expira_em)}</td>
                <td>{k.revogada_em ? <Badge tone="bad">revogada</Badge> : <Badge tone="ok">ativa</Badge>}</td>
                <td>{!k.revogada_em && !k.atual && <ConfirmButton label="Revogar" title={`Revogar a chave "${k.nome}"?`} confirmLabel="Revogar chave"
                  impact={`Os sistemas que usam a chave ${k.prefixo}… passam a receber 401 imediatamente, em todas as instâncias. Não dá para desfazer: será preciso criar outra chave.`}
                  onConfirm={() => void revoke(k.id)} />}</td>
              </tr>
            ))}
          </Table>
        )}
      </Card>
    </>
  );
}

const EVENTS = ['document.audited', 'batch.audit.completed', 'dfe.document.audited', 'certificate.expiring'];

interface Webhook { id: string; url: string; events: string[]; active: boolean; disabled_reason: string | null; created_at: string }
interface Delivery { id: string; webhook_id: string; event: string; status: string; attempts: number; next_attempt_at: string | null; last_status_code: number | null; created_at: string }

export function Webhooks() {
  const hooks = useLoad(() => portal<Webhook[]>('/v1/webhooks'));
  const [filter, setFilter] = useState('');
  const deliveries = useLoad(() => portal<Delivery[]>('/v1/webhooks/deliveries', { query: { status: filter } }), [filter]);
  const [url, setUrl] = useState('');
  const [events, setEvents] = useState<string[]>(['document.audited']);
  const [secret, setSecret] = useState<string>();
  const { busy, error, run } = useAction();

  const create = async (e: FormEvent) => {
    e.preventDefault();
    const r = await run(() => portal<{ secret: string }>('/v1/webhooks', { method: 'POST', body: { url, eventos: events } }));
    if (r) {
      setSecret(r.secret);
      setUrl('');
      void hooks.reload();
    }
  };

  const act = async (fn: () => Promise<unknown>, after: () => void) => {
    if (await run(fn) !== undefined) after();
  };

  return (
    <>
      <PageHeader title="Webhooks" description="Receba avisos assinados quando auditorias, lotes e DF-e ficarem prontos, ou quando o certificado estiver para vencer." />
      {secret && <SecretOnce label="Segredo de assinatura do webhook" value={secret} onClose={() => setSecret(undefined)} />}
      <Card title="Novo webhook">
        <form className="stack" onSubmit={create}>
          <Field label="URL (HTTPS)" hint="Endereços privados ou internos são recusados. Assinatura no header TaxGateway-Signature (t=…,v1=HMAC-SHA256).">
            <input type="url" value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://erp.exemplo.com.br/hooks/taxgateway" required />
          </Field>
          <fieldset className="field" style={{ border: 'none', padding: 0, margin: 0 }}>
            <legend className="field-label">Eventos</legend>
            {EVENTS.map((ev) => (
              <label className="checkbox" key={ev}>
                <input type="checkbox" checked={events.includes(ev)} onChange={(e) => setEvents(e.target.checked ? [...events, ev] : events.filter((x) => x !== ev))} />
                <code>{ev}</code>
              </label>
            ))}
          </fieldset>
          <Alert>{error}</Alert>
          <button type="submit" disabled={busy || events.length === 0}>Cadastrar</button>
        </form>
      </Card>
      <div style={{ height: 16 }} />
      <Card title="Webhooks cadastrados">
        {hooks.loading && <Loading />}
        <Alert>{hooks.error}</Alert>
        {hooks.data && (
          <Table head={['URL', 'Eventos', 'Situação', 'Criado', '']} empty={hooks.data.length === 0}>
            {hooks.data.map((h) => (
              <tr key={h.id}>
                <td className="mono" style={{ wordBreak: 'break-all' }}>{h.url}</td>
                <td>{h.events.map((e) => <Badge key={e}>{e}</Badge>)}</td>
                <td>{h.active ? <Badge tone="ok">ativo</Badge> : <Badge tone="bad">{h.disabled_reason ?? 'inativo'}</Badge>}</td>
                <td>{dateTime(h.created_at)}</td>
                <td className="row">
                  {h.active && (
                    <>
                      <button className="secondary" onClick={() => void act(async () => setSecret((await portal<{ secret: string }>(`/v1/webhooks/${h.id}/rotate-secret`, { method: 'POST' })).secret), () => undefined)}>
                        Girar segredo
                      </button>
                      <ConfirmButton label="Desativar" title="Desativar este webhook?" confirmLabel="Desativar webhook"
                        impact={`${h.url} deixa de receber eventos agora. Entregas pendentes não serão enviadas.`}
                        onConfirm={() => void act(() => portal(`/v1/webhooks/${h.id}`, { method: 'DELETE' }), () => void hooks.reload())} />
                    </>
                  )}
                </td>
              </tr>
            ))}
          </Table>
        )}
      </Card>
      <div style={{ height: 16 }} />
      <Card
        title="Entregas"
        actions={
          <select aria-label="Filtrar entregas" value={filter} onChange={(e) => setFilter(e.target.value)} style={{ width: 'auto' }}>
            <option value="">Todas</option>
            {['PENDING', 'RETRYING', 'DELIVERED', 'DEAD'].map((s) => <option key={s} value={s}>{s}</option>)}
          </select>
        }
      >
        {deliveries.loading && <Loading />}
        <Alert>{deliveries.error}</Alert>
        {deliveries.data && (
          <Table head={['Evento', 'Situação', 'Tentativas', 'Último HTTP', 'Próxima tentativa', 'Criada', '']} empty={deliveries.data.length === 0}>
            {deliveries.data.map((d) => (
              <tr key={d.id}>
                <td><code>{d.event}</code></td>
                <td><StatusBadge status={d.status} /></td>
                <td className="num">{d.attempts}</td>
                <td className="num">{d.last_status_code ?? '—'}</td>
                <td>{dateTime(d.next_attempt_at)}</td>
                <td>{dateTime(d.created_at)}</td>
                <td>{d.status !== 'DELIVERED' && <button className="link" onClick={() => void act(() => portal(`/v1/webhooks/deliveries/${d.id}/retry`, { method: 'POST' }), () => void deliveries.reload())}>Reenviar</button>}</td>
              </tr>
            ))}
          </Table>
        )}
      </Card>
    </>
  );
}
