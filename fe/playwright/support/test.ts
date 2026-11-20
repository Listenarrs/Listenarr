import { test as base } from '@playwright/test'
import { ApiStubs } from './api-stubs.js'
import { withPageCoverage } from './coverage.js'
import { frontendIntegrationCoverage } from '../frontend-integration/support/coverage.js'

/**
 * The `test` every frontend integration spec imports: its `api` fixture stubs the whole API before the test
 * body runs, so no request reaches a backend. With LISTENARR_COVERAGE=1, `jsCoverage` collects the V8
 * coverage of every page the test loads.
 */
export const test = base.extend<{ api: ApiStubs; jsCoverage: void }>({
  api: [
    async ({ page }, use) => {
      const api = new ApiStubs(page)
      await api.stubIntegrationDefaults()
      await use(api)
    },
    { auto: true },
  ],
  jsCoverage: [
    async ({ page }, use) => withPageCoverage(page, frontendIntegrationCoverage, use),
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
