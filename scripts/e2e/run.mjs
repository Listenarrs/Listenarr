// Runs the browser E2E suite (fe/playwright/e2e): Chromium drives the frontend, served by the Vite dev
// server, which forwards every API and SignalR request to Listenarr running in Docker. No Listenarr API
// is stubbed: only the app's own outbound requests are, by tests/Listenarr.FakeProviders,
// which answers metadata from fixtures and permits only the download scenarios' loopback qBittorrent
// sidecar. The container cannot resolve external DNS names.
//
// The runner builds the images once, writes the audiobook folders once (import-folders.mjs, from
// tests/Fixtures/Audiobooks/import-folders.json),
// then starts one stack per Playwright shard, each with its own container, ports and seeded directories,
// and runs the shards in parallel. With --coverage, coverage is collected on both sides:
// - frontend: V8 coverage from Chromium, mapped to the sources, one report per shard under
//   fe/test-results/e2e-coverage/frontend/ (the CI coverage job merges every lcov.info);
// - backend: dotnet-coverage around each host, merged into one Cobertura report in
//   fe/test-results/e2e-coverage-backend/, with summary.md for the library import code.
// Both are reported, not gated; a CI run with the coverage input merges them with the other test jobs'
// (scripts/test-coverage/aggregate.mjs).
// The containers, the images of a dirty checkout and the temporary data are removed at the end; the
// images of a clean checkout are tagged with its commit and kept for the next run on it.
//
//   node scripts/e2e/run.mjs [--coverage] [--stacks=N] [--serve] [-- <playwright test arguments>]
//
// --serve keeps one stack up (no sharding): run Playwright yourself with the variables it prints.
// Playwright arguments (a spec, --repeat-each, --grep, ...) are passed on to every shard; with any, the
// run uses one stack unless --stacks=N says otherwise (a filter can leave a shard without tests).
import { execFileSync } from 'node:child_process'
import { existsSync } from 'node:fs'
import { cp, mkdir, mkdtemp, readdir, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import {
  availablePort,
  ensureImages,
  frontendDirectory,
  playwrightCli,
  removeImages,
  repositoryRoot,
  start,
  startListenarrStack,
  waitForExit,
} from '../lib/listenarr-stack.mjs'
import { writeImportFolders } from './import-folders.mjs'
import { stackCount, stackPlan } from '../lib/e2e-plan.mjs'
import { summarizeBackendCoverage } from './backend-coverage.mjs'
import { convertVideos } from './webm-to-gif.mjs'

const argv = process.argv.slice(2)
const separator = argv.indexOf('--')
const options = separator === -1 ? argv : argv.slice(0, separator)
const playwrightArguments = separator === -1 ? [] : argv.slice(separator + 1)
const stacksOption = options.find((option) => option.startsWith('--stacks='))
const flags = options.filter((option) => option !== stacksOption && !option.startsWith('--stacks'))
for (const option of flags) {
  if (!['--coverage', '--serve'].includes(option)) throw new Error(`Unknown option ${option}.`)
}
const coverage = flags.includes('--coverage')
const serve = flags.includes('--serve')
const count = stackCount({
  stacks: stacksOption?.slice('--stacks='.length),
  environmentStacks: process.env.LISTENARR_E2E_STACKS,
  serve,
  coverage,
  playwrightArguments,
})

if (!existsSync(playwrightCli)) {
  throw new Error('Install frontend dependencies first with `npm ci --prefix fe --engine-strict`.')
}

const git = (...args) => execFileSync('git', args, { cwd: repositoryRoot, encoding: 'utf8' }).trim()
const head = git('rev-parse', 'HEAD')
const dirty = git('status', '--porcelain').length > 0
const runId = `${process.pid}-${Date.now().toString(36)}`

/** Distinct host ports for every stack's API and Vite dev server, chosen before the stacks start. */
async function allocatePorts(count) {
  const ports = new Set()
  while (ports.size < count) ports.add(await availablePort())
  return [...ports]
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

const plan = stackPlan({
  head,
  dirty,
  runId,
  count,
  coverage,
  coverageRoot: join(frontendDirectory, 'test-results'),
})
let fixtureTemplate
let stacks = []
let exitCode = 1
try {
  fixtureTemplate = await mkdtemp(join(tmpdir(), 'listenarr-e2e-fixtures-'))
  await writeImportFolders(fixtureTemplate)

  await ensureImages({ appImage: plan.appImage, hostImage: plan.hostImage, reuse: plan.reusable })
  // Chosen after the build, which can take minutes: a port is only free until a stack binds it.
  const ports = await allocatePorts(count * 2)
  const apiPorts = ports.slice(0, count)
  const webPorts = ports.slice(count)
  // Register each stack as soon as it starts, so a later shard failing cannot orphan the earlier ones.
  const startResults = await Promise.allSettled(
    plan.stacks.map(async (descriptor, index) => {
      if (descriptor.backendCoverageDirectory) await mkdir(descriptor.backendCoverageDirectory, { recursive: true })
      stacks[index] = await startListenarrStack({
        name: descriptor.name,
        apiPort: apiPorts[index],
        appImage: plan.appImage,
        hostImage: plan.hostImage,
        seedFile: join(repositoryRoot, 'scripts', 'e2e', 'seed-library.json'),
        rootFolders: [
          { name: 'E2E Import', path: '/e2e/import', isDefault: true },
          { name: 'E2E Second', path: '/e2e/second' },
          { name: 'E2E Empty', path: '/e2e/empty' },
          { name: 'E2E Library', path: '/e2e/library' },
        ],
        environment: {
          LISTENARR_E2E_QBITTORRENT: '1',
          ...(coverage ? { LISTENARR_COVERAGE_DIR: `/coverage-${descriptor.name}` } : {}),
        },
        dockerRunArguments: [
          // The test host stubs outbound clients except for the loopback sidecar.
          // Disabling DNS also isolates requests outside IHttpClientFactory from external hostnames.
          '--dns',
          '127.0.0.1',
          // dotnet-coverage writes its report when the host stops: give it the time.
          ...(coverage ? ['--stop-timeout', '120'] : []),
        ],
        volumes: descriptor.backendCoverageDirectory
          ? [`${descriptor.backendCoverageDirectory}:/coverage-${descriptor.name}`]
          : [],
        async generateFixtures(seedFilesystemDirectory) {
          await rm(seedFilesystemDirectory, { recursive: true, force: true })
          await cp(fixtureTemplate, seedFilesystemDirectory, { recursive: true })
        },
      })
    }),
  )
  const failedStart = startResults.find((result) => result.status === 'rejected')
  if (failedStart) throw failedStart.reason

  if (serve) {
    const testEnvironment = {
      ...stacks[0].testEnvironment,
      LISTENARR_API_URL: stacks[0].apiUrl,
      LISTENARR_E2E_WEB_PORT: String(webPorts[0]),
      ...(coverage
        ? {
            LISTENARR_COVERAGE: '1',
            LISTENARR_E2E_COVERAGE_DIR: plan.stacks[0].frontendCoverageDirectory,
          }
        : {}),
    }
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
    const results = await Promise.allSettled(
      stacks.map(async (stack, index) => {
        const descriptor = plan.stacks[index]
        const testEnvironment = {
          ...stack.testEnvironment,
          LISTENARR_API_URL: stack.apiUrl,
          LISTENARR_E2E_WEB_PORT: String(webPorts[index]),
          LISTENARR_E2E_OUTPUT_DIR: join(frontendDirectory, 'test-results', 'e2e', String(index)),
          LISTENARR_E2E_VITE_CACHE_DIR: join(frontendDirectory, 'node_modules', '.vite-e2e', String(index)),
          ...(coverage
            ? {
                LISTENARR_COVERAGE: '1',
                LISTENARR_E2E_COVERAGE_DIR: descriptor.frontendCoverageDirectory,
              }
            : {}),
        }
        const suite = start(
          process.execPath,
          [playwrightCli, 'test', '--config', 'playwright.e2e.config.ts', ...descriptor.shard, ...playwrightArguments],
          { cwd: frontendDirectory, env: { ...process.env, ...testEnvironment } },
        )
        try {
          await waitForExit(suite, `The E2E suite (shard ${index + 1}/${count})`)
        } catch (error) {
          await stack.run(
            ['logs', '--tail', '300', stack.containerName],
            `Listenarr container logs after E2E shard ${index + 1} failed`,
          )
          throw error
        }
      }),
    )
    const failures = results.filter((result) => result.status === 'rejected')
    if (failures.length === 0) exitCode = 0
    else failures.forEach((failure) => console.error(failure.reason.message))
  }

  if (coverage) {
    // The last process lifetime's report is written when each host stops.
    await Promise.all(
      stacks.map((stack) => stack.run(['stop', stack.containerName], 'Listenarr container stop for the coverage report')),
    )
    for (const [index, descriptor] of plan.stacks.entries()) {
      const reports = (await readdir(descriptor.backendCoverageDirectory)).filter((file) => file.endsWith('.xml'))
      for (const report of reports) {
        await cp(
          join(descriptor.backendCoverageDirectory, report),
          join(rawBackendCoverageDirectory, `${index}-${report}`),
        )
      }
      await rm(descriptor.backendCoverageDirectory, { recursive: true, force: true })
    }
    const reports = (await readdir(rawBackendCoverageDirectory)).filter((file) => file.endsWith('.xml'))
    if (reports.length === 0) {
      console.error('No backend coverage report was written.')
    } else {
      await stacks[0].run(
        [
          'run',
          '--rm',
          '--user',
          `${process.getuid?.() ?? 0}:${process.getgid?.() ?? 0}`,
          '--entrypoint',
          '/opt/dotnet-coverage/dotnet-coverage',
          '--volume',
          `${backendCoverageDirectory}:/coverage`,
          plan.hostImage,
          'merge',
          '--output-format',
          'cobertura',
          '--output',
          '/coverage/cobertura.xml',
          '/coverage/raw/*.xml',
        ],
        'Backend coverage merge',
      )
      await summarizeBackendCoverage(join(backendCoverageDirectory, 'cobertura.xml'), backendCoverageDirectory)
    }
  }
} finally {
  // Turn the videos Playwright kept for failed tests into GIFs, whichever path we took out of the suite.
  try {
    await convertVideos(join(frontendDirectory, 'test-results', 'e2e'))
  } finally {
    await Promise.allSettled(stacks.filter(Boolean).map((stack) => stack.dispose()))
    if (fixtureTemplate) await rm(fixtureTemplate, { recursive: true, force: true })
    if (!plan.reusable) {
      try {
        await removeImages(plan.appImage, plan.hostImage)
      } catch (error) {
        console.error(`Could not clean up the Listenarr images: ${error.message}`)
      }
    }
  }
}

process.exitCode = exitCode
