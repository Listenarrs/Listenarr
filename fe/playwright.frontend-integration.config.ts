import { defineConfig, devices } from '@playwright/test'

// Frontend integration tests: the frontend alone in a browser, every API response stubbed
// (playwright/support/api-stubs.ts). LISTENARR_COVERAGE=1
// also writes the frontend coverage to test-results/frontend-integration-coverage/.
const coverage = process.env.LISTENARR_COVERAGE === '1'

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
    baseURL: 'http://127.0.0.1:5173',
    trace: 'retain-on-failure',
  },
  webServer: {
    command: 'npm run dev -- --host 127.0.0.1 --port 5173 --strictPort',
    url: 'http://127.0.0.1:5173',
    // Always this checkout's own server: reusing whatever listens on the port could test another branch.
    reuseExistingServer: false,
    timeout: 120_000,
  },
})
