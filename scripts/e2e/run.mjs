// Runs the browser E2E suite (fe/playwright/e2e): Chromium drives the frontend, served by the Vite dev
// server, which forwards every API and SignalR request to Listenarr running in Docker. No Listenarr API
// is stubbed: only the app's own outbound requests are, by the test host (tests/Listenarr.TestHost),
// which answers Audible from fixtures and every other provider with 404; the container has no DNS
// either, so the suite is deterministic and offline.
//
// The runner builds the images, generates the audiobook folders (generate-library-fixtures.sh),
// seeds the library and root folders, then runs Playwright; every test restores the seeded database
// and files first. Coverage is collected on both sides:
// - frontend: V8 coverage from Chromium, mapped to the sources, in fe/test-results/e2e-coverage/
//   (index.html; the Library Import files must reach the thresholds of fe/playwright/e2e/support/coverage.ts);
// - backend: dotnet-coverage around the test host, merged into cobertura.xml with summary.md,
//   in fe/test-results/e2e-coverage-backend/, reported for the library import code (not gated).
// The container, the images and the temporary data are removed at the end.
//
//   node scripts/e2e/run.mjs [--no-coverage] [--serve] [-- <playwright test arguments>]
//
// --serve starts the stack and waits (Ctrl+C to stop): run Playwright yourself with the variables it prints.
// Playwright arguments (a spec, --repeat-each, --grep, ...) skip the coverage gate, which needs the whole suite.
import { existsSync } from 'node:fs'
import { mkdir, readdir, rm } from 'node:fs/promises'
import { join } from 'node:path'
import {
  availablePort,
  frontendDirectory,
  playwrightCli,
  repositoryRoot,
  start,
  startListenarrStack,
  waitForExit,
} from '../lib/listenarr-stack.mjs'
import { summarizeBackendCoverage } from './backend-coverage.mjs'

const argv = process.argv.slice(2)
const separator = argv.indexOf('--')
const options = separator === -1 ? argv : argv.slice(0, separator)
const playwrightArguments = separator === -1 ? [] : argv.slice(separator + 1)
for (const option of options) {
  if (!['--no-coverage', '--serve'].includes(option)) throw new Error(`Unknown option ${option}.`)
}
const coverage = !options.includes('--no-coverage')
const serve = options.includes('--serve')

if (!existsSync(playwrightCli)) {
  throw new Error('Install frontend dependencies first with `npm ci --prefix fe --engine-strict`.')
}

// Separate directories: the frontend report tool empties its own when the suite starts.
const coverageDirectory = join(frontendDirectory, 'test-results', 'e2e-coverage')
const backendCoverageDirectory = join(frontendDirectory, 'test-results', 'e2e-coverage-backend')
const rawBackendCoverageDirectory = join(backendCoverageDirectory, 'raw')
if (coverage) {
  await rm(coverageDirectory, { recursive: true, force: true })
  await rm(backendCoverageDirectory, { recursive: true, force: true })
  await mkdir(rawBackendCoverageDirectory, { recursive: true })
}

let exitCode = 1
let stack
try {
  stack = await startListenarrStack({
    name: 'listenarr-e2e',
    seedFile: join(repositoryRoot, 'scripts', 'backend-integration', 'seed-library.json'),
    rootFolders: [
      { name: 'E2E Import', path: '/backend-integration/import', isDefault: true },
      { name: 'E2E Second', path: '/backend-integration/second' },
      { name: 'E2E Empty', path: '/backend-integration/empty' },
      { name: 'E2E Library', path: '/backend-integration/library' },
    ],
    environment: coverage ? { LISTENARR_COVERAGE_DIR: '/coverage' } : {},
    dockerRunArguments: [
      // The test host answers every IHttpClientFactory client itself; a request made any other way
      // can't resolve a host name, so it fails at once rather than reach the network.
      '--dns',
      '127.0.0.1',
      // dotnet-coverage writes its report when the host stops: give it the time.
      ...(coverage ? ['--stop-timeout', '120'] : []),
    ],
    volumes: coverage ? [`${rawBackendCoverageDirectory}:/coverage`] : [],
    async generateFixtures(seedFilesystemDirectory) {
      const generator = start(
        'bash',
        [join(repositoryRoot, 'scripts', 'e2e', 'generate-library-fixtures.sh'), seedFilesystemDirectory],
        { cwd: repositoryRoot },
      )
      await waitForExit(generator, 'Synthetic audiobook library generation')
    },
  })

  const testEnvironment = {
    ...stack.testEnvironment,
    LISTENARR_API_URL: stack.apiUrl,
    LISTENARR_E2E_WEB_PORT: String(await availablePort()),
    ...(coverage ? { LISTENARR_E2E_COVERAGE: '1' } : {}),
    ...(coverage && playwrightArguments.length === 0 ? { LISTENARR_E2E_COVERAGE_GATE: '1' } : {}),
  }

  if (serve) {
    console.log('\nListenarr E2E stack is running. In fe/, run Playwright with:\n')
    console.log(
      `env ${Object.entries(testEnvironment)
        .map(([key, value]) => `${key}=${value}`)
        .join(' ')} npx playwright test --config playwright.e2e.config.ts\n`,
    )
    console.log('Ctrl+C stops and removes the stack.')
    // A pending promise alone doesn't keep Node running: the interval does, until a signal.
    const keepAlive = setInterval(() => {}, 60_000)
    await new Promise((resolveSignal) => {
      process.once('SIGINT', resolveSignal)
      process.once('SIGTERM', resolveSignal)
    })
    clearInterval(keepAlive)
    exitCode = 0
  } else {
    const suite = start(
      process.execPath,
      [playwrightCli, 'test', '--config', 'playwright.e2e.config.ts', ...playwrightArguments],
      { cwd: frontendDirectory, env: { ...process.env, ...testEnvironment } },
    )
    try {
      await waitForExit(suite, 'The E2E suite')
      exitCode = 0
    } catch (error) {
      await stack.run(['logs', '--tail', '300', stack.containerName], 'Listenarr container logs after the E2E suite failed')
      console.error(error.message)
    }
  }

  if (coverage) {
    // The last process lifetime's report is written when the host stops.
    await stack.run(['stop', stack.containerName], 'Listenarr container stop for the coverage report')
    const reports = (await readdir(rawBackendCoverageDirectory)).filter((file) => file.endsWith('.xml'))
    if (reports.length === 0) {
      console.error('No backend coverage report was written.')
    } else {
      await stack.run(
        [
          'run',
          '--rm',
          '--user',
          `${process.getuid?.() ?? 0}:${process.getgid?.() ?? 0}`,
          '--entrypoint',
          '/opt/dotnet-coverage/dotnet-coverage',
          '--volume',
          `${backendCoverageDirectory}:/coverage`,
          stack.hostImage,
          'merge',
          '--output-format',
          'cobertura',
          '--output',
          '/coverage/cobertura.xml',
          '/coverage/raw/*.xml',
        ],
        'Backend coverage merge',
      )
      await rm(rawBackendCoverageDirectory, { recursive: true, force: true })
      await summarizeBackendCoverage(join(backendCoverageDirectory, 'cobertura.xml'), backendCoverageDirectory)
    }
  }
} finally {
  await stack?.dispose()
}

process.exitCode = exitCode
