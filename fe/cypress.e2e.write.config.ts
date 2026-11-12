import { defineConfig } from 'cypress'

export default defineConfig({
  allowCypressEnv: false,
  e2e: {
    specPattern: 'cypress/e2e/write/**/*.{cy,spec}.{js,jsx,ts,tsx}',
    baseUrl: 'http://localhost:5173',
    supportFile: 'cypress/support/write.ts',
    setupNodeEvents(on) {
      on('task', {
        async resetWriteDatabase() {
          const controlUrl = process.env.LISTENARR_E2E_CONTROL_URL
          if (!controlUrl) throw new Error('The E2E database reset service is not available.')

          const response = await fetch(`${controlUrl}/reset-write-database`, { method: 'POST' })
          if (!response.ok) {
            throw new Error(`Could not reset the E2E database: ${await response.text()}`)
          }

          return null
        },
      })
    },
  },
})