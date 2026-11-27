import { useState } from 'react';
import { RefreshCw } from 'lucide-react';
import { Alert, Badge, Card, Field, Loading, PageHeader, useAction, useLoad } from '../components/ui';
import { backoffice } from '../lib/api';
import { dateTime } from '../lib/format';

// D-R39: painel de integrações — e-mail, Stripe, motor oficial, sincronização com a Receita, DF-e e cache. Mostra só
// situação, números e horários (nunca senha ou chave) e permite testar o e-mail e a Stripe na hora.

type Situacao = 'OK' | 'ATENCAO' | 'FALHA' | 'DESLIGADO';

export interface Integracao { id: string; nome: string; situacao: Situacao; resumo: string; detalhes: { rotulo: string; valor: string }[] }
export interface PainelIntegracoes { geral: 'OK' | 'ATENCAO' | 'FALHA'; verificado_em: string; integracoes: Integracao[] }

const TOM = { OK: 'ok', ATENCAO: 'warn', FALHA: 'bad', DESLIGADO: 'neutral' } as const;
const ROTULO: Record<Situacao, string> = { OK: 'funcionando', ATENCAO: 'atenção', FALHA: 'com falha', DESLIGADO: 'não configurado' };

function TesteEmail() {
  const [para, setPara] = useState('');
  const [msg, setMsg] = useState<{ ok: boolean; texto: string }>();
  const { busy, error, run } = useAction();
  const enviar = async () => {
    setMsg(undefined);
    const r = await run(() => backoffice<{ enviado: boolean; mensagem: string }>('/admin/v1/integracoes/email-teste', { method: 'POST', body: para ? { para } : {} }));
    if (r) setMsg({ ok: r.enviado, texto: r.mensagem });
  };
  return (
    <div className="stack" style={{ marginTop: 12 }}>
      <div className="row" style={{ gap: 8, alignItems: 'flex-end', flexWrap: 'wrap' }}>
        <Field label="Enviar para" hint="Vazio = o seu e-mail de operador. Outro endereço exige o escopo de segurança.">
          <input type="email" value={para} onChange={(e) => setPara(e.target.value)} placeholder="seu e-mail" style={{ maxWidth: 280 }} />
        </Field>
        <button type="button" className="secondary" disabled={busy} onClick={() => void enviar()}>{busy ? 'Enviando…' : 'Enviar e-mail de teste'}</button>
      </div>
      <Alert>{error}</Alert>
      {msg && <Alert kind={msg.ok ? 'success' : 'warning'}>{msg.texto}</Alert>}
    </div>
  );
}

function TesteStripe() {
  const [msg, setMsg] = useState<{ ok: boolean; texto: string }>();
  const { busy, error, run } = useAction();
  return (
    <div className="stack" style={{ marginTop: 12 }}>
      <div>
        <button type="button" className="secondary" disabled={busy} onClick={async () => {
          setMsg(undefined);
          const r = await run(() => backoffice<{ ok: boolean; mensagem: string }>('/admin/v1/integracoes/stripe-teste', { method: 'POST' }));
          if (r) setMsg({ ok: r.ok, texto: r.mensagem });
        }}>{busy ? 'Testando…' : 'Testar Stripe'}</button>
      </div>
      <Alert>{error}</Alert>
      {msg && <Alert kind={msg.ok ? 'success' : 'warning'}>{msg.texto}</Alert>}
    </div>
  );
}

export function Integracoes() {
  const painel = useLoad(() => backoffice<PainelIntegracoes>('/admin/v1/integracoes'));
  const d = painel.data;
  return (
    <>
      <PageHeader title="Integrações" description="Se e-mail, pagamentos, motor oficial, sincronização com a Receita e captura de notas estão funcionando. Só situação e números — nenhuma senha ou chave aparece aqui.">
        <button type="button" className="secondary" disabled={painel.loading} onClick={() => void painel.reload()}><RefreshCw size={15} aria-hidden="true" /> Verificar de novo</button>
      </PageHeader>
      {painel.loading && !d && <Loading />}
      <Alert>{painel.error}</Alert>
      {d && (
        <>
          <Alert kind={d.geral === 'OK' ? 'success' : d.geral === 'ATENCAO' ? 'warning' : 'error'}>
            {d.geral === 'OK' ? 'Tudo funcionando.' : d.geral === 'ATENCAO' ? 'Funcionando, com pontos de atenção abaixo.' : 'Há integração com falha — veja abaixo o que fazer.'}
            {' '}<span className="muted small">Verificado em {dateTime(d.verificado_em)}.</span>
          </Alert>
          <div className="grid" style={{ alignItems: 'start', marginTop: 16 }}>
            {d.integracoes.map((i) => (
              <Card key={i.id} title={i.nome} actions={<Badge tone={TOM[i.situacao]}>{ROTULO[i.situacao]}</Badge>}>
                <p>{i.resumo}</p>
                {i.detalhes.length > 0 && (
                  <dl className="kv" aria-label={`Detalhes: ${i.nome}`}>
                    {i.detalhes.map((x) => <div key={x.rotulo}><dt>{x.rotulo}</dt><dd>{x.valor}</dd></div>)}
                  </dl>
                )}
                {i.id === 'email' && <TesteEmail />}
                {i.id === 'stripe' && <TesteStripe />}
              </Card>
            ))}
          </div>
          <p className="muted small" style={{ marginTop: 16 }}>
            Servidor de e-mail e chaves da Stripe são configurados no ambiente (variáveis do deploy: <code>Email__*</code>, <code>Stripe__*</code>),
            não aqui: são segredos e não ficam no banco. Produtos e preços da Stripe vêm dos <strong>Planos</strong> e são enviados sozinhos pelos workers.
          </p>
        </>
      )}
    </>
  );
}
