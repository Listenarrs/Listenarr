import { writeCoverage } from '../../support/coverage.js'
import { e2eCoverage } from './coverage.js'

// After the suite: the frontend coverage report in test-results/e2e-coverage/ (index.html, lcov.info).
// Reported, not gated.
export default async function globalTeardown() {
  await writeCoverage(e2eCoverage)
}
