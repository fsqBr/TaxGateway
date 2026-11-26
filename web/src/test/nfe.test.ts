import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { accessKeyDv, rekeyExample } from '../lib/nfeExemplo';

const sample = readFileSync(resolve(__dirname, '../../public/exemplos/nfe-exemplo.xml'), 'utf-8');

describe('XML de exemplo (UX B1, D-J06)', () => {
  it('calcula o DV da chave como a SEFAZ (módulo 11)', () => {
    // Chave do exemplo original: 1127061122233300018155001000005000139595000 + DV 3.
    expect(accessKeyDv('1127061122233300018155001000005000139595000')).toBe(3);
  });

  it('troca o emitente, gera chave nova com DV válido e mantém o XML sem assinatura', () => {
    const { xml, chave } = rekeyExample(sample, '44555666000199', 123456789);
    expect(chave).toHaveLength(44);
    expect(chave.slice(6, 20)).toBe('44555666000199');
    expect(chave.slice(25, 34)).toBe('123456789');
    expect(Number(chave[43])).toBe(accessKeyDv(chave.slice(0, 43)));
    expect(xml).toContain(`Id="NFe${chave}"`);
    expect(xml).toContain(`<cDV>${chave[43]}</cDV>`);
    expect(xml).toContain('<emit><CNPJ>44555666000199</CNPJ>');
    expect(xml).not.toContain('Signature');
  });
});
