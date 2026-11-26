import { useId } from 'react';

/** Marca TaxGateway: portal (arco) com check — "passagem verificada". Ver docs/marca.md. */
export function LogoMark({ size = 32, title = 'TaxGateway' }: { size?: number; title?: string }) {
  const id = useId().replace(/:/g, '');
  return (
    <svg width={size} height={size} viewBox="0 0 48 48" role="img" aria-label={title} className="logo-mark">
      <defs>
        <linearGradient id={`g${id}`} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor="#14A37A" />
          <stop offset="1" stopColor="#0A4F3E" />
        </linearGradient>
      </defs>
      <rect width="48" height="48" rx="12" fill={`url(#g${id})`} />
      <path d="M14.5 36V21.5a9.5 9.5 0 0 1 19 0V36" fill="none" stroke="#fff" strokeWidth="3.6" strokeLinecap="round" />
      <path d="M19 27.2l3.6 3.6 6.6-7.2" fill="none" stroke="#7CF0C1" strokeWidth="3.4" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

export function Logo({ size = 30, suffix }: { size?: number; suffix?: string }) {
  return (
    <span className="logo" aria-label={suffix ? `TaxGateway ${suffix}` : 'TaxGateway'}>
      <LogoMark size={size} title="" />
      <span className="logo-word" aria-hidden="true">
        <span className="logo-tax">Tax</span>
        <span className="logo-gw">Gateway</span>
      </span>
      {suffix && <span className="logo-suffix" aria-hidden="true">{suffix}</span>}
    </span>
  );
}
