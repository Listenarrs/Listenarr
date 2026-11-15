import { defineConfig, devices } from '@playwright/test'

// Frontend integration tests: the frontend alone in a browser, every API response stubbed
// (playwright/support/api-stubs.ts).
export default defineConfig({
  testDir: './playwright/frontend-integration',
  testMatch: '**/*.spec.ts',
  outputDir: './test-results/frontend-integration',
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
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
})
