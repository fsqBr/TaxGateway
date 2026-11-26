import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { Bell } from 'lucide-react';
import { Alert, Badge, Card, Loading, PageHeader, useAction, useLoad } from '../components/ui';
import { portal } from '../lib/api';
import { dateTime } from '../lib/format';

export interface Aviso {
  id: string;
  tipo: string;
  severidade: 'info' | 'aviso' | 'critico';
  titulo: string;
  mensagem: string;
  link: string | null;
  criado_em: string;
  lido: boolean;
}

const TONE = { info: 'info', aviso: 'warn', critico: 'bad' } as const;
const SEV = { info: 'Informativo', aviso: 'Atenção', critico: 'Crítico' } as const;

/** Sino com a contagem de não lidos (D-J03). Atualiza a cada 2 min e quando a página de avisos marca como lidos. */
export function AvisosBell() {
  const [n, setN] = useState(0);
  useEffect(() => {
    let alive = true;
    const load = () => portal<{ nao_lidos: number }>('/v1/avisos', { query: { nao_lidos: true }, silent: true })
      .then((r) => { if (alive) setN(r.nao_lidos); }).catch(() => undefined);
    void load();
    const t = window.setInterval(load, 120_000);
    window.addEventListener('tg:avisos', load);
    return () => { alive = false; window.clearInterval(t); window.removeEventListener('tg:avisos', load); };
  }, []);
  return (
    <Link to="/portal/avisos" className="bell" aria-label={n > 0 ? `Avisos: ${n} não lido(s)` : 'Avisos'}>
      <Bell size={17} aria-hidden="true" />
      <span>Avisos</span>
      {n > 0 && <span className="bell-count">{n > 99 ? '99+' : n}</span>}
    </Link>
  );
}

export function Avisos() {
  const list = useLoad(() => portal<{ avisos: Aviso[]; nao_lidos: number }>('/v1/avisos'));
  const { error, run } = useAction();
  const marcar = async (id?: string) => {
    await run(() => portal(id ? `/v1/avisos/${id}/lido` : '/v1/avisos/lidos', { method: 'POST' }));
    window.dispatchEvent(new Event('tg:avisos'));
    await list.reload();
  };
  return (
    <>
      <PageHeader title="Avisos" description="Franquia, webhooks pausados, certificado A1 vencendo e mudanças de alíquota aprovadas. Os mesmos avisos vão por e-mail para os papéis responsáveis.">
        {(list.data?.nao_lidos ?? 0) > 0 && <button className="secondary" onClick={() => void marcar()}>Marcar todos como lidos</button>}
      </PageHeader>
      {list.loading && <Loading />}
      <Alert>{list.error ?? error}</Alert>
      {list.data && list.data.avisos.length === 0 && <Card><p className="muted">Nenhum aviso por enquanto. Quando algo precisar da sua atenção, aparece aqui e no sino do menu.</p></Card>}
      <div className="stack">
        {list.data?.avisos.map((a) => (
          <article key={a.id} className={`card notice${a.lido ? ' read' : ''}`} aria-label={a.titulo}>
            <div className="spread">
              <div className="row"><Badge tone={TONE[a.severidade]}>{SEV[a.severidade]}</Badge><strong>{a.titulo}</strong></div>
              <span className="muted small">{dateTime(a.criado_em)}</span>
            </div>
            <p>{a.mensagem}</p>
            <div className="row">
              {a.link && <Link to={a.link} className="button secondary">Abrir</Link>}
              {!a.lido && <button className="ghost" onClick={() => void marcar(a.id)}>Marcar como lido</button>}
            </div>
          </article>
        ))}
      </div>
    </>
  );
}
