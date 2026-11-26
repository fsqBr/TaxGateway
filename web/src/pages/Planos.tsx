import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { Check } from 'lucide-react';
import { Logo } from '../components/Logo';
import { account } from '../lib/api';
import { integer, money } from '../lib/format';

/** Plano público (GET /v1/conta/planos, D-C01). */
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

/** Cartões de planos (vitrine no site e troca de plano no portal). */
export function PlanCards({ planos, atual, acao }: { planos: PlanoPublico[]; atual?: string; acao: (p: PlanoPublico) => React.ReactNode }) {
  return (
    <div className="plan-grid">
      {planos.map((p) => (
        <article key={p.codigo} className={`card plan${p.codigo === 'PROFISSIONAL' ? ' featured' : ''}${p.codigo === atual ? ' current' : ''}`}>
          {p.codigo === 'PROFISSIONAL' && <span className="plan-flag">Mais escolhido</span>}
          <h3>{p.nome}</h3>
          <p className="muted small">{p.descricao}</p>
          <div className="plan-price">
            <strong>{precoLabel(p)}</strong>
            {p.preco_centavos > 0 && <span className="muted small">/mês</span>}
          </div>
          <ul className="plan-features">
            {p.recursos.map((r) => <li key={r}><Check size={15} aria-hidden="true" />{r}</li>)}
          </ul>
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
  return (
    <div className="site">
      <header className="site-header">
        <div className="inner">
          <Link to="/" aria-label="Início"><Logo size={30} /></Link>
          <nav className="site-nav" aria-label="Principal">
            <Link to="/ajuda" className="hide-sm">Ajuda</Link>
            <Link to="/portal" className="hide-sm">Entrar</Link>
            <Link className="button" to="/cadastro">Criar conta grátis</Link>
          </nav>
        </div>
      </header>
      <main id="conteudo" className="section">
        <div className="section-head">
          <div className="eyebrow">Planos</div>
          <h1 style={{ fontSize: 'clamp(1.9rem, 3.6vw, 2.6rem)', letterSpacing: '-0.03em' }}>Comece grátis. Pague pelo que usar.</h1>
          <p>Todos os planos usam o motor oficial da Receita, com isolamento por cliente e verificação em duas etapas. Troque de plano quando quiser pelo portal.</p>
        </div>
        {erro && <p className="muted">{erro}</p>}
        {planos && (
          <PlanCards planos={planos} acao={(p) => p.contratacao_online
            ? <Link className={p.codigo === 'PROFISSIONAL' ? 'button lg' : 'button secondary lg'} to="/cadastro">{p.preco_centavos === 0 ? 'Criar conta grátis' : 'Começar'}</Link>
            : <a className="button secondary lg" href="mailto:vendas@taxgateway.com.br?subject=TaxGateway%20-%20plano%20sob%20consulta">Falar com vendas</a>} />
        )}
        <div className="gap-lg" />
        <div className="card">
          <h2 style={{ fontSize: '1.15rem' }}>Como funciona a cobrança</h2>
          <ul className="help-list">
            <li>Mensalidade fixa + excedente: documentos auditados acima da franquia e CNPJs além dos incluídos. Simulações têm franquia generosa.</li>
            <li>O plano Sandbox não gera cobrança: ao chegar na franquia do mês, as chamadas param até a virada ou até você trocar de plano.</li>
            <li>Pagamento por cartão na Stripe; faturas e recibos ficam no portal, em <strong>Plano e cobrança</strong>.</li>
            <li>Valores em reais. Software houses e grandes volumes: plano OEM e Enterprise sob contrato.</li>
          </ul>
          {planos && (
            <p className="muted small" style={{ marginTop: 8 }}>
              Excedente por documento a partir de {money(Math.min(...planos.filter((p) => p.excedente_documento_centavos > 0).map((p) => p.excedente_documento_centavos)) / 100)}.
              Franquias de {integer(Math.min(...planos.map((p) => p.documentos_incluidos)))} a {integer(Math.max(...planos.map((p) => p.documentos_incluidos)))} documentos por mês.
            </p>
          )}
        </div>
      </main>
    </div>
  );
}
