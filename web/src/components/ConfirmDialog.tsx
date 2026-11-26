import { useEffect, useRef, useState } from 'react';
import { AlertTriangle } from 'lucide-react';

/**
 * Confirmação de ação destrutiva (UX F3): diz O QUE será afetado e o IMPACTO, foca "Cancelar" ao abrir, Esc fecha e,
 * para ações irreversíveis, pede para digitar uma palavra. Substitui o window.confirm("Revogar?") genérico, que não
 * dizia qual item nem a consequência e não seguia o tema.
 */
export function ConfirmButton({ label, title, impact, confirmLabel, typeToConfirm, onConfirm, tone = 'danger', disabled, className }: {
  label: string;
  title: string;
  impact: string;
  confirmLabel?: string;
  typeToConfirm?: string;
  onConfirm: () => unknown;
  tone?: 'danger' | 'secondary';
  disabled?: boolean;
  className?: string;
}) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <button type="button" className={className ?? tone} disabled={disabled} onClick={() => setOpen(true)}>{label}</button>
      {open && (
        <ConfirmDialog title={title} impact={impact} confirmLabel={confirmLabel ?? label} typeToConfirm={typeToConfirm}
          onCancel={() => setOpen(false)}
          onConfirm={async () => { setOpen(false); await onConfirm(); }} />
      )}
    </>
  );
}

export function ConfirmDialog({ title, impact, confirmLabel, typeToConfirm, onCancel, onConfirm }: {
  title: string; impact: string; confirmLabel: string; typeToConfirm?: string; onCancel: () => void; onConfirm: () => void;
}) {
  const cancel = useRef<HTMLButtonElement>(null);
  const [typed, setTyped] = useState('');
  useEffect(() => {
    cancel.current?.focus();
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onCancel(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onCancel]);
  const ok = !typeToConfirm || typed.trim().toUpperCase() === typeToConfirm.toUpperCase();
  return (
    <div className="modal-backdrop" role="presentation" onClick={onCancel}>
      <div className="modal" role="alertdialog" aria-modal="true" aria-labelledby="confirm-title" aria-describedby="confirm-impact" onClick={(e) => e.stopPropagation()}>
        <header className="modal-header">
          <span className="modal-icon danger"><AlertTriangle size={18} aria-hidden="true" /></span>
          <div><h2 id="confirm-title">{title}</h2></div>
        </header>
        <div className="modal-body">
          <p id="confirm-impact">{impact}</p>
          {typeToConfirm && (
            <label className="stack small">
              <span>Para confirmar, digite <strong>{typeToConfirm}</strong>:</span>
              <input value={typed} onChange={(e) => setTyped(e.target.value)} autoComplete="off" aria-label={`Digite ${typeToConfirm} para confirmar`} />
            </label>
          )}
        </div>
        <footer className="modal-footer">
          <button type="button" className="secondary" ref={cancel} onClick={onCancel}>Cancelar</button>
          <button type="button" className="danger" disabled={!ok} onClick={onConfirm}>{confirmLabel}</button>
        </footer>
      </div>
    </div>
  );
}
