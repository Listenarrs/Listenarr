/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'

describe('downloads store queue snapshot reconciliation', () => {
  beforeEach(() => {
    vi.resetModules()
    setActivePinia(createPinia())
  })

  it('removes stale queue-only entries when the queue snapshot switches to the tracked download id', async () => {
    let queueUpdateCallback: ((payload: unknown) => void) | null = null

    vi.doMock('@/services/signalr', () => ({
      signalRService: {
        onDownloadUpdate: vi.fn(() => () => undefined),
        onDownloadsList: vi.fn(() => () => undefined),
        onQueueUpdate: vi.fn((cb: (payload: unknown) => void) => {
          queueUpdateCallback = cb
          return () => undefined
        }),
        onAudiobookUpdate: vi.fn(() => () => undefined),
      },
    }))

    vi.doMock('@/services/api', () => ({
      apiService: {
        getDownloads: vi.fn(async () => [
          {
            id: 'tracked-artemis',
            title: 'Artemis',
            artist: 'Andy Weir',
            album: '',
            originalUrl: 'magnet:?xt=urn:btih:tracked-artemis',
            status: 'Completed',
            progress: 100,
            totalSize: 489100000,
            downloadedSize: 489100000,
            downloadPath: 'C:\\downloads\\Artemis',
            finalPath: 'C:\\library\\Andy Weir\\Artemis',
            startedAt: '2026-03-26T10:00:00Z',
            completedAt: '2026-03-26T10:30:00Z',
            downloadClientId: 'qb-1',
            metadata: { ClientDownloadId: 'HASH-ARTEMIS' },
          },
        ]),
      },
    }))

    const { useDownloadsStore } = await import('@/stores/downloads')
    const store = useDownloadsStore()

    await store.loadDownloads()

    expect(store.downloads).toHaveLength(1)
    expect(store.downloads[0]?.id).toBe('tracked-artemis')
    expect(queueUpdateCallback).not.toBeNull()

    queueUpdateCallback?.({
      items: [
        {
          id: 'HASH-ARTEMIS',
          title: 'Andy Weir - Artemis - 2017 - 125 kbps.m4b',
          status: 'completed',
          progress: 100,
          size: 489100000,
          downloaded: 489100000,
          downloadSpeed: 77300,
          quality: 'Unknown',
          downloadClient: 'QBIT',
          downloadClientId: 'qb-1',
          downloadClientType: 'qbittorrent',
          addedAt: '2026-03-26T10:00:00Z',
          canPause: false,
          canRemove: true,
        },
      ],
      clients: [],
      generatedAt: '2026-03-26T10:31:00Z',
      hasStaleData: false,
      hasUnavailableClients: false,
    })

    expect(store.downloads.map((download) => download.id)).toContain('HASH-ARTEMIS')
    expect(store.completedDownloads.map((download) => download.title)).toContain(
      'Andy Weir - Artemis - 2017 - 125 kbps.m4b',
    )

    queueUpdateCallback?.({
      items: [
        {
          id: 'tracked-artemis',
          title: 'Artemis',
          status: 'downloading',
          progress: 100,
          size: 489100000,
          downloaded: 489100000,
          downloadSpeed: 77300,
          quality: 'Unknown',
          downloadClient: 'QBIT',
          downloadClientId: 'qb-1',
          downloadClientType: 'qbittorrent',
          addedAt: '2026-03-26T10:00:00Z',
          canPause: false,
          canRemove: true,
        },
      ],
      clients: [],
      generatedAt: '2026-03-26T10:32:00Z',
      hasStaleData: false,
      hasUnavailableClients: false,
    })

    expect(store.downloads.map((download) => download.id)).toEqual(['tracked-artemis'])
    expect(
      store.downloads.some(
        (download) => download.title === 'Andy Weir - Artemis - 2017 - 125 kbps.m4b',
      ),
    ).toBe(false)
  })
})
