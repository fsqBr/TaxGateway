/// <reference types="vitest" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Mesma origem da API (D-W03): em dev o Vite encaminha as rotas da API; em produção o nginx faz o mesmo.
const api = process.env.TAXGATEWAY_API ?? 'http://127.0.0.1:5205';
const proxied = ['/v1', '/admin', '/oauth', '/openapi', '/docs', '/.well-known', '/health'];

export default defineConfig({
  plugins: [react()],
  server: { port: 5173, proxy: Object.fromEntries(proxied.map((p) => [p, { target: api, changeOrigin: false }])) },
  preview: { port: 4173, proxy: Object.fromEntries(proxied.map((p) => [p, { target: api, changeOrigin: false }])) },
  // Nada de data: URI (fontes pequenas viravam data:font e a CSP font-src 'self' bloqueava): tudo vira arquivo.
  build: { sourcemap: false, outDir: 'dist', assetsInlineLimit: 0 },
  test: { environment: 'jsdom', globals: true, setupFiles: ['./src/test/setup.ts'], css: false, include: ['src/**/*.test.{ts,tsx}'] },
});
