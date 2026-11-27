import { useEffect } from 'react';
import { Link } from 'react-router-dom';
import { SiteFooter, SiteHeader } from './SiteChrome';

// Termos de Uso e Política de Privacidade (UX A5, D-U10). VERSÃO PRELIMINAR: o texto é uma base honesta do que o
// sistema faz (verificável no código e em docs/lgpd/), mas precisa de revisão jurídica antes do lançamento comercial.

const DOCS = {
  termos: {
    title: 'Termos de Uso',
    sections: [
      ['O serviço', 'O TaxGateway oferece, por API e portal, simulação e auditoria de IBS, CBS e Imposto Seletivo usando os motores de cálculo publicados pela Receita Federal. O TaxGateway não é afiliado à Receita Federal.'],
      ['Natureza dos resultados', 'Os resultados refletem as regras e alíquotas oficiais vigentes na data de cada documento. Indícios de auditoria não constituem parecer tributário: a decisão sobre o enquadramento fiscal é do contribuinte e de seus assessores.'],
      ['Conta e acesso', 'Cada pessoa usa a própria conta, com senha e verificação em duas etapas. Quem administra a conta da empresa responde pelos acessos que concede. Credenciais de sistemas (API keys, clientes OAuth) devem ser guardadas em cofre de segredos e nunca compartilhadas.'],
      ['Planos e cobrança', 'Cada plano tem franquia, limites e preços publicados na página de planos. O consumo é medido por documento auditado, simulação e CNPJ ativo. Mudanças de preço são avisadas com antecedência e não alteram faturas já fechadas.'],
      ['Uso aceitável', 'É proibido usar o serviço para testar a segurança de terceiros, contornar limites, revender o acesso sem contrato de parceria ou enviar dados de que você não é controlador ou operador autorizado.'],
      ['Disponibilidade', 'O serviço depende dos motores oficiais e da SEFAZ. Os objetivos de disponibilidade e latência por plano estão na documentação; contratos Enterprise têm SLA específico.'],
      ['Encerramento', 'Você pode encerrar a conta a qualquer momento. Dados fiscais são mantidos pelo prazo legal e depois apagados, conforme a Política de Privacidade.'],
    ],
  },
  privacidade: {
    title: 'Política de Privacidade',
    sections: [
      ['Papéis na LGPD', 'Nos documentos fiscais enviados, o cliente é o controlador e o TaxGateway é o operador. Nos dados da conta (nome, e-mail, registros de acesso), o TaxGateway é o controlador.'],
      ['O que tratamos', 'Dados de conta e de acesso (nome, e-mail, IP, navegador, data e hora); documentos fiscais e seus resultados de cálculo e auditoria; certificado A1 quando você liga a captura de DF-e.'],
      ['Minimização', 'Por padrão o XML não é guardado. O CPF de destinatários é transformado num código (HMAC) com chave exclusiva do seu tenant, fora do banco de dados. O certificado A1 e segredos são guardados cifrados.'],
      ['Onde e por quanto tempo', 'Os dados são processados no Brasil. Resultados fiscais são mantidos pelo prazo legal de guarda; XMLs, só quando contratado e pelo prazo escolhido; registros de acesso, pelo prazo do Marco Civil da Internet.'],
      ['Direitos do titular', 'O cliente atende pedidos de titulares pelo portal (localizar e expurgar por CPF). Para dados da sua conta, fale com o encarregado pelo canal indicado no contrato.'],
      ['Segurança', 'Isolamento por cliente no banco de dados, cifra por envelope com chave por cliente, verificação em duas etapas obrigatória, trilha de auditoria de acessos e ações administrativas.'],
      ['Compartilhamento', 'Com provedores necessários à operação (infraestrutura em nuvem no Brasil, envio de e-mail e processamento de pagamento), sob contrato e só no necessário. Não vendemos dados.'],
    ],
  },
} as const;

export function LegalPage({ doc }: { doc: keyof typeof DOCS }) {
  const d = DOCS[doc];
  useEffect(() => { document.title = `${d.title} · TaxGateway`; }, [d.title]);
  return (
    <div className="site help">
      <SiteHeader />
      <main id="conteudo" className="help-layout" style={{ gridTemplateColumns: '1fr' }}>
        <article className="help-article">
          <div className="eyebrow">Documento legal</div>
          <h1>{d.title}</h1>
          <div className="help-callout warning"><div><strong>Versão preliminar.</strong> Este texto descreve fielmente o funcionamento do serviço, mas ainda passa por revisão jurídica antes do lançamento comercial.</div></div>
          {d.sections.map(([t, body]) => (
            <section key={t}><h2>{t}</h2><p>{body}</p></section>
          ))}
          <p className="muted small">Veja também: <Link to={doc === 'termos' ? '/privacidade' : '/termos'}>{doc === 'termos' ? 'Política de Privacidade' : 'Termos de Uso'}</Link>.</p>
        </article>
      </main>
      <SiteFooter />
    </div>
  );
}
