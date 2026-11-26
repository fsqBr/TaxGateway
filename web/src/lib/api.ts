// Cliente HTTP do portal e do backoffice (D-W03). Mesma origem da API: sem CORS e sem token em URL.
// A credencial fica em sessionStorage (some ao fechar a aba) e vai só em header; nunca em localStorage.

export type Realm = 'portal' | 'backoffice';

const STORAGE: Record<Realm, string> = { portal: 'tg.portal.key', backoffice: 'tg.backoffice.key' };
const HEADER: Record<Realm, string> = { portal: 'X-Api-Key', backoffice: 'X-Admin-Key' };

export function getCredential(realm: Realm): string | null {
  try {
    return sessionStorage.getItem(STORAGE[realm]);
  } catch {
    return null;
  }
}

export function setCredential(realm: Realm, value: string | null): void {
  try {
    if (value) sessionStorage.setItem(STORAGE[realm], value);
    else sessionStorage.removeItem(STORAGE[realm]);
  } catch {
    // navegação privada sem storage: o usuário precisa entrar de novo a cada recarga
  }
}

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
  credential?: string;
}

export async function request<T = unknown>(realm: Realm, path: string, opts: RequestOptions = {}): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  const credential = opts.credential ?? getCredential(realm);
  if (credential) headers[HEADER[realm]] = credential;
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
  const resp = await fetch(path + (qs === '?' ? '' : qs), { method: opts.method ?? 'GET', headers, body, credentials: 'omit' });
  const text = await resp.text();
  const json = text && (resp.headers.get('content-type') ?? '').includes('json') ? safeParse(text) : undefined;
  if (!resp.ok) {
    const p = (json ?? {}) as { code?: string; title?: string; detail?: string; error?: string; error_description?: string };
    throw new ApiError(resp.status, p.code ?? p.error ?? `HTTP_${resp.status}`, p.title ?? p.error_description ?? resp.statusText ?? 'Erro', p.detail);
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

/** Mensagem legível para o usuário a partir de qualquer erro. */
export function describeError(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.status === 401) return 'Credencial inválida, revogada ou expirada. Entre novamente.';
    if (e.status === 429) return 'Limite do plano atingido. Tente em alguns segundos.';
    return `${e.message} (${e.code})`;
  }
  return e instanceof Error ? e.message : String(e);
}
