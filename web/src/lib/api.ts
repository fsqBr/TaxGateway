// Cliente HTTP do portal e do backoffice (D-W03, D-U01). Mesma origem da API, sem CORS e sem token em URL.
// Pessoas entram com conta (e-mail, senha e MFA): a sessão é um cookie HttpOnly que o JavaScript não lê. Toda
// requisição leva o header anti-CSRF; API keys ficam para os sistemas (ERP → API), nunca no navegador.

export type Realm = 'portal' | 'backoffice';

export const CSRF_HEADER = 'X-TaxGateway-Csrf';

/** Rotas de conta de cada área. */
export const ACCOUNT_BASE: Record<Realm, string> = { portal: '/v1/conta', backoffice: '/admin/v1/conta' };
export const LOGIN_PATH: Record<Realm, string> = { portal: '/portal/entrar', backoffice: '/backoffice/entrar' };
export const MFA_PATH: Record<Realm, string> = { portal: '/portal/mfa', backoffice: '/backoffice/mfa' };

/** Erro no formato application/problem+json da API (campo `code` estável, ex.: ERR_FORBIDDEN_SCOPE). */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    public readonly title: string,
    public readonly detail?: string,
  ) {
    super(detail ? `${title}: ${detail}` : title);
  }
}

export interface RequestOptions {
  method?: string;
  body?: unknown;
  rawBody?: BodyInit;
  contentType?: string;
  query?: Record<string, string | number | boolean | undefined | null>;
  idempotent?: boolean;
  /** Não avisar a aplicação em 401/MFA (ex.: a própria checagem de sessão). */
  silent?: boolean;
}

/** Evento global: a sessão acabou (401) ou falta o MFA (403 ERR_MFA_NECESSARIO). O app leva para a tela certa. */
export const AUTH_EVENT = 'tg:auth';
export interface AuthEventDetail { realm: Realm; code: string }

export async function request<T = unknown>(realm: Realm, path: string, opts: RequestOptions = {}): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json', [CSRF_HEADER]: '1' };
  let body: BodyInit | undefined;
  if (opts.rawBody !== undefined) {
    body = opts.rawBody;
    if (opts.contentType) headers['Content-Type'] = opts.contentType;
  } else if (opts.body !== undefined) {
    body = JSON.stringify(opts.body);
    headers['Content-Type'] = 'application/json';
  }
  if (opts.idempotent) headers['Idempotency-Key'] = crypto.randomUUID();

  const qs = opts.query
    ? '?' +
      Object.entries(opts.query)
        .filter(([, v]) => v !== undefined && v !== null && v !== '')
        .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`)
        .join('&')
    : '';
  const resp = await fetch(path + (qs === '?' ? '' : qs), { method: opts.method ?? 'GET', headers, body, credentials: 'same-origin' });
  const text = await resp.text();
  const json = text && (resp.headers.get('content-type') ?? '').includes('json') ? safeParse(text) : undefined;
  if (!resp.ok) {
    const p = (json ?? {}) as { code?: string; title?: string; detail?: string; error?: string; error_description?: string };
    const err = new ApiError(resp.status, p.code ?? p.error ?? `HTTP_${resp.status}`, p.title ?? p.error_description ?? (resp.statusText || `Erro ${resp.status} do servidor`), p.detail);
    if (!opts.silent && (resp.status === 401 || err.code === 'ERR_MFA_NECESSARIO')) {
      window.dispatchEvent(new CustomEvent<AuthEventDetail>(AUTH_EVENT, { detail: { realm, code: err.code } }));
    }
    throw err;
  }
  return (json ?? (text as unknown)) as T;
}

function safeParse(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return undefined;
  }
}

export const portal = <T = unknown>(path: string, opts?: RequestOptions) => request<T>('portal', path, opts);
export const backoffice = <T = unknown>(path: string, opts?: RequestOptions) => request<T>('backoffice', path, opts);
export const account = <T = unknown>(realm: Realm, path: string, opts?: RequestOptions) => request<T>(realm, ACCOUNT_BASE[realm] + path, opts);

/** Mensagens com o que fazer, para os erros mais comuns (UX B1/C3). O código fica disponível em `codeOf`. */
const FRIENDLY: Record<string, string> = {
  ERR_CNPJ_NOT_PROVISIONED: 'O CNPJ desta operação não está entre os CNPJs atendidos da sua empresa. Cadastre-o em CNPJs e tente de novo.',
  ERR_FORBIDDEN_SCOPE: 'Seu acesso não permite esta ação. Peça a quem administra a conta.',
  ERR_TENANT_SUSPENDED: 'A conta da empresa está suspensa. Fale com o suporte do TaxGateway.',
  ERR_SEM_EMPRESA: 'Escolha a empresa no menu lateral para continuar.',
  ERR_CREDENCIAIS: 'E-mail ou senha incorretos.',
  ERR_TETO_EXCEDENTE: 'A franquia do mês acabou e o teto de excedente foi atingido. Aumente o teto em Plano e cobrança ou troque de plano.',
  ERR_FRANQUIA_ESGOTADA: 'A franquia do mês acabou. Troque de plano em Plano e cobrança para continuar.',
  ERR_PAGAMENTO_PENDENTE: 'Há um pagamento pendente: cálculos e auditorias estão bloqueados. Regularize em Plano e cobrança.',
  ERR_EMPRESA_NAO_VERIFICADA: 'Verifique a empresa em Plano e cobrança (certificado e-CNPJ ou código no e-mail do cadastro da Receita).',
};

/** Mensagem legível para o usuário a partir de qualquer erro. */
export function describeError(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.status === 401 && e.code !== 'ERR_CREDENCIAIS') return 'Sua sessão terminou. Entre novamente.';
    if (e.status === 429) return 'Muitas requisições. Aguarde alguns segundos e tente de novo.';
    if (FRIENDLY[e.code]) return FRIENDLY[e.code];
    return e.detail ? `${e.title}. ${e.detail}` : e.title;
  }
  return e instanceof Error ? e.message : String(e);
}

export const codeOf = (e: unknown): string | undefined => (e instanceof ApiError ? e.code : undefined);

/** Baixa um arquivo de uma rota da API (CSV, relatório) com a sessão e o header anti-CSRF. */
export async function downloadFile(path: string, filename: string, query?: Record<string, string | undefined>): Promise<void> {
  const qs = query ? Object.entries(query).filter(([, v]) => v).map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(v!)}`).join('&') : '';
  const resp = await fetch(path + (qs ? `?${qs}` : ''), { credentials: 'same-origin', headers: { [CSRF_HEADER]: '1' } });
  if (!resp.ok) {
    let detail: string | undefined;
    try {
      const p = (await resp.json()) as { title?: string; detail?: string; code?: string };
      throw new ApiError(resp.status, p.code ?? `HTTP_${resp.status}`, p.title ?? 'Falha no download', p.detail);
    } catch (e) {
      if (e instanceof ApiError) throw e;
      detail = resp.statusText;
    }
    throw new ApiError(resp.status, `HTTP_${resp.status}`, 'Falha no download', detail);
  }
  const url = URL.createObjectURL(await resp.blob());
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.click();
  URL.revokeObjectURL(url);
}
