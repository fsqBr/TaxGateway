import { Navigate, Route, Routes } from 'react-router-dom';
import Landing from './pages/Landing';
import PortalApp from './portal/PortalApp';
import BackofficeApp from './backoffice/BackofficeApp';
import HelpCenter from './help/HelpCenter';
import { LegalPage } from './pages/Legal';
import PlanosPage from './pages/Planos';
import { RequireSession, SessionProvider } from './auth/session';
import { ConfirmEmailPage, ForgotPasswordPage, InvitePage, LoginPage, MfaPage, ResetPasswordPage, SignupPage } from './auth/AuthPages';
import type { Realm } from './lib/api';

// Alvo do build (D-W03): "public" (site + portal), "backoffice" (só backoffice, publicado em host interno) ou "all" (dev).
const target = import.meta.env.VITE_APP_TARGET ?? 'all';
const hasPublic = target === 'public' || target === 'all';
const hasBackoffice = target === 'backoffice' || target === 'all';

/** Rotas de conta de uma área (D-U01): entrar, MFA, senha; o resto exige sessão com MFA. */
function Area({ realm, children }: { realm: Realm; children: React.ReactNode }) {
  return (
    <SessionProvider realm={realm}>
      <Routes>
        <Route path="entrar" element={<LoginPage realm={realm} />} />
        <Route path="mfa" element={<MfaPage realm={realm} />} />
        <Route path="esqueci-senha" element={<ForgotPasswordPage realm={realm} />} />
        <Route path="redefinir-senha" element={<ResetPasswordPage realm={realm} />} />
        {realm === 'portal' && <Route path="confirmar-email" element={<ConfirmEmailPage />} />}
        {realm === 'portal' && <Route path="convite" element={<InvitePage />} />}
        <Route path="*" element={<RequireSession>{children}</RequireSession>} />
      </Routes>
    </SessionProvider>
  );
}

export default function App() {
  return (
    <Routes>
      {hasPublic && (
        <>
          <Route path="/" element={<Landing />} />
          <Route path="/ajuda" element={<HelpCenter />} />
          <Route path="/ajuda/:slug" element={<HelpCenter />} />
          <Route path="/planos" element={<PlanosPage />} />
          <Route path="/termos" element={<LegalPage doc="termos" />} />
          <Route path="/privacidade" element={<LegalPage doc="privacidade" />} />
          <Route path="/cadastro" element={<SessionProvider realm="portal"><SignupPage /></SessionProvider>} />
          <Route path="/portal/*" element={<Area realm="portal"><PortalApp /></Area>} />
        </>
      )}
      {hasBackoffice && (
        <Route path="/backoffice/*" element={<Area realm="backoffice"><BackofficeApp /></Area>} />
      )}
      <Route path="*" element={<Navigate to={hasPublic ? '/' : '/backoffice'} replace />} />
    </Routes>
  );
}
