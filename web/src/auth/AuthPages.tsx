import { useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { CheckCircle2, Copy, Download, KeyRound, MailCheck, ShieldCheck, Smartphone } from 'lucide-react';
import QRCode from 'qrcode';
import { Logo } from '../components/Logo';
import { Alert, Field, useAction } from '../components/ui';
import { account, codeOf, LOGIN_PATH, MFA_PATH, type Realm } from '../lib/api';
import { onlyCnpjChars } from '../lib/format';
import { useSession } from './session';
import type { PlanoPublico } from '../pages/Planos';

// Telas de conta (D-U01 a D-U08): entrar, MFA, cadastro, confirmação de e-mail, senha e convite. Mesmo visual para
// portal e backoffice; o backoffice não tem cadastro (operadores são convidados).

const HOME: Record<Realm, string> = { portal: '/portal', backoffice: '/backoffice' };

const BRAND: Record<Realm, { title: string; headline: string; points: string[] }> = {
  portal: {
    title: 'Portal',
    headline: 'Tudo da sua operação fiscal em um só lugar.',
    points: ['Simulação e auditoria com o motor oficial da Receita', 'Lotes, webhooks e chaves para os seus sistemas', 'Certificado A1 e captura automática de DF-e', 'Equipe com papéis e verificação em duas etapas'],
  },
  backoffice: {
    title: 'Backoffice',
    headline: 'Operação interna do TaxGateway.',
    points: ['Tenants, planos e clientes OAuth', 'Alíquotas com aprovação por duas pessoas', 'Faturamento e trilha de auditoria'],
  },
};

export function AuthLayout({ realm, children }: { realm: Realm; children: ReactNode }) {
  const b = BRAND[realm];
  return (
    <div className={realm === 'backoffice' ? 'auth internal' : 'auth'}>
      <aside className="auth-brand">
        <Link to={realm === 'portal' ? '/' : '/backoffice'} aria-label="Início"><Logo size={32} suffix={b.title} /></Link>
        <div>
          <h2>{b.headline}</h2>
          <ul>{b.points.map((p) => <li key={p}><CheckCircle2 size={18} aria-hidden="true" />{p}</li>)}</ul>
        </div>
        <p className="small" style={{ color: '#7f8ea1', display: 'flex', gap: 8, alignItems: 'center' }}>
          <ShieldCheck size={16} aria-hidden="true" /> Sessão protegida: senha + código do app autenticador.
        </p>
      </aside>
      <main className="auth-form" id="conteudo">{children}</main>
    </div>
  );
}

/**
 * Para onde voltar depois do login (D-Q02): só caminhos internos do próprio app. Bloqueia open redirect
 * ("//evil", "/\evil", esquemas) — o react-router 6 tem aviso de segurança justamente para isso.
 */
export function safeReturnPath(from: string | undefined, fallback: string): string {
  if (!from || !/^\/(portal|backoffice)(\/[A-Za-z0-9\-_/]*)?$/.test(from)) return fallback;
  if (/\/(entrar|mfa|cadastro|redefinir-senha|esqueci-senha|confirmar-email|convite)$/.test(from)) return fallback;
  return from;
}

export function LoginPage({ realm }: { realm: Realm }) {
  const [email, setEmail] = useState('');
  const [senha, setSenha] = useState('');
  const { busy, error, run } = useAction();
  const [code, setCode] = useState<string>();
  const { reload } = useSession();
  const navigate = useNavigate();
  const location = useLocation();
  const state = location.state as { from?: string; expirou?: boolean; aviso?: string } | null;
  const from = safeReturnPath(state?.from, HOME[realm]);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setCode(undefined);
    const r = await run(async () => {
      try {
        return await account<{ mfa: string }>(realm, '/entrar', { method: 'POST', body: { email, senha }, silent: true });
      } catch (err) {
        setCode(codeOf(err));
        throw err;
      }
    });
    if (r) {
      await reload();
      navigate(MFA_PATH[realm], { replace: true, state: { from } });
    }
  };

  return (
    <AuthLayout realm={realm}>
      <form onSubmit={submit}>
        <div>
          <h1>Entrar</h1>
          <p className="muted" style={{ marginTop: 6 }}>{realm === 'portal' ? 'Use o e-mail e a senha da sua conta.' : 'Conta de operador (e-mail, senha e MFA).'}</p>
        </div>
        {state?.expirou && <Alert kind="info">Sua sessão terminou. Entre novamente.</Alert>}
        {state?.aviso && <Alert kind="success">{state.aviso}</Alert>}
        <Field label="E-mail"><input type="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} required /></Field>
        <Field label="Senha"><input type="password" autoComplete="current-password" value={senha} onChange={(e) => setSenha(e.target.value)} required /></Field>
        <Alert>{error}</Alert>
        {code === 'ERR_EMAIL_NAO_CONFIRMADO' && <ResendVerification email={email} />}
        <button type="submit" className="lg" disabled={busy}>{busy ? 'Entrando…' : 'Entrar'}</button>
        <div className="spread small">
          <Link to={`/${realm}/esqueci-senha`}>Esqueci minha senha</Link>
          {realm === 'portal' && <Link to="/cadastro">Criar conta grátis</Link>}
        </div>
        {realm === 'portal' && <a href="/" className="small">← Voltar ao site</a>}
      </form>
    </AuthLayout>
  );
}

function ResendVerification({ email }: { email: string }) {
  const [sent, setSent] = useState(false);
  const { busy, run } = useAction();
  return sent ? <Alert kind="success">Se houver cadastro pendente, enviamos um novo link para {email}.</Alert> : (
    <button type="button" className="secondary" disabled={busy || !email}
      onClick={async () => { if (await run(() => account('portal', '/reenviar-confirmacao', { method: 'POST', body: { email } })) !== undefined) setSent(true); }}>
      Reenviar e-mail de confirmação
    </button>
  );
}

/** Segundo fator: configura (QR + códigos de recuperação) ou verifica (código do app ou de recuperação). */
export function MfaPage({ realm }: { realm: Realm }) {
  const { sessao, loading, reload } = useSession();
  const navigate = useNavigate();
  const location = useLocation();
  const from = safeReturnPath((location.state as { from?: string } | null)?.from, HOME[realm]);
  const [setup, setSetup] = useState<{ segredo: string; otpauth_uri: string }>();
  const [codes, setCodes] = useState<string[]>();
  const [code, setCode] = useState('');
  const [useRecovery, setUseRecovery] = useState(false);
  const { busy, error, run } = useAction();
  const canvas = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    if (!loading && !sessao) navigate(LOGIN_PATH[realm], { replace: true });
    if (sessao?.mfa === 'ok' && !codes) navigate(from, { replace: true });
  }, [loading, sessao, codes, navigate, realm, from]);

  useEffect(() => {
    if (sessao?.mfa === 'configurar' && !setup) {
      void run(() => account<{ segredo: string; otpauth_uri: string }>(realm, '/mfa/iniciar', { method: 'POST' })).then((r) => r && setSetup(r));
    }
  }, [sessao, setup, realm, run]);

  useEffect(() => {
    if (setup && canvas.current) void QRCode.toCanvas(canvas.current, setup.otpauth_uri, { width: 200, margin: 1 });
  }, [setup]);

  if (!sessao) return null;

  const enable = async (e: FormEvent) => {
    e.preventDefault();
    const r = await run(() => account<{ codigos_recuperacao: string[] }>(realm, '/mfa/ativar', { method: 'POST', body: { codigo: code.trim() } }));
    if (r) setCodes(r.codigos_recuperacao);
  };

  const verify = async (e: FormEvent) => {
    e.preventDefault();
    const body = useRecovery ? { codigo_recuperacao: code.trim() } : { codigo: code.trim() };
    if (await run(() => account(realm, '/mfa/verificar', { method: 'POST', body })) !== undefined) {
      await reload();
      navigate(from, { replace: true });
    }
  };

  if (codes) {
    return (
      <AuthLayout realm={realm}>
        <div className="stack">
          <h1>Guarde seus códigos de recuperação</h1>
          <p className="muted">Se perder o celular, cada código abaixo permite entrar <strong>uma vez</strong>. Guarde num gerenciador de senhas ou imprima. Eles não serão exibidos de novo.</p>
          <RecoveryCodes codes={codes} />
          <button type="button" className="lg" onClick={async () => { await reload(); navigate(from, { replace: true }); }}>Já guardei, continuar</button>
        </div>
      </AuthLayout>
    );
  }

  return (
    <AuthLayout realm={realm}>
      {sessao.mfa === 'configurar' ? (
        <form onSubmit={enable}>
          <div>
            <h1>Proteja sua conta</h1>
            <p className="muted" style={{ marginTop: 6 }}>A verificação em duas etapas é obrigatória. Leva 1 minuto.</p>
          </div>
          <ol className="help-steps">
            <li><Smartphone size={14} aria-hidden="true" /> Instale um app autenticador (Google Authenticator, Microsoft Authenticator, 1Password, Authy…).</li>
            <li>Leia o QR code com o app {setup && <>ou digite a chave <code className="mono" style={{ wordBreak: 'break-all' }}>{setup.segredo}</code></>}.</li>
            <li>Digite o código de 6 dígitos que aparece no app.</li>
          </ol>
          <canvas ref={canvas} aria-label="QR code para o app autenticador" style={{ alignSelf: 'center', borderRadius: 8, background: '#fff' }} />
          <Field label="Código de 6 dígitos">
            <input inputMode="numeric" autoComplete="one-time-code" pattern="[0-9]{6}" maxLength={6} value={code} onChange={(e) => setCode(e.target.value.replace(/\D/g, ''))} required />
          </Field>
          <Alert>{error}</Alert>
          <button type="submit" className="lg" disabled={busy || code.length !== 6}>{busy ? 'Confirmando…' : 'Ativar e continuar'}</button>
        </form>
      ) : (
        <form onSubmit={verify}>
          <div>
            <h1>Verificação em duas etapas</h1>
            <p className="muted" style={{ marginTop: 6 }}>Olá, {sessao.usuario.nome.split(' ')[0]}. {useRecovery ? 'Digite um dos seus códigos de recuperação.' : 'Digite o código do app autenticador.'}</p>
          </div>
          <Field label={useRecovery ? 'Código de recuperação' : 'Código de 6 dígitos'}>
            {useRecovery
              ? <input autoComplete="off" placeholder="XXXX-XXXX" value={code} onChange={(e) => setCode(e.target.value.toUpperCase())} required />
              : <input inputMode="numeric" autoComplete="one-time-code" pattern="[0-9]{6}" maxLength={6} value={code} onChange={(e) => setCode(e.target.value.replace(/\D/g, ''))} required autoFocus />}
          </Field>
          <Alert>{error}</Alert>
          <button type="submit" className="lg" disabled={busy || !code}>{busy ? 'Verificando…' : 'Entrar'}</button>
          <button type="button" className="link small" onClick={() => { setUseRecovery(!useRecovery); setCode(''); }}>
            {useRecovery ? 'Usar o código do app' : 'Perdi o celular: usar código de recuperação'}
          </button>
        </form>
      )}
    </AuthLayout>
  );
}

export function RecoveryCodes({ codes }: { codes: string[] }) {
  const [copied, setCopied] = useState(false);
  const text = codes.join('\n');
  return (
    <div className="secret-once">
      <strong><KeyRound size={16} aria-hidden="true" /> Códigos de recuperação</strong>
      <div className="recovery-grid mono">{codes.map((c) => <span key={c}>{c}</span>)}</div>
      <div className="row">
        <button type="button" onClick={async () => { try { await navigator.clipboard.writeText(text); setCopied(true); } catch { setCopied(false); } }}>
          <Copy size={14} aria-hidden="true" /> {copied ? 'Copiado' : 'Copiar'}
        </button>
        <a className="button secondary" download="taxgateway-codigos-recuperacao.txt" href={`data:text/plain;charset=utf-8,${encodeURIComponent(`TaxGateway — códigos de recuperação\n\n${text}\n`)}`}>
          <Download size={14} aria-hidden="true" /> Baixar .txt
        </a>
      </div>
    </div>
  );
}

const passwordHint = 'Mínimo de 12 caracteres. Uma frase funciona bem (ex.: "notas conferidas antes do fechamento").';

/** Plano escolhido no site antes do cadastro; o portal lê esta chave para oferecer a contratação depois do login. */
export const PLANO_ESCOLHIDO_KEY = 'tg:plano-escolhido';

function lerPlanoEscolhido(): string | undefined {
  try { return sessionStorage.getItem(PLANO_ESCOLHIDO_KEY) ?? undefined; } catch { return undefined; }
}

const nomeDoCodigo = (codigo: string) => codigo.toLowerCase().split('_').map((w) => w.charAt(0).toUpperCase() + w.slice(1)).join(' ');

/** ?plano=CODIGO vindo de /planos: valida o código, guarda para o portal e busca o nome e se o plano tem DF-e. */
function usePlanoEscolhido() {
  const [params] = useSearchParams();
  const bruto = (params.get('plano') ?? '').trim().toUpperCase();
  const codigo = /^[A-Z][A-Z0-9_]{1,29}$/.test(bruto) && bruto !== 'SANDBOX' ? bruto : undefined;
  const [plano, setPlano] = useState<PlanoPublico>();
  useEffect(() => {
    if (!codigo) return;
    try { sessionStorage.setItem(PLANO_ESCOLHIDO_KEY, codigo); } catch { /* armazenamento bloqueado: o portal só não oferece o atalho */ }
    let vivo = true;
    account<PlanoPublico[]>('portal', '/planos', { silent: true })
      .then((ps) => { if (vivo) setPlano(ps.find((p) => p.codigo === codigo)); })
      .catch(() => undefined);
    return () => { vivo = false; };
  }, [codigo]);
  if (!codigo) return undefined;
  return { codigo, nome: plano?.nome ?? nomeDoCodigo(codigo), dfe: plano?.captura_dfe ?? true, gratis: plano ? plano.preco_centavos === 0 && plano.contratacao_online : false };
}

export function SignupPage() {
  const [form, setForm] = useState({ nome: '', email: '', senha: '', empresa: '', cnpj: '', aceite: false });
  const [done, setDone] = useState(false);
  const escolhido = usePlanoEscolhido();
  const planoPago = escolhido && !escolhido.gratis ? escolhido : undefined;
  const { busy, error, run } = useAction();
  const set = (k: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) =>
    setForm({ ...form, [k]: e.target.type === 'checkbox' ? e.target.checked : e.target.value });

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const r = await run(() => account('portal', '/cadastro', {
      method: 'POST',
      body: { nome: form.nome, email: form.email, senha: form.senha, empresa: { nome: form.empresa, cnpj: onlyCnpjChars(form.cnpj) }, aceite_termos: form.aceite },
    }));
    if (r !== undefined) setDone(true);
  };

  if (done) {
    return (
      <AuthLayout realm="portal">
        <div className="stack">
          <MailCheck size={40} aria-hidden="true" color="var(--accent)" />
          <h1>Confira seu e-mail</h1>
          <p className="muted">Enviamos um link de confirmação para <strong>{form.email}</strong>. Ele vale por 24 horas. Depois de confirmar, entre e configure a verificação em duas etapas.</p>
          {planoPago && (
            <Alert kind="info">
              Sua conta começa no Sandbox. Para contratar o plano <strong>{planoPago.nome}</strong>, entre no portal, verifique a empresa e
              assine em <strong>Plano e cobrança</strong>.
            </Alert>
          )}
          <Link className="button lg" to="/portal/entrar">Ir para o login</Link>
        </div>
      </AuthLayout>
    );
  }

  return (
    <AuthLayout realm="portal">
      <form onSubmit={submit}>
        <div>
          <h1>Criar conta grátis</h1>
          <p className="muted" style={{ marginTop: 6 }}>Comece no plano Sandbox, com o motor oficial e sem cartão: 200 auditorias e 2.000 simulações por mês. Troque de plano quando quiser.</p>
        </div>
        {planoPago && (
          <Alert kind="info">
            <strong>Você escolheu o plano {planoPago.nome}.</strong> A conta começa no Sandbox (grátis). Para pagar o plano {planoPago.nome}
            {planoPago.dfe ? ' e ligar a captura de notas de fornecedores (DF-e)' : ''}, será preciso verificar a empresa: com o certificado
            e-CNPJ, com um código enviado ao e-mail do cadastro do CNPJ na Receita Federal ou pela análise do nosso suporte. Depois de
            entrar, o portal oferece a contratação em Plano e cobrança.
          </Alert>
        )}
        <Field label="Seu nome"><input autoComplete="name" value={form.nome} onChange={set('nome')} required maxLength={120} /></Field>
        <Field label="E-mail de trabalho"><input type="email" autoComplete="email" value={form.email} onChange={set('email')} required /></Field>
        <Field label="Senha" hint={passwordHint}><input type="password" autoComplete="new-password" minLength={12} value={form.senha} onChange={set('senha')} required /></Field>
        <Field label="Empresa"><input autoComplete="organization" value={form.empresa} onChange={set('empresa')} required maxLength={200} /></Field>
        <Field label="CNPJ da empresa" hint="O CNPJ da matriz. Outros CNPJs (filiais, clientes) você cadastra depois."><input inputMode="numeric" value={form.cnpj} onChange={set('cnpj')} placeholder="00.000.000/0000-00" required /></Field>
        <label className="checkbox small">
          <input type="checkbox" checked={form.aceite} onChange={set('aceite')} required />
          <span>Li e aceito os <a href="/termos" target="_blank" rel="noreferrer">Termos de Uso</a> e a <a href="/privacidade" target="_blank" rel="noreferrer">Política de Privacidade</a>.</span>
        </label>
        <Alert>{error}</Alert>
        <button type="submit" className="lg" disabled={busy || !form.aceite}>{busy ? 'Criando…' : 'Criar conta'}</button>
        <p className="small muted">Já tem conta? <Link to="/portal/entrar">Entrar</Link></p>
      </form>
    </AuthLayout>
  );
}

export function ConfirmEmailPage() {
  const [params] = useSearchParams();
  const [state, setState] = useState<'wait' | 'ok' | 'error'>('wait');
  const { error, run } = useAction();
  const [planoEscolhido] = useState(lerPlanoEscolhido);
  const once = useRef(false);
  useEffect(() => {
    if (once.current) return;
    once.current = true;
    void run(() => account('portal', '/confirmar-email', { method: 'POST', body: { token: params.get('token') ?? '' } })).then((r) => setState(r === undefined ? 'error' : 'ok'));
  }, [params, run]);
  return (
    <AuthLayout realm="portal">
      <div className="stack">
        <h1>{state === 'ok' ? 'E-mail confirmado' : state === 'error' ? 'Não foi possível confirmar' : 'Confirmando…'}</h1>
        {state === 'ok' && <p className="muted">Pronto! Agora entre e configure a verificação em duas etapas.</p>}
        {state === 'ok' && planoEscolhido && (
          <p className="muted small">O plano que você escolheu ({nomeDoCodigo(planoEscolhido)}) fica à sua espera em <strong>Plano e cobrança</strong>, depois de verificar a empresa.</p>
        )}
        <Alert>{error}</Alert>
        {state !== 'wait' && <Link className="button lg" to="/portal/entrar">Ir para o login</Link>}
      </div>
    </AuthLayout>
  );
}

export function ForgotPasswordPage({ realm }: { realm: Realm }) {
  const [email, setEmail] = useState('');
  const [sent, setSent] = useState(false);
  const { busy, error, run } = useAction();
  return (
    <AuthLayout realm={realm}>
      <form onSubmit={async (e) => { e.preventDefault(); if (await run(() => account(realm, '/senha/esqueci', { method: 'POST', body: { email } })) !== undefined) setSent(true); }}>
        <div>
          <h1>Redefinir senha</h1>
          <p className="muted" style={{ marginTop: 6 }}>Enviaremos um link válido por 1 hora.</p>
        </div>
        {sent ? <Alert kind="success">Se houver uma conta com {email}, você vai receber o link em instantes. Confira também o spam.</Alert> : (
          <>
            <Field label="E-mail"><input type="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} required /></Field>
            <Alert>{error}</Alert>
            <button type="submit" className="lg" disabled={busy}>{busy ? 'Enviando…' : 'Enviar link'}</button>
          </>
        )}
        <Link to={LOGIN_PATH[realm]} className="small">← Voltar ao login</Link>
      </form>
    </AuthLayout>
  );
}

/** Nova senha pelo link do e-mail (redefinição ou primeiro acesso de operador). */
export function ResetPasswordPage({ realm }: { realm: Realm }) {
  const [params] = useSearchParams();
  const [senha, setSenha] = useState('');
  const [conf, setConf] = useState('');
  const { busy, error, run, setError } = useAction();
  const navigate = useNavigate();
  return (
    <AuthLayout realm={realm}>
      <form onSubmit={async (e) => {
        e.preventDefault();
        if (senha !== conf) { setError('As senhas não conferem.'); return; }
        if (await run(() => account(realm, '/senha/redefinir', { method: 'POST', body: { token: params.get('token') ?? '', senha } })) !== undefined) {
          navigate(LOGIN_PATH[realm], { replace: true, state: { aviso: 'Senha definida. Entre com a nova senha.' } });
        }
      }}>
        <div>
          <h1>{realm === 'backoffice' ? 'Defina sua senha' : 'Criar nova senha'}</h1>
          <p className="muted" style={{ marginTop: 6 }}>Depois disso, todas as sessões abertas são encerradas. A verificação em duas etapas continua exigida.</p>
        </div>
        <Field label="Nova senha" hint={passwordHint}><input type="password" autoComplete="new-password" minLength={12} value={senha} onChange={(e) => setSenha(e.target.value)} required /></Field>
        <Field label="Repita a senha"><input type="password" autoComplete="new-password" value={conf} onChange={(e) => setConf(e.target.value)} required /></Field>
        <Alert>{error}</Alert>
        <button type="submit" className="lg" disabled={busy}>{busy ? 'Salvando…' : 'Salvar senha'}</button>
      </form>
    </AuthLayout>
  );
}

/** Aceite de convite: cria a conta (quem não tem) ou pede login com o e-mail convidado. */
export function InvitePage() {
  const [params] = useSearchParams();
  const token = params.get('token') ?? '';
  const [info, setInfo] = useState<{ empresa: string; email: string; papel: string; tem_conta: boolean }>();
  const [form, setForm] = useState({ nome: '', senha: '' });
  const { busy, error, run } = useAction();
  const { sessao, reload } = useSession();
  const navigate = useNavigate();
  useEffect(() => {
    void run(() => account<{ empresa: string; email: string; papel: string; tem_conta: boolean }>('portal', '/convite', { query: { token }, silent: true })).then((r) => r && setInfo(r));
  }, [token, run]);

  const accept = async (e?: FormEvent) => {
    e?.preventDefault();
    const r = await run(() => account<{ proximo: string }>('portal', '/convite/aceitar', { method: 'POST', body: { token, ...(info?.tem_conta ? {} : form) } }));
    if (!r) return;
    if (r.proximo === 'portal') { await reload(); navigate('/portal', { replace: true }); }
    else navigate('/portal/entrar', { replace: true, state: { aviso: 'Conta criada. Entre e configure a verificação em duas etapas.' } });
  };

  return (
    <AuthLayout realm="portal">
      <div className="stack">
        <h1>Convite</h1>
        <Alert>{error}</Alert>
        {info && (
          <>
            <p className="muted">Você foi convidado(a) para acessar <strong>{info.empresa}</strong> como <strong>{info.papel}</strong> com o e-mail <strong>{info.email}</strong>.</p>
            {info.tem_conta ? (
              sessao?.mfa === 'ok' && sessao.usuario.email === info.email
                ? <button type="button" className="lg" disabled={busy} onClick={() => void accept()}>Aceitar convite</button>
                : <Link className="button lg" to="/portal/entrar" state={{ from: `/portal` }}>Entrar com {info.email} para aceitar</Link>
            ) : (
              <form onSubmit={accept}>
                <Field label="Seu nome"><input autoComplete="name" value={form.nome} onChange={(e) => setForm({ ...form, nome: e.target.value })} required /></Field>
                <Field label="Crie uma senha" hint={passwordHint}><input type="password" autoComplete="new-password" minLength={12} value={form.senha} onChange={(e) => setForm({ ...form, senha: e.target.value })} required /></Field>
                <button type="submit" className="lg" disabled={busy}>{busy ? 'Criando…' : 'Criar conta e aceitar'}</button>
              </form>
            )}
          </>
        )}
      </div>
    </AuthLayout>
  );
}
