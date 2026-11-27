import { useEffect, useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { AtualizacoesFiscais } from '../components/AtualizacoesFiscais';
import { MunicipioPicker, type Municipio } from '../components/MunicipioPicker';
import { Alert, Badge, Field, Loading, Table, useAction, useLoad } from '../components/ui';
import { portal } from '../lib/api';
import { SiteFooter, SiteHeader } from './SiteChrome';

// Página pública de alíquotas (D-R21): CBS e IBS vigentes por município e data, com origem, ato legal e vigência, e a
// linha do tempo da transição 2026–2033. Anônima: usa /v1/publico/*, a mesma resolução da simulação e da auditoria.

type TributoCodigo = 'CBS' | 'IBS_UF' | 'IBS_MUN';

export interface AliquotaTributo {
  tributo: TributoCodigo;
  nome: string;
  aliquota: number | null;
  origem?: 'REFERENCIA' | 'PROPRIA_DO_ENTE';
  forma_aplicacao?: 'SUBSTITUICAO' | 'ACRESCIMO' | 'DECRESCIMO' | null;
  referencia?: number;
  ato_legal?: string | null;
  fonte?: string | null;
  provisoria?: boolean;
  vigencia_inicio?: string;
  vigencia_fim?: string | null;
  catalogo_id?: number | string;
  mensagem?: string;
}

export interface ConsultaAliquotas {
  municipio: Municipio;
  data: string;
  tributos: AliquotaTributo[];
  total: number | null;
  observacao?: string;
  parametros_snapshot?: number | string;
}

interface LinhaTabela {
  tributo: string;
  aliquota: number;
  produto?: string | null;
  vigencia_inicio: string;
  vigencia_fim?: string | null;
  ato_legal?: string | null;
  fonte?: string | null;
  provisoria?: boolean;
}

interface TabelaAliquotas {
  referencias: LinhaTabela[];
  imposto_seletivo: LinhaTabela[];
  entes_com_aliquota_propria: { ufs: number; municipios: number };
}

const NOME_TRIBUTO: Record<string, string> = { CBS: 'CBS', IBS_UF: 'IBS estadual', IBS_MUN: 'IBS municipal', IS: 'Imposto Seletivo' };
const ANOS = [2026, 2027, 2028, 2029, 2030, 2031, 2032, 2033];

const pct = (v: number) => `${new Intl.NumberFormat('pt-BR', { minimumFractionDigits: 2, maximumFractionDigits: 4 }).format(v)}%`;

/** AAAA-MM-DD → DD/MM/AAAA, sem passar por Date (evita deslocamento de fuso). */
const dataBr = (iso: string | null | undefined) => {
  if (!iso) return '';
  const [y, m, d] = iso.slice(0, 10).split('-');
  return `${d}/${m}/${y}`;
};

const vigencia = (inicio?: string, fim?: string | null) =>
  inicio ? (fim ? `${dataBr(inicio)} a ${dataBr(fim)}` : `desde ${dataBr(inicio)}, sem data de fim`) : '—';

/** Hoje no fuso de Brasília, AAAA-MM-DD. */
export function hojeBrasilia(now = new Date()): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'America/Sao_Paulo', year: 'numeric', month: '2-digit', day: '2-digit' }).format(now);
}

function origemTexto(t: AliquotaTributo): string {
  if (t.origem === 'REFERENCIA') return 'Referência nacional';
  if (t.origem !== 'PROPRIA_DO_ENTE') return '—';
  const ente = t.tributo === 'IBS_MUN' ? 'do município' : t.tributo === 'IBS_UF' ? 'do estado' : 'do ente';
  const ref = t.referencia !== undefined ? pct(t.referencia) : 'a referência';
  switch (t.forma_aplicacao) {
    case 'SUBSTITUICAO': return `Própria ${ente}: substitui a referência nacional (${ref})`;
    case 'ACRESCIMO': return `Própria ${ente}: soma-se à referência nacional (${ref})`;
    case 'DECRESCIMO': return `Própria ${ente}: reduz a referência nacional (${ref})`;
    default: return `Própria ${ente}`;
  }
}

function AtoLegal({ ato, fonte }: { ato?: string | null; fonte?: string | null }) {
  if (!ato && !fonte) return <>—</>;
  const link = fonte && /^https:\/\//.test(fonte) ? fonte : undefined;
  return (
    <>
      {ato ?? ''}
      {fonte && (link
        ? <> · <a href={link} target="_blank" rel="noreferrer noopener">fonte</a></>
        : <span className="muted small">{ato ? ' · ' : ''}{fonte}</span>)}
    </>
  );
}

function Resultado({ r }: { r: ConsultaAliquotas }) {
  const provisoria = r.tributos.some((t) => t.provisoria);
  return (
    <section className="card" aria-label="Resultado da consulta">
      <h2 style={{ fontSize: '1.15rem' }}>{r.municipio.nome} / {r.municipio.uf} em {dataBr(r.data)}</h2>
      <p className="muted small" style={{ marginBottom: 12 }}>Código IBGE {r.municipio.codigo}</p>
      <Table head={['Tributo', 'Alíquota', 'Origem', 'Ato legal', 'Vigência']}>
        {r.tributos.map((t) => (
          <tr key={t.tributo}>
            <td><strong>{t.nome || NOME_TRIBUTO[t.tributo]}</strong></td>
            <td className="num">
              {t.aliquota === null ? <span className="muted">ainda não publicada</span> : pct(t.aliquota)}
              {t.provisoria && <> <Badge tone="warn">provisória</Badge></>}
            </td>
            <td>{t.aliquota === null ? (t.mensagem ?? '—') : origemTexto(t)}</td>
            <td><AtoLegal ato={t.ato_legal} fonte={t.fonte} /></td>
            <td>{t.aliquota === null ? '—' : vigencia(t.vigencia_inicio, t.vigencia_fim)}</td>
          </tr>
        ))}
        <tr>
          <td><strong>Total (CBS + IBS)</strong></td>
          <td className="num"><strong>{r.total === null ? '—' : pct(r.total)}</strong></td>
          <td colSpan={3} className="muted small">
            {r.total === null ? 'Sem total: falta alíquota publicada para esta data.' : 'Soma das alíquotas nominais, antes de reduções do produto ou serviço.'}
          </td>
        </tr>
      </Table>
      {provisoria && (
        <Alert kind="warning">
          Alíquota <strong>provisória</strong>: ainda pode mudar até a definição oficial. Se mudar com efeito retroativo, o TaxGateway
          reaudita sozinho, sem custo, as notas já auditadas.
        </Alert>
      )}
      {r.observacao && <Alert kind="info">{r.observacao}</Alert>}
      {r.parametros_snapshot !== undefined && (
        <p className="muted small" style={{ marginTop: 8 }}>Conjunto de parâmetros nº {r.parametros_snapshot} — o mesmo usado agora na simulação e na auditoria.</p>
      )}
    </section>
  );
}

/** Referências nacionais que valem em algum dia do ano. */
function noAno(linhas: LinhaTabela[], tributo: string, ano: number): LinhaTabela[] {
  const ini = `${ano}-01-01`;
  const fim = `${ano}-12-31`;
  return linhas.filter((l) => l.tributo === tributo && l.vigencia_inicio <= fim && (!l.vigencia_fim || l.vigencia_fim >= ini));
}

function LinhaDoTempo() {
  const { data, error, loading } = useLoad(() => portal<TabelaAliquotas>('/v1/publico/aliquotas/tabela', { silent: true }));
  if (loading) return <Loading />;
  if (error || !data) return <Alert kind="info">Não foi possível carregar a linha do tempo agora.</Alert>;
  const entes = data.entes_com_aliquota_propria;
  return (
    <>
      <Table head={['Ano', 'CBS', 'IBS estadual', 'IBS municipal']}>
        {ANOS.map((ano) => (
          <tr key={ano}>
            <td><strong>{ano}</strong>{ano === 2026 && <span className="muted small"> · ano de teste</span>}</td>
            {(['CBS', 'IBS_UF', 'IBS_MUN'] as const).map((t) => {
              const ls = noAno(data.referencias, t, ano);
              return (
                <td key={t}>
                  {ls.length === 0 ? <span className="muted">ainda não publicada</span> : ls.map((l) => (
                    <div key={`${l.vigencia_inicio}-${l.aliquota}`}>
                      {pct(l.aliquota)}{l.provisoria && <> <Badge tone="warn">provisória</Badge></>}
                    </div>
                  ))}
                </td>
              );
            })}
          </tr>
        ))}
      </Table>
      <p className="muted small" style={{ marginTop: 8 }}>
        Referência nacional por tributo. Estados e municípios podem fixar alíquota própria de IBS: hoje há{' '}
        <strong>{entes.ufs} {entes.ufs === 1 ? 'estado' : 'estados'}</strong> e <strong>{entes.municipios} {entes.municipios === 1 ? 'município' : 'municípios'}</strong> com
        alíquota própria cadastrada. Nos demais, vale a referência nacional.
      </p>

      <h3 style={{ marginTop: 20 }}>Atos legais das referências</h3>
      <Table head={['Tributo', 'Alíquota', 'Vigência', 'Ato legal']} empty={data.referencias.length === 0}>
        {data.referencias.map((l) => (
          <tr key={`${l.tributo}-${l.vigencia_inicio}`}>
            <td>{NOME_TRIBUTO[l.tributo] ?? l.tributo}</td>
            <td className="num">{pct(l.aliquota)}{l.provisoria && <> <Badge tone="warn">provisória</Badge></>}</td>
            <td>{vigencia(l.vigencia_inicio, l.vigencia_fim)}</td>
            <td><AtoLegal ato={l.ato_legal} fonte={l.fonte} /></td>
          </tr>
        ))}
      </Table>

      {data.imposto_seletivo.length > 0 && (
        <>
          <h3 style={{ marginTop: 20 }}>Imposto Seletivo por produto</h3>
          <Table head={['Produto', 'Alíquota', 'Vigência', 'Ato legal']}>
            {data.imposto_seletivo.map((l) => (
              <tr key={`${l.produto}-${l.vigencia_inicio}`}>
                <td>{l.produto}</td>
                <td className="num">{pct(l.aliquota)}{l.provisoria && <> <Badge tone="warn">provisória</Badge></>}</td>
                <td>{vigencia(l.vigencia_inicio, l.vigencia_fim)}</td>
                <td><AtoLegal ato={l.ato_legal} fonte={l.fonte} /></td>
              </tr>
            ))}
          </Table>
        </>
      )}
    </>
  );
}

export default function AliquotasPage() {
  useEffect(() => { document.title = 'Alíquotas por município · TaxGateway'; }, []);
  const [municipio, setMunicipio] = useState<Municipio>();
  const [data, setData] = useState(hojeBrasilia());
  const [resultado, setResultado] = useState<ConsultaAliquotas>();
  const { busy, error, run, setError } = useAction();

  const consultar = async (e: FormEvent) => {
    e.preventDefault();
    if (!municipio) { setError('Escolha o município na lista.'); return; }
    const r = await run(() => portal<ConsultaAliquotas>('/v1/publico/aliquotas', { query: { municipio: municipio.codigo, data }, silent: true }));
    if (r) setResultado(r);
  };

  return (
    <div className="site">
      <SiteHeader />
      <main id="conteudo" className="section">
        <div className="section-head">
          <div className="eyebrow">Alíquotas</div>
          <h1 style={{ fontSize: 'clamp(1.9rem, 3.6vw, 2.6rem)', letterSpacing: '-0.03em' }}>Alíquotas de CBS e IBS por município</h1>
          <p>
            Escolha o município e a data da operação: mostramos a alíquota de cada tributo, se ela é a referência nacional ou a
            própria do estado ou do município, o ato legal e a vigência. É a mesma tabela que o TaxGateway usa para calcular e auditar.
          </p>
        </div>

        <form className="card stack" onSubmit={consultar} aria-label="Consultar alíquotas">
          <div className="form-grid">
            <MunicipioPicker value={municipio} onChange={(m) => { setMunicipio(m); setResultado(undefined); }} />
            <Field label="Data da operação" hint="As alíquotas mudam ano a ano na transição.">
              <input type="date" min="2026-01-01" max="2100-12-31" value={data} onChange={(e) => setData(e.target.value)} required />
            </Field>
          </div>
          <Alert>{error}</Alert>
          <button type="submit" disabled={busy}>{busy ? 'Consultando…' : 'Consultar'}</button>
        </form>

        {resultado && <><div className="gap-lg" /><Resultado r={resultado} /></>}

        <div className="gap-lg" />
        <section className="card">
          <h2 style={{ fontSize: '1.15rem' }}>Linha do tempo 2026–2033</h2>
          <p className="muted small" style={{ marginBottom: 12 }}>
            2026 é o ano de teste (CBS 0,9% e IBS 0,1%, compensáveis com PIS/Cofins). A partir de 2027 a CBS substitui PIS e Cofins
            e o IBS cresce aos poucos até 2033. O que ainda não saiu no Diário Oficial aparece como “ainda não publicada”.
          </p>
          <LinhaDoTempo />
        </section>

        <div className="gap-lg" />
        <section className="card" id="atualizacoes">
          <h2 style={{ fontSize: '1.15rem' }}>Atualizações fiscais</h2>
          <AtualizacoesFiscais />
        </section>

        <div className="gap-lg" />
        <section className="card">
          <h2 style={{ fontSize: '1.15rem' }}>Para quem usa o TaxGateway</h2>
          <ul className="help-list">
            <li>A auditoria mostra a alíquota e o ato legal usados em cada tributo, confere o Imposto Seletivo pelo produto e avisa quando a alíquota é provisória.</li>
            <li>Simulação com alíquota informada por você sai marcada como <strong>não oficial</strong>.</li>
            <li>Liminar ou regime especial da sua empresa? Cadastre a exceção no portal, em <strong>Alíquotas e exceções</strong>, com o documento e a aprovação de outra pessoa: o cálculo ajustado aparece ao lado do oficial, sem substituí-lo.</li>
          </ul>
          <p className="muted small" style={{ marginTop: 8 }}>Saiba mais em <Link to="/ajuda/aliquotas">Alíquotas por município</Link>. Alíquotas nominais; reduções do produto ou serviço (cClassTrib) são aplicadas pelo motor oficial no cálculo.</p>
        </section>
      </main>
      <SiteFooter />
    </div>
  );
}
