import { useState } from 'react';
import { Download } from 'lucide-react';
import { Alert, Card, Field, Loading, PageHeader, Stat, Table, useAction, useLoad } from '../components/ui';
import { downloadFile, portal } from '../lib/api';
import { cnpj, currentMonth, integer, money } from '../lib/format';

interface Resumo {
  competencia: string;
  saidas: { documentos: number; cbs: number; ibs: number; is: number; declarado: { cbs: number; ibs: number; is: number }; diferenca: number; divergentes: number };
  entradas: { documentos: number; cbs: number; ibs: number; credito_potencial: number; credito_em_risco: number; divergentes: number };
  saldo: { debitos: number; creditos_aproveitaveis: number; a_recolher_estimado: number; credor_estimado: number };
  por_municipio: { codigo: number | null; nome: string | null; uf: string | null; documentos: number; cbs: number; ibs: number }[];
  por_classificacao: { cst: string | null; c_class_trib: string | null; itens: number; base: number; valor: number }[];
  observacao: string;
}

interface Fornecedor { cnpj: string; documentos: number; divergentes: number; indicios: number; credito_potencial: number; credito_em_risco: number; diferenca: number }

/**
 * Apuração do mês (D-R26): débitos das saídas, créditos das entradas e saldo estimado, por município e por
 * classificação; fornecedores ordenados pelo risco de crédito; CSV para o contador. Tudo a partir das notas auditadas.
 */
export function Apuracao() {
  const [mes, setMes] = useState(currentMonth());
  const resumo = useLoad(() => portal<Resumo>('/v1/apuracao', { query: { competencia: mes } }), [mes]);
  const forn = useLoad(() => portal<{ fornecedores: Fornecedor[] }>('/v1/apuracao/fornecedores', { query: { competencia: mes } }), [mes]);
  const r = resumo.data;
  const exportar = useAction();
  return (
    <>
      <PageHeader title="Apuração do mês" description="IBS e CBS das notas auditadas, pela data de emissão: o que você deve nas saídas, o crédito das entradas e o saldo estimado.">
        <button type="button" className="secondary" disabled={exportar.busy} onClick={() => void exportar.run(() => downloadFile('/v1/apuracao/exportar', `apuracao-${mes}.csv`, { competencia: mes }))}>
          <Download size={15} aria-hidden="true" /> Exportar para o contador (CSV)
        </button>
      </PageHeader>
      <Card>
        <Field label="Competência"><input type="month" min="2026-01" value={mes} onChange={(e) => e.target.value && setMes(e.target.value)} /></Field>
      </Card>
      {resumo.loading && <Loading />}
      <Alert>{resumo.error ?? exportar.error}</Alert>
      {r && (
        <>
          <div className="grid">
            <Stat label="Débitos (saídas)" value={money(r.saldo.debitos)} hint={`${integer(r.saidas.documentos)} notas · CBS ${money(r.saidas.cbs)} · IBS ${money(r.saidas.ibs)}`} />
            <Stat label="Créditos aproveitáveis (entradas)" value={money(r.saldo.creditos_aproveitaveis)} hint={`${integer(r.entradas.documentos)} notas · em risco ${money(r.entradas.credito_em_risco)}`} />
            <Stat label={r.saldo.credor_estimado > 0 ? 'Saldo credor estimado' : 'A recolher (estimado)'} value={money(r.saldo.credor_estimado > 0 ? r.saldo.credor_estimado : r.saldo.a_recolher_estimado)} hint="Débitos − créditos aproveitáveis" />
            <Stat label="Notas com divergência" value={integer(r.saidas.divergentes + r.entradas.divergentes)} hint={`Diferença declarado × recalculado nas saídas: ${money(r.saidas.diferenca)}`} />
          </div>
          <p className="muted small">{r.observacao}</p>
          <div className="grid-2">
            <Card title="Saídas por município de incidência" flush>
              <Table head={['Município', 'Notas', 'CBS', 'IBS']} empty={r.por_municipio.length === 0} emptyText="Nenhuma nota de saída auditada nesta competência.">
                {r.por_municipio.map((m, i) => (
                  <tr key={m.codigo ?? i}>
                    <td data-label="Município">{m.nome ? `${m.nome} / ${m.uf}` : m.codigo ?? '—'}</td>
                    <td data-label="Notas" className="num">{integer(m.documentos)}</td>
                    <td data-label="CBS" className="num">{money(m.cbs)}</td>
                    <td data-label="IBS" className="num">{money(m.ibs)}</td>
                  </tr>
                ))}
              </Table>
            </Card>
            <Card title="Saídas por classificação (CST / cClassTrib)" flush>
              <Table head={['CST', 'cClassTrib', 'Itens', 'Base', 'IBS+CBS declarado']} empty={r.por_classificacao.length === 0} emptyText="Sem itens.">
                {r.por_classificacao.map((c, i) => (
                  <tr key={`${c.cst}-${c.c_class_trib}-${i}`}>
                    <td data-label="CST" className="mono">{c.cst ?? '—'}</td>
                    <td data-label="cClassTrib" className="mono">{c.c_class_trib ?? '—'}</td>
                    <td data-label="Itens" className="num">{integer(c.itens)}</td>
                    <td data-label="Base" className="num">{money(c.base)}</td>
                    <td data-label="Valor" className="num">{money(c.valor)}</td>
                  </tr>
                ))}
              </Table>
            </Card>
          </div>
        </>
      )}
      <Card title="Fornecedores: crédito e divergências" description="Quem mais coloca o seu crédito em risco aparece primeiro. Use para cobrar a correção das notas." flush>
        {forn.loading ? <Loading /> : (
          <Table head={['Fornecedor', 'Notas', 'Divergentes', 'Indícios', 'Crédito potencial', 'Crédito em risco']} empty={!forn.data?.fornecedores.length} emptyText="Nenhuma nota de entrada auditada nesta competência.">
            {forn.data?.fornecedores.map((f) => (
              <tr key={f.cnpj}>
                <td data-label="Fornecedor" className="mono">{cnpj(f.cnpj)}</td>
                <td data-label="Notas" className="num">{integer(f.documentos)}</td>
                <td data-label="Divergentes" className="num">{integer(f.divergentes)}</td>
                <td data-label="Indícios" className="num">{integer(f.indicios)}</td>
                <td data-label="Crédito potencial" className="num">{money(f.credito_potencial)}</td>
                <td data-label="Crédito em risco" className="num"><strong>{money(f.credito_em_risco)}</strong></td>
              </tr>
            ))}
          </Table>
        )}
        <Alert>{forn.error}</Alert>
      </Card>
    </>
  );
}

/**
 * Preço e margem com a reforma (D-R26): o IBS/CBS é "por fora" — dado o preço sem tributo, o custo e o crédito das
 * compras, mostra a margem e o preço que mantém a margem desejada. Usa as alíquotas que o cliente informar (ou as da
 * simulação), sem chamar o motor.
 */
export function PrecoMargem() {
  const [preco, setPreco] = useState('100');
  const [custo, setCusto] = useState('60');
  const [aliq, setAliq] = useState('26,5');
  const [creditoCompras, setCreditoCompras] = useState('26,5');
  const [margemAlvo, setMargemAlvo] = useState('20');
  const n = (s: string) => Number(s.replace(',', '.')) || 0;
  const tributo = (n(preco) * n(aliq)) / 100;
  const credito = (n(custo) * n(creditoCompras)) / 100;
  const devido = Math.max(0, tributo - credito);
  const margem = n(preco) - n(custo);
  const margemPct = n(preco) > 0 ? (margem / n(preco)) * 100 : 0;
  const precoAlvo = n(custo) / (1 - n(margemAlvo) / 100);
  return (
    <>
      <PageHeader title="Preço e margem" description="Com a reforma, IBS e CBS são cobrados por fora do preço e geram crédito nas compras. Veja o que sobra e qual preço mantém a sua margem." />
      <div className="grid-2">
        <Card title="Dados">
          <div className="form-grid">
            <Field label="Preço de venda sem tributo (R$)"><input inputMode="decimal" value={preco} onChange={(e) => setPreco(e.target.value)} /></Field>
            <Field label="Custo do produto sem tributo (R$)"><input inputMode="decimal" value={custo} onChange={(e) => setCusto(e.target.value)} /></Field>
            <Field label="Alíquota IBS+CBS na venda (%)" hint="Consulte em Alíquotas ou simule a operação."><input inputMode="decimal" value={aliq} onChange={(e) => setAliq(e.target.value)} /></Field>
            <Field label="Alíquota IBS+CBS paga nas compras (%)" hint="Gera crédito (não cumulatividade)."><input inputMode="decimal" value={creditoCompras} onChange={(e) => setCreditoCompras(e.target.value)} /></Field>
            <Field label="Margem desejada (%)"><input inputMode="decimal" value={margemAlvo} onChange={(e) => setMargemAlvo(e.target.value)} /></Field>
          </div>
        </Card>
        <Card title="Resultado">
          <Table head={['', 'R$']}>
            <tr><td>Preço ao cliente (com IBS+CBS)</td><td className="num">{money(n(preco) + tributo)}</td></tr>
            <tr><td>IBS+CBS destacado na venda</td><td className="num">{money(tributo)}</td></tr>
            <tr><td>Crédito das compras</td><td className="num">{money(credito)}</td></tr>
            <tr><td>IBS+CBS a recolher</td><td className="num">{money(devido)}</td></tr>
            <tr><td><strong>Margem</strong></td><td className="num"><strong>{money(margem)} ({margemPct.toLocaleString('pt-BR', { maximumFractionDigits: 1 })}%)</strong></td></tr>
            <tr><td>Preço sem tributo para {margemAlvo}% de margem</td><td className="num">{Number.isFinite(precoAlvo) ? money(precoAlvo) : '—'}</td></tr>
          </Table>
          <p className="muted small">Cálculo gerencial: não considera reduções por cClassTrib, regimes específicos nem o período de transição — para isso, simule a operação.</p>
        </Card>
      </div>
    </>
  );
}
