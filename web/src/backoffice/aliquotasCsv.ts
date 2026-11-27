// Linhas de alíquota da proposta (formulário e importação CSV). Sem React: dá para testar isolado.

export const TRIBUTOS = ['CBS', 'IBS_UF', 'IBS_MUN', 'IS'] as const;
export const TRIBUTO_LABEL: Record<string, string> = { CBS: 'CBS', IBS_UF: 'IBS estadual', IBS_MUN: 'IBS municipal', IS: 'Imposto Seletivo' };
export const FONTES = ['LEI_FEDERAL', 'SENADO', 'TCU', 'CG_IBS', 'LEI_ENTE', 'BUNDLE_RFB'] as const;
export const FONTE_LABEL: Record<string, string> = {
  LEI_FEDERAL: 'Lei federal', SENADO: 'Resolução do Senado', TCU: 'Cálculo do TCU', CG_IBS: 'Comitê Gestor do IBS', LEI_ENTE: 'Lei do estado/município', BUNDLE_RFB: 'Pacote da Receita (RFB)',
};
export const FORMAS = ['SUBSTITUICAO', 'ACRESCIMO', 'DECRESCIMO'] as const;
export const FORMA_LABEL: Record<string, string> = { SUBSTITUICAO: 'Substitui a de referência', ACRESCIMO: 'Soma à de referência', DECRESCIMO: 'Reduz a de referência' };

/** Códigos IBGE das UFs (2 dígitos). */
export const UF_IBGE: Record<string, string> = {
  RO: '11', AC: '12', AM: '13', RR: '14', PA: '15', AP: '16', TO: '17', MA: '21', PI: '22', CE: '23', RN: '24', PB: '25', PE: '26', AL: '27',
  SE: '28', BA: '29', MG: '31', ES: '32', RJ: '33', SP: '35', PR: '41', SC: '42', RS: '43', MS: '50', MT: '51', GO: '52', DF: '53',
};
export const UF_POR_CODIGO: Record<string, string> = Object.fromEntries(Object.entries(UF_IBGE).map(([uf, c]) => [c, uf]));

export const MAX_LINHAS = 6000;

/** Linha como o operador edita (tudo texto, exceto provisória). */
export interface Linha {
  tributo: string; tipo: string; esfera: string; ente_codigo: string; forma_aplicacao: string; produto: string;
  aliquota: string; valid_from: string; valid_to: string; ato_legal: string; fonte: string; provisoria: boolean;
}

export const linhaVazia = (): Linha => ({
  tributo: 'CBS', tipo: 'REFERENCIA', esfera: 'UNIAO', ente_codigo: '', forma_aplicacao: '', produto: '',
  aliquota: '', valid_from: '', valid_to: '', ato_legal: '', fonte: 'SENADO', provisoria: false,
});

export const esferaDo = (tributo: string) => (tributo === 'IBS_UF' ? 'UF' : tributo === 'IBS_MUN' ? 'MUNICIPIO' : 'UNIAO');
/** CBS e IS só têm alíquota de referência da União; IBS pode ter a própria do ente. */
export const aceitaPropria = (tributo: string) => tributo === 'IBS_UF' || tributo === 'IBS_MUN';

/** Ajusta os campos dependentes quando o tributo ou o tipo mudam (o formulário só mostra o que vale). */
export function coerente(l: Linha): Linha {
  const tipo = aceitaPropria(l.tributo) ? l.tipo : 'REFERENCIA';
  const propria = tipo === 'PROPRIA_ENTE';
  return {
    ...l,
    tipo,
    esfera: esferaDo(l.tributo),
    ente_codigo: propria ? l.ente_codigo : '',
    forma_aplicacao: propria ? l.forma_aplicacao || 'SUBSTITUICAO' : '',
    produto: l.tributo === 'IS' ? l.produto : '',
  };
}

/** Corpo da linha para a API. */
export function paraApi(l: Linha) {
  return {
    tributo: l.tributo, tipo: l.tipo, esfera: l.esfera,
    ente_codigo: l.ente_codigo.trim() || null,
    forma_aplicacao: l.forma_aplicacao || null,
    produto: l.produto.trim() || null,
    aliquota: Number(l.aliquota.replace(',', '.')),
    valid_from: l.valid_from, valid_to: l.valid_to || null,
    ato_legal: l.ato_legal.trim(), fonte: l.fonte, provisoria: l.provisoria,
  };
}

/** Problemas óbvios antes de chamar a API (a validação completa é do servidor, na conferência). */
export function problemas(l: Linha): string[] {
  const p: string[] = [];
  const a = Number(l.aliquota.replace(',', '.'));
  if (l.aliquota.trim() === '' || Number.isNaN(a) || a < 0 || a > 100) p.push('alíquota deve ser um número de 0 a 100');
  if (!/^\d{4}-\d{2}-\d{2}$/.test(l.valid_from)) p.push('informe "vigente de" (data)');
  if (l.valid_to && !/^\d{4}-\d{2}-\d{2}$/.test(l.valid_to)) p.push('"vigente até" inválido');
  if (l.valid_to && l.valid_to <= l.valid_from) p.push('"vigente até" precisa ser depois de "vigente de"');
  if (!l.ato_legal.trim()) p.push('informe o ato legal');
  if (l.tipo === 'PROPRIA_ENTE') {
    if (l.esfera === 'UF' && !/^\d{2}$/.test(l.ente_codigo)) p.push('escolha a UF');
    if (l.esfera === 'MUNICIPIO' && !/^\d{7}$/.test(l.ente_codigo)) p.push('escolha o município');
    if (!l.forma_aplicacao) p.push('escolha a forma de aplicação');
  }
  if (l.tributo === 'IS' && !/^(NCM|CCLASS):\S+$/.test(l.produto.trim())) p.push('informe o produto (NCM:2203 ou CCLASS:000001)');
  return p;
}

export const CSV_CABECALHO = 'tributo;tipo;esfera;ente_codigo;forma_aplicacao;produto;aliquota;valid_from;valid_to;ato_legal;fonte;provisoria';

/** Divide uma linha de CSV respeitando aspas ("17,5" com separador vírgula). */
function dividir(linha: string, sep: string): string[] {
  const out: string[] = [];
  let atual = '';
  let aspas = false;
  for (let i = 0; i < linha.length; i++) {
    const c = linha[i];
    if (aspas) {
      if (c === '"' && linha[i + 1] === '"') { atual += '"'; i++; }
      else if (c === '"') aspas = false;
      else atual += c;
    } else if (c === '"') aspas = true;
    else if (c === sep) { out.push(atual.trim()); atual = ''; }
    else atual += c;
  }
  out.push(atual.trim());
  return out;
}

/** Aceita AAAA-MM-DD ou DD/MM/AAAA. */
function data(v: string): string {
  const br = /^(\d{2})\/(\d{2})\/(\d{4})$/.exec(v);
  return br ? `${br[3]}-${br[2]}-${br[1]}` : v;
}

const SIM = new Set(['true', 'sim', 's', '1', 'x', 'yes', 'verdadeiro']);

/**
 * Lê o CSV de alíquotas. Separador ";" ou "," (detectado no cabeçalho), vírgula decimal e datas DD/MM/AAAA aceitas.
 * Tipo e esfera em branco são deduzidos do tributo; forma em branco numa alíquota própria vira SUBSTITUICAO.
 */
export function lerCsv(texto: string): { linhas: Linha[]; erros: string[] } {
  const brutas = texto.replace(/^﻿/, '').split(/\r?\n/);
  const cab = brutas.findIndex((l) => l.trim() !== '');
  if (cab < 0) return { linhas: [], erros: ['Arquivo vazio.'] };
  const sep = brutas[cab].includes(';') ? ';' : ',';
  const nomes = dividir(brutas[cab], sep).map((n) => n.toLowerCase());
  const faltando = ['tributo', 'aliquota', 'valid_from', 'ato_legal', 'fonte'].filter((n) => !nomes.includes(n));
  if (faltando.length) return { linhas: [], erros: [`Cabeçalho sem a(s) coluna(s): ${faltando.join(', ')}. Use: ${CSV_CABECALHO}`] };

  const linhas: Linha[] = [];
  const erros: string[] = [];
  for (let i = cab + 1; i < brutas.length; i++) {
    if (brutas[i].trim() === '') continue;
    const v = dividir(brutas[i], sep);
    const col = (n: string) => (nomes.indexOf(n) >= 0 ? v[nomes.indexOf(n)] ?? '' : '');
    const tributo = col('tributo').toUpperCase();
    if (!(TRIBUTOS as readonly string[]).includes(tributo)) { erros.push(`Linha ${i + 1}: tributo "${col('tributo')}" desconhecido (use CBS, IBS_UF, IBS_MUN ou IS).`); continue; }
    const tipo = col('tipo').toUpperCase() || 'REFERENCIA';
    const forma = col('forma_aplicacao').toUpperCase();
    const l: Linha = {
      tributo, tipo, esfera: col('esfera').toUpperCase() || esferaDo(tributo),
      ente_codigo: col('ente_codigo'), forma_aplicacao: forma || (tipo === 'PROPRIA_ENTE' ? 'SUBSTITUICAO' : ''),
      produto: col('produto'), aliquota: col('aliquota').replace(',', '.'),
      valid_from: data(col('valid_from')), valid_to: data(col('valid_to')), ato_legal: col('ato_legal'),
      fonte: col('fonte').toUpperCase(), provisoria: SIM.has(col('provisoria').toLowerCase()),
    };
    const p = problemas(l);
    if (p.length) erros.push(`Linha ${i + 1}: ${p.join('; ')}.`);
    else linhas.push(l);
  }
  if (linhas.length > MAX_LINHAS) return { linhas: [], erros: [`O arquivo tem ${linhas.length} linhas; o máximo por proposta é ${MAX_LINHAS}. Divida em mais de uma proposta.`] };
  return { linhas, erros };
}

/** Nome legível do ente: "SP (35)" para UF; o código IBGE para município; "Referência nacional" sem ente. */
export function nomeEnte(_esfera: string, codigo: string | null | undefined): string {
  if (!codigo) return 'Referência nacional';
  if (codigo.length === 2) return `${UF_POR_CODIGO[codigo] ?? '?'} (${codigo})`;
  return codigo;
}

export const pct = (v: number) => `${v.toLocaleString('pt-BR', { minimumFractionDigits: 2, maximumFractionDigits: 4 })}%`;

/** "2027-01-01" → "01/01/2027" sem passar por fuso horário. */
export function dataBr(v: string | null | undefined): string {
  if (!v) return '—';
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(v);
  return m ? `${m[3]}/${m[2]}/${m[1]}` : v;
}

/** Lê o texto de um arquivo (FileReader funciona também no jsdom dos testes). */
export function lerArquivo(f: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve(String(r.result ?? ''));
    r.onerror = () => reject(r.error ?? new Error('Não foi possível ler o arquivo.'));
    r.readAsText(f, 'utf-8');
  });
}
