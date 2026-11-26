import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { FileUp, X } from 'lucide-react';
import { cnpj as formatCnpj, onlyCnpjChars } from '../lib/format';

/**
 * Área de arquivo em pt-BR (UX B1): o input nativo mostra "Choose File" no Chrome em inglês e não aceita arrastar
 * com feedback. Arrastar e soltar, clique ou Enter/Espaço; mostra o arquivo escolhido e permite trocar.
 */
export function Dropzone({ accept, label, hint, file, onFile }: { accept: string; label: string; hint?: string; file?: File; onFile: (f?: File) => void }) {
  const input = useRef<HTMLInputElement>(null);
  const [over, setOver] = useState(false);
  const id = useId();
  return (
    <div className="field">
      <span className="field-label" id={`${id}-l`}>{label}</span>
      <div
        className={`dropzone${over ? ' over' : ''}${file ? ' has-file' : ''}`}
        role="button"
        tabIndex={0}
        aria-labelledby={`${id}-l`}
        aria-describedby={hint ? `${id}-h` : undefined}
        onClick={() => input.current?.click()}
        onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); input.current?.click(); } }}
        onDragOver={(e) => { e.preventDefault(); setOver(true); }}
        onDragLeave={() => setOver(false)}
        onDrop={(e) => { e.preventDefault(); setOver(false); const f = e.dataTransfer.files?.[0]; if (f) onFile(f); }}
      >
        <FileUp size={22} aria-hidden="true" />
        {file ? (
          <span><strong>{file.name}</strong> <span className="muted small">({Math.max(1, Math.round(file.size / 1024))} KB)</span></span>
        ) : (
          <span><strong>Arraste o arquivo aqui</strong> ou <span className="link-like">clique para escolher</span></span>
        )}
        {file && (
          <button type="button" className="ghost icon-only" aria-label="Remover arquivo" onClick={(e) => { e.stopPropagation(); onFile(undefined); if (input.current) input.current.value = ''; }}>
            <X size={16} aria-hidden="true" />
          </button>
        )}
        <input ref={input} type="file" accept={accept} hidden onChange={(e) => onFile(e.target.files?.[0])} />
      </div>
      {hint && <span className="field-hint" id={`${id}-h`}>{hint}</span>}
    </div>
  );
}

/** Painel lateral (detalhe de documento, aviso...). Esc fecha; foco vai para o painel e volta ao fechar. */
export function Drawer({ title, onClose, children, wide }: { title: string; onClose: () => void; children: ReactNode; wide?: boolean }) {
  const panel = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    panel.current?.focus();
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => { window.removeEventListener('keydown', onKey); previous?.focus?.(); };
  }, [onClose]);
  return (
    <div className="drawer-backdrop" role="presentation" onClick={onClose}>
      <div className={`drawer${wide ? ' wide' : ''}`} role="dialog" aria-modal="true" aria-label={title} tabIndex={-1} ref={panel} onClick={(e) => e.stopPropagation()}>
        <header className="drawer-header">
          <h2>{title}</h2>
          <button type="button" className="ghost icon-only" aria-label="Fechar" onClick={onClose}><X size={18} aria-hidden="true" /></button>
        </header>
        <div className="drawer-body">{children}</div>
      </div>
    </div>
  );
}

/** CNPJ com máscara (00.000.000/0000-00); devolve só os 14 caracteres (aceita o CNPJ alfanumérico de 2026). */
export function CnpjInput({ value, onChange, ...rest }: { value: string; onChange: (raw: string) => void } & Omit<React.InputHTMLAttributes<HTMLInputElement>, 'value' | 'onChange'>) {
  const raw = onlyCnpjChars(value).slice(0, 14);
  return (
    <input
      {...rest}
      inputMode="text"
      autoComplete="off"
      placeholder={rest.placeholder ?? '00.000.000/0000-00'}
      value={raw.length === 14 ? formatCnpj(raw) : raw}
      onChange={(e) => onChange(onlyCnpjChars(e.target.value).slice(0, 14))}
      aria-invalid={raw.length > 0 && raw.length < 14 ? true : undefined}
    />
  );
}

export const UFS = ['AC', 'AL', 'AM', 'AP', 'BA', 'CE', 'DF', 'ES', 'GO', 'MA', 'MG', 'MS', 'MT', 'PA', 'PB', 'PE', 'PI', 'PR', 'RJ', 'RN', 'RO', 'RR', 'RS', 'SC', 'SE', 'SP', 'TO'];

export function UfSelect({ value, onChange, includeExterior, ...rest }: { value: string; onChange: (uf: string) => void; includeExterior?: boolean } & Omit<React.SelectHTMLAttributes<HTMLSelectElement>, 'value' | 'onChange'>) {
  return (
    <select {...rest} value={value} onChange={(e) => onChange(e.target.value)}>
      {UFS.map((u) => <option key={u} value={u}>{u}</option>)}
      {includeExterior && <option value="EX">EX (exterior)</option>}
    </select>
  );
}
