import { coverageOptions } from '../../support/coverage.js'

/** LISTENARR_COVERAGE=1 collects the frontend coverage. */
export const collectCoverage = process.env.LISTENARR_COVERAGE === '1'

export const frontendIntegrationCoverage = coverageOptions(
  'Listenarr frontend integration coverage',
  './test-results/frontend-integration-coverage',
)
