import { writeCoverage } from '../../support/coverage.js'
import { e2eCoverage } from './coverage.js'

// After the suite: this shard's frontend coverage report under test-results/e2e-coverage/frontend/<n>/.
// Reported, not gated.
export default async function globalTeardown() {
  await writeCoverage(e2eCoverage)
}
