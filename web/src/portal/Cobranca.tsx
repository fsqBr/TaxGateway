import { useState, type FormEvent } from 'react';
import { useSearchParams } from 'react-router-dom';
import { CreditCard, ExternalLink, ShieldCheck } from 'lucide-react';
import { useSession } from '../auth/session';
import { Alert, Badge, Card, Field, Loading, PageHeader, Table, useAction, useLoad } from '../components/ui';
import { ConfirmButton } from '../components/ConfirmDialog';
import { PlanCards, precoLabel, usePlanos } from '../pages/Planos';
import { portal } from '../lib/api';
import { dateTime, fileToBase64, money } from '../lib/format';

interface Cobranca {
  plano: string;
  assinatura: { situacao: string; renova_em: string | null } | null;
  pagamento_online: boolean;
  excedente: { teto_centavos: number; usado_centavos: number; franquia_rigida: boolean; preco_documento_milicentavos: number; preco_simulacao_milicentavos: number };
  verificacao: { verificada: boolean; verificada_em: string | null; metodo: string | null };
  inadimplencia: { desde: string; situacao: string; restringe_em: string; suspende_em: string } | null;
  faturas: { id: string; numero: string | null; situacao: string; valor_centavos: number; pago_centavos: number; link: string | null; pdf: string | null; criada_em: string }[];
}

const SITUACAO: Record<string, { label: string; tone: 'ok' | 'warn' | 'bad' | 'neutral' }> = {
  active: { label: 'ativa', tone: 'ok' }, trialing: { label: 'em teste', tone: 'ok' }, past_due: { label: 'pagamento pendente', tone: 'warn' },
  unpaid: { label: 'não paga', tone: 'bad' }, canceled: { label: 'cancelada', tone: 'neutral' }, incomplete: { label: 'incompleta', tone: 'warn' },
  paid: { label: 'paga', tone: 'ok' }, open: { label: 'em aberto', tone: 'warn' }, draft: { label: 'rascunho', tone: 'neutral' }, void: { label: 'anulada', tone: 'neutral' },
  uncollectible: { label: 'incobrável', tone: 'bad' },
};

const METODO: Record<string, string> = { CERTIFICADO: 'certificado e-CNPJ', EMAIL_RECEITA: 'código no e-mail do cadastro da Receita', MANUAL: 'análise do suporte' };

/** D-R09: régua de inadimplência — aviso, consumo bloqueado no dia 10, suspensão no dia 15. */
function Inadimplencia({ i }: { i: NonNullable<Cobranca['inadimplencia']> }) {
  if (i.situacao === 'RESTRICTED') {
    return <Alert kind="error">Pagamento pendente desde {dateTime(i.desde)}: cálculos e auditorias estão bloqueados (consultas e exportações continuam). A conta é suspensa em {dateTime(i.suspende_em)}. Regularize em "Gerenciar pagamento" — o acesso volta na hora.</Alert>;
  }
  if (i.situacao === 'SUSPENDED') {
    return <Alert kind="error">Conta suspensa por falta de pagamento. Regularize em "Gerenciar pagamento" — o acesso volta assim que a Stripe confirmar.</Alert>;
  }
  return <Alert kind="warning">A cobrança de {dateTime(i.desde)} falhou. A Stripe tenta de novo; atualize o cartão em "Gerenciar pagamento". Sem pagamento, cálculos e auditorias são bloqueados em {dateTime(i.restringe_em)} e a conta é suspensa em {dateTime(i.suspende_em)}.</Alert>;
}

/** D-R08: teto mensal de excedente. 0 = para ao esgotar a franquia (nada é cobrado sem autorização). */
function Teto({ e, onSaved }: { e: Cobranca['excedente']; onSaved: () => Promise<void> }) {
  const [valor, setValor] = useState(String(e.teto_centavos / 100));
  const { busy, error, run } = useAction();
  const [ok, setOk] = useState(false);
  if (e.franquia_rigida) {
    return <Card title="Excedente"><p className="muted small">Este plano tem franquia rígida: ao esgotar, o consumo para até o mês seguinte ou até a troca de plano. Não há cobrança de excedente.</p></Card>;
  }
  const semExcedente = e.preco_documento_milicentavos === 0 && e.preco_simulacao_milicentavos === 0;
  const salvar = async (ev: FormEvent) => {
    ev.preventDefault();
    setOk(false);
    const centavos = Math.round(Number(valor.replace(',', '.')) * 100);
    if (await run(() => portal('/v1/cobranca/teto', { method: 'PUT', body: { teto_centavos: centavos } }))) { setOk(true); await onSaved(); }
  };
  return (
    <Card title="Excedente do mês" description="Fora da franquia, cada documento ou simulação é cobrado pelo preço de excedente — só até o teto que você autorizar. Ao atingir o teto o consumo para (com aviso) e nada além disso é cobrado.">
      {semExcedente ? <p className="muted small">Este plano não cobra excedente.</p> : (
        <>
          <p>Usado neste mês: <strong>{money(e.usado_centavos / 100)}</strong> de {e.teto_centavos === 0 ? 'teto zero (para ao esgotar a franquia)' : money(e.teto_centavos / 100)}
            <span className="muted small"> · documento {money(e.preco_documento_milicentavos / 100000)} · simulação {money(e.preco_simulacao_milicentavos / 100000)}</span></p>
          <form className="row" style={{ gap: 12, alignItems: 'flex-end', flexWrap: 'wrap' }} onSubmit={(ev) => void salvar(ev)}>
            <Field label="Teto mensal de excedente (R$)" hint="0 = não cobrar excedente; o consumo para ao esgotar a franquia.">
              <input inputMode="decimal" value={valor} onChange={(ev) => setValor(ev.target.value)} />
            </Field>
            <button type="submit" disabled={busy}>Salvar teto</button>
          </form>
          {ok && <Alert kind="success">Teto atualizado. Vale para o consumo a partir de agora.</Alert>}
          <Alert>{error}</Alert>
        </>
      )}
    </Card>
  );
}

/** D-R10: titularidade do CNPJ — exigida para plano pago e captura de DF-e. */
function Verificacao({ v, onDone }: { v: Cobranca['verificacao']; onDone: () => Promise<void> }) {
  const status = useLoad(() => portal<{ cnpj: string; metodos_disponiveis: string[] }>('/v1/verificacao-cnpj'));
  const { busy, error, run } = useAction();
  const [pfx, setPfx] = useState<File>();
  const [senha, setSenha] = useState('');
  const [enviado, setEnviado] = useState<string>();
  const [codigo, setCodigo] = useState('');
  if (v.verificada) {
    return (
      <Card title="Titularidade da empresa">
        <p className="row" style={{ gap: 8 }}><ShieldCheck size={18} aria-hidden="true" /> <Badge tone="ok">verificada</Badge>
          <span className="muted small">por {METODO[v.metodo ?? ''] ?? v.metodo} em {dateTime(v.verificada_em)}</span></p>
      </Card>
    );
  }
  const porCertificado = async (ev: FormEvent) => {
    ev.preventDefault();
    if (pfx && await run(async () => portal('/v1/verificacao-cnpj/certificado', { method: 'POST', body: { pfx_base64: await fileToBase64(pfx), senha } }))) await onDone();
  };
  const enviarCodigo = async () => {
    const r = await run(() => portal<{ enviado_para: string }>('/v1/verificacao-cnpj/email', { method: 'POST' }));
    if (r) setEnviado(r.enviado_para);
  };
  const confirmar = async (ev: FormEvent) => {
    ev.preventDefault();
    if (await run(() => portal('/v1/verificacao-cnpj/email/confirmar', { method: 'POST', body: { codigo } }))) await onDone();
  };
  const email = status.data?.metodos_disponiveis.includes('EMAIL_RECEITA');
  return (
    <Card title="Verifique a empresa" description="Para contratar plano pago e ligar a captura de notas (DF-e), confirme que a empresa é sua. Isso protege o CNPJ contra uso por terceiros.">
      <Alert>{error ?? status.error}</Alert>
      <form onSubmit={(ev) => void porCertificado(ev)}>
        <p><strong>Com o certificado e-CNPJ (A1)</strong> <span className="muted small">— na hora. Usado só para conferir a cadeia ICP-Brasil e o CNPJ; não é guardado.</span></p>
        <div className="row" style={{ gap: 12, alignItems: 'flex-end', flexWrap: 'wrap' }}>
          <Field label="Arquivo .pfx"><input type="file" accept=".pfx,.p12" onChange={(ev) => setPfx(ev.target.files?.[0])} /></Field>
          <Field label="Senha do certificado"><input type="password" autoComplete="off" value={senha} onChange={(ev) => setSenha(ev.target.value)} /></Field>
          <button type="submit" disabled={busy || !pfx || !senha}>Verificar com certificado</button>
        </div>
      </form>
      {email && (
        <div style={{ marginTop: 16 }}>
          <p><strong>Com um código no e-mail do cadastro do CNPJ na Receita</strong></p>
          {!enviado ? <button type="button" className="secondary" disabled={busy} onClick={() => void enviarCodigo()}>Enviar código</button> : (
            <form className="row" style={{ gap: 12, alignItems: 'flex-end', flexWrap: 'wrap' }} onSubmit={(ev) => void confirmar(ev)}>
              <Field label={`Código enviado para ${enviado}`} hint="Vale por 30 minutos, até 5 tentativas."><input inputMode="numeric" maxLength={6} value={codigo} onChange={(ev) => setCodigo(ev.target.value.replace(/\D/g, ''))} /></Field>
              <button type="submit" disabled={busy || codigo.length !== 6}>Confirmar código</button>
            </form>
          )}
        </div>
      )}
      <p className="muted small" style={{ marginTop: 16 }}>Sem certificado nem acesso ao e-mail do cadastro? Escreva para suporte@taxgateway.com.br com o contrato social — a verificação manual sai em até 2 dias úteis.</p>
    </Card>
  );
}

/** Plano e cobrança (D-C02): contratar/trocar pela Stripe, gerenciar pagamento e ver faturas. Dono, admin e financeiro. */
export function PlanoCobranca() {
  const { reload } = useSession();
  const data = useLoad(() => portal<Cobranca>('/v1/cobranca'));
  const { planos } = usePlanos();
  const { busy, error, run } = useAction();
  const [params] = useSearchParams();

  const contratar = async (codigo: string) => {
    const r = await run(() => portal<{ acao: string; url?: string }>('/v1/cobranca/checkout', { method: 'POST', body: { plano: codigo } }));
    if (r?.url) window.location.assign(r.url);   // Checkout da Stripe
    else if (r) { await data.reload(); await reload(); }
  };
  const gerenciar = async () => {
    const r = await run(() => portal<{ url: string }>('/v1/cobranca/portal', { method: 'POST' }));
    if (r) window.location.assign(r.url);
  };

  const atual = data.data?.plano;
  const sit = data.data?.assinatura ? SITUACAO[data.data.assinatura.situacao] : undefined;
  return (
    <>
      <PageHeader title="Plano e cobrança" description="Seu plano, a assinatura e as faturas. O pagamento é processado pela Stripe; o TaxGateway não guarda dados de cartão.">
        {data.data?.assinatura && <button type="button" className="secondary" disabled={busy} onClick={() => void gerenciar()}><CreditCard size={15} aria-hidden="true" /> Gerenciar pagamento</button>}
      </PageHeader>
      {params.get('contratado') && <Alert kind="success">Pagamento recebido! O plano novo é aplicado em instantes — recarregue se ainda aparecer o anterior.</Alert>}
      {params.get('cancelado') && <Alert kind="info">Contratação cancelada. Nada foi cobrado.</Alert>}
      {data.data && !data.data.pagamento_online && <Alert kind="info">Pagamento online ainda não está habilitado nesta instalação. Para trocar de plano, fale com o suporte.</Alert>}
      {data.data?.inadimplencia ? <Inadimplencia i={data.data.inadimplencia} /> : data.data?.assinatura?.situacao === 'past_due' && <Alert kind="warning">Há um pagamento pendente. Atualize o cartão em "Gerenciar pagamento" para evitar a suspensão.</Alert>}
      <Alert>{error ?? data.error}</Alert>
      <Card title="Plano atual">
        {data.loading ? <Loading /> : (
          <div className="row" style={{ gap: 16, flexWrap: 'wrap' }}>
            <strong style={{ fontSize: '1.3rem' }}>{planos?.find((p) => p.codigo === atual)?.nome ?? atual}</strong>
            {sit && <Badge tone={sit.tone}>assinatura {sit.label}</Badge>}
            {data.data?.assinatura?.renova_em && <span className="muted small">renova em {dateTime(data.data.assinatura.renova_em)}</span>}
          </div>
        )}
      </Card>
      {data.data && <Verificacao v={data.data.verificacao} onDone={async () => { await data.reload(); await reload(); }} />}
      {data.data && <Teto key={data.data.excedente.teto_centavos} e={data.data.excedente} onSaved={data.reload} />}
      {planos && data.data && (
        <Card title={data.data.assinatura ? 'Trocar de plano' : 'Contratar um plano'} description="A troca vale na hora; a diferença proporcional entra na próxima fatura. Planos sob consulta: fale com vendas.">
          <PlanCards planos={planos} atual={atual} acao={(p) => {
            if (p.codigo === atual) return <Badge tone="info">plano atual</Badge>;
            if (!p.contratacao_online || p.preco_centavos === 0) {
              return p.preco_centavos === 0 && p.contratacao_online ? <span className="muted small">cancele a assinatura em Gerenciar pagamento</span>
                : <a className="button secondary" href="mailto:vendas@taxgateway.com.br">Falar com vendas</a>;
            }
            return data.data?.assinatura
              ? <ConfirmButton label={`Trocar para ${p.nome}`} className="secondary" title={`Trocar para o plano ${p.nome}?`} confirmLabel="Trocar de plano"
                  impact={`A mensalidade passa a ser ${precoLabel(p)}, com cobrança proporcional na próxima fatura. Os limites do plano novo valem na hora para todos os seus sistemas.`}
                  disabled={busy || !data.data.pagamento_online} onConfirm={() => contratar(p.codigo)} />
              : <button type="button" disabled={busy || !data.data?.pagamento_online || !data.data.verificacao.verificada} title={data.data?.verificacao.verificada ? undefined : 'Verifique a empresa primeiro'} onClick={() => void contratar(p.codigo)}>Contratar {p.nome}</button>;
          }} />
        </Card>
      )}
      <Card title="Faturas" flush>
        {data.loading ? <Loading /> : (
          <Table head={['Número', 'Emitida em', 'Valor', 'Situação', '']} empty={!data.data?.faturas.length} emptyText="Nenhuma fatura ainda.">
            {data.data?.faturas.map((f) => (
              <tr key={f.id}>
                <td className="mono">{f.numero ?? '—'}</td>
                <td>{dateTime(f.criada_em)}</td>
                <td className="num">{money(f.valor_centavos / 100)}</td>
                <td><Badge tone={SITUACAO[f.situacao]?.tone ?? 'neutral'}>{SITUACAO[f.situacao]?.label ?? f.situacao}</Badge></td>
                <td>{f.link && <a href={f.link} target="_blank" rel="noreferrer">Ver fatura <ExternalLink size={13} aria-hidden="true" /></a>}</td>
              </tr>
            ))}
          </Table>
        )}
      </Card>
    </>
  );
}
