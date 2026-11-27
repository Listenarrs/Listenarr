// Starts Listenarr in Docker for the browser E2E suite: the production image, the test host image built
// on top of it (scripts/e2e/e2e-host.Dockerfile: fixture-backed Audible client, ffprobe), one container
// on a temporary config and filesystem, a seeded library, and snapshots of the seeded database and files
// that tests restore. Used by scripts/e2e/run.mjs.
import { once } from 'node:events'
import { createHash } from 'node:crypto'
import { cp, mkdir, mkdtemp, readFile, rm } from 'node:fs/promises'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { tmpdir } from 'node:os'
import { createServer as createTcpServer } from 'node:net'
import { execFileSync, spawn } from 'node:child_process'
import { DatabaseSync } from 'node:sqlite'

export const repositoryRoot = resolve(fileURLToPath(new URL('../..', import.meta.url)))
export const frontendDirectory = join(repositoryRoot, 'fe')
export const playwrightCli = join(frontendDirectory, 'node_modules', '@playwright', 'test', 'cli.js')
const hostDockerfile = join(repositoryRoot, 'scripts', 'e2e', 'e2e-host.Dockerfile')

export function start(command, args, options) {
  const child = spawn(command, args, { stdio: 'inherit', ...options })
  child.spawnError = null
  child.on('error', (error) => {
    child.spawnError = error
  })
  return child
}

export async function waitFor(url, label, timeout = 120_000) {
  const deadline = Date.now() + timeout
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

export async function waitForExit(child, label) {
  const [code] = await once(child, 'close')
  if (child.spawnError) throw child.spawnError
  if (code !== 0) throw new Error(`${label} exited with code ${code}`)
}

/** Whether a tagged image is present in the local Docker daemon. */
export function imageExists(tag) {
  try {
    execFileSync('docker', ['image', 'inspect', tag], { stdio: 'ignore' })
    return true
  } catch {
    return false
  }
}

/**
 * Builds both images once, unless both are already present under a reusable (content-derived) tag.
 * Returns true when a build was necessary.
 */
export async function ensureImages({ appImage, hostImage, reuse = false }) {
  if (reuse && imageExists(appImage) && imageExists(hostImage)) return false
  const run = async (args, label) => {
    const child = start('docker', args, { cwd: repositoryRoot })
    await waitForExit(child, label)
  }
  await run(['build', '--tag', appImage, repositoryRoot], 'Listenarr Docker image build')
  await run(
    ['build', '--tag', hostImage, '--build-arg', `LISTENARR_IMAGE=${appImage}`, '--file', hostDockerfile, repositoryRoot],
    'Listenarr test host Docker image build',
  )
  return true
}

/** Removes run-specific images; reusable tagged images are kept for the next run. */
export async function removeImages(appImage, hostImage) {
  const child = start('docker', ['rmi', '--force', hostImage, appImage], { cwd: repositoryRoot })
  await waitForExit(child, 'Listenarr image cleanup')
}

/** A hash of every row of every table: equal hashes mean an unchanged database. */
export function databaseSnapshot(path) {
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

export async function availablePort() {
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

async function seedLibrary(apiUrl, seedFile, rootFolders) {
  const books = JSON.parse(await readFile(seedFile, 'utf8'))
  const tokenResponse = await fetch(`${apiUrl}/api/v1/antiforgery/token`)
  if (!tokenResponse.ok) throw new Error('Could not obtain an antiforgery token for the seed.')
  const { token } = await tokenResponse.json()
  const cookies = tokenResponse.headers.getSetCookie().map((cookie) => cookie.split(';', 1)[0])
  if (!token || cookies.length === 0) throw new Error('The API did not issue an antiforgery token and cookie for the seed.')
  const headers = {
    'Content-Type': 'application/json',
    Cookie: cookies.join('; '),
    'X-XSRF-TOKEN': token,
  }

  const profileResponse = await fetch(`${apiUrl}/api/v1/qualityprofile`, {
    method: 'POST',
    headers,
    body: JSON.stringify({ name: 'E2E Default', isDefault: true }),
  })
  if (!profileResponse.ok) {
    throw new Error(`Could not create the E2E quality profile: ${await profileResponse.text()}`)
  }

  for (const { authors, ...metadata } of books) {
    const addResponse = await fetch(`${apiUrl}/api/v1/library/add`, {
      method: 'POST',
      headers,
      body: JSON.stringify({ metadata: { ...metadata, authors: [] }, monitored: true, autoSearch: false }),
    })
    const addResult = await addResponse.json()
    if (!addResponse.ok || !addResult.audiobook?.id) {
      throw new Error(`Could not add seed book '${metadata.title}': ${JSON.stringify(addResult)}`)
    }

    // Adding authors invokes author enrichment; set them afterward to keep the seed offline.
    const updateResponse = await fetch(`${apiUrl}/api/v1/library/${addResult.audiobook.id}`, {
      method: 'PUT',
      headers,
      body: JSON.stringify({ authors }),
    })
    if (!updateResponse.ok) {
      throw new Error(`Could not set authors for seed book '${metadata.title}': ${await updateResponse.text()}`)
    }
  }

  for (const rootFolder of rootFolders) {
    const response = await fetch(`${apiUrl}/api/v1/rootfolders`, {
      method: 'POST',
      headers,
      body: JSON.stringify({ isDefault: false, caseSensitivityMode: 0, ...rootFolder }),
    })
    if (!response.ok) {
      throw new Error(`Could not create root folder '${rootFolder.name}': ${await response.text()}`)
    }
  }
}

/**
 * Starts the container from images the caller built (ensureImages) and seeds it.
 *
 * - `name`: prefix of the container and temporary directory names.
 * - `generateFixtures(seedFilesystemDirectory)`: writes the files the container sees under /e2e.
 * - `seedFile`: JSON array of books added to the library.
 * - `rootFolders`: root folders created after the books ({ name, path, isDefault? }).
 * - `environment`: extra container environment variables.
 * - `volumes`: extra `host:container` volume mounts.
 * - `dockerRunArguments`: extra `docker run` options.
 * - `appImage` / `hostImage`: image tags shared by all stacks of one run; the caller builds them, and
 *   removes them when they are run-specific.
 * - `apiPort`: host port, so parallel stacks get distinct ports chosen up front.
 *
 * Returns the stack: its URL and directories, `run(args, label)` for docker commands, `restart()`
 * (stop then start, waiting for readiness) and `dispose()`, which removes everything.
 */
export async function startListenarrStack({
  name,
  generateFixtures,
  seedFile,
  rootFolders,
  environment = {},
  volumes = [],
  dockerRunArguments = [],
  appImage,
  hostImage,
  apiPort,
}) {
  const contentRoot = await mkdtemp(join(tmpdir(), `${name}-`))
  const configDirectory = join(contentRoot, 'config')
  const seedConfigDirectory = join(contentRoot, 'seed-config')
  const filesystemDirectory = join(contentRoot, 'filesystem')
  const seedFilesystemDirectory = join(contentRoot, 'seed-filesystem')
  const databasePath = join(configDirectory, 'database', 'listenarr.db')
  const runId = `${process.pid}-${Date.now().toString(36)}`
  const containerName = `${name}-${runId}`
  const apiUrl = `http://127.0.0.1:${apiPort}`
  const run = async (args, label) => {
    const child = start('docker', args, { cwd: repositoryRoot })
    await waitForExit(child, label)
  }
  let dockerStarted = false

  const dispose = async () => {
    if (dockerStarted) {
      try {
        await run(['rm', '--force', '--volumes', containerName], 'Listenarr container cleanup')
      } catch (error) {
        console.error(`Could not clean up the Listenarr container: ${error.message}`)
      }
    }
    await rm(contentRoot, { recursive: true, force: true })
  }

  const readyUrl = `${apiUrl}/api/v1/system/ready`
  const restart = async (label) => {
    await run(['stop', containerName], `Listenarr container stop (${label})`)
    await run(['start', containerName], `Listenarr container start (${label})`)
    await waitFor(readyUrl, `Listenarr container (${label})`)
  }

  try {
    await mkdir(configDirectory, { recursive: true })
    await mkdir(seedFilesystemDirectory, { recursive: true })
    await generateFixtures(seedFilesystemDirectory)
    await cp(seedFilesystemDirectory, filesystemDirectory, { recursive: true })

    dockerStarted = true
    await run(
      [
        'run',
        '--detach',
        '--name',
        containerName,
        '--env',
        `PUID=${process.getuid?.() ?? 0}`,
        '--env',
        `PGID=${process.getgid?.() ?? 0}`,
        ...Object.entries(environment).flatMap(([key, value]) => ['--env', `${key}=${value}`]),
        '--publish',
        `127.0.0.1:${apiPort}:4545`,
        '--volume',
        `${configDirectory}:/app/config`,
        '--volume',
        `${filesystemDirectory}:/e2e`,
        ...volumes.flatMap((volume) => ['--volume', volume]),
        ...dockerRunArguments,
        hostImage,
      ],
      'Listenarr container startup',
    )
    await waitFor(readyUrl, 'Listenarr Docker container')

    const settingsResponse = await fetch(`${apiUrl}/api/v1/configuration/settings`)
    if (!settingsResponse.ok) throw new Error('Could not initialize the seed database.')
    await seedLibrary(apiUrl, seedFile, rootFolders)
    await run(['stop', containerName], 'Listenarr container stop for the seed snapshot')
    await cp(configDirectory, seedConfigDirectory, { recursive: true })
    const seedHash = databaseSnapshot(databasePath)
    await run(['start', containerName], 'Listenarr container restart after the seed snapshot')
    await waitFor(readyUrl, 'Listenarr Docker container after the seed snapshot')

    return {
      apiUrl,
      containerName,
      hostImage,
      configDirectory,
      seedConfigDirectory,
      filesystemDirectory,
      seedFilesystemDirectory,
      databasePath,
      seedHash,
      run,
      restart,
      dispose,
      /** The variables the Playwright support code (fe/playwright/support/docker-stack.ts) reads. */
      testEnvironment: {
        LISTENARR_E2E_BASE_URL: apiUrl,
        LISTENARR_E2E_CONTAINER: containerName,
        LISTENARR_E2E_CONFIG_DIR: configDirectory,
        LISTENARR_E2E_FILESYSTEM_DIR: filesystemDirectory,
        LISTENARR_E2E_SEED_FILESYSTEM_DIR: seedFilesystemDirectory,
        LISTENARR_E2E_SEED_CONFIG_DIR: seedConfigDirectory,
      },
    }
  } catch (error) {
    await dispose()
    throw error
  }
}
