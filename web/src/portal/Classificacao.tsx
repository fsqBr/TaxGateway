import { useState, type FormEvent } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Alert, Badge, Card, Field, PageHeader, Table, useAction } from '../components/ui';
import { ApiError, portal } from '../lib/api';

interface Resultado {
  consulta: { tipo: 'NCM' | 'NBS'; codigo: string; data: string; modelo: number };
  item: { descricao: string; imposto_seletivo: boolean; is_aliquota_ad_valorem: boolean; is_aliquota_ad_rem: boolean };
  sugestoes: {
    c_class_trib: string; cst: string; descricao: string; tratamento: string; tipo_aliquota: string;
    reducao_cbs_percentual: number; reducao_ibs_uf_percentual: number; reducao_ibs_mun_percentual: number; credito_adquirente: boolean; motivo: string;
  }[];
  aviso: string;
}

/**
 * Consulta de CST/cClassTrib por NCM ou NBS (D-J02; revisão de produto L1): a maior dor de quem integra é escolher a
 * classificação. A tela mostra o que o motor oficial sabe do código (descrição, Imposto Seletivo) e as classificações
 * compatíveis — como atalho de pesquisa, nunca como parecer.
 */
export function Classificacao() {
  const [params] = useSearchParams();
  const [tipo, setTipo] = useState<'ncm' | 'nbs'>(params.get('nbs') ? 'nbs' : 'ncm');
  const [codigo, setCodigo] = useState(params.get('nbs') ?? params.get('ncm') ?? '');
  const [texto, setTexto] = useState('');
  const [data, setData] = useState('2027-01-01');
  const [modelo, setModelo] = useState('55');
  const [r, setR] = useState<Resultado>();
  const { busy, error, run } = useAction();
  const digits = codigo.replace(/\D/g, '');
  const ok = tipo === 'ncm' ? digits.length === 8 : digits.length === 9;

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setR(await run(() => portal<Resultado>('/v1/classificacao', { query: { [tipo]: digits, q: texto || undefined, data, modelo } })));
  };

  return (
    <>
      <PageHeader title="Classificação tributária" description="Descubra o CST e o cClassTrib candidatos para um NCM (mercadoria) ou NBS (serviço), com os dados abertos do motor oficial da RFB na data da operação." />
      <Card>
        <form className="filters" onSubmit={submit}>
          <Field label="Tipo">
            <select value={tipo} onChange={(e) => setTipo(e.target.value as 'ncm' | 'nbs')}>
              <option value="ncm">NCM (mercadoria)</option>
              <option value="nbs">NBS (serviço)</option>
            </select>
          </Field>
          <Field label={tipo === 'ncm' ? 'NCM (8 dígitos)' : 'NBS (9 dígitos)'}>
            <input value={codigo} onChange={(e) => setCodigo(e.target.value)} inputMode="numeric" placeholder={tipo === 'ncm' ? '2202.10.00' : '1.0101.11.00'} aria-invalid={codigo.length > 0 && !ok ? true : undefined} />
          </Field>
          <Field label="Palavras do produto (opcional)" hint="Filtra as classificações, ex.: hortícola, medicamento.">
            <input value={texto} onChange={(e) => setTexto(e.target.value)} maxLength={100} />
          </Field>
          <Field label="Data da operação"><input type="date" min="2026-01-01" value={data} onChange={(e) => setData(e.target.value)} /></Field>
          <Field label="Documento">
            <select value={modelo} onChange={(e) => setModelo(e.target.value)}>
              <option value="55">NF-e (55)</option>
              <option value="65">NFC-e (65)</option>
            </select>
          </Field>
          <div className="filters-actions"><button type="submit" disabled={busy || !ok}>{busy ? 'Consultando…' : 'Consultar'}</button></div>
        </form>
        <Alert>{error}</Alert>
      </Card>
      {r && (
        <>
          <div style={{ height: 16 }} />
          <Card title={`${r.consulta.tipo} ${r.consulta.codigo}`} description={r.item.descricao}
            actions={r.item.imposto_seletivo ? <Badge tone="warn">Sujeito ao Imposto Seletivo</Badge> : <Badge>Fora do Imposto Seletivo</Badge>}>
            <Table head={['CST', 'cClassTrib', 'Situação', 'Redução CBS / IBS', 'Crédito do adquirente', 'Por quê']} empty={r.sugestoes.length === 0}
              emptyText="Nenhuma classificação compatível com o filtro. Tente sem as palavras.">
              {r.sugestoes.map((s) => (
                <tr key={s.c_class_trib}>
                  <td data-label="CST" className="mono">{s.cst}</td>
                  <td data-label="cClassTrib" className="mono">{s.c_class_trib}</td>
                  <td data-label="Situação">{s.descricao}<span className="sub">{s.tratamento} · {s.tipo_aliquota}</span></td>
                  <td data-label="Redução" className="num">{s.reducao_cbs_percentual.toLocaleString('pt-BR')}% / {s.reducao_ibs_uf_percentual.toLocaleString('pt-BR')}%</td>
                  <td data-label="Crédito">{s.credito_adquirente ? 'Sim' : 'Não'}</td>
                  <td data-label="Por quê" className="small">{s.motivo}</td>
                </tr>
              ))}
            </Table>
            <Alert kind="info">{r.aviso}</Alert>
          </Card>
        </>
      )}
    </>
  );
}

/** Lê "codigo;descricao;ncm" (ou com vírgula) e devolve os NCMs distintos, na ordem em que aparecem. */
export function lerProdutosCsv(texto: string): { linhas: { codigo: string; descricao: string; ncm: string }[]; erros: string[] } {
  const linhas: { codigo: string; descricao: string; ncm: string }[] = [];
  const erros: string[] = [];
  texto.split(/\r?\n/).forEach((raw, i) => {
    const l = raw.trim();
    if (!l || (i === 0 && /ncm/i.test(l))) return;
    const sep = l.includes(';') ? ';' : ',';
    const [codigo = '', descricao = '', ncm = ''] = l.split(sep).map((x) => x.trim().replace(/^"|"$/g, ''));
    const digits = ncm.replace(/\D/g, '');
    if (digits.length !== 8) erros.push(`linha ${i + 1}: NCM "${ncm}" precisa ter 8 dígitos`);
    else linhas.push({ codigo, descricao, ncm: digits });
  });
  return { linhas, erros };
}

const esperar = (ms: number) => new Promise((r) => setTimeout(r, ms));

/**
 * Classificação em massa (D-R26): o cadastro de produtos (código; descrição; NCM) vira uma planilha com o CST e o
 * cClassTrib sugeridos para cada um, a partir da mesma consulta da tela acima (até 300 NCMs distintos por arquivo).
 */
export function ClassificacaoEmMassa() {
  const [arquivo, setArquivo] = useState<File>();
  const [progresso, setProgresso] = useState<string>();
  const [erros, setErros] = useState<string[]>([]);
  const [csv, setCsv] = useState<string>();

  const processar = async () => {
    if (!arquivo) return;
    const { linhas, erros: errs } = lerProdutosCsv(await arquivo.text());
    const ncms = [...new Set(linhas.map((l) => l.ncm))];
    if (ncms.length > 300) {
      setErros([...errs, `O arquivo tem ${ncms.length} NCMs distintos; envie até 300 por vez.`]);
      return;
    }
    setErros(errs);
    const porNcm = new Map<string, Resultado['sugestoes'][number] | null>();
    for (const [i, ncm] of ncms.entries()) {
      setProgresso(`Consultando ${i + 1} de ${ncms.length} NCMs…`);
      for (let tentativa = 0; tentativa < 5; tentativa++) {
        try {
          const r = await portal<Resultado>('/v1/classificacao', { query: { ncm } });
          porNcm.set(ncm, r.sugestoes[0] ?? null);
          break;
        } catch (e) {
          if (e instanceof ApiError && e.status === 429) { await esperar(2000); continue; }
          porNcm.set(ncm, null);
          break;
        }
      }
    }
    const linhasCsv = ['codigo;descricao;ncm;cst_sugerido;c_class_trib_sugerido;tratamento;reducao_cbs_percentual'];
    for (const l of linhas) {
      const s = porNcm.get(l.ncm);
      linhasCsv.push([l.codigo, l.descricao, l.ncm, s?.cst ?? '', s?.c_class_trib ?? '', s?.tratamento ?? 'sem sugestão', s ? String(s.reducao_cbs_percentual).replace('.', ',') : ''].join(';'));
    }
    setCsv('﻿' + linhasCsv.join('\r\n'));
    setProgresso(`${linhas.length} produto(s) classificados.`);
  };

  return (
    <Card title="Classificar uma lista de produtos (CSV)" description="Envie um CSV com código; descrição; NCM (uma linha por produto). Devolvemos a planilha com o CST e o cClassTrib sugeridos — confira antes de usar no cadastro.">
      <div className="row" style={{ gap: 12, alignItems: 'flex-end', flexWrap: 'wrap' }}>
        <Field label="Arquivo CSV"><input type="file" accept=".csv,text/csv" onChange={(e) => { setArquivo(e.target.files?.[0]); setCsv(undefined); setProgresso(undefined); }} /></Field>
        <button type="button" disabled={!arquivo} onClick={() => void processar()}>Classificar</button>
        {csv && <a className="button secondary" download="produtos-classificados.csv" href={`data:text/csv;charset=utf-8,${encodeURIComponent(csv)}`}>Baixar planilha</a>}
      </div>
      {progresso && <p className="muted small" aria-live="polite">{progresso}</p>}
      {erros.length > 0 && <Alert kind="warning">{erros.slice(0, 10).map((e) => <div key={e}>{e}</div>)}</Alert>}
    </Card>
  );
}
