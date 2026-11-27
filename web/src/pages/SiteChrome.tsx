import { Link, NavLink } from 'react-router-dom';
import { Logo } from '../components/Logo';
import { VersionTag } from '../components/VersionNotice';

// Cabeçalho e rodapé únicos do site público (landing, planos, alíquotas, ajuda e documentos legais): mesmo menu,
// mesmo rótulo para o mesmo destino em todas as páginas.

/** Destinos do menu público, na ordem em que aparecem. `/docs/` e `/#…` são links comuns (fora do SPA ou âncora). */
export const SITE_NAV = [
  { label: 'Produto', href: '/#produto' },
  { label: 'Planos', to: '/planos' },
  { label: 'Alíquotas', to: '/aliquotas' },
  { label: 'Documentação da API', href: '/docs/' },
  { label: 'Ajuda', to: '/ajuda', always: true },
] as const;

export function SiteHeader({ suffix }: { suffix?: string }) {
  return (
    <header className="site-header">
      <div className="inner">
        <Link to="/" aria-label="Início"><Logo size={30} suffix={suffix} /></Link>
        <nav className="site-nav" aria-label="Principal">
          {SITE_NAV.map((item) => {
            const cls = 'always' in item && item.always ? undefined : 'hide-sm';
            return 'to' in item
              ? <NavLink key={item.label} to={item.to} className={cls} end={item.to !== '/ajuda'}>{item.label}</NavLink>
              : <a key={item.label} href={item.href} className={cls}>{item.label}</a>;
          })}
          <Link to="/portal" className="hide-sm">Entrar</Link>
          <Link className="button" to="/cadastro">Criar conta grátis</Link>
        </nav>
      </div>
    </header>
  );
}

export function SiteFooter() {
  return (
    <footer className="site-footer">
      <div className="inner">
        <div>
          <Logo size={26} />
          <p className="muted small" style={{ marginTop: 10, maxWidth: 320 }}>Cálculo e auditoria de IBS, CBS e Imposto Seletivo sobre os motores oficiais da Receita Federal.</p>
        </div>
        <div>
          <h4>Produto</h4>
          <ul>
            <li><a href="/#produto">Produto</a></li>
            <li><Link to="/planos">Planos</Link></li>
            <li><Link to="/aliquotas">Alíquotas</Link></li>
            <li><a href="/#seguranca">Segurança e LGPD</a></li>
            <li><Link to="/ajuda/novidades">Novidades</Link></li>
          </ul>
        </div>
        <div>
          <h4>Desenvolvedores</h4>
          <ul>
            <li><a href="/docs/">Documentação da API</a></li>
            <li><a href="/openapi/v1.json">OpenAPI (JSON)</a></li>
            <li><Link to="/ajuda">Ajuda</Link></li>
          </ul>
        </div>
        <div>
          <h4>Acesso</h4>
          <ul>
            <li><Link to="/cadastro">Criar conta grátis</Link></li>
            <li><Link to="/portal">Entrar</Link></li>
            <li><Link to="/termos">Termos de Uso</Link></li>
            <li><Link to="/privacidade">Privacidade</Link></li>
          </ul>
        </div>
      </div>
      <div className="legal"><div className="inner"><span>© TaxGateway · <VersionTag className="" /></span><span>Indícios não constituem parecer tributário.</span></div></div>
    </footer>
  );
}
