import { defineConfig } from '@playwright/test';

// E2E contra os containers nginx (público e backoffice) com a API real atrás (D-W07). Suba antes:
//   API em :5205 (Development), docker run taxgateway/web:dev em :8088 e taxgateway/web-backoffice:dev em :8089.
export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  retries: 0,
  workers: 1,
  reporter: [['list']],
  use: { headless: true, locale: 'pt-BR', timezoneId: 'America/Sao_Paulo', trace: 'retain-on-failure' },
});
