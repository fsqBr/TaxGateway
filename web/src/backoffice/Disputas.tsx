import { Alert, Badge, Card, Loading, PageHeader, Table, useAction, useLoad } from '../components/ui';
import { ConfirmButton } from '../components/ConfirmDialog';
import { backoffice } from '../lib/api';
import { cnpj, dateTime } from '../lib/format';

interface Disputa {
  id: string; cnpj: string; tenant_id: string; empresa: string; requerente: string; requerente_nome: string; certificado: string | null;
  situacao: string; criada_em: string; resolvida_por: string | null; resolvida_em: string | null;
}

const TONE: Record<string, 'warn' | 'ok' | 'neutral'> = { ABERTA: 'warn', TRANSFERIDA: 'ok', REJEITADA: 'neutral' };
const LABEL: Record<string, string> = { ABERTA: 'aberta', TRANSFERIDA: 'transferida', REJEITADA: 'rejeitada' };

/**
 * D-R10: contestações de titularidade de CNPJ. Quem contestou provou com e-CNPJ ICP-Brasil da mesma raiz;
 * transferir remove as pessoas da conta atual e torna o requerente dono — a empresa fica verificada.
 */
export function Disputas() {
  const list = useLoad(() => backoffice<{ disputas: Disputa[] }>('/admin/v1/disputas'));
  const { busy, error, run } = useAction();
  const resolver = async (id: string, acao: 'transferir' | 'rejeitar') => {
    if (await run(() => backoffice(`/admin/v1/disputas/${id}/resolver`, { method: 'POST', body: { acao } })) !== undefined) await list.reload();
  };
  return (
    <>
      <PageHeader title="Disputas de titularidade" description="Pedidos de quem provou, com o certificado e-CNPJ, ser titular de um CNPJ cadastrado por outra conta. Fale com as duas partes antes de decidir." />
      <Alert>{error ?? list.error}</Alert>
      <Card flush>
        {list.loading ? <Loading /> : (
          <Table head={['Empresa', 'CNPJ raiz', 'Requerente', 'Certificado', 'Aberta em', 'Situação', '']} empty={!list.data?.disputas.length} emptyText="Nenhuma disputa.">
            {list.data?.disputas.map((d) => (
              <tr key={d.id}>
                <td data-label="Empresa">{d.empresa}</td>
                <td data-label="CNPJ raiz" className="mono">{cnpj(d.cnpj)}</td>
                <td data-label="Requerente">{d.requerente_nome}<span className="sub">{d.requerente}</span></td>
                <td data-label="Certificado" className="mono small">{d.certificado ?? '—'}</td>
                <td data-label="Aberta em">{dateTime(d.criada_em)}</td>
                <td data-label="Situação"><Badge tone={TONE[d.situacao] ?? 'neutral'}>{LABEL[d.situacao] ?? d.situacao}</Badge>{d.resolvida_por && <span className="sub">{d.resolvida_por} · {dateTime(d.resolvida_em)}</span>}</td>
                <td>
                  {d.situacao === 'ABERTA' && (
                    <div className="row">
                      <ConfirmButton label="Transferir" tone="secondary" disabled={busy} title={`Transferir ${d.empresa} para ${d.requerente}?`} confirmLabel="Transferir empresa"
                        impact="Todas as pessoas da conta atual perdem o acesso; o requerente vira dono e a empresa fica verificada. API keys e dados continuam com a empresa."
                        onConfirm={() => resolver(d.id, 'transferir')} />
                      <ConfirmButton label="Rejeitar" tone="secondary" disabled={busy} title="Rejeitar a contestação?" confirmLabel="Rejeitar"
                        impact="A conta atual continua com a empresa." onConfirm={() => resolver(d.id, 'rejeitar')} />
                    </div>
                  )}
                </td>
              </tr>
            ))}
          </Table>
        )}
      </Card>
    </>
  );
}
