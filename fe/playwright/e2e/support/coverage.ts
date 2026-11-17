import { coverageOptions } from '../../support/coverage.js'

/** LISTENARR_COVERAGE=1 (set by scripts/e2e/run.mjs --coverage) collects the frontend coverage. */
export const collectCoverage = process.env.LISTENARR_COVERAGE === '1'

export const e2eCoverage = coverageOptions('Listenarr E2E coverage', './test-results/e2e-coverage')
