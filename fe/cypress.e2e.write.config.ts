import { defineConfig } from 'cypress'
import { execFileSync } from 'node:child_process'
import { access, cp, rm } from 'node:fs/promises'
import { resolve } from 'node:path'

const composeFile = process.env.LISTENARR_E2E_COMPOSE_FILE
const composeProject = process.env.LISTENARR_E2E_COMPOSE_PROJECT
const configDirectory = process.env.LISTENARR_E2E_CONFIG_DIR
const seedConfigDirectory = process.env.LISTENARR_E2E_SEED_CONFIG_DIR
const filesystemDirectory = process.env.LISTENARR_E2E_FILESYSTEM_DIR
const seedFilesystemDirectory = process.env.LISTENARR_E2E_SEED_FILESYSTEM_DIR
const baseUrl = process.env.LISTENARR_E2E_BASE_URL ?? 'http://localhost:4545'

function runCompose(...args: string[]) {
  if (!composeFile || !composeProject) {
    throw new Error('The Docker-backed E2E stack is not configured.')
  }

  execFileSync('docker', ['compose', '--file', composeFile, '--project-name', composeProject, ...args], {
    stdio: 'inherit',
    env: process.env,
  })
}

async function waitForApi() {
  const readyUrl = new URL('/api/v1/system/ready', baseUrl)
  const appUrl = new URL('/', baseUrl)
  const deadline = Date.now() + 120_000
  while (Date.now() < deadline) {
    try {
      const [apiResponse, appResponse] = await Promise.all([
        fetch(readyUrl, { signal: AbortSignal.timeout(2_000) }),
        fetch(appUrl, { signal: AbortSignal.timeout(2_000) }),
      ])
      if (apiResponse.ok && appResponse.ok) {
        const html = await appResponse.text()
        const scriptPath = html.match(/<script[^>]+src=["']([^"']+\.js)["']/i)?.[1]
        if (scriptPath) {
          const bundleResponse = await fetch(new URL(scriptPath, baseUrl), {
            signal: AbortSignal.timeout(2_000),
          })
          if (bundleResponse.ok) {
            const bundle = await bundleResponse.text()
            const settingsChunk = bundle.match(/(?:\/assets\/|\.\/)?(SettingsView-[\w-]+\.js)/)?.[1]
            if (settingsChunk) {
              const settingsResponse = await fetch(new URL(`/assets/${settingsChunk}`, baseUrl), {
                signal: AbortSignal.timeout(2_000),
              })
              const settingsContentType = settingsResponse.headers.get('content-type')?.toLowerCase() ?? ''
              if (settingsResponse.ok && settingsContentType.includes('javascript')) return
            }
          }
        }
      }
    } catch {
      // The container is still starting.
    }
    await new Promise((resolve) => setTimeout(resolve, 500))
  }

  throw new Error(`Listenarr did not become ready at ${readyUrl} and ${appUrl}.`)
}

export default defineConfig({
  allowCypressEnv: false,
  e2e: {
    specPattern: 'cypress/e2e/write/**/*.{cy,spec}.{js,jsx,ts,tsx}',
    baseUrl,
    supportFile: 'cypress/support/write.ts',
    setupNodeEvents(on) {
      on('task', {
        async assertAudioFileMoved({ sourcePath, destinationPath }: { sourcePath: string; destinationPath: string }) {
          if (!filesystemDirectory || !sourcePath.startsWith('/e2e/') || !destinationPath.startsWith('/e2e/')) {
            throw new Error('Audio file paths must be inside the E2E filesystem mount.')
          }

          const toHostPath = (containerPath: string) => {
            const hostPath = resolve(filesystemDirectory, containerPath.slice('/e2e/'.length))
            if (!hostPath.startsWith(`${resolve(filesystemDirectory)}/`)) {
              throw new Error('Audio file path escaped the E2E filesystem mount.')
            }
            return hostPath
          }

          await access(toHostPath(destinationPath))
          try {
            await access(toHostPath(sourcePath))
            throw new Error('The imported audiobook still exists in the source folder.')
          } catch (error) {
            if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error
          }

          return null
        },
        async resetWriteDatabase() {
          if (!configDirectory || !seedConfigDirectory || !filesystemDirectory || !seedFilesystemDirectory) {
            throw new Error('The Docker-backed E2E database and filesystem snapshots are not available.')
          }

          runCompose('stop', 'listenarr')
          await rm(configDirectory, { recursive: true, force: true })
          await cp(seedConfigDirectory, configDirectory, { recursive: true })
          await rm(filesystemDirectory, { recursive: true, force: true })
          await cp(seedFilesystemDirectory, filesystemDirectory, { recursive: true })
          runCompose('start', 'listenarr')
          await waitForApi()

          return null
        },
      })
    },
  },
})