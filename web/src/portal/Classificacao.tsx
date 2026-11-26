import { useState, type FormEvent } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Alert, Badge, Card, Field, PageHeader, Table, useAction } from '../components/ui';
import { portal } from '../lib/api';

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
                  <td data-label="Redução" className="num">{s.reducao_cbs_percentual}% / {s.reducao_ibs_uf_percentual}%</td>
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
