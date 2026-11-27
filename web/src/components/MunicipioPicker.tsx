import { useEffect, useId, useRef, useState } from 'react';
import { portal } from '../lib/api';

export interface Municipio { codigo: number; nome: string; uf: string }

/**
 * Município pelo NOME (D-R16): o cliente digita "sao paulo" e escolhe; o código IBGE vai para a API por baixo.
 * Usa a busca pública /v1/publico/municipios (cadastro oficial do IBGE, sem acento e sem diferenciar maiúsculas).
 */
export function MunicipioPicker({ label = 'Município', value, onChange, hint }: {
  label?: string;
  value?: Municipio;
  onChange: (m?: Municipio) => void;
  hint?: string;
}) {
  const id = useId();
  const [termo, setTermo] = useState(value ? `${value.nome} / ${value.uf}` : '');
  const [opcoes, setOpcoes] = useState<Municipio[]>([]);
  const [aberto, setAberto] = useState(false);
  const [ativo, setAtivo] = useState(0);
  const timer = useRef<number | undefined>(undefined);

  useEffect(() => {
    if (value) setTermo(`${value.nome} / ${value.uf}`);
  }, [value]);

  const buscar = (t: string) => {
    setTermo(t);
    onChange(undefined);
    window.clearTimeout(timer.current);
    if (t.trim().length < 2) {
      setOpcoes([]);
      return;
    }

    timer.current = window.setTimeout(() => {
      portal<Municipio[]>('/v1/publico/municipios', { query: { busca: t.trim() }, silent: true })
        .then((r) => { setOpcoes(r); setAtivo(0); setAberto(true); })
        .catch(() => setOpcoes([]));
    }, 250);
  };

  const escolher = (m: Municipio) => {
    onChange(m);
    setTermo(`${m.nome} / ${m.uf}`);
    setAberto(false);
  };

  return (
    <div className="field municipio-picker">
      <label className="field-label" htmlFor={id}>{label}</label>
      <input
        id={id}
        role="combobox"
        aria-expanded={aberto && opcoes.length > 0}
        aria-controls={`${id}-lista`}
        aria-autocomplete="list"
        autoComplete="off"
        placeholder="Digite o nome da cidade"
        value={termo}
        onChange={(e) => buscar(e.target.value)}
        onFocus={() => opcoes.length > 0 && setAberto(true)}
        onBlur={() => window.setTimeout(() => setAberto(false), 150)}
        onKeyDown={(e) => {
          if (!aberto || opcoes.length === 0) return;
          if (e.key === 'ArrowDown') { e.preventDefault(); setAtivo((a) => Math.min(a + 1, opcoes.length - 1)); }
          if (e.key === 'ArrowUp') { e.preventDefault(); setAtivo((a) => Math.max(a - 1, 0)); }
          if (e.key === 'Enter') { e.preventDefault(); escolher(opcoes[ativo]); }
          if (e.key === 'Escape') setAberto(false);
        }}
      />
      {aberto && opcoes.length > 0 && (
        <ul className="picker-list" role="listbox" id={`${id}-lista`}>
          {opcoes.map((m, i) => (
            <li key={m.codigo} role="option" aria-selected={i === ativo} className={i === ativo ? 'active' : undefined}
              onMouseDown={(e) => { e.preventDefault(); escolher(m); }}>
              {m.nome} <span className="muted small">/ {m.uf} · {m.codigo}</span>
            </li>
          ))}
        </ul>
      )}
      <span className="field-hint">{hint ?? (value ? `Código IBGE ${value.codigo}` : 'Escolha na lista; o código IBGE é preenchido sozinho.')}</span>
    </div>
  );
}
