import { useState } from 'react';
import { ConfirmButton } from '../components/ConfirmDialog';
import { Alert, Card, Field, useAction } from '../components/ui';
import { backoffice } from '../lib/api';
import { integer } from '../lib/format';

export interface Limites { rpm?: number; burst?: number; concurrency?: number; batch_max_docs?: number; batch_max_bytes?: number }

const MB = 1024 * 1024;
const CAMPOS: { k: keyof Limites; label: string; hint: string }[] = [
  { k: 'rpm', label: 'Requisições por minuto', hint: 'Ritmo sustentado.' },
  { k: 'burst', label: 'Rajada', hint: 'Requisições extras aceitas num pico curto.' },
  { k: 'concurrency', label: 'Requisições simultâneas', hint: 'Quantas ao mesmo tempo.' },
  { k: 'batch_max_docs', label: 'Documentos por lote', hint: 'Máximo de notas num lote.' },
  { k: 'batch_max_bytes', label: 'Tamanho do lote (MB)', hint: 'Tamanho máximo de um lote.' },
];

const paraTexto = (k: keyof Limites, v?: number) => (v == null ? '' : k === 'batch_max_bytes' ? String(Math.round((v / MB) * 100) / 100) : String(v));

function resumo(l?: Limites | null): string {
  const partes = CAMPOS.filter((c) => l?.[c.k] != null).map((c) => `${c.label.toLowerCase()}: ${c.k === 'batch_max_bytes' ? `${paraTexto(c.k, l![c.k])} MB` : integer(l![c.k]!)}`);
  return partes.length ? partes.join(' · ') : 'limites do plano';
}

/**
 * Limites negociados em contrato para um cliente (sobrepõem os do plano). Campo vazio = vale o do plano;
 * "Voltar aos limites do plano" apaga todos. Exige admin:commercial (quem chama já confere).
 */
export function LimitesContrato({ tenantId, nome, atuais }: { tenantId: string; nome: string; atuais?: Limites | null }) {
  const [vigentes, setVigentes] = useState<Limites | null | undefined>(atuais);
  const [form, setForm] = useState<Record<string, string>>(() => Object.fromEntries(CAMPOS.map((c) => [c.k, paraTexto(c.k, atuais?.[c.k])])));
  const [msg, setMsg] = useState<string>();
  const [invalido, setInvalido] = useState<string>();
  const { busy, error, run } = useAction();

  const enviar = async (body: Limites) => {
    setMsg(undefined);
    const r = await run(() => backoffice<{ tenant_id: string; limites: Limites | null }>(`/admin/v1/tenants/${tenantId}/limites`, { method: 'PUT', body }));
    if (!r) return;
    setVigentes(r.limites ?? {});
    setForm(Object.fromEntries(CAMPOS.map((c) => [c.k, paraTexto(c.k, r.limites?.[c.k])])));
    setMsg(Object.keys(body).length ? 'Limites de contrato salvos; valem em até 5 segundos.' : 'O cliente voltou aos limites do plano.');
  };

  const salvar = () => {
    const body: Limites = {};
    for (const c of CAMPOS) {
      const t = form[c.k].trim().replace(',', '.');
      if (!t) continue;
      const v = c.k === 'batch_max_bytes' ? Math.round(Number(t) * MB) : Number(t);
      if (!Number.isFinite(v) || v <= 0 || (c.k !== 'batch_max_bytes' && !Number.isInteger(v))) { setInvalido(`${c.label}: use um número inteiro maior que zero.`); return; }
      body[c.k] = v;
    }
    setInvalido(undefined);
    if (Object.keys(body).length === 0) { setInvalido('Preencha ao menos um campo, ou use “Voltar aos limites do plano”.'); return; }
    void enviar(body);
  };

  return (
    <Card title="Limites de contrato" description="Sobrepõem os limites do plano só para este cliente. Campo em branco = vale o do plano. Fica na trilha.">
      <div className="stack">
        <p className="small">Em vigor: <strong>{resumo(vigentes)}</strong> <span className="muted">· Salvar grava exatamente os campos preenchidos.</span></p>
        <div className="form-grid">
          {CAMPOS.map((c) => (
            <Field key={c.k} label={c.label} hint={c.hint}>
              <input inputMode="numeric" placeholder="do plano" value={form[c.k]} onChange={(e) => setForm({ ...form, [c.k]: e.target.value })} />
            </Field>
          ))}
        </div>
        <Alert>{invalido ?? error}</Alert>
        <Alert kind="success">{msg}</Alert>
        <div className="row">
          <button type="button" className="secondary" disabled={busy} onClick={salvar}>Salvar limites</button>
          <ConfirmButton label="Voltar aos limites do plano" tone="secondary" disabled={busy} title={`Voltar ${nome} aos limites do plano?`}
            impact="Os limites de contrato deste cliente são apagados e passam a valer os do plano, em até 5 segundos." confirmLabel="Voltar ao plano" onConfirm={() => enviar({})} />
        </div>
      </div>
    </Card>
  );
}
