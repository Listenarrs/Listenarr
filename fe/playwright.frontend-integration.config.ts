import { defineConfig, devices } from '@playwright/test'

// Frontend integration tests: the frontend alone in a browser, every API response stubbed
// (playwright/support/api-stubs.ts). LISTENARR_COVERAGE=1
// also writes the frontend coverage to test-results/frontend-integration-coverage/.
const coverage = process.env.LISTENARR_COVERAGE === '1'
// Not Vite's 5173 (nor the next ports it falls back to), so the suite runs beside `npm run dev`.
const webPort = Number(process.env.LISTENARR_FRONTEND_INTEGRATION_WEB_PORT ?? 5190)
const webUrl = `http://127.0.0.1:${webPort}`

export default defineConfig({
  testDir: './playwright/frontend-integration',
  testMatch: '**/*.spec.ts',
  outputDir: './test-results/frontend-integration',
  ...(coverage
    ? {
        globalSetup: './playwright/frontend-integration/support/global-setup.ts',
        globalTeardown: './playwright/frontend-integration/support/global-teardown.ts',
      }
    : {}),
  workers: 4,
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  reporter: 'list',
  use: {
    ...devices['Desktop Chrome'],
    baseURL: webUrl,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  webServer: {
    command: `npm run dev -- --host 127.0.0.1 --port ${webPort} --strictPort`,
    url: webUrl,
    // Always this checkout's own server: reusing whatever listens on the port could test another branch.
    reuseExistingServer: false,
    timeout: 120_000,
  },
})
