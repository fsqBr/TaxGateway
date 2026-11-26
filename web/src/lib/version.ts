// Versão do app e avisos de mudança (D-W11).

export const APP_VERSION: string = typeof __APP_VERSION__ === 'string' ? __APP_VERSION__ : '0.0.0';
export const APP_COMMIT: string = typeof __APP_COMMIT__ === 'string' ? __APP_COMMIT__ : '';
export const CHANGELOG = typeof __CHANGELOG__ === 'undefined' ? [] : __CHANGELOG__;

export type ChangelogEntry = (typeof CHANGELOG)[number];

const SEEN_KEY = 'tg.versao.vista';

/** Compara versões semânticas simples (1.2.3; sufixos -rc são tratados como anteriores à final). */
export function compareVersions(a: string, b: string): number {
  const parse = (v: string) => {
    const [core, pre] = v.split('-', 2);
    return { nums: core.split('.').map((n) => parseInt(n, 10) || 0), pre: pre ?? '' };
  };
  const x = parse(a);
  const y = parse(b);
  for (let i = 0; i < 3; i++) {
    const d = (x.nums[i] ?? 0) - (y.nums[i] ?? 0);
    if (d !== 0) return d < 0 ? -1 : 1;
  }
  if (x.pre === y.pre) return 0;
  if (!x.pre) return 1;
  if (!y.pre) return -1;
  return x.pre < y.pre ? -1 : 1;
}

function readSeen(): string | null {
  try {
    return localStorage.getItem(SEEN_KEY);
  } catch {
    return null;
  }
}

export function markSeen(version = APP_VERSION): void {
  try {
    localStorage.setItem(SEEN_KEY, version);
  } catch {
    // sem storage (navegação privada): o aviso pode reaparecer, sem outro efeito
  }
}

/**
 * Novidades a mostrar agora: as versões publicadas depois da última que este navegador viu, até a atual.
 * Primeira visita não mostra nada (não há "mudança" para quem acabou de chegar) e só registra a versão.
 */
export function pendingWhatsNew(current = APP_VERSION, changelog: ChangelogEntry[] = CHANGELOG, seen = readSeen()): ChangelogEntry[] {
  if (!seen) {
    markSeen(current);
    return [];
  }
  if (compareVersions(current, seen) <= 0) return [];
  return changelog.filter((e) => compareVersions(e.version, seen) > 0 && compareVersions(e.version, current) <= 0);
}

export interface DeployedVersion { version: string; commit: string; builtAt?: string }

/** Lê /version.json (gerado no build e servido sem cache). Em dev ele não existe: devolve null. */
export async function fetchDeployedVersion(signal?: AbortSignal): Promise<DeployedVersion | null> {
  try {
    const r = await fetch(`/version.json?t=${Date.now()}`, { cache: 'no-store', signal });
    if (!r.ok || !(r.headers.get('content-type') ?? '').includes('json')) return null;
    return (await r.json()) as DeployedVersion;
  } catch {
    return null;
  }
}

/** Há versão publicada diferente da que está carregada nesta aba? */
export function isNewDeploy(deployed: DeployedVersion | null, current = APP_VERSION, commit = APP_COMMIT): boolean {
  if (!deployed?.version) return false;
  if (deployed.version !== current) return true;
  return Boolean(deployed.commit && commit && deployed.commit !== commit);
}
