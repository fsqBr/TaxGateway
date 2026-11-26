import { useState, type FormEvent } from 'react';
import { ConfirmButton } from '../components/ConfirmDialog';
import { Alert, Badge, Card, Field, Loading, PageHeader, Table, statusLabel, statusTone, useAction, useLoad } from '../components/ui';
import { ApiError, portal } from '../lib/api';
import { cnpj, currentMonth, dateTime, fileToBase64, integer, onlyCnpjChars } from '../lib/format';

interface CnpjRow { cnpj: string; ativo: boolean; cobranca_encerrada_a_partir_de: string | null; criado_em: string }
interface UsoCnpj { competencia: string; cnpjs_ativos: number; por_cnpj: { cnpj: string; documentos_auditados: number; simulacoes: number }[] }

export function Cnpjs() {
  const list = useLoad(() => portal<{ cnpjs: CnpjRow[] }>('/v1/oem/cnpjs'));
  const [month, setMonth] = useState(currentMonth());
  const usage = useLoad(() => portal<UsoCnpj>('/v1/oem/usage', { query: { month } }), [month]);
  const [novo, setNovo] = useState('');
  const { busy, error, run } = useAction();

  const add = async (e: FormEvent) => {
    e.preventDefault();
    if (await run(() => portal('/v1/oem/cnpjs', { method: 'POST', body: { cnpj: onlyCnpjChars(novo) } })) !== undefined) {
      setNovo('');
      void list.reload();
    }
  };

  return (
    <>
      <PageHeader title="CNPJs atendidos" description="Os CNPJs em nome dos quais você chama a API. Cada CNPJ com uso na competência entra na cobrança." />
      <Card title="Cadastrar ou reativar CNPJ">
        <form className="row" onSubmit={add}>
          <input aria-label="CNPJ" value={novo} onChange={(e) => setNovo(e.target.value)} placeholder="00.000.000/0000-00" style={{ maxWidth: 260 }} required />
          <button type="submit" disabled={busy}>Cadastrar</button>
        </form>
        <Alert>{error}</Alert>
        <p className="muted">Desativar um CNPJ recusa as próximas chamadas dele; a cobrança termina na competência seguinte.</p>
      </Card>
      <div style={{ height: 16 }} />
      <div className="grid" style={{ alignItems: 'start' }}>
        <Card title="Cadastro">
          {list.loading && <Loading />}
          <Alert>{list.error}</Alert>
          {list.data && (
            <Table head={['CNPJ', 'Situação', 'Desde', '']} empty={list.data.cnpjs.length === 0}>
              {list.data.cnpjs.map((c) => (
                <tr key={c.cnpj}>
                  <td className="mono">{cnpj(c.cnpj)}</td>
                  <td>{c.ativo ? <Badge tone="ok">ativo</Badge> : <Badge tone="bad">desativado</Badge>}</td>
                  <td>{dateTime(c.criado_em)}</td>
                  <td>
                    {c.ativo && (
                      <ConfirmButton label="Desativar" title={`Desativar o CNPJ ${cnpj(c.cnpj)}?`} confirmLabel="Desativar CNPJ"
                        impact="Chamadas em nome deste CNPJ passam a ser recusadas na hora (403). A cobrança dele termina na próxima competência. Ele só volta se for cadastrado de novo — o auto-cadastro não o reativa."
                        onConfirm={() => void run(() => portal(`/v1/oem/cnpjs/${c.cnpj}`, { method: 'DELETE' })).then(() => list.reload())} />
                    )}
                  </td>
                </tr>
              ))}
            </Table>
          )}
        </Card>
        <Card title="Uso por CNPJ" actions={<input type="month" aria-label="Competência" value={month} onChange={(e) => setMonth(e.target.value)} style={{ width: 'auto' }} />}>
          {usage.loading && <Loading />}
          <Alert>{usage.error}</Alert>
          {usage.data && (
            <Table head={['CNPJ', 'Auditorias', 'Simulações']} empty={usage.data.por_cnpj.length === 0}>
              {usage.data.por_cnpj.map((u) => (
                <tr key={u.cnpj}>
                  <td className="mono">{cnpj(u.cnpj)}</td>
                  <td className="num">{integer(u.documentos_auditados)}</td>
                  <td className="num">{integer(u.simulacoes)}</td>
                </tr>
              ))}
            </Table>
          )}
        </Card>
      </div>
    </>
  );
}

interface Cert { cnpj: string; thumbprint: string; titular: string; vence_em: string }
interface DfeStatus {
  cnpj: string; ativo: boolean; ult_nsu: number; max_nsu: number | null; proxima_consulta: string; ultimo_cstat: string | null;
  ultima_consulta: string | null; ultimo_656: string | null; manifestar_ciencia: boolean; documentos: Record<string, number>;
}

async function optional<T>(p: Promise<T>): Promise<T | null> {
  try {
    return await p;
  } catch (e) {
    if (e instanceof ApiError && e.status === 404) return null;
    throw e;
  }
}

export function Dfe() {
  const [alvo, setAlvo] = useState('');
  const [selected, setSelected] = useState<string>();
  const cert = useLoad(() => (selected ? optional(portal<Cert>(`/v1/cnpjs/${selected}/certificado`)) : Promise.resolve(null)), [selected]);
  const status = useLoad(() => (selected ? optional(portal<DfeStatus>(`/v1/cnpjs/${selected}/dfe/status`)) : Promise.resolve(null)), [selected]);
  const [pfx, setPfx] = useState<File>();
  const [senha, setSenha] = useState('');
  const [uf, setUf] = useState(35);
  const [ciencia, setCiencia] = useState(true);
  const { busy, error, run } = useAction();

  const upload = async (e: FormEvent) => {
    e.preventDefault();
    if (!pfx || !selected) return;
    const r = await run(async () => portal(`/v1/cnpjs/${selected}/certificado`, { method: 'PUT', body: { pfx_base64: await fileToBase64(pfx), senha } }));
    setSenha('');
    if (r) void cert.reload();
  };

  const sync = async () => {
    if (await run(() => portal(`/v1/cnpjs/${selected}/dfe/sync`, { method: 'POST', body: { c_uf_autor: uf, manifestar_ciencia: ciencia } })) !== undefined) {
      void status.reload();
    }
  };

  return (
    <>
      <PageHeader title="DF-e e certificado A1" description="Capture automaticamente as notas em que o CNPJ é destinatário e audite a entrada." />
      <Card>
        <form className="row" onSubmit={(e) => { e.preventDefault(); setSelected(onlyCnpjChars(alvo)); }}>
          <input aria-label="CNPJ" value={alvo} onChange={(e) => setAlvo(e.target.value)} placeholder="CNPJ do destinatário" style={{ maxWidth: 260 }} required />
          <button type="submit">Abrir</button>
        </form>
        <p className="muted">A captura busca na SEFAZ as notas em que o CNPJ é destinatário, manifesta a Ciência da Operação e audita a entrada. As regras anti-bloqueio da SEFAZ (1 h após “nenhum documento”, até 20 consultas por hora) são aplicadas automaticamente.</p>
      </Card>
      <Alert>{error}</Alert>
      {selected && (
        <div className="grid" style={{ alignItems: 'start', marginTop: 16 }}>
          <Card title={`Certificado de ${cnpj(selected)}`}>
            {cert.loading && <Loading />}
            <Alert>{cert.error}</Alert>
            {cert.data ? (
              <>
                <p><strong>{cert.data.titular}</strong></p>
                <p className="muted mono">{cert.data.thumbprint}</p>
                <p>Vence em {dateTime(cert.data.vence_em)}</p>
                <ConfirmButton label="Remover certificado" title="Remover o certificado A1?" confirmLabel="Remover certificado" typeToConfirm="REMOVER"
                  impact="A captura automática de notas (DF-e) deste CNPJ para na próxima consulta. O PFX e a senha são apagados e não podem ser recuperados — para voltar, envie o arquivo de novo."
                  onConfirm={() => void run(() => portal(`/v1/cnpjs/${selected}/certificado`, { method: 'DELETE' })).then(() => cert.reload())} />
              </>
            ) : (!cert.loading && <p className="muted">Nenhum certificado enviado.</p>)}
            <form className="stack" onSubmit={upload} style={{ marginTop: 12 }}>
              <Field label="Arquivo .pfx" hint="O PFX e a senha são guardados cifrados e nunca voltam pela API."><input type="file" accept=".pfx,.p12" onChange={(e) => setPfx(e.target.files?.[0])} /></Field>
              <Field label="Senha do certificado"><input type="password" autoComplete="off" value={senha} onChange={(e) => setSenha(e.target.value)} /></Field>
              <button type="submit" disabled={busy || !pfx || !senha}>{cert.data ? 'Substituir' : 'Enviar'}</button>
            </form>
          </Card>
          <Card title="Captura de DF-e">
            {status.loading && <Loading />}
            <Alert>{status.error}</Alert>
            {status.data ? (
              <>
                <div className="row">
                  <Badge tone={status.data.ativo ? 'ok' : 'neutral'}>{status.data.ativo ? 'ligada' : 'desligada'}</Badge>
                  {status.data.ultimo_cstat && <Badge tone={status.data.ultimo_cstat === '656' ? 'bad' : 'info'}>cStat {status.data.ultimo_cstat}</Badge>}
                </div>
                <Table head={['', '']}>
                  <tr><td>Último NSU</td><td className="num">{status.data.ult_nsu} / {status.data.max_nsu ?? '—'}</td></tr>
                  <tr><td>Última consulta</td><td>{dateTime(status.data.ultima_consulta)}</td></tr>
                  <tr><td>Próxima consulta</td><td>{dateTime(status.data.proxima_consulta)}</td></tr>
                  <tr><td>Último 656</td><td>{dateTime(status.data.ultimo_656)}</td></tr>
                </Table>
                <div className="row" style={{ marginTop: 8 }}>
                  {Object.entries(status.data.documentos).map(([k, v]) => <Badge key={k} tone={statusTone(k)}>{statusLabel(k)}: {v}</Badge>)}
                </div>
              </>
            ) : (!status.loading && <p className="muted">Captura ainda não configurada.</p>)}
            <div className="stack" style={{ marginTop: 12 }}>
              <Field label="UF do interessado (código IBGE)"><input type="number" min={11} max={53} value={uf} onChange={(e) => setUf(Number(e.target.value))} /></Field>
              <label className="checkbox"><input type="checkbox" checked={ciencia} onChange={(e) => setCiencia(e.target.checked)} /> Manifestar Ciência da Operação automaticamente</label>
              <button type="button" disabled={busy || !cert.data} onClick={() => void sync()}>{status.data ? 'Salvar configuração' : 'Ligar captura'}</button>
              {!cert.data && <span className="muted">Envie o certificado antes de ligar a captura.</span>}
            </div>
          </Card>
        </div>
      )}
    </>
  );
}

interface Localizados { documentos: { id: string; direcao: string; chave_acesso: string | null; xml_armazenado: boolean; criado_em: string }[]; documentos_expurgados?: number; xmls_apagados?: number }

export function Titulares() {
  const [cpf, setCpf] = useState('');
  const [result, setResult] = useState<Localizados>();
  const [done, setDone] = useState<string>();
  const { busy, error, run } = useAction();
  const body = () => ({ cpf: cpf.replace(/\D/g, '') });

  return (
    <>
      <PageHeader title="LGPD: direitos do titular" description="Atenda pedidos de titulares de dados (LGPD, art. 18). Você é o controlador; o TaxGateway executa como operador." />
      <Card>
        <p className="muted">Você é o controlador: use esta tela para atender pedidos de titulares (LGPD art. 18). O CPF não é guardado; buscamos pelo HMAC calculado com a chave do seu tenant. O expurgo apaga o XML e o vínculo com o CPF e mantém o registro fiscal sem dado pessoal.</p>
        <form className="row" onSubmit={async (e) => { e.preventDefault(); setDone(undefined); setResult(await run(() => portal<Localizados>('/v1/titulares/localizar', { method: 'POST', body: body() }))); }}>
          <input aria-label="CPF" value={cpf} onChange={(e) => setCpf(e.target.value)} placeholder="000.000.000-00" style={{ maxWidth: 220 }} required />
          <button type="submit" disabled={busy}>Localizar</button>
        </form>
        <Alert>{error}</Alert>
        <Alert kind="success">{done}</Alert>
      </Card>
      {result && (
        <>
          <div style={{ height: 16 }} />
          <Card
            title={`${result.documentos.length} documento(s)`}
            actions={result.documentos.length > 0 && (
              <ConfirmButton
                label="Expurgar dados deste titular"
                title="Expurgar os dados deste titular?"
                confirmLabel="Expurgar definitivamente"
                typeToConfirm="EXPURGAR"
                impact={`Apaga definitivamente ${result.documentos.filter((d) => d.xml_armazenado).length} XML(s) guardado(s) e o vínculo com o CPF em ${result.documentos.length} documento(s). Os valores fiscais e os achados continuam, sem dado pessoal. Não pode ser desfeito; o pedido fica registrado para a sua prestação de contas (LGPD).`}
                onConfirm={async () => {
                  const r = await run(() => portal<Localizados>('/v1/titulares/expurgar', { method: 'POST', body: body() }));
                  if (r) {
                    setDone(`${r.documentos_expurgados} documento(s) expurgado(s); ${r.xmls_apagados} XML(s) apagado(s).`);
                    setResult(undefined);
                  }
                }}
              />
            )}
          >
            <Table head={['Chave de acesso', 'Direção', 'XML guardado', 'Data']} empty={result.documentos.length === 0}>
              {result.documentos.map((d) => (
                <tr key={d.id}>
                  <td className="mono">{d.chave_acesso ?? '—'}</td>
                  <td>{d.direcao}</td>
                  <td>{d.xml_armazenado ? 'sim' : 'não'}</td>
                  <td>{dateTime(d.criado_em)}</td>
                </tr>
              ))}
            </Table>
          </Card>
        </>
      )}
    </>
  );
}
