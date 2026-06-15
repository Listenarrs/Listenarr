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
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { flushAsync } from '@/test/utils/wait'

type ActivityItem = {
  id: string
  title?: string
  status?: string
  downloadClientId?: string
  downloadClient?: string
  downloadClientType?: string
  progress?: number
  canRemove?: boolean
}

type ActivityViewVm = {
  allActivityItems: ActivityItem[]
  filteredQueue: ActivityItem[]
  filterText: string
  showRemoveModal: boolean
  clientHasQueueEntry: boolean | null
  queueHealthClients: Array<{ name: string; isUnavailable?: boolean }>
  removeFromQueue: (item: ActivityItem) => Promise<void> | void
  confirmRemove: () => Promise<void>
}

const mockSignalR = () => {
  vi.doMock('@/services/signalr', () => ({
    signalRService: {
      onQueueUpdate: vi.fn(() => () => undefined),
    },
  }))
}

const mockApi = (overrides: Record<string, unknown> = {}) => {
  const apiService = {
    getQueue: vi.fn(async () => []),
    removeFromQueue: vi.fn(async () => undefined),
    cancelDownload: vi.fn(async () => undefined),
    ...overrides,
  }

  vi.doMock('@/services/api', () => ({
    apiService,
  }))

  return apiService
}

const mockConfigurationStore = (showCompletedExternalDownloads = false) => {
  vi.doMock('@/stores/configuration', () => ({
    useConfigurationStore: () => ({
      applicationSettings: { showCompletedExternalDownloads },
      loadApplicationSettings: vi.fn(async () => undefined),
    }),
  }))
}

const mockLibraryStore = (audiobooks: Array<{ id: number; title: string }> = []) => {
  vi.doMock('@/stores/library', () => ({
    useLibraryStore: () => ({
      audiobooks,
    }),
  }))
}

const mockDownloadsStore = (overrides: Record<string, unknown> = {}) => {
  const store = {
    activeDownloads: [],
    completedDownloads: [],
    failedDownloads: [],
    loadDownloads: vi.fn(async () => undefined),
    ...overrides,
  }

  vi.doMock('@/stores/downloads', () => ({
    useDownloadsStore: () => store,
  }))

  return store
}

const mountActivityView = async () => {
  const { default: ActivityViewComponent } = await import('@/views/activity/ActivityView.vue')
  const wrapper = mount(ActivityViewComponent, {
    global: {
      stubs: {
        CustomSelect: true,
        RouterLink: { template: '<a><slot /></a>' },
      },
    },
  })

  await flushPromises()
  await flushAsync()
  return wrapper
}

describe('ActivityView', () => {
  beforeEach(() => {
    vi.resetModules()
    vi.clearAllMocks()
    vi.spyOn(globalThis, 'setInterval').mockReturnValue(1 as any as ReturnType<typeof setInterval>)
    vi.spyOn(globalThis, 'clearInterval').mockImplementation(() => undefined)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('includes completed external downloads from the downloads store in the unified list', async () => {
    mockSignalR()
    mockApi()
    mockConfigurationStore(false)
    mockLibraryStore()
    mockDownloadsStore({
      completedDownloads: [
        {
          id: 'd1',
          status: 'Completed',
          progress: 100,
          downloadClientId: 'SABnzbd',
          startedAt: new Date().toISOString(),
          title: 'One',
          downloadedSize: 1000,
          totalSize: 1000,
        },
        {
          id: 'd2',
          status: 'Completed',
          progress: 100,
          downloadClientId: 'qbittorrent',
          startedAt: new Date().toISOString(),
          title: 'Two',
          downloadedSize: 2000,
          totalSize: 2000,
        },
        {
          id: 'd3',
          status: 'Completed',
          progress: 100,
          downloadClientId: 'transmission',
          startedAt: new Date().toISOString(),
          title: 'Three',
          downloadedSize: 3000,
          totalSize: 3000,
        },
        {
          id: 'd4',
          status: 'Completed',
          progress: 100,
          downloadClientId: 'nzbget',
          startedAt: new Date().toISOString(),
          title: 'Four',
          downloadedSize: 4000,
          totalSize: 4000,
        },
      ],
    })

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as any as ActivityViewVm

    expect(vm.allActivityItems.map((item) => item.id)).toEqual(
      expect.arrayContaining(['d1', 'd2', 'd3', 'd4']),
    )
    expect(vm.filteredQueue).toHaveLength(4)
  })

  it('filters the unified activity list by text', async () => {
    mockSignalR()
    mockApi()
    mockConfigurationStore(true)
    mockLibraryStore()
    mockDownloadsStore({
      completedDownloads: [
        {
          id: 'd1',
          status: 'Completed',
          progress: 100,
          downloadClientId: 'SABnzbd',
          startedAt: new Date().toISOString(),
          title: 'One',
          downloadedSize: 1000,
          totalSize: 1000,
        },
        {
          id: 'd2',
          status: 'Completed',
          progress: 100,
          downloadClientId: 'qbittorrent',
          startedAt: new Date().toISOString(),
          title: 'Two',
          downloadedSize: 2000,
          totalSize: 2000,
        },
      ],
    })

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as any as ActivityViewVm

    vm.filterText = 'two'
    await flushPromises()

    expect(vm.filteredQueue).toHaveLength(1)
    expect(vm.filteredQueue[0]?.id).toBe('d2')
  })

  it('removes a queue-backed item from the client', async () => {
    const queueItem = {
      id: 'q1',
      title: 'Queue Item',
      status: 'downloading',
      progress: 50,
      size: 1000,
      downloaded: 500,
      downloadClientId: 'qbittorrent',
      downloadClient: 'qbittorrent',
      canRemove: true,
    }

    mockSignalR()
    const apiService = mockApi({
      getQueue: vi.fn(async () => [queueItem]),
    })
    mockConfigurationStore(false)
    mockLibraryStore()
    mockDownloadsStore()

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as any as ActivityViewVm
    const item = vm.allActivityItems.find((entry) => entry.id === 'q1')

    expect(item).toBeDefined()

    await vm.removeFromQueue(item!)
    expect(vm.showRemoveModal).toBe(true)
    expect(vm.clientHasQueueEntry).toBe(true)

    await vm.confirmRemove()
    expect(apiService.removeFromQueue).toHaveBeenCalledWith('q1', 'qbittorrent')
  })

  it('offers Listenarr-only removal when an external item is no longer in the client queue', async () => {
    mockSignalR()
    const apiService = mockApi({
      getQueue: vi.fn(async () => []),
    })
    mockConfigurationStore(false)
    mockLibraryStore()
    const downloadsStore = mockDownloadsStore({
      completedDownloads: [
        {
          id: 'ext-1',
          status: 'Completed',
          progress: 100,
          downloadClientId: 'SABnzbd',
          startedAt: new Date().toISOString(),
          title: 'Completed External',
          downloadedSize: 100,
          totalSize: 100,
        },
      ],
    })

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as any as ActivityViewVm
    const item = vm.allActivityItems.find((entry) => entry.id === 'ext-1')

    expect(item).toBeDefined()

    await vm.removeFromQueue(item!)
    expect(vm.showRemoveModal).toBe(true)
    expect(vm.clientHasQueueEntry).toBe(false)

    await vm.confirmRemove()
    expect(apiService.cancelDownload).toHaveBeenCalledWith('ext-1')
    expect(downloadsStore.loadDownloads).toHaveBeenCalled()
  })

  it('deduplicates failed queue items against failed download records', async () => {
    const queueFailed = {
      id: 'q1',
      title: 'Queue Failed',
      status: 'failed',
      progress: 0,
      size: 0,
      downloaded: 0,
      downloadClientId: 'qbittorrent',
      downloadClient: 'qbittorrent',
    }

    mockSignalR()
    mockApi({
      getQueue: vi.fn(async () => [queueFailed]),
    })
    mockConfigurationStore(false)
    mockLibraryStore()
    mockDownloadsStore({
      failedDownloads: [
        {
          id: 'q1',
          status: 'Failed',
          progress: 0,
          downloadClientId: 'qbittorrent',
          title: 'Queue Failed (DB copy)',
        },
        { id: 'd1', status: 'Failed', progress: 0, downloadClientId: 'DDL', title: 'DDL Failed' },
      ],
    })

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as any as ActivityViewVm

    expect(vm.allActivityItems).toHaveLength(2)
    expect(vm.allActivityItems.filter((item) => item.id === 'q1')).toHaveLength(1)
    expect(vm.allActivityItems.some((item) => item.id === 'd1')).toBe(true)
  })

  it('removes a failed DDL download through Listenarr cancellation', async () => {
    mockSignalR()
    const apiService = mockApi()
    mockConfigurationStore(false)
    mockLibraryStore()
    const downloadsStore = mockDownloadsStore({
      failedDownloads: [
        { id: 'd1', status: 'Failed', progress: 0, downloadClientId: 'DDL', title: 'DDL Failed' },
      ],
    })

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as any as ActivityViewVm
    const item = vm.allActivityItems.find((entry) => entry.id === 'd1')

    expect(item).toBeDefined()

    await vm.removeFromQueue(item!)
    expect(vm.clientHasQueueEntry).toBe(true)

    await vm.confirmRemove()
    expect(apiService.cancelDownload).toHaveBeenCalledWith('d1')
    expect(downloadsStore.loadDownloads).toHaveBeenCalled()
  })

  it('maps ImportPending and ImportBlocked downloads to activity rows', async () => {
    mockSignalR()
    mockApi()
    mockConfigurationStore(false)
    mockLibraryStore()
    mockDownloadsStore({
      activeDownloads: [
        {
          id: 'd-importpending',
          title: 'Import Pending',
          status: 'ImportPending',
          progress: 99,
          totalSize: 1000,
          downloadedSize: 990,
          downloadClientId: 'qbittorrent',
          startedAt: new Date().toISOString(),
        },
      ],
      failedDownloads: [
        {
          id: 'd-importblocked',
          title: 'Import Blocked',
          status: 'ImportBlocked',
          progress: 100,
          totalSize: 1000,
          downloadedSize: 1000,
          downloadClientId: 'qbittorrent',
          startedAt: new Date().toISOString(),
        },
      ],
    })

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as any as ActivityViewVm

    expect(vm.allActivityItems.find((item) => item.id === 'd-importpending')?.status).toBe(
      'importpending',
    )
    expect(vm.allActivityItems.find((item) => item.id === 'd-importblocked')?.status).toBe(
      'importblocked',
    )
  })

  it('shows unavailable client health even when no queue items are returned', async () => {
    mockSignalR()
    mockApi({
      getQueue: vi.fn(async () => ({
        items: [],
        clients: [
          {
            clientId: 'qb-1',
            clientName: 'qBittorrent',
            clientType: 'qbittorrent',
            snapshotState: 'unavailable',
            isStaleSnapshot: false,
            isUnavailable: true,
            snapshotFailureReason: 'timeout',
            itemCount: 0,
          },
        ],
        generatedAt: new Date().toISOString(),
        hasStaleData: false,
        hasUnavailableClients: true,
      })),
    })
    mockConfigurationStore(false)
    mockLibraryStore()
    mockDownloadsStore()

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as any as ActivityViewVm

    expect(vm.queueHealthClients).toHaveLength(1)
    expect(vm.queueHealthClients[0]?.name).toBe('qBittorrent')
    expect(wrapper.text()).toContain('Some queue data is unavailable')
    expect(wrapper.text()).toContain('qBittorrent unavailable after a timeout')
  })

  it('prefers the queue snapshot over an external active download with the same tracked id', async () => {
    mockSignalR()
    mockApi({
      getQueue: vi.fn(async () => ({
        items: [
          {
            id: 'tracked-artemis',
            title: 'Artemis',
            status: 'completed',
            progress: 100,
            size: 489100000,
            downloaded: 489100000,
            downloadSpeed: 77300,
            quality: 'Unknown',
            downloadClient: 'QBIT',
            downloadClientId: 'qb-1',
            downloadClientType: 'qbittorrent',
            addedAt: new Date().toISOString(),
            canPause: false,
            canRemove: true,
          },
        ],
        clients: [],
        generatedAt: new Date().toISOString(),
        hasStaleData: false,
        hasUnavailableClients: false,
      })),
    })
    mockConfigurationStore(true)
    mockLibraryStore()
    mockDownloadsStore({
      activeDownloads: [
        {
          id: 'tracked-artemis',
          title: 'Artemis',
          status: 'Downloading',
          progress: 100,
          totalSize: 489100000,
          downloadedSize: 489100000,
          downloadClientId: 'qb-1',
          startedAt: new Date().toISOString(),
        },
      ],
    })

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as any as ActivityViewVm

    expect(vm.allActivityItems).toHaveLength(1)
    expect(vm.allActivityItems[0]?.id).toBe('tracked-artemis')
    expect(vm.allActivityItems[0]?.status).toBe('completed')
    expect(vm.allActivityItems[0]?.title).toBe('Artemis')
  })
})
