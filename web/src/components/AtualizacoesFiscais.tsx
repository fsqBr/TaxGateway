import { Alert, Badge, Loading, useLoad } from './ui';
import { portal } from '../lib/api';

// D-R30/D-R31: linha do tempo das atualizações fiscais — alíquotas que a Receita publicou (aplicadas sozinhas, com a data
// em que passam a valer) e versões novas do motor oficial. Usada no site (/aliquotas) e no portal.

export interface AtualizacaoFiscal {
  id: string;
  tipo: 'ALIQUOTA' | 'MOTOR';
  titulo: string;
  texto: string;
  vigencia?: string | null;
  origem: 'MOTOR_OFICIAL' | 'CATALOGO';
  publicado_em: string;
}

export interface AtualizacoesResposta {
  bundle: string;
  atualizacoes: AtualizacaoFiscal[];
}

const dataBr = (iso?: string | null) => {
  if (!iso) return '';
  const [y, m, d] = iso.slice(0, 10).split('-');
  return `${d}/${m}/${y}`;
};

/** Hoje no fuso de Brasília (AAAA-MM-DD), para dizer se a vigência é futura. */
const hoje = () => new Date(Date.now() - 3 * 3600_000).toISOString().slice(0, 10);

export function AtualizacoesFiscais({ limite = 10 }: { limite?: number }) {
  const { data, error, loading } = useLoad(() => portal<AtualizacoesResposta>('/v1/publico/atualizacoes-fiscais', { query: { limite }, silent: true }));
  if (loading) return <Loading />;
  if (error || !data) return <Alert kind="info">Não foi possível carregar as atualizações agora.</Alert>;
  return (
    <>
      <p className="muted small" style={{ marginBottom: 12 }}>
        O TaxGateway confere a base oficial da calculadora da Receita todos os dias e aplica sozinho o que ela publicar, respeitando
        a data em que cada alíquota passa a valer. Quem é afetado recebe aviso no portal e por e-mail. Versão do motor oficial em uso: <strong>{data.bundle}</strong>.
      </p>
      {data.atualizacoes.length === 0 ? (
        <p className="muted">Nenhuma atualização publicada até agora.</p>
      ) : (
        <ol className="help-list" aria-label="Atualizações fiscais">
          {data.atualizacoes.map((a) => (
            <li key={a.id}>
              <strong>{a.titulo}</strong>{' '}
              <Badge tone={a.tipo === 'MOTOR' ? 'info' : 'ok'}>{a.tipo === 'MOTOR' ? 'motor oficial' : 'alíquota'}</Badge>
              {a.vigencia && a.vigencia > hoje() && <> <Badge tone="warn">vale a partir de {dataBr(a.vigencia)}</Badge></>}
              <div className="small">{a.texto}</div>
              <div className="muted small">
                Publicada em {dataBr(a.publicado_em)}
                {a.vigencia && a.vigencia <= hoje() && <> · vale desde {dataBr(a.vigencia)}</>}
                {a.origem === 'MOTOR_OFICIAL' ? ' · fonte: base oficial da Receita Federal' : ' · fonte: catálogo revisado pela equipe fiscal'}
              </div>
            </li>
          ))}
        </ol>
      )}
    </>
  );
}
