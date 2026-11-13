import { defineConfig } from 'cypress'

export default defineConfig({
  allowCypressEnv: false,
  e2e: {
    specPattern: 'cypress/e2e/read-only/**/*.{cy,spec}.{js,jsx,ts,tsx}',
    baseUrl: process.env.LISTENARR_E2E_BASE_URL ?? 'http://localhost:4545',
    supportFile: 'cypress/support/e2e.ts',
  },
})
