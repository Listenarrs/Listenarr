import { defineConfig, devices } from '@playwright/test'

// Browser tests of the frontend alone: every API response is stubbed (playwright/support/api-stubs.ts).
export default defineConfig({
  testDir: './playwright/integration',
  testMatch: '**/*.spec.ts',
  outputDir: './test-results/integration',
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
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
})
