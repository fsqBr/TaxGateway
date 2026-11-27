/// <reference types="vitest" />
import { execSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { defineConfig, type Plugin } from 'vite';
import react from '@vitejs/plugin-react';
import { parseChangelog } from './src/lib/changelog';

// Mesma origem da API (D-W03): em dev o Vite encaminha as rotas da API; em produção o nginx faz o mesmo.
const api = process.env.TAXGATEWAY_API ?? 'http://127.0.0.1:5205';
const proxied = ['/v1', '/admin', '/oauth', '/openapi', '/docs', '/.well-known', '/health', '/version'];

// Versão única do produto (D-W11): ../VERSION e ../CHANGELOG.md (a imagem Docker usa a raiz do repo como contexto).
const root = resolve(__dirname, '..');
const read = (f: string) => (existsSync(resolve(root, f)) ? readFileSync(resolve(root, f), 'utf8') : '');
const version = read('VERSION').trim() || JSON.parse(readFileSync(resolve(__dirname, 'package.json'), 'utf8')).version;
const commit = (process.env.BUILD_COMMIT || gitCommit()).slice(0, 12);
const builtAt = new Date().toISOString();

function gitCommit(): string {
  try {
    return execSync('git rev-parse HEAD', { cwd: root, stdio: ['ignore', 'pipe', 'ignore'] }).toString().trim();
  } catch {
    return '';
  }
}

/** /version.json no build: o portal compara com a própria versão e avisa quando sai uma nova. */
const versionFile = (): Plugin => ({
  name: 'taxgateway-version-file',
  apply: 'build',
  generateBundle() {
    this.emitFile({ type: 'asset', fileName: 'version.json', source: JSON.stringify({ version, commit, builtAt }) });
  },
});

export default defineConfig({
  plugins: [react(), versionFile()],
  define: {
    __APP_VERSION__: JSON.stringify(version),
    __APP_COMMIT__: JSON.stringify(commit),
    __APP_BUILT_AT__: JSON.stringify(builtAt),
    __CHANGELOG__: JSON.stringify(parseChangelog(read('CHANGELOG.md'))),
  },
  server: { host: '127.0.0.1', port: 5173, proxy: Object.fromEntries(proxied.map((p) => [p, { target: api, changeOrigin: false }])) },
  preview: { host: '127.0.0.1', port: 4173, proxy: Object.fromEntries(proxied.map((p) => [p, { target: api, changeOrigin: false }])) },
  // Nada de data: URI (fontes pequenas viravam data:font e a CSP font-src 'self' bloqueava): tudo vira arquivo.
  build: { sourcemap: false, outDir: 'dist', assetsInlineLimit: 0 },
  test: {
    environment: 'jsdom', globals: true, setupFiles: ['./src/test/setup.ts'], css: false, include: ['src/**/*.test.{ts,tsx}'],
    // Meta do projeto: ≥ 90% em todas as frentes (o CI roda com --coverage e falha abaixo disto).
    coverage: {
      provider: 'v8', include: ['src/**'], exclude: ['src/test/**', 'src/main.tsx', 'src/**/*.d.ts'],
      thresholds: { lines: 90, statements: 90, branches: 90, functions: 90 },
    },
  },
});
