import { coverageOptions } from '../../support/coverage.js'

export { collectCoverage } from '../../support/coverage.js'

// Each Playwright shard writes its own report; the CI coverage job merges every lcov.info.
export const e2eCoverage = coverageOptions(
  'Listenarr E2E coverage',
  process.env.LISTENARR_E2E_COVERAGE_DIR ?? './test-results/e2e-coverage',
)
