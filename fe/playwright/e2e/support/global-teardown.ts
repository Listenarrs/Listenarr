import MCR from 'monocart-coverage-reports'
import { coverageOptions, THRESHOLDS } from './coverage.js'

// After the suite: the frontend coverage report (test-results/e2e-coverage/index.html), and with
// LISTENARR_E2E_COVERAGE_GATE=1 (the whole suite, scripts/e2e/run.mjs) a failure below THRESHOLDS.
export default async function globalTeardown() {
  const results = await MCR(coverageOptions).generate()
  if (!results) throw new Error('No E2E coverage was collected.')
  if (process.env.LISTENARR_E2E_COVERAGE_GATE !== '1') return

  const failures = Object.entries(THRESHOLDS)
    .map(([metric, minimum]) => {
      const pct = results.summary[metric as keyof typeof THRESHOLDS].pct
      return typeof pct === 'number' && pct >= minimum ? null : `${metric} ${pct}% < ${minimum}%`
    })
    .filter(Boolean)
  if (failures.length > 0) {
    throw new Error(`Library Import E2E coverage is below its thresholds: ${failures.join(', ')}.`)
  }
  console.log(
    `Library Import E2E coverage: ${Object.keys(THRESHOLDS)
      .map((metric) => `${metric} ${results.summary[metric as keyof typeof THRESHOLDS].pct}%`)
      .join(', ')}.`,
  )
}
