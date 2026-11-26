import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import { account, ApiError, AUTH_EVENT, LOGIN_PATH, MFA_PATH, type AuthEventDetail, type Realm } from '../lib/api';

/** Resposta de GET /conta/sessao (D-U01). */
export interface Sessao {
  usuario: { id: string; nome: string; email: string };
  mfa: 'ok' | 'verificar' | 'configurar';
  codigos_recuperacao_restantes?: number | null;
  escopos?: string[];
  // Portal
  empresas?: { tenant_id: string; nome: string; cnpj_raiz: string; papel: string; plano: string; situacao: string }[];
  empresa_ativa?: string;
  papel?: string;
  plano?: string;
  max_usuarios?: number | null;
}

interface Ctx {
  realm: Realm;
  sessao: Sessao | null;
  loading: boolean;
  reload: () => Promise<Sessao | null>;
  logout: () => Promise<void>;
  can: (scope: string) => boolean;
}

const SessionContext = createContext<Ctx | null>(null);

export function useSession(): Ctx {
  const c = useContext(SessionContext);
  if (!c) throw new Error('useSession fora do SessionProvider');
  return c;
}

/** Carrega a sessão da área e reage a 401/MFA vindos de qualquer chamada (evento global do cliente HTTP). */
export function SessionProvider({ realm, children }: { realm: Realm; children: ReactNode }) {
  const [sessao, setSessao] = useState<Sessao | null>(null);
  const [loading, setLoading] = useState(true);
  const navigate = useNavigate();
  const location = useLocation();

  const reload = useCallback(async () => {
    try {
      const s = await account<Sessao>(realm, '/sessao', { silent: true });
      setSessao(s);
      return s;
    } catch (e) {
      if (!(e instanceof ApiError) || e.status !== 401) throw e;
      setSessao(null);
      return null;
    } finally {
      setLoading(false);
    }
  }, [realm]);

  useEffect(() => { void reload(); }, [reload]);

  useEffect(() => {
    const onAuth = (ev: Event) => {
      const d = (ev as CustomEvent<AuthEventDetail>).detail;
      if (d.realm !== realm) return;
      if (d.code === 'ERR_MFA_NECESSARIO') navigate(MFA_PATH[realm], { replace: true });
      else {
        setSessao(null);
        navigate(LOGIN_PATH[realm], { replace: true, state: { from: location.pathname, expirou: true } });
      }
    };
    window.addEventListener(AUTH_EVENT, onAuth);
    return () => window.removeEventListener(AUTH_EVENT, onAuth);
  }, [realm, navigate, location.pathname]);

  const logout = useCallback(async () => {
    try {
      await account(realm, '/sair', { method: 'POST', silent: true });
    } finally {
      setSessao(null);
      navigate(LOGIN_PATH[realm], { replace: true });
    }
  }, [realm, navigate]);

  const can = useCallback((scope: string) => (sessao?.escopos ?? []).includes(scope), [sessao]);

  return <SessionContext.Provider value={{ realm, sessao, loading, reload, logout, can }}>{children}</SessionContext.Provider>;
}

/** Área interna: exige sessão com MFA concluído; senão leva ao login ou ao MFA. */
export function RequireSession({ children }: { children: ReactNode }) {
  const { realm, sessao, loading } = useSession();
  const location = useLocation();
  if (loading) return <p className="muted small" style={{ padding: 32 }} aria-live="polite">Carregando…</p>;
  if (!sessao) return <Navigate to={LOGIN_PATH[realm]} replace state={{ from: location.pathname }} />;
  if (sessao.mfa !== 'ok') return <Navigate to={MFA_PATH[realm]} replace />;
  return <>{children}</>;
}
