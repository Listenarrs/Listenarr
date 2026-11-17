import { writeCoverage } from '../../support/coverage.js'
import { frontendIntegrationCoverage } from './coverage.js'

// After the suite: the coverage report in test-results/frontend-integration-coverage/ (index.html, lcov.info).
export default async function globalTeardown() {
  await writeCoverage(frontendIntegrationCoverage)
}
