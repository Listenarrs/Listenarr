import { execFileSync } from 'node:child_process'
import { access, cp, rm } from 'node:fs/promises'
import { resolve } from 'node:path'
import type { APIRequestContext } from '@playwright/test'

// Set by scripts/backend-integration/run.mjs, which starts the container the tests talk to.
const container = process.env.LISTENARR_BACKEND_INTEGRATION_CONTAINER
const configDirectory = process.env.LISTENARR_BACKEND_INTEGRATION_CONFIG_DIR
const seedConfigDirectory = process.env.LISTENARR_BACKEND_INTEGRATION_SEED_CONFIG_DIR
const filesystemDirectory = process.env.LISTENARR_BACKEND_INTEGRATION_FILESYSTEM_DIR
const seedFilesystemDirectory = process.env.LISTENARR_BACKEND_INTEGRATION_SEED_FILESYSTEM_DIR
const baseUrl = process.env.LISTENARR_BACKEND_INTEGRATION_BASE_URL ?? 'http://localhost:4545'

/** Where the container sees the backend integration filesystem (`LISTENARR_BACKEND_INTEGRATION_FILESYSTEM_DIR` on the host). */
const containerFilesystemRoot = '/backend-integration/'

async function waitForApi() {
  const readyUrl = new URL('/api/v1/system/ready', baseUrl)
  const deadline = Date.now() + 120_000
  while (Date.now() < deadline) {
    try {
      const response = await fetch(readyUrl, { signal: AbortSignal.timeout(2_000) })
      if (response.ok) return
    } catch {
      // The container is still starting.
    }
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 500))
  }

  throw new Error(`Listenarr did not become ready at ${readyUrl}.`)
}

/** Puts the database and the files back as they were seeded, with the application stopped meanwhile. */
export async function restoreSeed() {
  if (!container || !configDirectory || !seedConfigDirectory || !filesystemDirectory || !seedFilesystemDirectory) {
    throw new Error('The backend integration write suite needs the container started by scripts/backend-integration/run.mjs.')
  }

  execFileSync('docker', ['stop', container], { stdio: 'ignore' })
  await rm(configDirectory, { recursive: true, force: true })
  await cp(seedConfigDirectory, configDirectory, { recursive: true })
  await rm(filesystemDirectory, { recursive: true, force: true })
  await cp(seedFilesystemDirectory, filesystemDirectory, { recursive: true })
  execFileSync('docker', ['start', container], { stdio: 'ignore' })
  await waitForApi()
}

/** The host path of a file the container sees under /backend-integration/. */
function hostPath(containerPath: string) {
  if (!filesystemDirectory || !containerPath.startsWith(containerFilesystemRoot)) {
    throw new Error(`'${containerPath}' is not inside the backend integration filesystem mount.`)
  }

  const root = resolve(filesystemDirectory)
  const path = resolve(root, containerPath.slice(containerFilesystemRoot.length))
  if (!path.startsWith(`${root}/`)) throw new Error(`'${containerPath}' escapes the backend integration filesystem mount.`)
  return path
}

/** Whether a file the container sees under /backend-integration/ exists. */
export async function fileExists(containerPath: string) {
  try {
    await access(hostPath(containerPath))
    return true
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return false
    throw error
  }
}

/** The header a mutating request needs; the matching cookie stays in the request context. */
export async function antiforgeryHeaders(request: APIRequestContext) {
  const response = await request.get('/api/v1/antiforgery/token')
  if (!response.ok()) throw new Error(`The API did not issue an antiforgery token (${response.status()}).`)
  const { token } = (await response.json()) as { token: string }
  return { 'X-XSRF-TOKEN': token }
}
