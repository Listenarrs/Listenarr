import { execFile } from 'node:child_process'
import { promisify } from 'node:util'
import type { APIRequestContext } from '@playwright/test'
import { antiforgeryHeaders } from '../support/docker-stack.js'
import { expect, test, type ListenarrApi } from './support/test.js'

const exec = promisify(execFile)
const qbitImage =
  'ghcr.io/linuxserver/qbittorrent@sha256:e887804fa9de4c60f5f0fb1f3a7e4febe40a8f69fe736f70c4e8589b1f868dab'
const clientId = 'e2e-qbittorrent'
const clientName = 'E2E qBittorrent'
const downloadPath = '/api/v1/download/send'

async function docker(...args: string[]) {
  return exec('docker', args, { timeout: 120_000 })
}

const downloadTest = test.extend<{
  qbittorrent: {
    name: string
    torrents: () => Promise<{ hash: string }[]>
    remove: (hash: string) => Promise<void>
  }
}>({
  qbittorrent: [
    async ({ listenarr }, use) => {
      const app = process.env.LISTENARR_E2E_CONTAINER
      if (!app) throw new Error('The download scenarios need the Docker E2E stack.')
      const name = `${app}-qbittorrent`
      await docker(
        'run',
        '--detach',
        '--name',
        name,
        '--network',
        `container:${app}`,
        '--env',
        'WEBUI_PORT=8080',
        qbitImage,
      )
      try {
        let password = ''
        await expect
          .poll(
            async () => {
              const { stdout, stderr } = await docker('logs', name)
              password =
                /temporary password is provided for this session:\s*(\S+)/i.exec(
                  stdout + stderr,
                )?.[1] ?? ''
              return password.length > 0
            },
            { timeout: 30_000 },
          )
          .toBe(true)

        const request = async (method: string, form?: URLSearchParams) => {
          const cookies = '/tmp/listenarr-e2e-qbit.cookies'
          const { stdout } = await docker(
            'exec',
            name,
            'curl',
            '--fail',
            '--silent',
            '--show-error',
            '--max-time',
            '5',
            '--cookie',
            cookies,
            '--cookie-jar',
            cookies,
            '--referer',
            'http://127.0.0.1:8080/',
            ...(form ? ['--data', form.toString()] : []),
            `http://127.0.0.1:8080/api/v2/${method}`,
          )
          return stdout
        }

        await request('auth/login', new URLSearchParams({ username: 'admin', password }))
        const torrents = async () =>
          JSON.parse(await request('torrents/info')) as { hash: string }[]
        expect(await torrents()).toEqual([])

        await listenarr.send('POST', '/api/v1/download-clients', {
          id: clientId,
          name: clientName,
          type: 'qbittorrent',
          host: '127.0.0.1',
          port: 8080,
          username: 'admin',
          password,
          isEnabled: true,
        })

        await use({
          name,
          torrents,
          async remove(hash) {
            await request(
              'torrents/delete',
              new URLSearchParams({ hashes: hash, deleteFiles: 'false' }),
            )
          },
        })
      } finally {
        await docker('rm', '--force', '--volumes', name)
      }
    },
    { timeout: 180_000 },
  ],
})

async function getGrab(listenarr: ListenarrApi) {
  const book = (await listenarr.library()).find((item) => item.asin === 'B002V5CW08')
  if (!book) throw new Error('The seeded Robinson Crusoe audiobook is missing.')
  const response = await listenarr.send('POST', '/api/v1/test/downloads/reference')
  const { downloadReference } = (await response.json()) as { downloadReference: string }
  return { audiobookId: book.id, downloadClientId: clientId, downloadReference }
}

type Grab = Awaited<ReturnType<typeof getGrab>>

async function assertDuplicate(request: APIRequestContext, grab: Grab) {
  const response = await request.post(downloadPath, {
    data: grab,
    headers: await antiforgeryHeaders(request),
  })
  expect(response.status()).toBe(409)
  expect(await response.json()).toMatchObject({
    message: 'A download for this audiobook is already active',
  })
}

async function grabBook(listenarr: ListenarrApi, grab: Grab) {
  const response = await listenarr.send('POST', downloadPath, grab)
  const { downloadId } = (await response.json()) as { downloadId: string }
  expect(downloadId).toBeTruthy()
  return downloadId
}

downloadTest(
  'deleting the sole torrent allows the same audiobook to be grabbed again',
  async ({ listenarr, request, qbittorrent }) => {
    const grab = await getGrab(listenarr)
    const originalId = await grabBook(listenarr, grab)
    await expect.poll(qbittorrent.torrents).toHaveLength(1)
    const [torrent] = await qbittorrent.torrents()
    if (!torrent) throw new Error('qBittorrent lost the original test torrent.')
    await assertDuplicate(request, grab)

    await qbittorrent.remove(torrent.hash)
    await expect.poll(qbittorrent.torrents).toHaveLength(0)
    await listenarr.send('POST', `/api/v1/test/downloads/${originalId}/reconcile`)

    const replacementId = await grabBook(listenarr, grab)
    expect(replacementId).not.toBe(originalId)
    await expect
      .poll(async () => (await qbittorrent.torrents()).map(({ hash }) => hash))
      .toEqual([torrent.hash])
  },
)

downloadTest(
  'Activity hides a download during a client outage and restores it on recovery',
  async ({ listenarr, page, request, qbittorrent }) => {
    downloadTest.setTimeout(150_000)
    const grab = await getGrab(listenarr)
    const downloadId = await grabBook(listenarr, grab)
    await expect.poll(qbittorrent.torrents).toHaveLength(1)
    const originalHashes = (await qbittorrent.torrents()).map(({ hash }) => hash)

    await page.goto('/activity')
    const row = page.locator('.queue-row').filter({
      has: page.locator(`a[href="/audiobooks/${grab.audiobookId}"]`),
    })
    const warning = page.getByText('Some queue data is unavailable')
    const refresh = page.getByRole('button', { name: 'Refresh' })
    await expect(row).toBeVisible()

    await docker('pause', qbittorrent.name)
    try {
      await refresh.click()
      await expect(warning).toBeVisible({ timeout: 45_000 })
      await expect(page.getByText(`${clientName} unavailable`, { exact: false })).toBeVisible()
      await expect(row).toHaveCount(0)
      const reconciliation = await request.post(`/api/v1/test/downloads/${downloadId}/reconcile`, {
        headers: await antiforgeryHeaders(request),
        timeout: 60_000,
      })
      expect(reconciliation.status()).toBe(204)
    } finally {
      await docker('unpause', qbittorrent.name)
    }

    await expect(async () => {
      await refresh.click({ timeout: 5_000 })
      await expect(row).toBeVisible({ timeout: 5_000 })
    }).toPass({ timeout: 45_000 })
    await expect(warning).toHaveCount(0)
    await assertDuplicate(request, grab)
    expect((await qbittorrent.torrents()).map(({ hash }) => hash)).toEqual(originalHashes)
  },
)
