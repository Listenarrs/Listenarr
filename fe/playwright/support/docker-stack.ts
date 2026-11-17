import { execFileSync } from 'node:child_process'
import { access, cp, readdir, rm } from 'node:fs/promises'
import { join, resolve } from 'node:path'
import type { APIRequestContext } from '@playwright/test'

// The Listenarr container the E2E suite talks to, started by
// scripts/lib/listenarr-stack.mjs (through scripts/e2e/run.mjs).
const container = process.env.LISTENARR_E2E_CONTAINER
const configDirectory = process.env.LISTENARR_E2E_CONFIG_DIR
const seedConfigDirectory = process.env.LISTENARR_E2E_SEED_CONFIG_DIR
const filesystemDirectory = process.env.LISTENARR_E2E_FILESYSTEM_DIR
const seedFilesystemDirectory = process.env.LISTENARR_E2E_SEED_FILESYSTEM_DIR
const baseUrl = process.env.LISTENARR_E2E_BASE_URL ?? 'http://localhost:4545'

/** Where the container sees the E2E filesystem (`LISTENARR_E2E_FILESYSTEM_DIR` on the host). */
const containerFilesystemRoot = '/e2e/'

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

/**
 * Makes a directory hold exactly what another holds. The directory itself stays: it is mounted into the
 * running container, which would keep seeing a removed one.
 */
async function replaceContents(directory: string, source: string) {
  for (const entry of await readdir(directory)) {
    await rm(join(directory, entry), { recursive: true, force: true })
  }
  await cp(source, directory, { recursive: true })
}

/** Stops the app inside its container and waits until it exited (scripts/e2e/e2e-host-entrypoint.sh). */
async function stopApp(name: string) {
  execFileSync('docker', ['kill', '--signal', 'USR1', name], { stdio: 'ignore' })
  const deadline = Date.now() + 120_000
  while (Date.now() < deadline) {
    try {
      execFileSync('docker', ['exec', name, 'test', '-f', '/run/listenarr-host.stopped'], {
        stdio: 'ignore',
      })
      return
    } catch {
      await new Promise((resolveDelay) => setTimeout(resolveDelay, 250))
    }
  }
  throw new Error('The Listenarr test host did not stop.')
}

/**
 * Puts the database and the files back as they were seeded, with the app stopped meanwhile. The app
 * restarts inside its running container: restarting the container would change the host's network,
 * and Chromium would abort the page loads in flight.
 */
export async function restoreSeed() {
  if (
    !container ||
    !configDirectory ||
    !seedConfigDirectory ||
    !filesystemDirectory ||
    !seedFilesystemDirectory
  ) {
    throw new Error('This suite needs the container started by scripts/e2e/run.mjs.')
  }

  await stopApp(container)
  await replaceContents(configDirectory, seedConfigDirectory)
  await replaceContents(filesystemDirectory, seedFilesystemDirectory)
  execFileSync('docker', ['kill', '--signal', 'USR2', container], { stdio: 'ignore' })
  await waitForApi()
}

/** The host path of a file the container sees under /e2e/. */
export function hostPath(containerPath: string) {
  if (!filesystemDirectory || !containerPath.startsWith(containerFilesystemRoot)) {
    throw new Error(`'${containerPath}' is not inside the E2E filesystem mount.`)
  }

  const root = resolve(filesystemDirectory)
  const path = resolve(root, containerPath.slice(containerFilesystemRoot.length))
  if (!path.startsWith(`${root}/`))
    throw new Error(`'${containerPath}' escapes the E2E filesystem mount.`)
  return path
}

/** Whether a file the container sees under /e2e/ exists. */
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
  if (!response.ok())
    throw new Error(`The API did not issue an antiforgery token (${response.status()}).`)
  const { token } = (await response.json()) as { token: string }
  return { 'X-XSRF-TOKEN': token }
}
