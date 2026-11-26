import { useSearchParams } from 'react-router-dom';
import { CreditCard, ExternalLink } from 'lucide-react';
import { useSession } from '../auth/session';
import { Alert, Badge, Card, Loading, PageHeader, Table, useAction, useLoad } from '../components/ui';
import { ConfirmButton } from '../components/ConfirmDialog';
import { PlanCards, precoLabel, usePlanos } from '../pages/Planos';
import { portal } from '../lib/api';
import { dateTime, money } from '../lib/format';

interface Cobranca {
  plano: string;
  assinatura: { situacao: string; renova_em: string | null } | null;
  pagamento_online: boolean;
  faturas: { id: string; numero: string | null; situacao: string; valor_centavos: number; pago_centavos: number; link: string | null; pdf: string | null; criada_em: string }[];
}

const SITUACAO: Record<string, { label: string; tone: 'ok' | 'warn' | 'bad' | 'neutral' }> = {
  active: { label: 'ativa', tone: 'ok' }, trialing: { label: 'em teste', tone: 'ok' }, past_due: { label: 'pagamento pendente', tone: 'warn' },
  unpaid: { label: 'não paga', tone: 'bad' }, canceled: { label: 'cancelada', tone: 'neutral' }, incomplete: { label: 'incompleta', tone: 'warn' },
  paid: { label: 'paga', tone: 'ok' }, open: { label: 'em aberto', tone: 'warn' }, draft: { label: 'rascunho', tone: 'neutral' }, void: { label: 'anulada', tone: 'neutral' },
  uncollectible: { label: 'incobrável', tone: 'bad' },
};

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
      {data.data?.assinatura?.situacao === 'past_due' && <Alert kind="warning">Há um pagamento pendente. Atualize o cartão em "Gerenciar pagamento" para evitar a suspensão.</Alert>}
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
              : <button type="button" disabled={busy || !data.data?.pagamento_online} onClick={() => void contratar(p.codigo)}>Contratar {p.nome}</button>;
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
