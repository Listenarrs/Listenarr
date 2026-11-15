import { defineConfig } from '@playwright/test'

// Full-stack tests: API requests against the application running in Docker, no browser.
// scripts/e2e/run-e2e.mjs starts that container and runs one project after the other.
export default defineConfig({
  testDir: './playwright/e2e',
  testMatch: '**/*.spec.ts',
  outputDir: './test-results/e2e',
  // One container and one database: the tests run one at a time.
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  reporter: 'list',
  use: {
    baseURL: process.env.LISTENARR_E2E_BASE_URL ?? 'http://localhost:4545',
  },
  projects: [
    { name: 'read-only', testDir: './playwright/e2e/read-only' },
    { name: 'write', testDir: './playwright/e2e/write' },
  ],
})
