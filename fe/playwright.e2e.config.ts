import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './playwright/e2e',
  testMatch: '**/*.spec.ts',
  fullyParallel: false,
  workers: 1,
  reporter: 'list',
  use: {
    baseURL: process.env.LISTENARR_E2E_BASE_URL ?? 'http://localhost:4545',
    trace: 'retain-on-failure',
  },
})
