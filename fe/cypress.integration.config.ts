import { defineConfig } from 'cypress'

const defaultSpecPattern = 'cypress/integration/**/*.{cy,spec}.{js,jsx,ts,tsx}'

export default defineConfig({
  allowCypressEnv: false,
  e2e: {
    specPattern: process.env.CYPRESS_INTEGRATION_SPEC || defaultSpecPattern,
    supportFile: 'cypress/support/integration.ts',
    baseUrl: 'http://127.0.0.1:5173',
  },
})