/** Escopos de tenant (iguais a Scopes.TenantDefault na API). */
export const ALL_SCOPES: { id: string; label: string }[] = [
  { id: 'simulacao:write', label: 'Simular' },
  { id: 'auditoria:write', label: 'Auditar XML e lotes' },
  { id: 'usage:read', label: 'Ler consumo' },
  { id: 'keys:manage', label: 'Gerenciar API keys' },
  { id: 'webhooks:manage', label: 'Gerenciar webhooks' },
  { id: 'cnpjs:manage', label: 'Gerenciar CNPJs (OEM)' },
  { id: 'dfe:manage', label: 'Certificado A1 e DF-e' },
  { id: 'titulares:manage', label: 'LGPD: titulares' },
];
