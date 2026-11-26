import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import App from '../App';
import { WhatsNewModal } from '../components/VersionNotice';
import { RichText } from '../components/RichText';
import { ARTICLES, searchArticles } from '../help/articles';
import { APP_VERSION, CHANGELOG, compareVersions, isNewDeploy, pendingWhatsNew } from '../lib/version';
import { parseChangelog } from '../lib/changelog';
import { readFileSync } from 'node:fs';

const entry = (version: string) => ({ version, date: '2026-01-01', sections: [{ title: 'Novidades', items: [`item ${version}`] }] });
const renderAt = (path: string) => render(<MemoryRouter initialEntries={[path]}><App /></MemoryRouter>);

describe('versão (D-W11)', () => {
  beforeEach(() => localStorage.clear());

  it('compara versões semânticas com pré-release', () => {
    expect(compareVersions('0.10.0', '0.9.9')).toBe(1);
    expect(compareVersions('1.0.0', '1.0.0')).toBe(0);
    expect(compareVersions('1.0.0-rc.1', '1.0.0')).toBe(-1);
    expect(compareVersions('1.2', '1.2.0')).toBe(0);
  });

  it('novidades: nada na primeira visita; versões entre a vista e a atual depois', () => {
    const log = [entry('0.12.0'), entry('0.11.0'), entry('0.10.0'), entry('0.9.0')];
    expect(pendingWhatsNew('0.11.0', log, null)).toEqual([]);
    expect(localStorage.getItem('tg.versao.vista')).toBe('0.11.0');
    expect(pendingWhatsNew('0.11.0', log, '0.11.0')).toEqual([]);
    expect(pendingWhatsNew('0.11.0', log, '0.9.0').map((e) => e.version)).toEqual(['0.11.0', '0.10.0']);
  });

  it('detecta deploy novo por versão ou commit', () => {
    expect(isNewDeploy(null, '1.0.0', 'abc')).toBe(false);
    expect(isNewDeploy({ version: '1.0.0', commit: 'abc' }, '1.0.0', 'abc')).toBe(false);
    expect(isNewDeploy({ version: '1.1.0', commit: 'abc' }, '1.0.0', 'abc')).toBe(true);
    expect(isNewDeploy({ version: '1.0.0', commit: 'def' }, '1.0.0', 'abc')).toBe(true);
    expect(isNewDeploy({ version: '1.0.0', commit: 'def' }, '1.0.0', '')).toBe(false);
  });

  it('a versão do bundle é a do arquivo VERSION e o CHANGELOG tem a seção dela', () => {
    expect(APP_VERSION).toBe(readFileSync('../VERSION', 'utf-8').trim());
    expect(CHANGELOG[0]?.version).toBe(APP_VERSION);
    expect(CHANGELOG[0]?.sections.length).toBeGreaterThan(0);
  });

  it('lê o formato do CHANGELOG', () => {
    const log = parseChangelog('# Changelog\n\n## [1.1.0] - 2026-12-01\n### Novidades\n- a **b**\n- c\n### Mudanças na API\n- d\n\n## [1.0.0] - 2026-11-01\n- solto\n');
    expect(log).toEqual([
      { version: '1.1.0', date: '2026-12-01', sections: [{ title: 'Novidades', items: ['a **b**', 'c'] }, { title: 'Mudanças na API', items: ['d'] }] },
      { version: '1.0.0', date: '2026-11-01', sections: [{ title: 'Novidades', items: ['solto'] }] },
    ]);
  });

  it('modal aparece para versão nova, fecha com Entendi e não volta', async () => {
    localStorage.setItem('tg.versao.vista', '0.0.1');
    const { unmount } = render(<MemoryRouter><WhatsNewModal /></MemoryRouter>);
    const dialog = await screen.findByRole('dialog', { name: `Novidades da versão ${APP_VERSION}` });
    expect(within(dialog).getByRole('button', { name: 'Entendi' })).toHaveFocus();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Entendi' }));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(localStorage.getItem('tg.versao.vista')).toBe(APP_VERSION);
    unmount();
    render(<MemoryRouter><WhatsNewModal /></MemoryRouter>);
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('modal fecha com Esc', async () => {
    localStorage.setItem('tg.versao.vista', '0.0.1');
    render(<MemoryRouter><WhatsNewModal /></MemoryRouter>);
    await screen.findByRole('dialog');
    await userEvent.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).toBeNull();
  });
});

describe('aviso de nova versão publicada', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('mostra o banner quando /version.json traz outra versão', async () => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.startsWith('/version.json')) return new Response(JSON.stringify({ version: '99.0.0', commit: 'x' }), { headers: { 'content-type': 'application/json' } });
      if (url.startsWith('/v1/conta/sessao')) return new Response(JSON.stringify({ usuario: { id: 'u', nome: 'X', email: 'x@x' }, mfa: 'ok', empresas: [{ tenant_id: 't', nome: 'X', cnpj_raiz: '11222333000181', papel: 'owner', plano: 'GROWTH', situacao: 'ACTIVE' }], empresa_ativa: 't', papel: 'owner', escopos: [] }), { headers: { 'content-type': 'application/json' } });
      if (url.startsWith('/v1/me')) return new Response(JSON.stringify({ tenant_id: 't', nome: 'X', plano: 'GROWTH', situacao: 'ACTIVE', credencial: 'api_key', auto_provisionar_cnpj: false, scopes: [], limites: { rpm: 1, concorrencia: 1 } }), { headers: { 'content-type': 'application/json' } });
      return new Response(JSON.stringify({ code: 'ERR_NOT_FOUND' }), { status: 404, headers: { 'content-type': 'application/problem+json' } });
    }));
    renderAt('/portal');
    expect(await screen.findByText(/Nova versão disponível/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Recarregar' })).toBeInTheDocument();
  });
});

describe('central de ajuda (D-W12)', () => {
  it('slugs únicos, imagens locais e links internos válidos', () => {
    const slugs = new Set(ARTICLES.map((a) => a.slug));
    expect(slugs.size).toBe(ARTICLES.length);
    for (const a of ARTICLES) {
      for (const b of a.blocks) {
        if (b.kind === 'image') expect(b.src).toMatch(/^\/ajuda\/img\/[a-z-]+\.jpg$/);
        const text = JSON.stringify(b);
        for (const m of text.matchAll(/\]\((\/ajuda\/[a-z-]+)\)/g)) {
          const slug = m[1].split('/')[2];
          expect(slug === 'novidades' || slugs.has(slug), `${a.slug} → ${m[1]}`).toBe(true);
        }
      }
    }
  });

  it('busca ignora acento e caixa', () => {
    expect(searchArticles('ASSINATURA')[0].slug).toBe('webhooks');
    expect(searchArticles('certificado').map((a) => a.slug)).toContain('dfe');
    expect(searchArticles('simulacao')[0].slug).toBe('simulacao');
    expect(searchArticles('xyzzy')).toEqual([]);
  });

  it('índice, busca e artigo com código em abas', async () => {
    renderAt('/ajuda');
    expect(screen.getByRole('heading', { name: 'Como podemos ajudar?' })).toBeInTheDocument();
    await userEvent.type(screen.getByRole('searchbox', { name: 'Buscar na ajuda' }), 'hmac');
    await userEvent.click(await screen.findByRole('link', { name: /Receber eventos \(webhooks\)/ }));
    expect(await screen.findByRole('heading', { level: 1, name: 'Receber eventos (webhooks)' })).toBeInTheDocument();
    expect(screen.getAllByText(/20c4d2aac7cefd4a7418877853c004d8c11fafafd0588e9f836b3fb5929fc069/).length).toBeGreaterThan(0);
    await userEvent.click(screen.getByRole('tab', { name: 'Python' }));
    expect(screen.getByRole('tabpanel', { name: 'Python' }).textContent).toContain('hmac.compare_digest');
  });

  it('novidades lista o CHANGELOG e marca a versão como vista', () => {
    localStorage.clear();
    renderAt('/ajuda/novidades');
    expect(screen.getByRole('heading', { level: 2, name: new RegExp(`Versão ${APP_VERSION.replace(/\./g, '\\.')}`) })).toBeInTheDocument();
    expect(localStorage.getItem('tg.versao.vista')).toBe(APP_VERSION);
  });

  it('artigo inexistente mostra aviso', () => {
    renderAt('/ajuda/nao-existe');
    expect(screen.getByRole('heading', { name: 'Artigo não encontrado' })).toBeInTheDocument();
  });

  it('RichText não renderiza HTML nem links externos', () => {
    const { container } = render(<MemoryRouter><RichText text={'<img src=x onerror=alert(1)> [x](https://evil.com) [y](//evil.com) [z](/ajuda/erros)'} /></MemoryRouter>);
    expect(container.querySelector('img')).toBeNull();
    const hrefs = [...container.querySelectorAll('a')].map((a) => a.getAttribute('href'));
    expect(hrefs).toEqual(['#', '#', '/ajuda/erros']);
  });
});
