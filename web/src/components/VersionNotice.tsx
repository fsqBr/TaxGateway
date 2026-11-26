import { useCallback, useEffect, useRef, useState } from 'react';
import { RefreshCw, Sparkles, X } from 'lucide-react';
import { APP_COMMIT, APP_VERSION, fetchDeployedVersion, isNewDeploy, markSeen, pendingWhatsNew, type ChangelogEntry, type DeployedVersion } from '../lib/version';
import { RichText } from './RichText';

/** "v0.10.0 · 66eb2cf5" — etiqueta discreta (rodapé, barra lateral). */
export const VersionTag = ({ className = 'version-tag' }: { className?: string }) => (
  <span className={className} title={APP_COMMIT ? `build ${APP_COMMIT}` : undefined}>
    v{APP_VERSION}{APP_COMMIT ? ` · ${APP_COMMIT.slice(0, 7)}` : ''}
  </span>
);

/** Modal de novidades: aparece uma vez por versão nova (D-W11). */
export function WhatsNewModal() {
  const [entries, setEntries] = useState<ChangelogEntry[]>([]);
  const button = useRef<HTMLButtonElement>(null);
  const close = useCallback(() => {
    markSeen();
    setEntries([]);
  }, []);
  useEffect(() => setEntries(pendingWhatsNew()), []);
  useEffect(() => {
    if (!entries.length) return undefined;
    button.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') close();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [entries.length, close]);
  if (!entries.length) return null;
  return (
    <div className="modal-backdrop" role="presentation" onClick={close}>
      <div className="modal" role="dialog" aria-modal="true" aria-labelledby="whatsnew-title" onClick={(e) => e.stopPropagation()}>
        <header className="modal-header">
          <span className="modal-icon"><Sparkles size={18} aria-hidden="true" /></span>
          <div>
            <h2 id="whatsnew-title">Novidades da versão {APP_VERSION}</h2>
            <p className="muted small">O TaxGateway foi atualizado. Veja o que mudou.</p>
          </div>
          <button type="button" className="ghost icon-only" aria-label="Fechar" onClick={close}><X size={18} /></button>
        </header>
        <div className="modal-body">
          {entries.slice().reverse().map((e) => (
            <section key={e.version} className="whatsnew-entry">
              {entries.length > 1 && <h3>Versão {e.version} <span className="muted small">· {e.date}</span></h3>}
              {e.sections.map((s) => (
                <div key={s.title} className={s.title.startsWith('Mudanças na API') ? 'whatsnew-section api' : 'whatsnew-section'}>
                  <h4>{s.title}</h4>
                  <ul>{s.items.map((i) => <li key={i}><RichText text={i} /></li>)}</ul>
                </div>
              ))}
            </section>
          ))}
        </div>
        <footer className="modal-footer">
          <a href="/ajuda/novidades" onClick={() => markSeen()}>Ver todas as novidades</a>
          <button type="button" ref={button} onClick={close}>Entendi</button>
        </footer>
      </div>
    </div>
  );
}

/** Aviso de nova versão publicada enquanto a aba está aberta: confere /version.json a cada 5 min e ao voltar à aba. */
export function UpdateBanner({ intervalMs = 5 * 60_000 }: { intervalMs?: number }) {
  const [deployed, setDeployed] = useState<DeployedVersion | null>(null);
  useEffect(() => {
    const ctrl = new AbortController();
    const check = async () => {
      const d = await fetchDeployedVersion(ctrl.signal);
      if (isNewDeploy(d)) setDeployed(d);
    };
    void check();
    const id = window.setInterval(check, intervalMs);
    const onVisible = () => document.visibilityState === 'visible' && void check();
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      ctrl.abort();
      window.clearInterval(id);
      document.removeEventListener('visibilitychange', onVisible);
    };
  }, [intervalMs]);
  if (!deployed) return null;
  return (
    <div className="update-banner" role="status">
      <RefreshCw size={16} aria-hidden="true" />
      <span>
        Nova versão disponível: <strong>{deployed.version}</strong>
        {deployed.version === APP_VERSION && ' (atualização)'}. Recarregue para usar a versão mais recente.
      </span>
      <button type="button" onClick={() => window.location.reload()}>Recarregar</button>
    </div>
  );
}
