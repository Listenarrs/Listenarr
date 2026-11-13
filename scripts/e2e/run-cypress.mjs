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
const cypressCli = join(frontendDirectory, 'node_modules', 'cypress', 'bin', 'cypress')
const composeFile = join(repositoryRoot, 'scripts', 'e2e', 'docker-compose.yml')
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

if (!existsSync(cypressCli)) {
  throw new Error('Install frontend dependencies first with `npm ci --prefix fe --engine-strict`.')
}

const contentRoot = await mkdtemp(join(tmpdir(), 'listenarr-cypress-e2e-'))
const configDirectory = join(contentRoot, 'config')
const seedConfigDirectory = join(contentRoot, 'seed-config')
const databasePath = join(configDirectory, 'database', 'listenarr.db')
const projectName = `listenarr-cypress-${process.pid}-${Date.now().toString(36)}`
const apiPort = await availablePort()
const apiUrl = `http://127.0.0.1:${apiPort}`
const composeEnvironment = {
  ...process.env,
  LISTENARR_E2E_CONFIG_DIR: configDirectory,
  LISTENARR_E2E_PORT: String(apiPort),
  LISTENARR_E2E_IMAGE_TAG: `${process.pid}-${Date.now().toString(36)}`,
  LISTENARR_E2E_UID: String(process.getuid?.() ?? 0),
  LISTENARR_E2E_GID: String(process.getgid?.() ?? 0),
}
const composeArgs = ['compose', '--file', composeFile, '--project-name', projectName]
const runCompose = async (args, label) => {
  const child = start('docker', [...composeArgs, ...args], { cwd: repositoryRoot, env: composeEnvironment })
  await waitForExit(child, label)
}
let exitCode = 1
let composeStarted = false
try {
  await mkdir(configDirectory, { recursive: true })
  composeStarted = true
  await runCompose(['up', '--build', '--detach'], 'Docker Compose E2E stack startup')
  await waitFor(`${apiUrl}/api/v1/system/ready`, 'Listenarr Docker container')

  const settingsResponse = await fetch(`${apiUrl}/api/v1/configuration/settings`)
  if (!settingsResponse.ok) throw new Error('Could not initialize the E2E seed database.')
  await seedLibrary(apiUrl)
  await runCompose(['stop', 'listenarr'], 'Docker Compose E2E app stop for seed snapshot')
  await cp(configDirectory, seedConfigDirectory, { recursive: true })
  const seedHash = databaseSnapshot(databasePath)
  await runCompose(['start', 'listenarr'], 'Docker Compose E2E app restart')
  await waitFor(`${apiUrl}/api/v1/system/ready`, 'Listenarr Docker container after seed snapshot')

  const runCypressSpec = async (spec, configFile) => {
    const cypress = start(
      process.execPath,
      [cypressCli, 'run', '--e2e', '--config-file', configFile, '--spec', spec],
      {
        cwd: frontendDirectory,
        env: {
          ...composeEnvironment,
          LISTENARR_E2E_BASE_URL: apiUrl,
          LISTENARR_E2E_COMPOSE_FILE: composeFile,
          LISTENARR_E2E_COMPOSE_PROJECT: projectName,
          LISTENARR_E2E_SEED_CONFIG_DIR: seedConfigDirectory,
        },
      },
    )
    await waitForExit(cypress, `Cypress spec ${spec}`)
  }

  if (e2eMode !== 'write') {
    await runCypressSpec('cypress/e2e/read-only/loads-seeded-library-from-live-api.cy.ts', 'cypress.config.ts')
    await runCompose(['stop', 'listenarr'], 'Docker Compose E2E app stop after read-only tests')
    if (databaseSnapshot(databasePath) !== seedHash) {
      throw new Error('The read-only E2E suite modified the seeded database.')
    }
    await runCompose(['start', 'listenarr'], 'Docker Compose E2E app restart before write tests')
    await waitFor(`${apiUrl}/api/v1/system/ready`, 'Listenarr Docker container before write tests')
  }

  if (e2eMode !== 'read-only') {
    await runCypressSpec('cypress/e2e/write/write-settings.cy.ts', 'cypress.e2e.write.config.ts')
    await runCypressSpec('cypress/e2e/write/write-library-persistence.cy.ts', 'cypress.e2e.write.config.ts')
    await runCypressSpec('cypress/e2e/write/write-repair-persistence.cy.ts', 'cypress.e2e.write.config.ts')
  }
  exitCode = 0
} finally {
  if (composeStarted) {
    try {
      await runCompose(['down', '--volumes', '--remove-orphans', '--rmi', 'local'], 'Docker Compose E2E stack cleanup')
    } catch (error) {
      console.error(`Could not clean up the Docker Compose E2E stack: ${error.message}`)
    }
  }
  await rm(contentRoot, { recursive: true, force: true })
}

process.exitCode = exitCode