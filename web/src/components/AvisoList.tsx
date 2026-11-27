import { Alert } from './ui';

export interface Aviso { codigo: string; mensagem: string }

/** Avisos em linguagem de negócio: a mensagem na frente, o código técnico discreto (para o suporte e o dev). */
export function AvisoList({ avisos, kind = 'warning' }: { avisos: Aviso[]; kind?: 'warning' | 'info' }) {
  if (avisos.length === 0) return null;
  return (
    <Alert kind={kind}>
      <ul className="aviso-list">
        {avisos.map((a, i) => (
          <li key={`${a.codigo}-${i}`}>{a.mensagem} <span className="muted small mono" title="Código para o suporte e a integração">({a.codigo})</span></li>
        ))}
      </ul>
    </Alert>
  );
}

export const REGIME_LABEL: Record<string, string> = {
  REGULAR: 'Regime regular',
  SN_PLENO: 'Simples Nacional',
  SN_SUBLIMITE: 'Simples Nacional acima do sublimite',
  SOMENTE_RECEITA: 'Simples Nacional (só receita)',
};
