import { useEffect, useMemo, useState } from 'react';
import { Link, NavLink, useParams } from 'react-router-dom';
import { AlertTriangle, ArrowLeft, ArrowRight, Check, CheckCircle2, Copy, Info, Search, Sparkles } from 'lucide-react';
import { Logo } from '../components/Logo';
import { RichText } from '../components/RichText';
import { VersionTag } from '../components/VersionNotice';
import { APP_VERSION, CHANGELOG, compareVersions, markSeen } from '../lib/version';
import { ARTICLES, CATEGORIES, NOVIDADES_ICON, searchArticles } from './articles';
import type { Article, Block } from './types';

// Central de ajuda pública (D-W12): /ajuda, /ajuda/:slug e /ajuda/novidades. Conteúdo estático do bundle, sem chamadas
// à API — funciona antes do login e não depende de credencial.

function useTitle(title: string) {
  useEffect(() => {
    const prev = document.title;
    document.title = `${title} · Ajuda TaxGateway`;
    return () => { document.title = prev; };
  }, [title]);
}

function HelpHeader() {
  return (
    <header className="site-header">
      <div className="inner">
        <Link to="/" aria-label="Início"><Logo size={30} suffix="Ajuda" /></Link>
        <nav className="site-nav" aria-label="Principal">
          <Link to="/ajuda" className="hide-sm">Central de ajuda</Link>
          <Link to="/ajuda/novidades" className="hide-sm">Novidades</Link>
          <a href="/docs/">Referência da API</a>
          <Link className="button" to="/portal">Entrar</Link>
        </nav>
      </div>
    </header>
  );
}

function HelpFooter() {
  return (
    <footer className="help-footer">
      <span>© TaxGateway · <VersionTag className="" /></span>
      <span>Indícios de auditoria não constituem parecer tributário.</span>
    </footer>
  );
}

function CodeBlock({ block }: { block: Extract<Block, { kind: 'code' }> }) {
  const [tab, setTab] = useState(0);
  const [copied, setCopied] = useState(false);
  const current = block.tabs[Math.min(tab, block.tabs.length - 1)];
  const multi = block.tabs.length > 1;
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(current.code);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch { /* sem permissão de área de transferência: o usuário copia manualmente */ }
  };
  return (
    <div className="help-code">
      <div className="help-code-bar">
        {block.title && <span className="help-code-title">{block.title}</span>}
        {multi ? (
          <div className="help-code-tabs" role="tablist" aria-label={block.title ?? 'Exemplo'}>
            {block.tabs.map((t, i) => (
              <button key={t.label} type="button" role="tab" aria-selected={i === tab} className={i === tab ? 'active' : ''} onClick={() => setTab(i)}>
                {t.label}
              </button>
            ))}
          </div>
        ) : <span className="help-code-tabs help-code-lang">{current.label}</span>}
        <button type="button" className="help-copy" onClick={copy} aria-label="Copiar código">
          {copied ? <Check size={14} aria-hidden="true" /> : <Copy size={14} aria-hidden="true" />}
          {copied ? 'Copiado' : 'Copiar'}
        </button>
      </div>
      <pre data-lang={current.lang} role={multi ? 'tabpanel' : undefined} aria-label={multi ? current.label : undefined}><code>{current.code}</code></pre>
    </div>
  );
}

function Figure({ block }: { block: Extract<Block, { kind: 'image' }> }) {
  const [failed, setFailed] = useState(false);
  if (failed) return null;   // captura ainda não gerada (npm run screenshots): some sem quebrar o artigo
  return (
    <figure className="help-figure">
      <img src={block.src} alt={block.alt} loading="lazy" decoding="async" onError={() => setFailed(true)} />
      {block.caption && <figcaption>{block.caption}</figcaption>}
    </figure>
  );
}

const CALLOUT_ICON = { info: Info, warning: AlertTriangle, success: CheckCircle2 } as const;

export function BlockView({ block }: { block: Block }) {
  switch (block.kind) {
    case 'p': return <p><RichText text={block.text} /></p>;
    case 'h': return <h2 id={block.id}>{block.text}</h2>;
    case 'steps': return <ol className="help-steps">{block.items.map((s, i) => <li key={i}><RichText text={s} /></li>)}</ol>;
    case 'list': return <ul className="help-list">{block.items.map((s, i) => <li key={i}><RichText text={s} /></li>)}</ul>;
    case 'callout': {
      const Icon = CALLOUT_ICON[block.tone];
      return <div className={`help-callout ${block.tone}`}><Icon size={18} aria-hidden="true" /><div><RichText text={block.text} /></div></div>;
    }
    case 'image': return <Figure block={block} />;
    case 'code': return <CodeBlock block={block} />;
    case 'table':
      return (
        <div className="table-wrap help-table">
          <table>
            <thead><tr>{block.head.map((h) => <th key={h}><RichText text={h} /></th>)}</tr></thead>
            <tbody>{block.rows.map((r, i) => <tr key={i}>{r.map((c, j) => <td key={j}><RichText text={c} /></td>)}</tr>)}</tbody>
          </table>
        </div>
      );
  }
}

function Sidebar() {
  return (
    <nav className="help-sidebar" aria-label="Artigos da ajuda">
      {CATEGORIES.map((cat) => (
        <div key={cat}>
          <h4>{cat}</h4>
          <ul>
            {ARTICLES.filter((a) => a.category === cat).map((a) => (
              <li key={a.slug}><NavLink to={`/ajuda/${a.slug}`}>{a.title}</NavLink></li>
            ))}
          </ul>
        </div>
      ))}
      <div>
        <h4>Plataforma</h4>
        <ul><li><NavLink to="/ajuda/novidades">Novidades da versão</NavLink></li></ul>
      </div>
    </nav>
  );
}

function ArticleCard({ a }: { a: Article }) {
  return (
    <Link to={`/ajuda/${a.slug}`} className="card help-card">
      <span className="icon"><a.icon size={20} aria-hidden="true" /></span>
      <h3>{a.title}</h3>
      <p className="muted">{a.summary}</p>
    </Link>
  );
}

function HelpHome() {
  useTitle('Central de ajuda');
  const [q, setQ] = useState('');
  const results = useMemo(() => searchArticles(q), [q]);
  const latest = CHANGELOG[0];
  return (
    <>
      <section className="help-hero">
        <div className="eyebrow">Central de ajuda</div>
        <h1>Como podemos ajudar?</h1>
        <p className="lead">Guias passo a passo com exemplos reais da API e telas do portal.</p>
        <label className="help-search">
          <Search size={18} aria-hidden="true" />
          <span className="sr-only">Buscar na ajuda</span>
          <input type="search" placeholder="Busque por simular, webhook, certificado, erro 403…" value={q} onChange={(e) => setQ(e.target.value)} autoFocus />
        </label>
      </section>
      <div className="help-home">
        {q.trim() ? (
          <section aria-live="polite">
            <h2 className="help-cat-title">{results.length ? `${results.length} resultado(s) para "${q.trim()}"` : `Nada encontrado para "${q.trim()}"`}</h2>
            {results.length ? <div className="grid help-grid">{results.map((a) => <ArticleCard key={a.slug} a={a} />)}</div>
              : <p className="muted">Tente outras palavras ou veja a <a href="/docs/">referência da API</a>.</p>}
          </section>
        ) : (
          <>
            {latest && (
              <Link to="/ajuda/novidades" className="help-news">
                <Sparkles size={18} aria-hidden="true" />
                <span><strong>Novidades da versão {latest.version}</strong> — veja o que mudou na plataforma.</span>
                <ArrowRight size={16} aria-hidden="true" />
              </Link>
            )}
            {CATEGORIES.map((cat) => (
              <section key={cat}>
                <h2 className="help-cat-title">{cat}</h2>
                <div className="grid help-grid">{ARTICLES.filter((a) => a.category === cat).map((a) => <ArticleCard key={a.slug} a={a} />)}</div>
              </section>
            ))}
          </>
        )}
      </div>
    </>
  );
}

function ArticleView({ article }: { article: Article }) {
  useTitle(article.title);
  useEffect(() => { window.scrollTo(0, 0); }, [article.slug]);
  const idx = ARTICLES.indexOf(article);
  const next = ARTICLES[idx + 1];
  return (
    <div className="help-layout">
      <Sidebar />
      <article className="help-article">
        <Link to="/ajuda" className="help-back"><ArrowLeft size={14} aria-hidden="true" /> Central de ajuda</Link>
        <div className="eyebrow">{article.category}</div>
        <h1>{article.title}</h1>
        <p className="lead">{article.summary}</p>
        {article.blocks.map((b, i) => <BlockView key={i} block={b} />)}
        {next && (
          <Link to={`/ajuda/${next.slug}`} className="card help-next">
            <span className="muted small">Próximo</span>
            <strong>{next.title} <ArrowRight size={14} aria-hidden="true" /></strong>
          </Link>
        )}
      </article>
    </div>
  );
}

function NovidadesView() {
  useTitle('Novidades');
  useEffect(() => { markSeen(); }, []);   // quem leu a página inteira não precisa ver o modal
  const Icon = NOVIDADES_ICON;
  return (
    <div className="help-layout">
      <Sidebar />
      <article className="help-article">
        <Link to="/ajuda" className="help-back"><ArrowLeft size={14} aria-hidden="true" /> Central de ajuda</Link>
        <div className="eyebrow">Plataforma</div>
        <h1><Icon size={26} aria-hidden="true" /> Novidades</h1>
        <p className="lead">
          O que mudou em cada versão. A versão em uso é <strong>{APP_VERSION}</strong>; a API informa a dela em
          {' '}<code>GET /version</code> e no header <code>X-TaxGateway-Version</code>.
        </p>
        {CHANGELOG.length === 0 && <p className="muted">Nenhuma versão publicada ainda.</p>}
        {CHANGELOG.map((e) => (
          <section key={e.version} className="help-release" id={`v${e.version}`}>
            <h2>
              Versão {e.version}
              {compareVersions(e.version, APP_VERSION) === 0 && <span className="badge badge-ok">atual</span>}
              <span className="muted small">{e.date}</span>
            </h2>
            {e.sections.map((s) => (
              <div key={s.title} className={s.title === 'Mudanças na API' ? 'help-release-api' : undefined}>
                <h3>{s.title}</h3>
                <ul className="help-list">{s.items.map((it, i) => <li key={i}><RichText text={it} /></li>)}</ul>
              </div>
            ))}
          </section>
        ))}
      </article>
    </div>
  );
}

function NotFound() {
  useTitle('Artigo não encontrado');
  return (
    <div className="help-home">
      <h1>Artigo não encontrado</h1>
      <p className="muted">O endereço pode ter mudado. <Link to="/ajuda">Voltar para a central de ajuda</Link>.</p>
    </div>
  );
}

export default function HelpCenter() {
  const { slug } = useParams();
  let body;
  if (!slug) body = <HelpHome />;
  else if (slug === 'novidades') body = <NovidadesView />;
  else {
    const article = ARTICLES.find((a) => a.slug === slug);
    body = article ? <ArticleView article={article} /> : <NotFound />;
  }
  return (
    <div className="site help">
      <HelpHeader />
      <main id="conteudo">{body}</main>
      <HelpFooter />
    </div>
  );
}
