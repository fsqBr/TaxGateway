import { defineConfig } from '@playwright/test';

// Capturas da central de ajuda e da landing (D-W13): `npm run screenshots` com a pilha do compose no ar.
export default defineConfig({
  testDir: './screenshots',
  timeout: 240_000,
  retries: 0,
  workers: 1,
  reporter: [['list']],
  use: { headless: true },
});
