import { Fragment, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { Check, Minus } from 'lucide-react';
import { account } from '../lib/api';
import { integer, money } from '../lib/format';
import { SiteFooter, SiteHeader } from './SiteChrome';

/** Plano público (GET /v1/conta/planos, D-C01). Os campos de requisições em diante são opcionais para versões antigas da API. */
export interface PlanoPublico {
  codigo: string;
  nome: string;
  descricao: string;
  recursos: string[];
  preco_centavos: number;
  documentos_incluidos: number;
  simulacoes_incluidas: number;
  cnpjs_incluidos: number | null;
  usuarios: number | null;
  excedente_documento_centavos: number;
  cnpj_extra_centavos: number;
  contratacao_online: boolean;
  requisicoes_por_minuto?: number;
  requisicoes_simultaneas?: number;
  requisicoes_por_minuto_por_cnpj?: number | null;
  lote_max_documentos?: number;
  lote_max_bytes?: number;
  excedente_simulacao_milicentavos?: number;
  captura_dfe?: boolean;
  franquia_rigida?: boolean;
}

export function usePlanos() {
  const [planos, setPlanos] = useState<PlanoPublico[]>();
  const [erro, setErro] = useState<string>();
  useEffect(() => {
    account<PlanoPublico[]>('portal', '/planos', { silent: true }).then(setPlanos).catch(() => setErro('Não foi possível carregar os planos agora.'));
  }, []);
  return { planos, erro };
}

export function precoLabel(p: PlanoPublico): string {
  if (p.preco_centavos === 0) return p.contratacao_online ? 'Grátis' : 'Sob consulta';
  return money(p.preco_centavos / 100);
}

/**
 * Plano em destaque derivado dos dados (não fixo no código): o plano pago contratável online de menor preço que inclui a
 * captura de DF-e. Sem o campo `captura_dfe` (API antiga), cai no Profissional se existir.
 */
export function planoDestaque(planos: PlanoPublico[]): string | undefined {
  const pagos = planos.filter((p) => p.preco_centavos > 0 && p.contratacao_online);
  if (planos.some((p) => p.captura_dfe !== undefined)) {
    return pagos.filter((p) => p.captura_dfe).sort((a, b) => a.preco_centavos - b.preco_centavos)[0]?.codigo;
  }
  return pagos.find((p) => p.codigo === 'PROFISSIONAL')?.codigo;
}

/** Valor em reais com até 4 casas (excedentes de simulação custam frações de centavo). */
const precoFino = (reais: number) =>
  new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL', minimumFractionDigits: 2, maximumFractionDigits: 4 }).format(reais);

const mb = (bytes: number) => `${integer(Math.round(bytes / (1024 * 1024)))} MB`;

/** O que cada plano dá, em linhas curtas (franquia, excedente, CNPJs, usuários, DF-e, lote e limite da API). */
export function detalhesDoPlano(p: PlanoPublico): { rotulo: string; valor: string }[] {
  const pago = p.preco_centavos > 0 || !p.contratacao_online;
  const rigida = p.franquia_rigida ?? !pago;
  const linhas: { rotulo: string; valor: string }[] = [
    { rotulo: 'Franquia por mês', valor: `${integer(p.documentos_incluidos)} auditorias e ${integer(p.simulacoes_incluidas)} simulações` },
  ];
  if (rigida) {
    linhas.push({ rotulo: 'Ao esgotar', valor: 'para até o mês seguinte (sem cobrança de excedente)' });
  } else {
    const doc = p.excedente_documento_centavos > 0 ? `${precoFino(p.excedente_documento_centavos / 100)} por auditoria` : 'auditorias sem custo extra';
    const simMili = p.excedente_simulacao_milicentavos;
    const sim = simMili === undefined ? undefined : simMili > 0 ? `${precoFino(simMili / 100_000)} por simulação` : 'simulações sem custo extra';
    linhas.push({ rotulo: 'Excedente', valor: `${[doc, sim].filter(Boolean).join(' e ')}, até o teto que você definir` });
  }
  linhas.push({
    rotulo: 'CNPJs',
    valor: p.cnpjs_incluidos === null
      ? `ilimitados${p.cnpj_extra_centavos > 0 ? ` (${money(p.cnpj_extra_centavos / 100)} por CNPJ ativo)` : ''}`
      : `${integer(p.cnpjs_incluidos)}${p.cnpj_extra_centavos > 0 ? ` (${money(p.cnpj_extra_centavos / 100)} por CNPJ extra)` : ''}`,
  });
  linhas.push({ rotulo: 'Usuários', valor: p.usuarios === null ? 'ilimitados' : integer(p.usuarios) });
  if (p.captura_dfe !== undefined) linhas.push({ rotulo: 'Captura de DF-e', valor: p.captura_dfe ? 'sim' : 'não' });
  if (p.lote_max_documentos !== undefined) {
    linhas.push({ rotulo: 'Lote', valor: `até ${integer(p.lote_max_documentos)} notas${p.lote_max_bytes ? ` (${mb(p.lote_max_bytes)})` : ''}` });
  }
  if (p.requisicoes_por_minuto !== undefined) {
    const porCnpj = p.requisicoes_por_minuto_por_cnpj ? ` + ${integer(p.requisicoes_por_minuto_por_cnpj)}/min por CNPJ atendido` : '';
    linhas.push({ rotulo: 'API', valor: `${integer(p.requisicoes_por_minuto)} requisições/min${porCnpj}` });
  }
  return linhas;
}

type Celula = string | boolean;

/** Linhas da matriz de comparação: o que muda de um plano para outro, a partir do cadastro de planos. */
export function linhasDaMatriz(planos: PlanoPublico[]): { grupo: string; rotulo: string; valores: Celula[] }[] {
  const pago = (p: PlanoPublico) => p.preco_centavos > 0 || !p.contratacao_online;
  // Plano sob contrato sem valores no cadastro (Enterprise): franquia e excedente são negociados.
  const contrato = (p: PlanoPublico) => !p.contratacao_online && p.preco_centavos === 0;
  const linhas: { grupo: string; rotulo: string; valores: Celula[] }[] = [
    { grupo: 'Preço e franquia', rotulo: 'Mensalidade', valores: planos.map((p) => (p.preco_centavos > 0 ? `${precoLabel(p)}/mês` : precoLabel(p))) },
    { grupo: 'Preço e franquia', rotulo: 'Auditorias por mês', valores: planos.map((p) => (contrato(p) && !p.documentos_incluidos ? 'sob contrato' : integer(p.documentos_incluidos))) },
    { grupo: 'Preço e franquia', rotulo: 'Simulações por mês', valores: planos.map((p) => (contrato(p) && !p.simulacoes_incluidas ? 'sob contrato' : integer(p.simulacoes_incluidas))) },
    {
      grupo: 'Preço e franquia', rotulo: 'Auditoria além da franquia',
      valores: planos.map((p) => (contrato(p) ? 'sob contrato' : (p.franquia_rigida ?? !pago(p)) ? 'para no limite' : p.excedente_documento_centavos > 0 ? precoFino(p.excedente_documento_centavos / 100) : 'sem custo extra')),
    },
  ];
  if (planos.some((p) => p.excedente_simulacao_milicentavos !== undefined)) {
    linhas.push({
      grupo: 'Preço e franquia', rotulo: 'Simulação além da franquia',
      valores: planos.map((p) => (contrato(p) ? 'sob contrato' : (p.franquia_rigida ?? !pago(p)) ? 'para no limite' : (p.excedente_simulacao_milicentavos ?? 0) > 0 ? precoFino(p.excedente_simulacao_milicentavos! / 100_000) : 'sem custo extra')),
    });
  }
  linhas.push(
    { grupo: 'Preço e franquia', rotulo: 'Teto de excedente definido por você', valores: planos.map((p) => !(p.franquia_rigida ?? !pago(p))) },
    { grupo: 'Empresa e equipe', rotulo: 'CNPJs incluídos', valores: planos.map((p) => (p.cnpjs_incluidos === null ? 'ilimitados' : p.cnpjs_incluidos === 0 && p.cnpj_extra_centavos > 0 ? 'cada CNPJ é cobrado' : integer(p.cnpjs_incluidos))) },
    { grupo: 'Empresa e equipe', rotulo: 'CNPJ além dos incluídos', valores: planos.map((p) => (p.cnpj_extra_centavos > 0 ? `${money(p.cnpj_extra_centavos / 100)}/mês` : false)) },
    { grupo: 'Empresa e equipe', rotulo: 'Usuários', valores: planos.map((p) => (p.usuarios === null ? 'ilimitados' : integer(p.usuarios))) },
  );
  if (planos.some((p) => p.captura_dfe !== undefined)) {
    linhas.push({ grupo: 'Recursos', rotulo: 'Captura de notas de fornecedores (DF-e)', valores: planos.map((p) => !!p.captura_dfe) });
  }
  linhas.push(
    { grupo: 'Recursos', rotulo: 'Cálculo e auditoria pelo motor oficial da Receita', valores: planos.map(() => true) },
    { grupo: 'Recursos', rotulo: 'Apuração do mês e exportação para o contador', valores: planos.map(() => true) },
    { grupo: 'Recursos', rotulo: 'Alíquotas atualizadas automaticamente, com aviso', valores: planos.map(() => true) },
    { grupo: 'Recursos', rotulo: 'Webhooks e lote de notas', valores: planos.map(() => true) },
  );
  if (planos.some((p) => p.lote_max_documentos !== undefined)) {
    linhas.push({ grupo: 'Integração', rotulo: 'Notas por lote', valores: planos.map((p) => (p.lote_max_documentos ? integer(p.lote_max_documentos) : false)) });
  }
  if (planos.some((p) => p.requisicoes_por_minuto !== undefined)) {
    linhas.push({ grupo: 'Integração', rotulo: 'Requisições por minuto (API)', valores: planos.map((p) => (p.requisicoes_por_minuto ? integer(p.requisicoes_por_minuto) : false)) });
  }
  if (planos.some((p) => p.requisicoes_simultaneas !== undefined)) {
    linhas.push({ grupo: 'Integração', rotulo: 'Requisições simultâneas', valores: planos.map((p) => (p.requisicoes_simultaneas ? integer(p.requisicoes_simultaneas) : false)) });
  }
  if (planos.some((p) => p.requisicoes_por_minuto_por_cnpj)) {
    linhas.push({ grupo: 'Integração', rotulo: 'Sub-limite por CNPJ atendido', valores: planos.map((p) => (p.requisicoes_por_minuto_por_cnpj ? `${integer(p.requisicoes_por_minuto_por_cnpj)}/min` : false)) });
  }
  return linhas;
}

const Valor = ({ v }: { v: Celula }) =>
  typeof v === 'boolean'
    ? (v ? <Check size={16} aria-label="incluído" color="var(--ok)" /> : <Minus size={16} aria-label="não incluído" color="var(--muted)" />)
    : <>{v}</>;

/** Matriz de comparação (recurso × plano). Rola na horizontal dentro do quadro, com a primeira coluna fixa. */
export function PlanMatrix({ planos, atual }: { planos: PlanoPublico[]; atual?: string }) {
  const linhas = linhasDaMatriz(planos);
  const destaque = planoDestaque(planos);
  return (
    <div className="plan-matrix-wrap" role="region" aria-label="Comparação dos planos" tabIndex={0}>
      <table className="plan-matrix">
        <thead>
          <tr>
            <th scope="col"><span className="sr-only">Recurso</span></th>
            {planos.map((p) => (
              <th key={p.codigo} scope="col" className={p.codigo === atual ? 'current' : p.codigo === destaque ? 'featured' : undefined}>
                {p.nome}{p.codigo === atual && <span className="muted small"> (atual)</span>}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {linhas.map((l, i) => (
            <Fragment key={l.rotulo}>
              {(i === 0 || linhas[i - 1].grupo !== l.grupo) && (
                <tr className="group"><th scope="rowgroup" colSpan={planos.length + 1}>{l.grupo}</th></tr>
              )}
              <tr>
                <th scope="row">{l.rotulo}</th>
                {l.valores.map((v, j) => <td key={planos[j].codigo} className={planos[j].codigo === atual ? 'current' : undefined}><Valor v={v} /></td>)}
              </tr>
            </Fragment>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** Cartões de planos (vitrine no site e troca de plano no portal). `detalhes` mostra a ficha completa de cada plano. */
export function PlanCards({ planos, atual, acao, detalhes }: { planos: PlanoPublico[]; atual?: string; acao: (p: PlanoPublico) => React.ReactNode; detalhes?: boolean }) {
  const destaque = planoDestaque(planos);
  return (
    <div className="plan-grid">
      {planos.map((p) => (
        <article key={p.codigo} aria-label={`Plano ${p.nome}`} className={`card plan${p.codigo === destaque ? ' featured' : ''}${p.codigo === atual ? ' current' : ''}`}>
          {p.codigo === destaque && <span className="plan-flag">Recomendado</span>}
          <h3>{p.nome}</h3>
          <p className="muted small">{p.descricao}</p>
          <div className="plan-price">
            <strong>{precoLabel(p)}</strong>
            {p.preco_centavos > 0 && <span className="muted small">/mês</span>}
          </div>
          {detalhes ? (
            <dl className="kv plan-features">
              {detalhesDoPlano(p).map((d) => <div key={d.rotulo}><dt>{d.rotulo}</dt><dd>{d.valor}</dd></div>)}
            </dl>
          ) : (
            <ul className="plan-features">
              {p.recursos.map((r) => <li key={r}><Check size={15} aria-hidden="true" />{r}</li>)}
            </ul>
          )}
          <div className="plan-cta">{acao(p)}</div>
        </article>
      ))}
    </div>
  );
}

/** Página pública de planos (/planos). Valores vindos do cadastro de planos do backoffice. */
export default function PlanosPage() {
  const { planos, erro } = usePlanos();
  useEffect(() => { document.title = 'Planos · TaxGateway'; }, []);
  const destaque = planos ? planoDestaque(planos) : undefined;
  return (
    <div className="site">
      <SiteHeader />
      <main id="conteudo" className="section">
        <div className="section-head">
          <div className="eyebrow">Planos</div>
          <h1 style={{ fontSize: 'clamp(1.9rem, 3.6vw, 2.6rem)', letterSpacing: '-0.03em' }}>Comece grátis. Pague pelo que usar.</h1>
          <p>Toda conta começa no Sandbox, grátis e sem cartão. Todos os planos usam o motor oficial da Receita, com isolamento por cliente e verificação em duas etapas. Troque de plano quando quiser pelo portal.</p>
        </div>
        {erro && <p className="muted">{erro}</p>}
        {planos && (
          <PlanCards planos={planos} detalhes acao={(p) => {
            if (!p.contratacao_online) {
              return <a className="button secondary lg" href="mailto:vendas@taxgateway.com.br?subject=TaxGateway%20-%20plano%20sob%20consulta">Falar com vendas</a>;
            }
            if (p.preco_centavos === 0) return <Link className="button secondary lg" to="/cadastro">Criar conta grátis</Link>;
            return <Link className={p.codigo === destaque ? 'button lg' : 'button secondary lg'} to={`/cadastro?plano=${encodeURIComponent(p.codigo)}`}>Escolher {p.nome}</Link>;
          }} />
        )}
        {planos && planos.length > 1 && (
          <section className="card" id="comparar" style={{ marginTop: 24 }}>
            <h2 style={{ fontSize: '1.15rem', marginBottom: 12 }}>Compare os planos</h2>
            <PlanMatrix planos={planos} />
          </section>
        )}
        <div className="gap-lg" />
        <div className="grid">
          <div className="card">
            <h2 style={{ fontSize: '1.15rem' }}>Como funciona a cobrança</h2>
            <ul className="help-list">
              <li><strong>Sandbox (grátis):</strong> 200 auditorias e 2.000 simulações por mês, 1 CNPJ e sem captura de DF-e. Ao esgotar, as chamadas param até o mês seguinte — nunca há cobrança.</li>
              <li><strong>Planos pagos:</strong> mensalidade fixa com franquia mensal de auditorias e de simulações. O que passar da franquia é cobrado pelo preço de excedente do plano, <strong>só até o teto mensal que você definir</strong> em Plano e cobrança.</li>
              <li><strong>O teto começa em zero:</strong> sem mexer nele, as chamadas param ao esgotar a franquia, sem cobrança surpresa. Avisamos ao chegar em 80% e em 100% da franquia e do teto.</li>
              <li>CNPJs além dos incluídos no plano são cobrados por CNPJ ativo na competência.</li>
              <li>Reenviar a mesma nota não é cobrado. Reauditar a pedido (<code>reprocess=true</code>) é cobrado como nova auditoria. Quando uma alíquota oficial muda com efeito retroativo, o TaxGateway reprocessa sozinho, sem cobrar.</li>
              <li>Pagamento por cartão na Stripe; faturas e recibos ficam no portal, em <strong>Plano e cobrança</strong>. Valores em reais.</li>
            </ul>
          </div>
          <div className="card">
            <h2 style={{ fontSize: '1.15rem' }}>Verificação da empresa</h2>
            <p className="muted" style={{ marginTop: 6 }}>
              Para contratar um plano pago e para ligar a captura de notas de fornecedores (DF-e, do Profissional em diante), confirme
              que a empresa é sua. Isso protege o seu CNPJ contra uso por terceiros. Escolha um caminho no portal:
            </p>
            <ul className="help-list">
              <li>certificado <strong>e-CNPJ</strong> da empresa (na hora);</li>
              <li><strong>código enviado ao e-mail</strong> do cadastro do CNPJ na Receita Federal;</li>
              <li><strong>análise do suporte</strong>, com o contrato social.</li>
            </ul>
          </div>
          <div className="card">
            <h2 style={{ fontSize: '1.15rem' }}>Se o pagamento falhar</h2>
            <ul className="help-list">
              <li><strong>Dias 0, 3 e 7:</strong> avisos no portal e por e-mail; a Stripe tenta cobrar de novo.</li>
              <li><strong>Dia 10:</strong> cálculo e auditoria ficam bloqueados. Consultas, exportações e o portal continuam.</li>
              <li><strong>Dia 15:</strong> a conta é suspensa.</li>
              <li>Pagou, o acesso volta na hora.</li>
            </ul>
          </div>
          <div className="card">
            <h2 style={{ fontSize: '1.15rem' }}>Limites de requisição</h2>
            <ul className="help-list">
              <li>O limite por minuto de cada plano vale para os seus <strong>sistemas</strong> (API key ou OAuth). Pessoas no portal têm um limite próprio, que não consome o dos sistemas.</li>
              <li>Enviar um lote conta como 10 requisições.</li>
              <li>No Parceiro OEM, cada CNPJ atendido tem também um sub-limite próprio, para um cliente não esgotar o limite dos outros.</li>
              <li>Ao passar do limite, a API responde <code>429</code> com o tempo de espera. Detalhes em <Link to="/ajuda/limites">Limites de requisição</Link>.</li>
            </ul>
          </div>
        </div>
        {planos && planos.length > 0 && (
          <p className="muted small" style={{ marginTop: 16 }}>
            Franquias de {integer(Math.min(...planos.map((p) => p.documentos_incluidos)))} a {integer(Math.max(...planos.map((p) => p.documentos_incluidos)))} auditorias por mês.
            Software houses e grandes volumes: Parceiro OEM e Enterprise sob contrato. Dúvidas? Veja <Link to="/ajuda/planos-e-cobranca">Planos, franquia e cobrança</Link>.
          </p>
        )}
      </main>
      <SiteFooter />
    </div>
  );
}
