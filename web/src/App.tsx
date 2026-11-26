import { Navigate, Route, Routes } from 'react-router-dom';
import { Login, RequireCredential } from './components/Shell';
import Landing from './pages/Landing';
import PortalApp from './portal/PortalApp';
import BackofficeApp from './backoffice/BackofficeApp';

// Alvo do build (D-W03): "public" (site + portal), "backoffice" (só backoffice, publicado em host interno) ou "all" (dev).
const target = import.meta.env.VITE_APP_TARGET ?? 'all';
const hasPublic = target === 'public' || target === 'all';
const hasBackoffice = target === 'backoffice' || target === 'all';

export default function App() {
  return (
    <Routes>
      {hasPublic && (
        <>
          <Route path="/" element={<Landing />} />
          <Route path="/portal/entrar" element={<Login realm="portal" />} />
          <Route path="/portal/*" element={<RequireCredential realm="portal"><PortalApp /></RequireCredential>} />
        </>
      )}
      {hasBackoffice && (
        <>
          <Route path="/backoffice/entrar" element={<Login realm="backoffice" />} />
          <Route path="/backoffice/*" element={<RequireCredential realm="backoffice"><BackofficeApp /></RequireCredential>} />
        </>
      )}
      <Route path="*" element={<Navigate to={hasPublic ? '/' : '/backoffice'} replace />} />
    </Routes>
  );
}
