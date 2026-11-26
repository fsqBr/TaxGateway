import { Fragment } from 'react';
import { Link } from 'react-router-dom';

/** Markdown mínimo do changelog e da ajuda: **negrito**, `código` e [texto](link interno). Sem HTML (sem XSS). */
export function RichText({ text }: { text: string }) {
  const parts = text.split(/(\*\*[^*]+\*\*|`[^`]+`|\[[^\]]+\]\([^)]+\))/g);
  return (
    <>
      {parts.map((p, i) => {
        if (p.startsWith('**') && p.endsWith('**')) return <strong key={i}>{p.slice(2, -2)}</strong>;
        if (p.startsWith('`') && p.endsWith('`')) return <code key={i}>{p.slice(1, -1)}</code>;
        const link = /^\[([^\]]+)\]\(([^)]+)\)$/.exec(p);
        if (link) {
          const href = link[2];
          const safe = href.startsWith('/') && !href.startsWith('//') ? href : '#';   // só links internos
          // Rotas do SPA navegam sem recarregar; /docs e /openapi são servidos pela API (link comum).
          if (/^\/(ajuda|portal|backoffice)(\/|$)/.test(safe)) return <Link key={i} to={safe}>{link[1]}</Link>;
          return <a key={i} href={safe}>{link[1]}</a>;
        }
        return <Fragment key={i}>{p}</Fragment>;
      })}
    </>
  );
}
