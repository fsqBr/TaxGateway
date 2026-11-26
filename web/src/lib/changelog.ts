// Parser do CHANGELOG.md da raiz (D-W11). Puro (sem Node): usado pelo vite.config.ts no build e pelos testes.

export interface ChangelogEntry { version: string; date: string; sections: { title: string; items: string[] }[] }

/** "## [0.10.0] - 2026-11-26" + "### Título" + "- item".  */
export function parseChangelog(md: string): ChangelogEntry[] {
  const out: ChangelogEntry[] = [];
  let cur: ChangelogEntry | undefined;
  for (const raw of md.split(/\r?\n/)) {
    const line = raw.trimEnd();
    const v = /^## \[([^\]]+)\]\s*-\s*(\S+)/.exec(line);
    if (v) {
      cur = { version: v[1], date: v[2], sections: [] };
      out.push(cur);
      continue;
    }
    const h = /^### (.+)/.exec(line);
    if (h && cur) {
      cur.sections.push({ title: h[1].trim(), items: [] });
      continue;
    }
    const item = /^- (.+)/.exec(line);
    if (item && cur) {
      if (!cur.sections.length) cur.sections.push({ title: 'Novidades', items: [] });   // item fora de seção
      cur.sections[cur.sections.length - 1].items.push(item[1].trim());
    }
  }
  return out;
}
