import { defineConfig } from '@playwright/test'

// Backend integration tests: API requests against the application running in Docker, no browser.
// scripts/backend-integration/run.mjs starts that container and runs one project after the other.
export default defineConfig({
  testDir: './playwright/backend-integration',
  testMatch: '**/*.spec.ts',
  outputDir: './test-results/backend-integration',
  // One container and one database: the tests run one at a time.
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  reporter: 'list',
  use: {
    baseURL: process.env.LISTENARR_BACKEND_INTEGRATION_BASE_URL ?? 'http://localhost:4545',
  },
  projects: [
    { name: 'read-only', testDir: './playwright/backend-integration/read-only' },
    { name: 'write', testDir: './playwright/backend-integration/write' },
  ],
})
