import { useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { ShieldCheck } from 'lucide-react';
import { useSession } from '../auth/session';
import { Alert, Badge, Card, Field, Loading, PageHeader, useAction, useLoad } from '../components/ui';
import { portal } from '../lib/api';
import { cnpj, dateTime, fileToBase64 } from '../lib/format';
import { METODO } from './Cobranca';

interface StatusVerificacao { cnpj: string; verificada: boolean; verificada_em: string | null; metodo: string | null; metodos_disponiveis: string[] }

/** D-R10: titularidade do CNPJ — exigida para plano pago e captura de DF-e. */
function Verificacao({ v, metodos, onDone }: { v: StatusVerificacao; metodos: string[]; onDone: () => Promise<void> }) {
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
  const email = metodos.includes('EMAIL_RECEITA');
  return (
    <Card title="Verifique a empresa" description="Para contratar plano pago e ligar a captura de notas (DF-e), confirme que a empresa é sua. Isso protege o CNPJ contra uso por terceiros.">
      <Alert>{error}</Alert>
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

/**
 * Empresa (D-R25): verificação da titularidade do CNPJ — libera plano pago E a captura de notas (DF-e), por isso fica
 * aqui e não em Cobrança (o fiscal não acessa Cobrança). Atalhos para CNPJs, certificado/DF-e e LGPD.
 */
export function Empresa() {
  const { reload, can } = useSession();
  const status = useLoad(() => portal<StatusVerificacao>('/v1/verificacao-cnpj'));
  return (
    <>
      <PageHeader title="Empresa" description={status.data ? `CNPJ ${cnpj(status.data.cnpj)}` : undefined} />
      {status.loading && <Loading />}
      <Alert>{status.error}</Alert>
      {status.data && <Verificacao v={status.data} metodos={status.data.metodos_disponiveis} onDone={async () => { await status.reload(); await reload(); }} />}
      <div className="grid-2">
        {can('cnpjs:manage') && <Card title="CNPJs atendidos" description="Matriz e filiais que podem simular e auditar."><Link to="/portal/cnpjs">Gerenciar CNPJs</Link></Card>}
        {can('dfe:manage') && <Card title="Certificado A1 e captura de notas" description="Notas de fornecedores direto da SEFAZ (DF-e)."><Link to="/portal/dfe">Configurar DF-e</Link></Card>}
        {can('titulares:manage') && <Card title="LGPD" description="Localizar e expurgar dados de um titular (CPF)."><Link to="/portal/titulares">Pedidos de titulares</Link></Card>}
      </div>
    </>
  );
}
