import { Route, Routes } from 'react-router-dom';
import { Building2, Calculator, DownloadCloud, FileCheck2, Gauge, KeyRound, Layers, UserRoundCheck, Webhook } from 'lucide-react';
import { Shell, type NavItem } from '../components/Shell';
import { Badge, useLoad } from '../components/ui';
import { portal } from '../lib/api';
import { cnpj } from '../lib/format';
import { Overview, Simular, Auditar, Lotes } from './Operacao';
import { ApiKeys, Webhooks } from './Integracao';
import { Cnpjs, Dfe, Titulares } from './Cadastros';

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

const nav: NavItem[] = [
  { to: '/portal', label: 'Visão geral', icon: Gauge },
  { to: '/portal/simular', label: 'Simular', icon: Calculator, section: 'Operação' },
  { to: '/portal/auditoria', label: 'Auditar XML', icon: FileCheck2, section: 'Operação' },
  { to: '/portal/lotes', label: 'Lotes', icon: Layers, section: 'Operação' },
  { to: '/portal/api-keys', label: 'API keys', icon: KeyRound, section: 'Integração' },
  { to: '/portal/webhooks', label: 'Webhooks', icon: Webhook, section: 'Integração' },
  { to: '/portal/cnpjs', label: 'CNPJs', icon: Building2, section: 'Cadastro' },
  { to: '/portal/dfe', label: 'DF-e e certificado', icon: DownloadCloud, section: 'Cadastro' },
  { to: '/portal/titulares', label: 'LGPD: titulares', icon: UserRoundCheck, section: 'Conformidade' },
];

export default function PortalApp() {
  const me = useLoad(() => portal<Me>('/v1/me'));
  const workspace = me.data && (
    <>
      <strong title={me.data.nome}>{me.data.nome}</strong>
      <span className="muted small mono">{cnpj(me.data.cnpj_raiz)}</span>
      <span><Badge tone="info">{me.data.plano}</Badge></span>
    </>
  );
  return (
    <Shell realm="portal" nav={nav} workspace={workspace}>
      <Routes>
        <Route index element={<Overview me={me.data} />} />
        <Route path="simular" element={me.data ? <Simular me={me.data} /> : null} />
        <Route path="auditoria" element={<Auditar />} />
        <Route path="lotes" element={<Lotes />} />
        <Route path="api-keys" element={<ApiKeys me={me.data} />} />
        <Route path="webhooks" element={<Webhooks />} />
        <Route path="cnpjs" element={<Cnpjs />} />
        <Route path="dfe" element={<Dfe />} />
        <Route path="titulares" element={<Titulares />} />
      </Routes>
    </Shell>
  );
}
