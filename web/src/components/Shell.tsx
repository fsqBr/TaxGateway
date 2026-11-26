import { useEffect, useState, type ReactNode } from 'react';
import { NavLink, useLocation } from 'react-router-dom';
import { BookOpen, CircleUserRound, LifeBuoy, LogOut, Menu, X, type LucideIcon } from 'lucide-react';
import { useSession } from '../auth/session';
import { account, type Realm } from '../lib/api';
import { cnpj } from '../lib/format';
import { Logo } from './Logo';
import { Badge } from './ui';
import { UpdateBanner, VersionTag, WhatsNewModal } from './VersionNotice';

export { safeReturnPath } from '../auth/AuthPages';

export interface NavItem {
  to: string;
  label: string;
  icon: LucideIcon;
  section?: string;
  /** Só aparece para quem tem o escopo (papel da pessoa). */
  scope?: string;
}

/** Empresa ativa e troca entre empresas (escritórios e software houses com vários tenants, D-U05). */
function Workspace() {
  const { sessao, reload } = useSession();
  const empresas = sessao?.empresas ?? [];
  const ativa = empresas.find((e) => e.tenant_id === sessao?.empresa_ativa);
  if (!ativa) return null;
  return (
    <div className="workspace">
      {empresas.length > 1 ? (
        <select aria-label="Empresa" value={ativa.tenant_id} onChange={async (e) => {
          await account('portal', '/empresa-ativa', { method: 'POST', body: { tenant_id: e.target.value } });
          await reload();
          window.location.assign('/portal');   // recarrega tudo no contexto da outra empresa
        }}>
          {empresas.map((e) => <option key={e.tenant_id} value={e.tenant_id}>{e.nome}</option>)}
        </select>
      ) : <strong title={ativa.nome}>{ativa.nome}</strong>}
      <span className="muted small mono">{cnpj(ativa.cnpj_raiz)}</span>
      <span><Badge tone="info">{ativa.plano}</Badge></span>
    </div>
  );
}

/** Barra lateral com seções + conteúdo. Sair encerra a sessão no servidor. */
export function Shell({ realm, nav, workspace, topbar, children }: { realm: Realm; nav: NavItem[]; workspace?: ReactNode; topbar?: ReactNode; children: ReactNode }) {
  const { sessao, logout, can } = useSession();
  // Celular (UX: antes o menu inteiro empurrava o conteúdo para baixo da dobra): menu recolhido atrás de um botão.
  const [open, setOpen] = useState(false);
  const location = useLocation();
  useEffect(() => setOpen(false), [location.pathname]);
  let lastSection: string | undefined;
  return (
    <div className={`shell${open ? ' nav-open' : ''}`}>
      <a className="skip-link" href="#conteudo">Pular para o conteúdo</a>
      <header className="mobile-bar">
        <Logo size={24} suffix={realm === 'portal' ? 'Portal' : 'Backoffice'} />
        <div className="row">
          {topbar}
          <button type="button" className="ghost icon-only" aria-expanded={open} aria-controls="menu-lateral" aria-label={open ? 'Fechar menu' : 'Abrir menu'} onClick={() => setOpen(!open)}>
            {open ? <X size={20} aria-hidden="true" /> : <Menu size={20} aria-hidden="true" />}
          </button>
        </div>
      </header>
      <aside className="sidebar" id="menu-lateral">
        <Logo size={28} suffix={realm === 'portal' ? 'Portal' : 'Backoffice'} />
        {topbar && <div className="sidebar-topbar">{topbar}</div>}
        {realm === 'backoffice' && <div className="internal-banner">Ambiente interno</div>}
        {realm === 'portal' ? <Workspace /> : workspace && <div className="workspace">{workspace}</div>}
        <nav className="nav" aria-label={realm === 'portal' ? 'Portal' : 'Backoffice'}>
          {nav.filter((n) => !n.scope || can(n.scope)).map((n) => {
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
          {realm === 'portal' && (
            <NavLink to="/portal/conta" end>
              <CircleUserRound size={17} aria-hidden="true" />
              <span className="truncate" title={sessao?.usuario.email}>{sessao?.usuario.nome.split(' ')[0] ?? 'Minha conta'}</span>
            </NavLink>
          )}
          <a href="/ajuda" target="_blank" rel="noreferrer"><LifeBuoy size={17} aria-hidden="true" />Central de ajuda</a>
          <a href="/docs/" target="_blank" rel="noreferrer"><BookOpen size={17} aria-hidden="true" />Referência da API</a>
          <button type="button" className="ghost" onClick={() => void logout()}>
            <LogOut size={17} aria-hidden="true" /> Sair
          </button>
          <VersionTag />
        </div>
      </aside>
      <main className="content" id="conteudo" tabIndex={-1}>
        <UpdateBanner />
        {children}
      </main>
      <WhatsNewModal />
    </div>
  );
}
