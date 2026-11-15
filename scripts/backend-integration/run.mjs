// Runs the backend integration suites (fe/playwright/backend-integration) against the application in Docker:
// builds the production image and the backend integration host image on top of it, starts one container on a
// temporary config and filesystem, seeds its library, then runs the read-only suite (which must
// leave the database untouched) and the write suite (which restores the seed before each test).
// The container, the images and the temporary data are removed at the end.
//
//   node scripts/backend-integration/run.mjs [all|read-only|write]
import { once } from 'node:events'
import { createHash } from 'node:crypto'
import { existsSync } from 'node:fs'
import { cp, mkdir, mkdtemp, readFile, rm } from 'node:fs/promises'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { tmpdir } from 'node:os'
import { createServer as createTcpServer } from 'node:net'
import { spawn } from 'node:child_process'
import { DatabaseSync } from 'node:sqlite'

const repositoryRoot = resolve(fileURLToPath(new URL('../..', import.meta.url)))
const frontendDirectory = join(repositoryRoot, 'fe')
const playwrightCli = join(frontendDirectory, 'node_modules', '@playwright', 'test', 'cli.js')
const hostDockerfile = join(repositoryRoot, '.devcontainer', 'backend-integration.Dockerfile')
const suiteMode = process.argv[2] ?? 'all'

if (!['all', 'read-only', 'write'].includes(suiteMode)) {
  throw new Error('Use `all`, `read-only`, or `write` as the backend integration runner mode.')
}

function start(command, args, options) {
  const child = spawn(command, args, { stdio: 'inherit', ...options })
  child.spawnError = null
  child.on('error', (error) => {
    child.spawnError = error
  })
  return child
}

async function waitFor(url, label) {
  const deadline = Date.now() + 120_000
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(2_000) })
      if (response.ok) return
    } catch {
      // The service is still starting.
    }

    await new Promise((resolveDelay) => setTimeout(resolveDelay, 500))
  }

  throw new Error(`${label} did not become ready at ${url}`)
}

async function waitForExit(child, label) {
  const [code] = await once(child, 'close')
  if (child.spawnError) throw child.spawnError
  if (code !== 0) throw new Error(`${label} exited with code ${code}`)
}

function databaseSnapshot(path) {
  const database = new DatabaseSync(path, { readOnly: true })
  try {
    const tables = database
      .prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name")
      .all()
      .map((table) => table.name)
    const contents = tables.map((table) => {
      const quotedTable = table.replaceAll('"', '""')
      const rows = database
        .prepare(`SELECT * FROM "${quotedTable}"`)
        .all()
        .map((row) => JSON.stringify(row))
        .sort()
      return [table, rows]
    })
    return createHash('sha256').update(JSON.stringify(contents)).digest('hex')
  } finally {
    database.close()
  }
}

async function availablePort() {
  const server = createTcpServer()
  const port = await new Promise((resolveListen, rejectListen) => {
    server.once('error', rejectListen)
    server.listen(0, '127.0.0.1', resolveListen)
  }).then(() => server.address().port)
  await new Promise((resolveClose, rejectClose) => {
    server.close((error) => (error ? rejectClose(error) : resolveClose()))
  })
  return port
}

async function seedLibrary(apiUrl) {
  const books = JSON.parse(await readFile(join(repositoryRoot, 'scripts', 'backend-integration', 'seed-library.json'), 'utf8'))
  const tokenResponse = await fetch(`${apiUrl}/api/v1/antiforgery/token`)
  if (!tokenResponse.ok) throw new Error('Could not obtain an antiforgery token for the backend integration seed.')
  const { token } = await tokenResponse.json()
  const cookies = tokenResponse.headers.getSetCookie().map((cookie) => cookie.split(';', 1)[0])
  if (!token || cookies.length === 0) throw new Error('The API did not issue an antiforgery token and cookie for the backend integration seed.')
  const headers = {
    'Content-Type': 'application/json',
    Cookie: cookies.join('; '),
    'X-XSRF-TOKEN': token,
  }

  for (const { authors, ...metadata } of books) {
    const addResponse = await fetch(`${apiUrl}/api/v1/library/add`, {
      method: 'POST',
      headers,
      body: JSON.stringify({ metadata: { ...metadata, authors: [] }, monitored: true, autoSearch: false }),
    })
    const addResult = await addResponse.json()
    if (!addResponse.ok || !addResult.audiobook?.id) {
      throw new Error(`Could not add backend integration seed book '${metadata.title}': ${JSON.stringify(addResult)}`)
    }

    // Adding authors invokes live author enrichment; set them afterward to keep the seed offline.
    const updateResponse = await fetch(`${apiUrl}/api/v1/library/${addResult.audiobook.id}`, {
      method: 'PUT',
      headers,
      body: JSON.stringify({ authors }),
    })
    if (!updateResponse.ok) {
      throw new Error(`Could not set authors for backend integration seed book '${metadata.title}': ${await updateResponse.text()}`)
    }
  }

  for (const rootFolder of [
    { name: 'Backend Integration Import', path: '/backend-integration/import' },
    { name: 'Backend Integration Library', path: '/backend-integration/library' },
  ]) {
    const response = await fetch(`${apiUrl}/api/v1/rootfolders`, {
      method: 'POST',
      headers,
      body: JSON.stringify({ ...rootFolder, isDefault: false, caseSensitivityMode: 0 }),
    })
    if (!response.ok) {
      throw new Error(`Could not create backend integration root folder '${rootFolder.name}': ${await response.text()}`)
    }
  }
}

if (!existsSync(playwrightCli)) {
  throw new Error('Install frontend dependencies first with `npm ci --prefix fe --engine-strict`.')
}

const contentRoot = await mkdtemp(join(tmpdir(), 'listenarr-backend-integration-'))
const configDirectory = join(contentRoot, 'config')
const seedConfigDirectory = join(contentRoot, 'seed-config')
const filesystemDirectory = join(contentRoot, 'filesystem')
const seedFilesystemDirectory = join(contentRoot, 'seed-filesystem')
const databasePath = join(configDirectory, 'database', 'listenarr.db')
const runId = `${process.pid}-${Date.now().toString(36)}`
const containerName = `listenarr-backend-integration-${runId}`
const appImage = `listenarr-backend-integration-app:${runId}`
const hostImage = `listenarr-backend-integration-host:${runId}`
const apiPort = await availablePort()
const apiUrl = `http://127.0.0.1:${apiPort}`
const runDocker = async (args, label) => {
  const child = start('docker', args, { cwd: repositoryRoot })
  await waitForExit(child, label)
}
let exitCode = 1
let dockerStarted = false
try {
  await mkdir(configDirectory, { recursive: true })
  await mkdir(join(seedFilesystemDirectory, 'import'), { recursive: true })
  await mkdir(join(seedFilesystemDirectory, 'library'), { recursive: true })
  const audioFixtureGenerator = start(
    'bash',
    [join(repositoryRoot, 'scripts', 'backend-integration', 'generate-audio-fixtures.sh'), join(seedFilesystemDirectory, 'import')],
    { cwd: repositoryRoot },
  )
  await waitForExit(audioFixtureGenerator, 'Synthetic audiobook fixture generation')
  await cp(seedFilesystemDirectory, filesystemDirectory, { recursive: true })

  dockerStarted = true
  await runDocker(['build', '--tag', appImage, repositoryRoot], 'Listenarr Docker image build')
  await runDocker(
    [
      'build',
      '--tag',
      hostImage,
      '--build-arg',
      `LISTENARR_IMAGE=${appImage}`,
      '--file',
      hostDockerfile,
      repositoryRoot,
    ],
    'Listenarr backend integration Docker image build',
  )
  await runDocker(
    [
      'run',
      '--detach',
      '--name',
      containerName,
      '--env',
      `PUID=${process.getuid?.() ?? 0}`,
      '--env',
      `PGID=${process.getgid?.() ?? 0}`,
      '--publish',
      `127.0.0.1:${apiPort}:4545`,
      '--volume',
      `${configDirectory}:/app/config`,
      '--volume',
      `${filesystemDirectory}:/backend-integration`,
      hostImage,
    ],
    'Listenarr backend integration container startup',
  )
  await waitFor(`${apiUrl}/api/v1/system/ready`, 'Listenarr Docker container')

  const settingsResponse = await fetch(`${apiUrl}/api/v1/configuration/settings`)
  if (!settingsResponse.ok) throw new Error('Could not initialize the backend integration seed database.')
  await seedLibrary(apiUrl)
  await runDocker(['stop', containerName], 'Listenarr backend integration container stop for seed snapshot')
  await cp(configDirectory, seedConfigDirectory, { recursive: true })
  const seedHash = databaseSnapshot(databasePath)
  await runDocker(['start', containerName], 'Listenarr backend integration container restart')
  await waitFor(`${apiUrl}/api/v1/system/ready`, 'Listenarr Docker container after seed snapshot')

  // One Playwright project per suite (fe/playwright.backend-integration.config.ts); the tests only send API requests.
  const runSuite = async (project) => {
    const suite = start(
      process.execPath,
      [playwrightCli, 'test', '--config', 'playwright.backend-integration.config.ts', '--project', project],
      {
        cwd: frontendDirectory,
        env: {
          ...process.env,
          LISTENARR_BACKEND_INTEGRATION_BASE_URL: apiUrl,
          LISTENARR_BACKEND_INTEGRATION_CONTAINER: containerName,
          LISTENARR_BACKEND_INTEGRATION_CONFIG_DIR: configDirectory,
          LISTENARR_BACKEND_INTEGRATION_FILESYSTEM_DIR: filesystemDirectory,
          LISTENARR_BACKEND_INTEGRATION_SEED_FILESYSTEM_DIR: seedFilesystemDirectory,
          LISTENARR_BACKEND_INTEGRATION_SEED_CONFIG_DIR: seedConfigDirectory,
        },
      },
    )
    try {
      await waitForExit(suite, `The ${project} backend integration suite`)
    } catch (error) {
      await runDocker(['logs', containerName], `Listenarr backend integration container logs after the ${project} suite failed`)
      throw error
    }
  }

  if (suiteMode !== 'write') {
    await runSuite('read-only')
    await runDocker(['stop', containerName], 'Listenarr backend integration container stop after read-only tests')
    if (databaseSnapshot(databasePath) !== seedHash) {
      throw new Error('The read-only backend integration suite modified the seeded database.')
    }
    await runDocker(['start', containerName], 'Listenarr backend integration container restart before write tests')
    await waitFor(`${apiUrl}/api/v1/system/ready`, 'Listenarr Docker container before write tests')
  }

  if (suiteMode !== 'read-only') await runSuite('write')
  exitCode = 0
} finally {
  if (dockerStarted) {
    for (const [args, label] of [
      [['rm', '--force', '--volumes', containerName], 'container'],
      [['rmi', '--force', hostImage, appImage], 'images'],
    ]) {
      try {
        await runDocker(args, `Listenarr backend integration ${label} cleanup`)
      } catch (error) {
        console.error(`Could not clean up the Listenarr backend integration ${label}: ${error.message}`)
      }
    }
  }
  await rm(contentRoot, { recursive: true, force: true })
}

process.exitCode = exitCode
