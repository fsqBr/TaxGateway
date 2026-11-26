// XML de exemplo para a primeira auditoria (UX B1): uma NF-e de homologação, sem assinatura, com o CNPJ do próprio
// cliente como emitente e chave de acesso nova (número aleatório + DV recalculado). Vai com assinatura=opcional, então
// o resultado vem com o aviso ASSINATURA_AUSENTE — e conta na franquia como qualquer auditoria.

/** DV da chave de acesso: módulo 11, pesos 2..9 da direita para a esquerda, valor do caractere = ASCII − 48 (NT 2026.004). */
export function accessKeyDv(first43: string): number {
  let sum = 0;
  let weight = 2;
  for (let i = first43.length - 1; i >= 0; i--) {
    sum += (first43.charCodeAt(i) - 48) * weight;
    weight = weight === 9 ? 2 : weight + 1;
  }
  const rest = sum % 11;
  return rest < 2 ? 0 : 11 - rest;
}

/** Troca o emitente pelo CNPJ informado e gera chave nova; devolve o XML e a chave. */
export function rekeyExample(xml: string, cnpj: string, numero: number): { xml: string; chave: string } {
  const id = /Id="NFe(\d{6})([0-9A-Z]{14})(\d{2})(\d{3})(\d{9})(\d)(\d{8})\d"/.exec(xml);
  if (!id) throw new Error('XML de exemplo fora do formato esperado.');
  const nNF = String(numero).padStart(9, '0');
  const base = `${id[1]}${cnpj}${id[3]}${id[4]}${nNF}${id[6]}${id[7]}`;
  const dv = accessKeyDv(base);
  const chave = `${base}${dv}`;
  const out = xml
    .replace(/Id="NFe[0-9A-Z]{44}"/, `Id="NFe${chave}"`)
    .replace(/<cDV>\d<\/cDV>/, `<cDV>${dv}</cDV>`)
    .replace(/<nNF>\d+<\/nNF>/, `<nNF>${numero}</nNF>`)
    .replace(/(<emit>\s*<CNPJ>)[0-9A-Z]{14}(<\/CNPJ>)/, `$1${cnpj}$2`);
  return { xml: out, chave };
}

export async function buildExampleXml(cnpj: string): Promise<File> {
  const resp = await fetch('/exemplos/nfe-exemplo.xml');
  if (!resp.ok) throw new Error('Não foi possível carregar o XML de exemplo.');
  const numero = 100_000_000 + Math.floor(Math.random() * 800_000_000);
  const { xml, chave } = rekeyExample(await resp.text(), cnpj, numero);
  return new File([xml], `exemplo-${chave}.xml`, { type: 'application/xml' });
}
