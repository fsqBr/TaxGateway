const brl = new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' });
const num = new Intl.NumberFormat('pt-BR');
const dt = new Intl.DateTimeFormat('pt-BR', { dateStyle: 'short', timeStyle: 'short', timeZone: 'America/Sao_Paulo' });

export const money = (v: number | null | undefined) => (v === null || v === undefined ? '—' : brl.format(v));
export const integer = (v: number | null | undefined) => (v === null || v === undefined ? '—' : num.format(v));
export const dateTime = (v: string | null | undefined) => (v ? dt.format(new Date(v)) : '—');

/** 11222333000181 → 11.222.333/0001-81 (aceita CNPJ alfanumérico). */
export function cnpj(v: string | null | undefined): string {
  if (!v) return '—';
  const s = v.replace(/[^0-9A-Za-z]/g, '').toUpperCase();
  return s.length === 14 ? `${s.slice(0, 2)}.${s.slice(2, 5)}.${s.slice(5, 8)}/${s.slice(8, 12)}-${s.slice(12)}` : v;
}

export const onlyCnpjChars = (v: string) => v.replace(/[^0-9A-Za-z]/g, '').toUpperCase();

/** Competência corrente AAAA-MM no fuso de Brasília. */
export function currentMonth(now = new Date()): string {
  const parts = new Intl.DateTimeFormat('en-CA', { timeZone: 'America/Sao_Paulo', year: 'numeric', month: '2-digit' }).formatToParts(now);
  return `${parts.find((p) => p.type === 'year')!.value}-${parts.find((p) => p.type === 'month')!.value}`;
}

export async function fileToBase64(file: Blob): Promise<string> {
  const buffer = typeof file.arrayBuffer === 'function' ? await file.arrayBuffer() : await new Promise<ArrayBuffer>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as ArrayBuffer);
    reader.onerror = () => reject(reader.error ?? new Error('Falha ao ler o arquivo'));
    reader.readAsArrayBuffer(file);
  });
  const bytes = new Uint8Array(buffer);
  let bin = '';
  for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(bin);
}
