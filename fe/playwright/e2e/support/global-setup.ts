import { chromium, type FullConfig } from '@playwright/test'
import MCR from 'monocart-coverage-reports'
import { coverageOptions } from './coverage.js'

// Before the first test: an empty coverage cache, and a Vite server that has already optimized its
// dependencies, so it doesn't reload a page in the middle of a test to apply a new optimization.
export default async function globalSetup(config: FullConfig) {
  if (process.env.LISTENARR_E2E_COVERAGE === '1') MCR(coverageOptions).cleanCache()

  const baseURL = config.projects[0]?.use.baseURL
  if (!baseURL) throw new Error('The E2E configuration has no baseURL.')
  const browser = await chromium.launch()
  try {
    const page = await browser.newPage({ baseURL })
    for (const path of ['/', '/library-import', '/settings', '/wanted']) {
      await page.goto(path)
      await page.waitForLoadState('networkidle')
    }
  } finally {
    await browser.close()
  }
}
