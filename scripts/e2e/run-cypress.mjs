import { once } from 'node:events'
import { createHash } from 'node:crypto'
import { existsSync } from 'node:fs'
import { copyFile, cp, mkdir, mkdtemp, readFile, rm } from 'node:fs/promises'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { tmpdir } from 'node:os'
import { createServer as createTcpServer } from 'node:net'
import { createServer as createHttpServer } from 'node:http'
import { spawn } from 'node:child_process'
import { DatabaseSync } from 'node:sqlite'

const repositoryRoot = resolve(fileURLToPath(new URL('../..', import.meta.url)))
const frontendDirectory = join(repositoryRoot, 'fe')
const cypressCli = join(frontendDirectory, 'node_modules', 'cypress', 'bin', 'cypress')
const viteCli = join(frontendDirectory, 'node_modules', 'vite', 'bin', 'vite.js')
const children = []
const e2eMode = process.argv[2] ?? 'all'

if (!['all', 'read-only', 'write'].includes(e2eMode)) {
  throw new Error('Use `all`, `read-only`, or `write` as the E2E runner mode.')
}

function start(command, args, options) {
  const child = spawn(command, args, { stdio: 'inherit', ...options })
  child.spawnError = null
  child.on('error', (error) => {
    child.spawnError = error
  })
  children.push(child)
  return child
}

async function waitFor(url, child, label) {
  const deadline = Date.now() + 120_000
  while (Date.now() < deadline) {
    if (child.spawnError) throw child.spawnError
    if (child.exitCode !== null) throw new Error(`${label} exited with code ${child.exitCode}`)

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
  if (code !== 0) throw new Error(`${label} exited with code ${code}`)
}

async function stop(child) {
  if (child.exitCode !== null || child.signalCode !== null || child.spawnError) return

  child.kill('SIGTERM')
  const waitForClose = (timeoutMs) =>
    new Promise((resolveClose) => {
      const timeoutHandle = setTimeout(() => {
        child.removeListener('close', onClose)
        resolveClose(false)
      }, timeoutMs)
      const onClose = () => {
        clearTimeout(timeoutHandle)
        resolveClose(true)
      }
      child.once('close', onClose)
    })

  if (!(await waitForClose(5_000))) {
    child.kill('SIGKILL')
    if (!(await waitForClose(5_000))) {
      throw new Error(`Could not stop process ${child.pid}`)
    }
  }
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

async function listen(server) {
  await new Promise((resolveListen, rejectListen) => {
    server.once('error', rejectListen)
    server.listen(0, '127.0.0.1', resolveListen)
  })
  return server.address().port
}

async function availablePort() {
  const server = createTcpServer()
  const port = await listen(server)
  await new Promise((resolveClose, rejectClose) => {
    server.close((error) => (error ? rejectClose(error) : resolveClose()))
  })
  return port
}

async function seedLibrary(apiUrl) {
  const books = JSON.parse(await readFile(join(repositoryRoot, 'scripts', 'e2e', 'seed-library.json'), 'utf8'))
  const tokenResponse = await fetch(`${apiUrl}/api/v1/antiforgery/token`)
  if (!tokenResponse.ok) throw new Error('Could not obtain an antiforgery token for the E2E seed.')
  const { token } = await tokenResponse.json()
  const cookies = tokenResponse.headers.getSetCookie().map((cookie) => cookie.split(';', 1)[0])
  if (!token || cookies.length === 0) throw new Error('The API did not issue an antiforgery token and cookie for the E2E seed.')
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
      throw new Error(`Could not add E2E seed book '${metadata.title}': ${JSON.stringify(addResult)}`)
    }

    // Adding authors invokes live author enrichment; set them afterward to keep the seed offline.
    const updateResponse = await fetch(`${apiUrl}/api/v1/library/${addResult.audiobook.id}`, {
      method: 'PUT',
      headers,
      body: JSON.stringify({ authors }),
    })
    if (!updateResponse.ok) {
      throw new Error(`Could not set authors for E2E seed book '${metadata.title}': ${await updateResponse.text()}`)
    }
  }
}

if (!existsSync(cypressCli) || !existsSync(viteCli)) {
  throw new Error('Install frontend dependencies first with `npm ci --prefix fe --engine-strict`.')
}

const contentRoot = await mkdtemp(join(tmpdir(), 'listenarr-cypress-e2e-'))
let exitCode = 1
let api
let controlServer
try {
  const databaseDirectory = join(contentRoot, 'database')
  const databasePath = join(databaseDirectory, 'listenarr.db')
  const configDirectory = join(contentRoot, 'config')
  const seedDirectory = join(contentRoot, 'seed')
  const seedDatabaseDirectory = join(seedDirectory, 'database')
  const seedConfigDirectory = join(seedDirectory, 'config')
  await mkdir(databaseDirectory, { recursive: true })
  await mkdir(join(contentRoot, 'wwwroot'), { recursive: true })
  const apiPort = await availablePort()
  const apiUrl = `http://127.0.0.1:${apiPort}`
  const applicationDirectory = join(contentRoot, 'app')
  const buildDirectory = join(repositoryRoot, 'listenarr.api', 'bin', 'Release', 'net10.0')
  const apiProject = join(repositoryRoot, 'listenarr.api', 'Listenarr.Api.csproj')

  const build = start(
    'dotnet',
    ['build', apiProject, '--configuration', 'Release', '--verbosity', 'quiet', '--disable-build-servers'],
    { cwd: repositoryRoot, env: process.env },
  )
  await waitForExit(build, 'API build')
  if (!existsSync(join(buildDirectory, 'Listenarr.Api.dll'))) {
    throw new Error(`API build output was not found at ${buildDirectory}`)
  }
  await cp(buildDirectory, applicationDirectory, { recursive: true })

  const startApi = () =>
    start('dotnet', [join(applicationDirectory, 'Listenarr.Api.dll')], {
      cwd: repositoryRoot,
      env: {
        ...process.env,
        ASPNETCORE_ENVIRONMENT: 'Production',
        DOTNET_ENVIRONMENT: 'Production',
        ASPNETCORE_URLS: apiUrl,
        LISTENARR_TEST_MODE: 'false',
        LISTENARR_CONTENT_ROOT: contentRoot,
        DOTNET_TEST_RUNNER: '',
        VSTEST_SESSION_ID: '',
        Listenarr__SqliteDbPath: join(contentRoot, 'database', 'listenarr.db'),
        Listenarr__DisableHostedServices: 'true',
      },
    })

  api = startApi()
  await waitFor(`${apiUrl}/api/v1/system/ready`, api, 'Listenarr API')
  const settingsResponse = await fetch(`${apiUrl}/api/v1/configuration/settings`)
  if (!settingsResponse.ok) throw new Error('Could not initialize the E2E seed database.')
  await seedLibrary(apiUrl)
  await stop(api)
  api = undefined

  let previousDatabaseHash = databaseSnapshot(databasePath)
  let databaseIsStable = false
  for (let attempt = 0; attempt < 3; attempt += 1) {
    api = startApi()
    await waitFor(`${apiUrl}/api/v1/system/ready`, api, 'Listenarr API while stabilizing E2E seed')
    await stop(api)
    api = undefined

    const currentDatabaseHash = databaseSnapshot(databasePath)
    if (currentDatabaseHash === previousDatabaseHash) {
      databaseIsStable = true
      break
    }
    previousDatabaseHash = currentDatabaseHash
  }
  if (!databaseIsStable) throw new Error('The initialized E2E seed database did not stabilize.')

  await mkdir(seedDatabaseDirectory, { recursive: true })
  await copyFile(databasePath, join(seedDatabaseDirectory, 'listenarr.db'))
  if (existsSync(configDirectory)) await cp(configDirectory, seedConfigDirectory, { recursive: true })
  const seedHash = databaseSnapshot(join(seedDatabaseDirectory, 'listenarr.db'))

  api = startApi()
  await waitFor(`${apiUrl}/api/v1/system/ready`, api, 'Listenarr API')

  let resettingDatabase = false
  const resetWriteDatabase = async () => {
    if (resettingDatabase) throw new Error('A database reset is already in progress.')
    resettingDatabase = true
    try {
      if (api) await stop(api)
      api = undefined
      await rm(databaseDirectory, { recursive: true, force: true })
      await cp(seedDatabaseDirectory, databaseDirectory, { recursive: true })
      await rm(configDirectory, { recursive: true, force: true })
      if (existsSync(seedConfigDirectory)) {
        await cp(seedConfigDirectory, configDirectory, { recursive: true })
      }
      api = startApi()
      await waitFor(`${apiUrl}/api/v1/system/ready`, api, 'Listenarr API after database reset')
    } finally {
      resettingDatabase = false
    }
  }

  controlServer = createHttpServer((request, response) => {
    if (request.method !== 'POST' || request.url !== '/reset-write-database') {
      response.writeHead(404).end()
      return
    }

    void resetWriteDatabase()
      .then(() => response.writeHead(204).end())
      .catch((error) => response.writeHead(500).end(String(error)))
  })
  const controlPort = await listen(controlServer)
  const controlUrl = `http://127.0.0.1:${controlPort}`

  const vite = start(
    process.execPath,
    [viteCli, '--host', '127.0.0.1', '--port', '5173', '--strictPort'],
    {
      cwd: frontendDirectory,
      env: { ...process.env, LISTENARR_E2E_API_TARGET: apiUrl },
    },
  )
  await waitFor('http://127.0.0.1:5173/', vite, 'Vite')

  const runCypressSpec = async (spec, configFile) => {
    const cypress = start(
      process.execPath,
      [cypressCli, 'run', '--e2e', '--config-file', configFile, '--spec', spec],
      {
        cwd: frontendDirectory,
        env: { ...process.env, LISTENARR_E2E_CONTROL_URL: controlUrl },
      },
    )
    await waitForExit(cypress, `Cypress spec ${spec}`)
  }

  if (e2eMode !== 'write') {
    await runCypressSpec('cypress/e2e/read-only/full-stack-smoke.cy.ts', 'cypress.config.ts')
    await stop(api)
    api = undefined
    if (databaseSnapshot(databasePath) !== seedHash) {
      throw new Error('The read-only E2E suite modified the seeded database.')
    }
  }

  if (e2eMode !== 'read-only') {
    await runCypressSpec('cypress/e2e/write/write-settings.cy.ts', 'cypress.e2e.write.config.ts')
  }
  exitCode = 0
} finally {
  if (controlServer?.listening) {
    await new Promise((resolveClose) => controlServer.close(resolveClose))
  }
  for (const child of children.reverse()) await stop(child)
  await rm(contentRoot, { recursive: true, force: true })
}

process.exitCode = exitCode