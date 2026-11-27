import { Link, Route, Routes } from 'react-router-dom';
import { Building2, Calculator, CreditCard, DownloadCloud, FileCheck2, FileSearch, Gauge, KeyRound, Layers, ListTree, UserRoundCheck, Users, Webhook } from 'lucide-react';
import { PlanoCobranca } from './Cobranca';
import { Shell, type NavItem } from '../components/Shell';
import { PageHeader, useLoad } from '../components/ui';
import { portal } from '../lib/api';
import { Overview, Simular, Auditar, Lotes } from './Operacao';
import { ApiKeys, Webhooks } from './Integracao';
import { Cnpjs, Dfe, Titulares } from './Cadastros';
import { ContestarTitularidade, Equipe, MinhaConta } from './Conta';
import { useSession } from '../auth/session';
import { Documentos } from './Documentos';
import { Classificacao } from './Classificacao';
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

// Cada item só aparece para quem tem o escopo (papel da pessoa, D-U05).
const nav: NavItem[] = [
  { to: '/portal', label: 'Visão geral', icon: Gauge },
  { to: '/portal/simular', label: 'Simular', icon: Calculator, section: 'Operação', scope: 'simulacao:write' },
  { to: '/portal/classificacao', label: 'Classificação (NCM/NBS)', icon: ListTree, section: 'Operação', scope: 'simulacao:write' },
  { to: '/portal/auditoria', label: 'Auditar XML', icon: FileCheck2, section: 'Operação', scope: 'auditoria:write' },
  { to: '/portal/lotes', label: 'Lotes', icon: Layers, section: 'Operação', scope: 'auditoria:write' },
  { to: '/portal/documentos', label: 'Documentos auditados', icon: FileSearch, section: 'Operação', scope: 'auditoria:read' },
  { to: '/portal/api-keys', label: 'API keys', icon: KeyRound, section: 'Integração', scope: 'keys:manage' },
  { to: '/portal/webhooks', label: 'Webhooks', icon: Webhook, section: 'Integração', scope: 'webhooks:manage' },
  { to: '/portal/cnpjs', label: 'CNPJs', icon: Building2, section: 'Cadastro', scope: 'cnpjs:manage' },
  { to: '/portal/dfe', label: 'DF-e e certificado', icon: DownloadCloud, section: 'Cadastro', scope: 'dfe:manage' },
  { to: '/portal/titulares', label: 'LGPD: titulares', icon: UserRoundCheck, section: 'Conformidade', scope: 'titulares:manage' },
  { to: '/portal/equipe', label: 'Equipe', icon: Users, section: 'Conta', scope: 'membros:manage' },
  { to: '/portal/cobranca', label: 'Plano e cobrança', icon: CreditCard, section: 'Conta', scope: 'billing:manage' },
];

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
        <Route path="simular" element={me.data ? <Simular me={me.data} /> : null} />
        <Route path="auditoria" element={<Auditar me={me.data} />} />
        <Route path="documentos" element={<Documentos />} />
        <Route path="classificacao" element={<Classificacao />} />
        <Route path="avisos" element={<Avisos />} />
        <Route path="lotes" element={<Lotes />} />
        <Route path="api-keys" element={<ApiKeys me={me.data} />} />
        <Route path="webhooks" element={<Webhooks />} />
        <Route path="cnpjs" element={<Cnpjs />} />
        <Route path="dfe" element={<Dfe />} />
        <Route path="titulares" element={<Titulares />} />
        <Route path="conta" element={<MinhaConta />} />
        <Route path="equipe" element={<Equipe />} />
        <Route path="cobranca" element={<PlanoCobranca />} />
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
