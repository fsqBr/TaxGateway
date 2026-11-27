import type { ReactNode } from 'react';
import { Link, Route, Routes } from 'react-router-dom';
import { Bell, Landmark, ScrollText, TrendingUp, Building2, Calculator, CreditCard, DownloadCloud, FileCheck2, FileSearch, Gauge, KeyRound, Layers, ListTree, UserRoundCheck, Users, Webhook, Percent, ShieldCheck } from 'lucide-react';
import { PlanoCobranca } from './Cobranca';
import { Shell, podeVer, type NavItem } from '../components/Shell';
import { Empresa } from './Empresa';
import { Aliquotas } from './Aliquotas';
import { PageHeader, useLoad } from '../components/ui';
import { portal } from '../lib/api';
import { Overview, Simular, Auditar, Lotes } from './Operacao';
import { ApiKeys, Webhooks } from './Integracao';
import { Cnpjs, Dfe, Titulares } from './Cadastros';
import { ContestarTitularidade, Equipe, MinhaConta } from './Conta';
import { useSession } from '../auth/session';
import { Documentos, Dossie } from './Documentos';
import { Apuracao, PrecoMargem } from './Apuracao';
import { ClientesOAuth, LogChamadas } from './Credenciais';
import { Classificacao, ClassificacaoEmMassa } from './Classificacao';
import { Avisos, AvisosBell } from './Avisos';

export interface Me {
  tenant_id: string;
  nome: string;
  cnpj_raiz: string;
  plano: string;
  situacao: string;
  credencial: string;
  scopes: string[];
  auto_provisionar_cnpj: boolean;
  limites: { rpm: number; concorrencia: number };
}

// Menu por TAREFA do cliente (D-R25). Cada item só aparece para quem tem o escopo (papel da pessoa, D-U05), e a rota
// também é protegida (antes o menu escondia, mas a página abria e dava 403).
const nav: NavItem[] = [
  { to: '/portal', label: 'Visão geral', icon: Gauge },
  { to: '/portal/apuracao', label: 'Apuração do mês', icon: Landmark, section: 'Apurar', scope: ['auditoria:read', 'auditoria:write'] },
  { to: '/portal/simular', label: 'Simular operação', icon: Calculator, section: 'Apurar', scope: 'simulacao:write' },
  { to: '/portal/preco-margem', label: 'Preço e margem', icon: TrendingUp, section: 'Apurar' },
  { to: '/portal/auditoria', label: 'Enviar XML', icon: FileCheck2, section: 'Conferir notas', scope: 'auditoria:write' },
  { to: '/portal/lotes', label: 'Lotes (ZIP)', icon: Layers, section: 'Conferir notas', scope: 'auditoria:write' },
  { to: '/portal/documentos', label: 'Notas auditadas', icon: FileSearch, section: 'Conferir notas', scope: ['auditoria:read', 'auditoria:write'] },
  { to: '/portal/dfe', label: 'Notas de fornecedores (DF-e)', icon: DownloadCloud, section: 'Conferir notas', scope: 'dfe:manage' },
  { to: '/portal/aliquotas', label: 'Alíquotas e exceções', icon: Percent, section: 'Consultas', scope: ['auditoria:read', 'auditoria:write', 'simulacao:write'] },
  { to: '/portal/classificacao', label: 'Classificação (NCM/NBS)', icon: ListTree, section: 'Consultas', scope: 'simulacao:write' },
  { to: '/portal/api-keys', label: 'Credenciais (API e OAuth)', icon: KeyRound, section: 'Integrar', scope: 'keys:manage' },
  { to: '/portal/webhooks', label: 'Webhooks', icon: Webhook, section: 'Integrar', scope: 'webhooks:manage' },
  { to: '/portal/logs', label: 'Log de chamadas', icon: ScrollText, section: 'Integrar', scope: ['keys:manage', 'webhooks:manage'] },
  { to: '/portal/empresa', label: 'Dados e verificação', icon: ShieldCheck, section: 'Empresa', scope: ['cnpjs:manage', 'billing:manage'] },
  { to: '/portal/cnpjs', label: 'CNPJs', icon: Building2, section: 'Empresa', scope: 'cnpjs:manage' },
  { to: '/portal/titulares', label: 'LGPD: titulares', icon: UserRoundCheck, section: 'Empresa', scope: 'titulares:manage' },
  { to: '/portal/equipe', label: 'Equipe', icon: Users, section: 'Conta', scope: 'membros:manage' },
  { to: '/portal/cobranca', label: 'Plano e cobrança', icon: CreditCard, section: 'Conta', scope: 'billing:manage' },
  { to: '/portal/avisos', label: 'Avisos', icon: Bell, section: 'Conta' },
];

/** Rota protegida pelo papel: sem o escopo, explica em vez de abrir uma tela que só daria erro. */
function Guard({ scope, children }: { scope: string | string[]; children: ReactNode }) {
  const { can } = useSession();
  if (podeVer(scope, can)) return <>{children}</>;
  return (
    <>
      <PageHeader title="Sem acesso a esta área" description="O seu papel na empresa não inclui esta função. Peça a quem administra a conta (dono ou administrador) para ajustar o seu papel em Equipe." />
      <Link to="/portal" className="button">Ir para a visão geral</Link>
    </>
  );
}


/** Conta sem empresa (convite pendente ou CNPJ já cadastrado por outra pessoa, D-R10). */
function SemEmpresa() {
  return (
    <>
      <PageHeader title="Sua conta ainda não tem empresa" description="Peça um convite a quem administra a empresa no TaxGateway. Se você é o titular e o CNPJ foi cadastrado por outra pessoa, conteste abaixo com o e-CNPJ." />
      <ContestarTitularidade />
    </>
  );
}

export default function PortalApp() {
  const { sessao } = useSession();
  const semEmpresa = !!sessao && (sessao.empresas?.length ?? 0) === 0;
  const me = useLoad(() => (semEmpresa ? Promise.resolve(undefined) : portal<Me>('/v1/me')), [semEmpresa]);
  return (
    <Shell realm="portal" nav={nav} topbar={<AvisosBell />}>
      <Routes>
        <Route index element={semEmpresa ? <SemEmpresa /> : <Overview me={me.data} />} />
        <Route path="simular" element={<Guard scope="simulacao:write">{me.data ? <Simular me={me.data} /> : null}</Guard>} />
        <Route path="auditoria" element={<Guard scope="auditoria:write"><Auditar me={me.data} /></Guard>} />
        <Route path="documentos" element={<Guard scope={['auditoria:read', 'auditoria:write']}><Documentos /></Guard>} />
        <Route path="classificacao" element={<Guard scope="simulacao:write"><Classificacao /><div style={{ height: 16 }} /><ClassificacaoEmMassa /></Guard>} />
        <Route path="aliquotas" element={<Guard scope={['auditoria:read', 'auditoria:write', 'simulacao:write']}><Aliquotas /></Guard>} />
        <Route path="avisos" element={<Avisos />} />
        <Route path="lotes" element={<Guard scope="auditoria:write"><Lotes /></Guard>} />
        <Route path="api-keys" element={<Guard scope="keys:manage"><ApiKeys me={me.data} /><div style={{ height: 16 }} /><ClientesOAuth /></Guard>} />
        <Route path="logs" element={<Guard scope={['keys:manage', 'webhooks:manage']}><LogChamadas /></Guard>} />
        <Route path="apuracao" element={<Guard scope={['auditoria:read', 'auditoria:write']}><Apuracao /></Guard>} />
        <Route path="preco-margem" element={<PrecoMargem />} />
        <Route path="documentos/:id/dossie" element={<Guard scope={['auditoria:read', 'auditoria:write']}><Dossie /></Guard>} />
        <Route path="webhooks" element={<Guard scope="webhooks:manage"><Webhooks /></Guard>} />
        <Route path="empresa" element={<Guard scope={['cnpjs:manage', 'billing:manage']}><Empresa /></Guard>} />
        <Route path="cnpjs" element={<Guard scope="cnpjs:manage"><Cnpjs /></Guard>} />
        <Route path="dfe" element={<Guard scope="dfe:manage"><Dfe /></Guard>} />
        <Route path="titulares" element={<Guard scope="titulares:manage"><Titulares /></Guard>} />
        <Route path="conta" element={<MinhaConta />} />
        <Route path="equipe" element={<Guard scope="membros:manage"><Equipe /></Guard>} />
        <Route path="cobranca" element={<Guard scope="billing:manage"><PlanoCobranca /></Guard>} />
        <Route path="*" element={<NotFound />} />
      </Routes>
    </Shell>
  );
}

function NotFound() {
  return (
    <>
      <PageHeader title="Página não encontrada" description="O endereço pode ter mudado ou você não tem acesso a esta área." />
      <Link to="/portal" className="button">Ir para a visão geral</Link>
    </>
  );
}
