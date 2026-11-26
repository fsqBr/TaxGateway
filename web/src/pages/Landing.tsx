import { useState } from 'react';
import { Link } from 'react-router-dom';
import {
  ArrowRight, BadgeCheck, Building2, Calculator, CheckCircle2, Cpu, DownloadCloud, FileCheck2, Fingerprint, KeyRound,
  Layers, Lock, MapPin, ScrollText, ShieldCheck, Webhook,
} from 'lucide-react';
import { Logo } from '../components/Logo';
import { VersionTag } from '../components/VersionNotice';

// Capturas reais do portal com dados de demonstração (D-W13), geradas por `npm run screenshots`.
const showcase = [
  { key: 'simular', label: 'Simular', path: '/portal/simular', title: 'Simulação por tributo, na hora', text: 'O mesmo JSON da API, com o resultado de CBS, IBS estadual, IBS municipal e IS por item, a fonte de cada alíquota e os avisos do motor.', help: '/ajuda/simulacao' },
  { key: 'auditoria', label: 'Auditoria', path: '/portal/auditoria', title: 'Cada ocorrência com regra e fundamento', text: 'Envie o XML autorizado e veja a situação, os valores declarados contra os recalculados e cada ocorrência com o campo do XML e a base legal.', help: '/ajuda/auditoria' },
  { key: 'lotes', label: 'Lotes', path: '/portal/lotes', title: 'Milhares de notas num ZIP', text: 'Acompanhe o processamento de cada lote e baixe o relatório CSV com uma linha por nota.', help: '/ajuda/lotes' },
  { key: 'visao-geral', label: 'Consumo', path: '/portal', title: 'Consumo e limites à vista', text: 'Documentos auditados, simulações, CNPJs ativos e o limite do plano na competência.', help: '/ajuda/primeiros-passos' },
  { key: 'webhooks', label: 'Webhooks', path: '/portal/webhooks', title: 'Eventos com histórico e reenvio', text: 'Cadastre URLs, gire o segredo sem parar a integração e reenvie entregas que falharam.', help: '/ajuda/webhooks' },
] as const;

function Showcase() {
  const [i, setI] = useState(0);
  const cur = showcase[i];
  return (
    <section className="section" id="funcionando">
      <div className="section-head">
        <div className="eyebrow">Veja funcionando</div>
        <h2>O portal, com dados de verdade.</h2>
        <p>Telas reais do TaxGateway rodando os motores oficiais, com um cliente de demonstração.</p>
      </div>
      <div className="showcase-tabs" role="tablist" aria-label="Telas do portal">
        {showcase.map((s, j) => (
          <button key={s.key} type="button" role="tab" aria-selected={j === i} className={j === i ? 'active' : ''} onClick={() => setI(j)}>{s.label}</button>
        ))}
      </div>
      <div className="showcase" role="tabpanel">
        <div className="showcase-frame">
          <div className="window-bar"><i /><i /><i /> taxgateway{cur.path}</div>
          <img src={`/ajuda/img/${cur.key}.jpg`} alt={`Tela ${cur.label} do portal TaxGateway`} width={1440} height={900} loading="lazy" decoding="async" />
        </div>
        <div className="showcase-text">
          <div className="eyebrow">{cur.label}</div>
          <h3>{cur.title}</h3>
          <p>{cur.text}</p>
          <Link to={cur.help} className="button secondary" style={{ marginTop: 12 }}>Como funciona <ArrowRight size={16} aria-hidden="true" /></Link>
        </div>
      </div>
    </section>
  );
}

// Conteúdo só com fatos do produto (D-W09): nada de clientes, números de mercado ou preços inventados.
const features = [
  { icon: Calculator, title: 'Simulação na hora da venda', text: 'CBS, IBS e Imposto Seletivo por item, calculados pelo motor oficial da Receita Federal, com a alíquota e o ato legal de cada tributo.' },
  { icon: FileCheck2, title: 'Auditoria do XML', text: 'Recalculamos a NF-e/NFC-e autorizada e apontamos cada divergência com regra, campo, valor de referência e fundamentação legal.' },
  { icon: Layers, title: 'Lotes de milhares de notas', text: 'Envie um ZIP, acompanhe o progresso e receba o relatório por webhook. O lote não disputa capacidade com o seu checkout.' },
  { icon: DownloadCloud, title: 'Captura automática de DF-e', text: 'Com o certificado A1, buscamos as notas em que você é destinatário, manifestamos a ciência e auditamos a entrada.' },
  { icon: Building2, title: 'Multi-CNPJ para software houses', text: 'Atenda todos os seus clientes com uma integração só, com uso e cobrança separados por CNPJ.' },
  { icon: Webhook, title: 'Eventos assinados', text: 'Webhooks com assinatura HMAC-SHA256, reenvio automático e histórico de entregas.' },
];

const facts = [
  { icon: Cpu, text: 'Motores oficiais da RFB' },
  { icon: ScrollText, text: 'NF-e e NFC-e (leiaute RTC)' },
  { icon: KeyRound, text: 'OAuth 2.0 e API key' },
  { icon: MapPin, text: 'Dados processados no Brasil' },
  { icon: Fingerprint, text: 'LGPD por padrão' },
];

export default function Landing() {
  return (
    <div className="site">
      <header className="site-header">
        <div className="inner">
          <Link to="/" aria-label="Início"><Logo size={30} /></Link>
          <nav className="site-nav" aria-label="Principal">
            <a href="#produto" className="hide-sm">Produto</a>
            <a href="#funcionando" className="hide-sm">Veja funcionando</a>
            <Link to="/planos" className="hide-sm">Planos</Link>
            <a href="#seguranca" className="hide-sm">Segurança</a>
            <a href="#desenvolvedores" className="hide-sm">Desenvolvedores</a>
            <Link to="/ajuda">Ajuda</Link>
            <a href="/docs/" className="hide-sm">Documentação</a>
            <Link to="/portal" className="hide-sm">Entrar</Link>
            <Link className="button" to="/cadastro">Criar conta grátis</Link>
          </nav>
        </div>
      </header>

      <section className="hero">
        <div className="inner">
          <div>
            <span className="pill"><b>Novo</b> Reforma Tributária do Consumo · IBS · CBS · IS</span>
            <h1>Cálculo e auditoria de <em>IBS, CBS e Imposto Seletivo</em>, por API.</h1>
            <p className="lead">
              O TaxGateway roda os motores oficiais de cálculo da Receita Federal e acrescenta o que falta para produção:
              leitura do XML, regras de auditoria, lotes, captura de DF-e e operação multiempresa — com segurança e LGPD de verdade.
            </p>
            <div className="row">
              <Link className="button lg" to="/cadastro">Criar conta grátis <ArrowRight size={16} aria-hidden="true" /></Link>
              <Link className="button secondary lg" to="/portal">Acessar o portal</Link>
            </div>
            <p className="hero-note">Plano Sandbox sem cartão, com o mesmo motor que a Receita Federal publica.</p>
          </div>
          <div aria-label="Exemplo de chamada e resposta">
            <div className="window">
              <div className="window-bar"><i /><i /><i /> POST /v1/calculo/simular</div>
              <pre>
                <span className="tok-c"># uma linha de item de R$ 1.000,00 em São Paulo, junho de 2027</span>{'\n'}
                <span className="tok-k">curl</span> -X POST https://SEU-HOST/v1/calculo/simular \{'\n'}
                {'  '}-H <span className="tok-s">"X-Api-Key: $TAXGATEWAY_KEY"</span> \{'\n'}
                {'  '}-d <span className="tok-s">{`'{"cnpj_emitente":"11222333000181",`}</span>{'\n'}
                {'       '}<span className="tok-s">{`"regime_tributario":"REGIME_REGULAR",`}</span>{'\n'}
                {'       '}<span className="tok-s">{`"data_fato_gerador":"2027-06-15T14:00:00-03:00",`}</span>{'\n'}
                {'       '}<span className="tok-s">{`"municipio_ibge":3550308, "uf":"SP",`}</span>{'\n'}
                {'       '}<span className="tok-s">{`"itens":[{"cst_ibs_cbs":"000","c_class_trib":"000001",`}</span>{'\n'}
                {'                 '}<span className="tok-s">{`"valor_base_calculo":1000, ...}]}'`}</span>
              </pre>
            </div>
            <div className="result-card">
              <div className="spread"><span>Tributos sobre o consumo</span><span className="badge badge-ok">200 OK</span></div>
              <div className="total">R$ 113,00</div>
              <table>
                <tbody>
                  <tr><td>CBS · 11,20%</td><td className="num">R$ 112,00</td></tr>
                  <tr><td>IBS estadual · 0,05%</td><td className="num">R$ 0,50</td></tr>
                  <tr><td>IBS municipal · 0,05%</td><td className="num">R$ 0,50</td></tr>
                </tbody>
              </table>
              <p className="small" style={{ color: '#667085', marginTop: 8 }}>Resposta real do motor com o catálogo de 2027 (CBS provisória).</p>
            </div>
          </div>
        </div>
      </section>

      <div className="facts">
        <div className="inner">
          {facts.map((f) => <span key={f.text}><f.icon size={16} aria-hidden="true" />{f.text}</span>)}
        </div>
      </div>

      <section className="section" id="produto">
        <div className="section-head">
          <div className="eyebrow">Produto</div>
          <h2>O motor da Receita é gratuito. Colocá-lo em produção, não.</h2>
          <p>Mapear o XML, manter alíquotas e regras atualizadas, escalar sem travar o checkout e operar 24×7 é o que o TaxGateway entrega pronto.</p>
        </div>
        <div className="grid features-grid">
          {features.map((f) => (
            <article className="card feature" key={f.title}>
              <span className="icon"><f.icon size={20} aria-hidden="true" /></span>
              <h3>{f.title}</h3>
              <p>{f.text}</p>
            </article>
          ))}
        </div>
      </section>

      <section className="section" style={{ paddingTop: 0 }}>
        <div className="split">
          <div>
            <div className="eyebrow">Auditoria</div>
            <h2 style={{ fontSize: '1.9rem', letterSpacing: '-0.03em', marginTop: 8 }}>Cada divergência, explicada.</h2>
            <p className="muted" style={{ marginTop: 12, fontSize: '1.02rem' }}>
              Não basta dizer que está errado. Cada ocorrência traz a regra, o campo do XML, o valor declarado, o valor de
              referência e o fundamento legal — para o fiscal decidir em segundos.
            </p>
            <ul className="checklist">
              <li><CheckCircle2 size={18} aria-hidden="true" />Saída (você emitiu) e entrada (você recebeu), com crédito potencial</li>
              <li><CheckCircle2 size={18} aria-hidden="true" />Validação de schema e assinatura digital ICP-Brasil</li>
              <li><CheckCircle2 size={18} aria-hidden="true" />Reprocessamento quando a alíquota oficial muda</li>
            </ul>
          </div>
          <div className="card finding" aria-label="Exemplo de ocorrência de auditoria">
            <div className="finding-head"><strong>Nota 000.000.001 · item 1</strong><span className="badge badge-warn">Divergente</span></div>
            <div className="table-wrap">
              <table>
                <tbody>
                  <tr><td>Regra</td><td><code>R012</code> · determinística</td></tr>
                  <tr><td>Campo</td><td><code>gCBS/pCBS</code></td></tr>
                  <tr><td>Declarado</td><td>9,40%</td></tr>
                  <tr><td>Referência</td><td><strong>11,20%</strong></td></tr>
                  <tr><td>Fundamento</td><td>LC 214/2025, arts. 14 a 19 e 343 a 347</td></tr>
                </tbody>
              </table>
            </div>
            <p className="muted small">Exemplo ilustrativo com a estrutura real de uma ocorrência.</p>
          </div>
        </div>
      </section>

      <Showcase />

      <section className="band-dark" id="seguranca">
        <div className="section">
          <div className="section-head">
            <div className="eyebrow">Segurança e LGPD</div>
            <h2>Seus dados fiscais isolados, cifrados e sob controle.</h2>
            <p>Construído para quem responde por dados de terceiros: o cliente é o controlador; nós somos o operador.</p>
          </div>
          <div className="grid">
            {[
              { icon: Lock, t: 'Isolamento por cliente', d: 'Cada consulta ao banco é restrita ao seu tenant por política do próprio banco — testado a cada build.' },
              { icon: ShieldCheck, t: 'XML não guardado por padrão', d: 'Retenção só quando você pede, cifrada com chave exclusiva sua. Expurgo de titular sob demanda.' },
              { icon: Fingerprint, t: 'CPF pseudonimizado', d: 'Nos registros o CPF vira um HMAC com chave fora do banco. Um vazamento do banco não revela CPFs.' },
              { icon: BadgeCheck, t: 'Credenciais fortes', d: 'API keys com hash e pepper, OAuth 2.0 com rotação de chave sem interrupção e revogação imediata.' },
            ].map((x) => (
              <article className="card feature" key={x.t}>
                <span className="icon"><x.icon size={20} aria-hidden="true" /></span>
                <h3>{x.t}</h3>
                <p className="muted">{x.d}</p>
              </article>
            ))}
          </div>
        </div>
      </section>

      <section className="section" id="desenvolvedores">
        <div className="section-head">
          <div className="eyebrow">Desenvolvedores</div>
          <h2>Integre em uma tarde.</h2>
          <p>API REST com JSON, erros padronizados com código estável, idempotência e referência interativa.</p>
        </div>
        <div className="steps">
          <div className="card"><h3>Pegue sua credencial</h3><p className="muted" style={{ marginTop: 6 }}>API key ou cliente OAuth 2.0, com escopos por função.</p></div>
          <div className="card"><h3>Simule e audite</h3><p className="muted" style={{ marginTop: 6 }}>Simule no checkout; audite o XML depois da autorização ou em lote.</p></div>
          <div className="card"><h3>Reaja a eventos</h3><p className="muted" style={{ marginTop: 6 }}>Webhooks assinados avisam o seu ERP quando cada resultado fica pronto.</p></div>
        </div>
        <div className="gap-lg" />
        <div className="cta">
          <div>
            <h2>Pronto para a Reforma Tributária?</h2>
            <p style={{ marginTop: 6 }}>Comece pelo portal ou explore a referência completa da API.</p>
          </div>
          <div className="row">
            <Link className="button lg" to="/cadastro">Criar conta grátis</Link>
            <a className="button secondary lg" href="/docs/">Referência da API</a>
          </div>
        </div>
      </section>

      <footer className="site-footer">
        <div className="inner">
          <div>
            <Logo size={26} />
            <p className="muted small" style={{ marginTop: 10, maxWidth: 320 }}>Cálculo e auditoria de IBS, CBS e Imposto Seletivo sobre os motores oficiais da Receita Federal.</p>
          </div>
          <div><h4>Produto</h4><ul><li><a href="#produto">Recursos</a></li><li><a href="#seguranca">Segurança</a></li><li><Link to="/ajuda/novidades">Novidades</Link></li></ul></div>
          <div><h4>Desenvolvedores</h4><ul><li><Link to="/ajuda">Central de ajuda</Link></li><li><a href="/docs/">Referência da API</a></li><li><a href="/openapi/v1.json">OpenAPI (JSON)</a></li></ul></div>
          <div><h4>Acesso</h4><ul><li><Link to="/cadastro">Criar conta</Link></li><li><Link to="/portal">Portal do cliente</Link></li><li><Link to="/termos">Termos de Uso</Link></li><li><Link to="/privacidade">Privacidade</Link></li></ul></div>
        </div>
        <div className="legal"><div className="inner"><span>© TaxGateway · <VersionTag className="" /></span><span>Indícios não constituem parecer tributário.</span></div></div>
      </footer>
    </div>
  );
}
