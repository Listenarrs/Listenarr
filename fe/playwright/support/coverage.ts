import { readdirSync } from 'node:fs'
import { join, relative } from 'node:path'
import type { Page } from '@playwright/test'
import MCR, { type CoverageReportOptions, type CoverageResults } from 'monocart-coverage-reports'

// Frontend coverage of the Playwright suites: V8 coverage from Chromium of the modules the Vite dev server
// serves from src/, mapped back to their sources. Off by default (LISTENARR_COVERAGE=1 turns it on). The report lists
// the files the tests loaded; a CI run with the coverage input merges its lcov.info with the other test jobs'
// coverage (scripts/test-coverage/aggregate.mjs). Reported, not gated.

const frontendDirectory = join(import.meta.dirname, '..', '..')

/** Unit tests, their helpers and type declarations: not app code. */
export function isAppSource(path: string) {
  return (
    /^src\/.+\.(ts|vue)$/.test(path) &&
    !/(^|\/)__tests__\//.test(path) &&
    !/\.(spec|test)\.ts$/.test(path) &&
    !path.endsWith('.d.ts')
  )
}

function listSources(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name)
    return entry.isDirectory() ? listSources(path) : [relative(frontendDirectory, path)]
  })
}

const appSources = listSources(join(frontendDirectory, 'src'))
  .map((path) => path.replaceAll('\\', '/'))
  .filter(isAppSource)
const appSourceSet = new Set(appSources)

/** The report of one suite, in fe/<outputDir>: index.html, lcov.info, coverage-summary.json. */
export function coverageOptions(name: string, outputDir: string): CoverageReportOptions {
  return {
    name,
    outputDir,
    reports: ['v8', 'console-summary', 'json-summary', 'lcovonly'],
    // The app's own modules as the dev server serves them (/src/...), not their style blocks
    // (`?vue&type=style`) or the dependencies.
    entryFilter: (entry) => {
      const url = new URL(entry.url)
      return /^\/src\/.+\.(ts|vue)$/.test(url.pathname) && !url.searchParams.has('type')
    },
    // Name each source by its path under fe/, whatever form its source map gives (a bare file name for
    // some), and keep only the app's sources.
    sourcePath: (filePath, info) => {
      for (const candidate of [filePath, info.distFile ?? '']) {
        const index = candidate.lastIndexOf('src/')
        const path = index === -1 ? '' : candidate.slice(index).replace(/[?#].*$/, '')
        if (appSourceSet.has(path)) return path
      }
      const name = filePath.split('/').pop()
      const matches = appSources.filter((source) => source.endsWith(`/${name}`))
      return matches.length === 1 ? matches[0]! : filePath
    },
    sourceFilter: (sourcePath) => appSourceSet.has(sourcePath),
  }
}

/** Empties the coverage cache before the first test (Playwright global setup). */
export function cleanCoverage(options: CoverageReportOptions) {
  MCR(options).cleanCache()
}

/** Writes the report of what the tests added (Playwright global teardown) and prints its totals. */
export async function writeCoverage(options: CoverageReportOptions): Promise<CoverageResults> {
  const results = await MCR(options).generate()
  if (!results) throw new Error(`No coverage was collected for ${options.name}.`)
  return results
}

/** LISTENARR_COVERAGE=1 (set by scripts/e2e/run.mjs --coverage) turns frontend coverage on. */
export const collectCoverage = process.env.LISTENARR_COVERAGE === '1'

/** The `jsCoverage` auto fixture both suites share: collects a test's page coverage when enabled. */
export async function withPageCoverage(
  page: Page,
  options: CoverageReportOptions,
  use: () => Promise<void>,
) {
  if (!collectCoverage) {
    await use()
    return
  }
  await collectPageCoverage(page, options, () => use())
}

/**
 * Collects the V8 coverage of every page a test loads while `run` runs. Chromium only reports the current
 * document's scripts, so the coverage is taken before each load (goto, reload) and again at the end.
 */
export async function collectPageCoverage(
  page: Page,
  options: CoverageReportOptions,
  run: () => Promise<void>,
) {
  const report = MCR(options)
  const startOptions = { resetOnNavigation: false }
  const collect = async () => {
    const entries = await page.coverage.stopJSCoverage()
    if (entries.length > 0) await report.add(entries)
  }
  const goto = page.goto.bind(page)
  const reload = page.reload.bind(page)
  page.goto = async (...args: Parameters<Page['goto']>) => {
    await collect()
    await page.coverage.startJSCoverage(startOptions)
    return goto(...args)
  }
  page.reload = async (...args: Parameters<Page['reload']>) => {
    await collect()
    await page.coverage.startJSCoverage(startOptions)
    return reload(...args)
  }
  await page.coverage.startJSCoverage(startOptions)
  await run()
  await collect()
}
