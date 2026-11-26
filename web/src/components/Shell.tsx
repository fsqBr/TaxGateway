import { useState, type FormEvent, type ReactNode } from 'react';
import { NavLink, Navigate, useLocation, useNavigate } from 'react-router-dom';
import { BookOpen, CheckCircle2, LogOut, ShieldCheck, type LucideIcon } from 'lucide-react';
import { getCredential, request, setCredential, type Realm } from '../lib/api';
import { Logo } from './Logo';
import { Alert, Field, useAction } from './ui';

export interface NavItem {
  to: string;
  label: string;
  icon: LucideIcon;
  section?: string;
}

/** Barra lateral com seções + conteúdo; sair limpa a credencial da aba. */
export function Shell({ realm, nav, workspace, children }: { realm: Realm; nav: NavItem[]; workspace?: ReactNode; children: ReactNode }) {
  const navigate = useNavigate();
  let lastSection: string | undefined;
  return (
    <div className="shell">
      <aside className="sidebar">
        <Logo size={28} suffix={realm === 'portal' ? 'Portal' : 'Backoffice'} />
        {realm === 'backoffice' && <div className="internal-banner">Ambiente interno</div>}
        {workspace && <div className="workspace">{workspace}</div>}
        <nav className="nav" aria-label={realm === 'portal' ? 'Portal' : 'Backoffice'}>
          {nav.map((n) => {
            const header = n.section && n.section !== lastSection ? <div className="nav-section" key={`s-${n.section}`}>{n.section}</div> : null;
            lastSection = n.section ?? lastSection;
            const Icon = n.icon;
            return [
              header,
              <NavLink key={n.to} to={n.to} end>
                <Icon size={17} aria-hidden="true" />
                {n.label}
              </NavLink>,
            ];
          })}
        </nav>
        <div className="sidebar-footer nav">
          <a href="/docs/" target="_blank" rel="noreferrer"><BookOpen size={17} aria-hidden="true" />Referência da API</a>
          <button
            type="button"
            className="ghost"
            onClick={() => {
              setCredential(realm, null);
              navigate(realm === 'portal' ? '/portal/entrar' : '/backoffice/entrar');
            }}
          >
            <LogOut size={17} aria-hidden="true" /> Sair
          </button>
        </div>
      </aside>
      <main className="content">{children}</main>
    </div>
  );
}

/** Rotas internas só com credencial na aba; senão, vai para o login. */
export function RequireCredential({ realm, children }: { realm: Realm; children: ReactNode }) {
  const location = useLocation();
  if (!getCredential(realm)) {
    return <Navigate to={realm === 'portal' ? '/portal/entrar' : '/backoffice/entrar'} replace state={{ from: location.pathname }} />;
  }
  return <>{children}</>;
}

const LOGIN: Record<Realm, { title: string; headline: string; points: string[]; label: string; hint: string; probe: string; home: string; placeholder: string }> = {
  portal: {
    title: 'Portal',
    headline: 'Tudo da sua integração fiscal em um só lugar.',
    points: ['Consumo da competência e prévia da fatura', 'Chaves, webhooks e CNPJs atendidos', 'Certificado A1 e captura automática de DF-e', 'Atendimento a titulares (LGPD)'],
    label: 'API key',
    hint: 'Use uma API key com os escopos das telas que vai usar. Ela fica só nesta aba e some ao fechá-la.',
    probe: '/v1/me',
    home: '/portal',
    placeholder: 'tg_live_sk_…',
  },
  backoffice: {
    title: 'Backoffice',
    headline: 'Operação interna do TaxGateway.',
    points: ['Tenants, planos e clientes OAuth', 'Alíquotas com aprovação por duas pessoas', 'Fechamento e reconciliação do faturamento'],
    label: 'Chave de operador',
    hint: 'Chave individual de operador (X-Admin-Key). Acesso registrado.',
    probe: '/admin/v1/me',
    home: '/backoffice',
    placeholder: 'chave de operador',
  },
};

export function Login({ realm }: { realm: Realm }) {
  const cfg = LOGIN[realm];
  const [value, setValue] = useState('');
  const { busy, error, run } = useAction();
  const navigate = useNavigate();
  const location = useLocation();
  const from = safeReturnPath((location.state as { from?: string } | null)?.from, cfg.home);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const ok = await run(() => request(realm, cfg.probe, { credential: value.trim() }));
    if (ok !== undefined) {
      setCredential(realm, value.trim());
      navigate(from, { replace: true });
    }
  };

  return (
    <div className={realm === 'backoffice' ? 'auth internal' : 'auth'}>
      <aside className="auth-brand">
        <Logo size={32} suffix={cfg.title} />
        <div>
          <h2>{cfg.headline}</h2>
          <ul>
            {cfg.points.map((p) => (
              <li key={p}><CheckCircle2 size={18} aria-hidden="true" />{p}</li>
            ))}
          </ul>
        </div>
        <p className="small" style={{ color: '#7f8ea1', display: 'flex', gap: 8, alignItems: 'center' }}>
          <ShieldCheck size={16} aria-hidden="true" /> Credencial enviada só por HTTPS, em header, nunca na URL.
        </p>
      </aside>
      <div className="auth-form">
        <form onSubmit={submit}>
          <div>
            <h1>Entrar</h1>
            <p className="muted" style={{ marginTop: 6 }}>{realm === 'portal' ? 'Acesse com uma API key do seu tenant.' : 'Acesse com a sua chave de operador.'}</p>
          </div>
          <Field label={cfg.label} hint={cfg.hint}>
            <input type="password" autoComplete="off" value={value} placeholder={cfg.placeholder} onChange={(e) => setValue(e.target.value)} required />
          </Field>
          <Alert>{error}</Alert>
          <button type="submit" className="lg" disabled={busy || !value.trim()}>{busy ? 'Verificando…' : 'Entrar'}</button>
          {realm === 'portal' && <a href="/" className="small">← Voltar ao site</a>}
        </form>
      </div>
    </div>
  );
}

/**
 * Para onde voltar depois do login (D-Q02): só caminhos internos do próprio app. Bloqueia open redirect
 * ("//evil", "/\evil", esquemas) — o react-router 6 tem aviso de segurança justamente para isso.
 */
export function safeReturnPath(from: string | undefined, fallback: string): string {
  if (!from || !/^\/(portal|backoffice)(\/[A-Za-z0-9\-_/]*)?$/.test(from)) return fallback;
  return from;
}
