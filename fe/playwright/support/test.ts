import { test as base } from '@playwright/test'
import { ApiStubs } from './api-stubs.js'

/**
 * The `test` every frontend integration spec imports: its `api` fixture stubs the whole API before the test
 * body runs, so no request reaches a backend.
 */
export const test = base.extend<{ api: ApiStubs }>({
  api: [
    async ({ page }, use) => {
      const api = new ApiStubs(page)
      await api.stubIntegrationDefaults()
      await use(api)
    },
    { auto: true },
  ],
})

export { expect } from '@playwright/test'
export * from './api-stubs.js'
