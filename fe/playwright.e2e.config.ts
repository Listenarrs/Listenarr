import { defineConfig, devices } from '@playwright/test'

// Browser E2E tests: Chromium drives the frontend (Vite dev server), whose API and SignalR requests go
// to Listenarr running in Docker; nothing is stubbed but the metadata providers (tests/Listenarr.FakeProviders).
// scripts/e2e/run.mjs (`npm run test:e2e`) starts the container and sets the variables read here.
const webPort = Number(process.env.LISTENARR_E2E_WEB_PORT ?? 5174)
const webUrl = `http://127.0.0.1:${webPort}`
const coverage = process.env.LISTENARR_COVERAGE === '1'
// Each parallel shard writes its traces and videos under its own directory.
const outputDir = process.env.LISTENARR_E2E_OUTPUT_DIR ?? './test-results/e2e'

export default defineConfig({
  testDir: './playwright/e2e',
  testMatch: '**/*.spec.ts',
  outputDir,
  // One container and one database per shard: the tests of a shard run one at a time, each from the
  // restored seed. fullyParallel only makes --shard split by test rather than by file, so one long spec
  // file does not hold up a whole shard.
  fullyParallel: true,
  workers: 1,
  forbidOnly: !!process.env.CI,
  timeout: 120_000,
  expect: { timeout: 15_000 },
  reporter: 'list',
  globalSetup: './playwright/e2e/support/global-setup.ts',
  ...(coverage ? { globalTeardown: './playwright/e2e/support/global-teardown.ts' } : {}),
  use: {
    ...devices['Desktop Chrome'],
    baseURL: webUrl,
    trace: 'retain-on-failure',
    video: 'retain-on-failure',
  },
  webServer: {
    command: `npm run dev -- --host 127.0.0.1 --port ${webPort} --strictPort`,
    url: webUrl,
    reuseExistingServer: false,
    // Same-origin API and SignalR, through the dev server's proxy to the container, as in production;
    // .env.development points them at localhost:4545 instead.
    env: { ...(process.env as Record<string, string>), VITE_API_BASE_URL: '/api' },
    timeout: 120_000,
  },
})
