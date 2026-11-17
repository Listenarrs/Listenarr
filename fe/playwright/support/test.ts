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

/**
 * The library as the app shows it: on `/` it replaces the URL with this one, which changes the route name,
 * and App.vue's page transition then mounts a second view while the first leaves. Open this URL so that a
 * test never acts during that swap.
 */
export const LIBRARY_PATH = '/audiobooks?group=books'

export { expect } from '@playwright/test'
export * from './api-stubs.js'
