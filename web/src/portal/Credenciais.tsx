import { useState, type FormEvent } from 'react';
import { useSession } from '../auth/session';
import { ConfirmButton } from '../components/ConfirmDialog';
import { Alert, Badge, Card, Field, Loading, PageHeader, SecretOnce, Table, useAction, useLoad } from '../components/ui';
import { portal } from '../lib/api';
import { dateTime } from '../lib/format';
import { ALL_SCOPES } from '../lib/scopes';

interface ClienteOAuth { client_id: string; nome: string; scopes: string[]; criado_em: string; revogado_em: string | null }

/**
 * Clientes OAuth 2.0 (client credentials) criados pelo próprio cliente (D-R27): para sistemas que preferem token
 * de curta duração a API key fixa. O segredo aparece uma vez; revogar derruba os tokens já emitidos.
 */
export function ClientesOAuth() {
  const { can } = useSession();
  const list = useLoad(() => portal<{ clientes: ClienteOAuth[] }>('/v1/oauth-clients'));
  const { busy, error, run } = useAction();
  const [nome, setNome] = useState('');
  const [scopes, setScopes] = useState<string[]>(['simulacao:write', 'auditoria:write']);
  const [criado, setCriado] = useState<{ client_id: string; client_secret: string }>();

  const criar = async (e: FormEvent) => {
    e.preventDefault();
    const r = await run(() => portal<{ client_id: string; client_secret: string }>('/v1/oauth-clients', { method: 'POST', body: { nome, scopes } }));
    if (r) {
      setCriado(r);
      setNome('');
      await list.reload();
    }
  };

  return (
    <Card title="Clientes OAuth 2.0" description="Para sistemas que trocam client_id e client_secret por um token de curta duração em /oauth/token (client credentials).">
      {list.loading && <Loading />}
      <Alert>{list.error ?? error}</Alert>
      {criado && <SecretOnce label={`client_secret de ${criado.client_id}`} value={criado.client_secret} onClose={() => setCriado(undefined)} />}
      <Table head={['client_id', 'Nome', 'Escopos', 'Criado em', 'Situação', '']} empty={!list.data?.clientes.length} emptyText="Nenhum cliente OAuth.">
        {list.data?.clientes.map((c) => (
          <tr key={c.client_id}>
            <td data-label="client_id" className="mono">{c.client_id}</td>
            <td data-label="Nome">{c.nome}</td>
            <td data-label="Escopos" className="small">{c.scopes.join(', ')}</td>
            <td data-label="Criado em">{dateTime(c.criado_em)}</td>
            <td data-label="Situação">{c.revogado_em ? <Badge tone="neutral">revogado</Badge> : <Badge tone="ok">ativo</Badge>}</td>
            <td>{!c.revogado_em && (
              <ConfirmButton label="Revogar" title={`Revogar o cliente "${c.nome}"?`} confirmLabel="Revogar"
                impact="Os tokens já emitidos param de valer na hora e o sistema que usa este cliente recebe 401."
                onConfirm={async () => { await run(() => portal(`/v1/oauth-clients/${c.client_id}`, { method: 'DELETE' })); await list.reload(); }} />
            )}</td>
          </tr>
        ))}
      </Table>
      <form className="stack" style={{ marginTop: 12 }} onSubmit={(e) => void criar(e)}>
        <Field label="Nome do sistema"><input value={nome} onChange={(e) => setNome(e.target.value)} placeholder="Ex.: ERP produção" /></Field>
        <fieldset className="stack">
          <legend className="field-label">Escopos</legend>
          {ALL_SCOPES.map((s) => (
            <label key={s.id} className="checkbox">
              <input type="checkbox" disabled={!can(s.id)} checked={scopes.includes(s.id)}
                onChange={(e) => setScopes(e.target.checked ? [...scopes, s.id] : scopes.filter((x) => x !== s.id))} />
              {s.label} <span className="muted small mono">{s.id}</span>
            </label>
          ))}
        </fieldset>
        <div><button type="submit" disabled={busy || nome.trim().length < 2 || scopes.length === 0}>Criar cliente OAuth</button></div>
      </form>
    </Card>
  );
}

interface Chamada { em: string; metodo: string; rota: string; status: number; duracao_ms: number; credencial: string; erro: string | null; trace_id: string | null }

/** Registro das chamadas da API (D-R27): erros e escritas dos últimos 30 dias, para quem integra achar o problema. */
export function LogChamadas() {
  const [soErros, setSoErros] = useState(false);
  const list = useLoad(() => portal<{ chamadas: Chamada[] }>('/v1/logs', { query: { status: soErros ? 'erros' : undefined } }), [soErros]);
  return (
    <>
      <PageHeader title="Log de chamadas" description="As últimas 200 chamadas com erro ou que gravaram algo (simulação, auditoria, lote...). Leituras com sucesso não são registradas. Guardado por 30 dias." />
      <Card flush actions={<label className="checkbox"><input type="checkbox" checked={soErros} onChange={(e) => setSoErros(e.target.checked)} /> Só erros</label>}>
        {list.loading ? <Loading /> : (
          <Table head={['Quando', 'Chamada', 'Resultado', 'Tempo', 'Credencial', 'Rastreio']} empty={!list.data?.chamadas.length} emptyText="Nenhuma chamada registrada ainda.">
            {list.data?.chamadas.map((c, i) => (
              <tr key={`${c.em}-${i}`}>
                <td data-label="Quando">{dateTime(c.em)}</td>
                <td data-label="Chamada" className="mono small">{c.metodo} {c.rota}</td>
                <td data-label="Resultado"><Badge tone={c.status >= 500 ? 'bad' : c.status >= 400 ? 'warn' : 'ok'}>{c.status}</Badge>{c.erro && <span className="sub mono">{c.erro}</span>}</td>
                <td data-label="Tempo" className="num">{c.duracao_ms} ms</td>
                <td data-label="Credencial" className="small">{c.credencial}</td>
                <td data-label="Rastreio" className="mono small">{c.trace_id?.slice(0, 16) ?? '—'}</td>
              </tr>
            ))}
          </Table>
        )}
        <Alert>{list.error}</Alert>
      </Card>
    </>
  );
}
