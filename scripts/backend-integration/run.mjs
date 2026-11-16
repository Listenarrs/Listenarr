// Runs the backend integration suites (fe/playwright/backend-integration) against the application in Docker:
// builds the production image and the test host image on top of it, starts one container on a
// temporary config and filesystem, seeds its library, then runs the read-only suite (which must
// leave the database untouched) and the write suite (which restores the seed before each test).
// The container, the images and the temporary data are removed at the end.
//
//   node scripts/backend-integration/run.mjs [all|read-only|write]
import { existsSync } from 'node:fs'
import { mkdir } from 'node:fs/promises'
import { join } from 'node:path'
import {
  databaseSnapshot,
  frontendDirectory,
  playwrightCli,
  repositoryRoot,
  start,
  startListenarrStack,
  waitFor,
  waitForExit,
} from '../lib/listenarr-stack.mjs'

const suiteMode = process.argv[2] ?? 'all'

if (!['all', 'read-only', 'write'].includes(suiteMode)) {
  throw new Error('Use `all`, `read-only`, or `write` as the backend integration runner mode.')
}

if (!existsSync(playwrightCli)) {
  throw new Error('Install frontend dependencies first with `npm ci --prefix fe --engine-strict`.')
}

let exitCode = 1
let stack
try {
  stack = await startListenarrStack({
    name: 'listenarr-backend-integration',
    seedFile: join(repositoryRoot, 'scripts', 'backend-integration', 'seed-library.json'),
    rootFolders: [
      { name: 'Backend Integration Import', path: '/backend-integration/import' },
      { name: 'Backend Integration Library', path: '/backend-integration/library' },
    ],
    async generateFixtures(seedFilesystemDirectory) {
      await mkdir(join(seedFilesystemDirectory, 'import'), { recursive: true })
      await mkdir(join(seedFilesystemDirectory, 'library'), { recursive: true })
      const generator = start(
        'bash',
        [join(repositoryRoot, 'scripts', 'backend-integration', 'generate-audio-fixtures.sh'), join(seedFilesystemDirectory, 'import')],
        { cwd: repositoryRoot },
      )
      await waitForExit(generator, 'Synthetic audiobook fixture generation')
    },
  })

  // One Playwright project per suite (fe/playwright.backend-integration.config.ts); the tests only send API requests.
  const runSuite = async (project) => {
    const suite = start(
      process.execPath,
      [playwrightCli, 'test', '--config', 'playwright.backend-integration.config.ts', '--project', project],
      { cwd: frontendDirectory, env: { ...process.env, ...stack.testEnvironment } },
    )
    try {
      await waitForExit(suite, `The ${project} backend integration suite`)
    } catch (error) {
      await stack.run(['logs', stack.containerName], `Listenarr container logs after the ${project} suite failed`)
      throw error
    }
  }

  if (suiteMode !== 'write') {
    await runSuite('read-only')
    await stack.run(['stop', stack.containerName], 'Listenarr container stop after read-only tests')
    if (databaseSnapshot(stack.databasePath) !== stack.seedHash) {
      throw new Error('The read-only backend integration suite modified the seeded database.')
    }
    await stack.run(['start', stack.containerName], 'Listenarr container restart before write tests')
    await waitFor(`${stack.apiUrl}/api/v1/system/ready`, 'Listenarr Docker container before write tests')
  }

  if (suiteMode !== 'read-only') await runSuite('write')
  exitCode = 0
} finally {
  await stack?.dispose()
}

process.exitCode = exitCode
