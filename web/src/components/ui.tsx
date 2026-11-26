import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { AlertTriangle, CheckCircle2, Copy, Inbox, Info, KeyRound, XCircle, type LucideIcon } from 'lucide-react';
import { describeError } from '../lib/api';

export function useLoad<T>(load: () => Promise<T>, deps: unknown[] = []) {
  const [data, setData] = useState<T | undefined>();
  const [error, setError] = useState<string | undefined>();
  const [loading, setLoading] = useState(true);
  const reload = useCallback(async () => {
    setLoading(true);
    setError(undefined);
    try {
      setData(await load());
    } catch (e) {
      setError(describeError(e));
    } finally {
      setLoading(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);
  useEffect(() => {
    void reload();
  }, [reload]);
  return { data, error, loading, reload };
}

/** Executa uma ação de formulário mostrando erro e estado de envio. */
export function useAction() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const run = useCallback(async <T,>(fn: () => Promise<T>): Promise<T | undefined> => {
    setBusy(true);
    setError(undefined);
    try {
      return await fn();
    } catch (e) {
      setError(describeError(e));
      return undefined;
    } finally {
      setBusy(false);
    }
  }, []);
  return { busy, error, run, setError };
}

export const PageHeader = ({ title, description, eyebrow, children }: { title: string; description?: string; eyebrow?: string; children?: ReactNode }) => (
  <div className="page-header">
    <div>
      {eyebrow && <div className="eyebrow">{eyebrow}</div>}
      <h1>{title}</h1>
      {description && <p>{description}</p>}
    </div>
    {children && <div className="row">{children}</div>}
  </div>
);

export const Card = ({ title, description, children, actions, flush }: { title?: string; description?: string; children: ReactNode; actions?: ReactNode; flush?: boolean }) => (
  <section className={flush ? 'card flush' : 'card'}>
    {(title || actions) && (
      <header className="card-header">
        <div>
          {title && <h2>{title}</h2>}
          {description && <p className="muted small">{description}</p>}
        </div>
        {actions && <div className="row">{actions}</div>}
      </header>
    )}
    {children}
  </section>
);

const ALERT_ICON: Record<string, LucideIcon> = { error: XCircle, info: Info, success: CheckCircle2, warning: AlertTriangle };

export const Alert = ({ kind = 'error', children }: { kind?: 'error' | 'info' | 'success' | 'warning'; children: ReactNode }) => {
  if (!children) return null;
  const Icon = ALERT_ICON[kind];
  return (
    <div className={`alert alert-${kind}`} role={kind === 'error' ? 'alert' : 'status'}>
      <Icon size={16} aria-hidden="true" />
      <div>{children}</div>
    </div>
  );
};

export const Loading = () => <p className="muted small" aria-live="polite">Carregando…</p>;

export function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <div className="field">
      <label className="field">
        <span className="field-label">{label}</span>
        {children}
      </label>
      {hint && <span className="field-hint">{hint}</span>}
    </div>
  );
}

export const Badge = ({ tone = 'neutral', children }: { tone?: 'neutral' | 'ok' | 'warn' | 'bad' | 'info'; children: ReactNode }) => (
  <span className={`badge badge-${tone}`}>{children}</span>
);

const STATUS_LABEL: Record<string, string> = {
  ACTIVE: 'Ativo', SUSPENDED: 'Suspenso', CLOSED: 'Encerrado', CONFORME: 'Conforme', DIVERGENTE: 'Divergente', INDICIO: 'Indício',
  SCHEMA_ERROR: 'Fora do schema', ASSINATURA_INVALIDA: 'Assinatura inválida', INDETERMINADO: 'Indeterminado',
  DELIVERED: 'Entregue', PENDING: 'Pendente', RETRYING: 'Nova tentativa', DEAD: 'Falhou', QUEUED: 'Na fila', RUNNING: 'Processando',
  COMPLETED: 'Concluído', FAILED: 'Falhou', REJECTED: 'Recusado', PROPOSED: 'Aguardando aprovação', APPLIED: 'Aplicada',
  AUDITADO: 'Auditado', REJEITADO: 'Recusado', CIENCIA_ENVIADA: 'Ciência enviada', CIENCIA_FALHOU: 'Ciência falhou', RECEBIDO: 'Recebido', IGNORADO: 'Ignorado',
};

export const statusLabel = (s: string | null | undefined) => (s ? STATUS_LABEL[s] ?? s : '—');

export function statusTone(status: string | null | undefined): 'neutral' | 'ok' | 'warn' | 'bad' | 'info' {
  switch (status) {
    case 'ACTIVE': case 'CONFORME': case 'DELIVERED': case 'COMPLETED': case 'AUDITADO': case 'APPLIED': case 'CIENCIA_ENVIADA':
      return 'ok';
    case 'DIVERGENTE': case 'RETRYING': case 'PENDING': case 'QUEUED': case 'RUNNING': case 'PROPOSED': case 'INDICIO':
      return 'warn';
    case 'SUSPENDED': case 'DEAD': case 'FAILED': case 'REJEITADO': case 'SCHEMA_ERROR': case 'ASSINATURA_INVALIDA': case 'CIENCIA_FALHOU': case 'REJECTED': case 'CLOSED':
      return 'bad';
    default:
      return 'neutral';
  }
}

export const StatusBadge = ({ status }: { status: string | null | undefined }) => <Badge tone={statusTone(status)}>{statusLabel(status)}</Badge>;

export function Stat({ label, value, icon: Icon, hint, meter }: { label: string; value: string; icon?: LucideIcon; hint?: ReactNode; meter?: number }) {
  return (
    <div className="card stat">
      <div className="stat-top">
        <span>{label}</span>
        {Icon && <span className="stat-icon"><Icon size={16} aria-hidden="true" /></span>}
      </div>
      <span className="stat-value">{value}</span>
      {meter !== undefined && (
        <div className={meter > 1 ? 'meter over' : 'meter'} role="progressbar" aria-valuenow={Math.round(meter * 100)} aria-valuemin={0} aria-valuemax={100} aria-label={label}>
          <span style={{ width: `${Math.min(100, Math.round(meter * 100))}%` }} />
        </div>
      )}
      {hint && <span className="muted small">{hint}</span>}
    </div>
  );
}

/** Segredo exibido uma única vez (API key, client_secret, segredo de webhook). */
export function SecretOnce({ label, value, onClose }: { label: string; value: string; onClose: () => void }) {
  const [copied, setCopied] = useState(false);
  return (
    <div className="secret-once" role="dialog" aria-label={label}>
      <strong><KeyRound size={16} aria-hidden="true" /> {label}</strong>
      <p className="muted small">Copie agora: este valor não será exibido de novo. Guarde em um cofre de segredos, nunca no código.</p>
      <code className="secret-value">{value}</code>
      <div className="row">
        <button
          type="button"
          onClick={async () => {
            try {
              await navigator.clipboard.writeText(value);
              setCopied(true);
            } catch {
              setCopied(false);
            }
          }}
        >
          <Copy size={14} aria-hidden="true" /> {copied ? 'Copiado' : 'Copiar'}
        </button>
        <button type="button" className="secondary" onClick={onClose}>Já guardei</button>
      </div>
    </div>
  );
}

export function Table({ head, children, empty, emptyText = 'Nada por aqui ainda.' }: { head: string[]; children: ReactNode; empty?: boolean; emptyText?: string }) {
  return (
    <div className="table-wrap">
      <table>
        <thead>
          <tr>{head.map((h, i) => <th key={`${h}-${i}`}>{h}</th>)}</tr>
        </thead>
        <tbody>
          {empty ? (
            <tr>
              <td colSpan={head.length} className="empty">
                <Inbox size={28} aria-hidden="true" />
                <div>{emptyText}</div>
              </td>
            </tr>
          ) : (
            children
          )}
        </tbody>
      </table>
    </div>
  );
}

export function Confirm({ label, onConfirm, tone = 'danger', disabled }: { label: string; onConfirm: () => void; tone?: 'danger' | 'secondary'; disabled?: boolean }) {
  return (
    <button
      type="button"
      className={tone}
      disabled={disabled}
      onClick={() => {
        if (window.confirm(`${label}?`)) onConfirm();
      }}
    >
      {label}
    </button>
  );
}

export const Json = ({ value }: { value: unknown }) => <pre className="json">{JSON.stringify(value, null, 2)}</pre>;
