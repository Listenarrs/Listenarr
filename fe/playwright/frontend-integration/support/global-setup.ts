import { cleanCoverage } from '../../support/coverage.js'
import { frontendIntegrationCoverage } from './coverage.js'

// Before the first test: an empty coverage cache (the config only runs this with LISTENARR_COVERAGE=1).
export default function globalSetup() {
  cleanCoverage(frontendIntegrationCoverage)
}
