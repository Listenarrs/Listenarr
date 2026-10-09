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
import { flushPromises, mount } from '@vue/test-utils'
import { setActivePinia, createPinia } from 'pinia'
import { describe, it, beforeEach, expect, vi } from 'vitest'
import { API_BASE_PATH } from '@/services/apiBase'
import { useLibraryStore } from '@/stores/library'
import { useScanNotificationsStore } from '@/stores/scanNotifications'
import { useFilesystemReadinessStore } from '@/stores/filesystemReadiness'
import { apiService, ensureImageCached } from '@/services/api'
import AudiobookDetailViewCmp from '@/views/library/AudiobookDetailView.vue'
const routerPushMock = vi.fn()
const routeParams = vi.hoisted(() => ({ id: '5' }))
// Mock useRoute to provide params for the detail view
vi.mock('vue-router', () => ({
  useRoute: () => ({ params: routeParams }),
  useRouter: () => ({ push: routerPushMock }),
}))

// Mock api service ensureImageCached and getImageUrl
vi.mock('@/services/api', () => ({
  apiService: {
    getImageUrl: vi.fn((url: string) => url || 'https://via.placeholder.com/300x450?text=No+Image'),
    getQualityProfiles: vi.fn(async () => []),
    getLibrary: vi.fn(async () => []),
    getAudiobook: vi.fn(async () => undefined),
    scanAudiobook: vi.fn(),
    getWeakStorageMissingFiles: vi.fn(async () => ({ items: [] })),
    confirmWeakStorageMissingFiles: vi.fn(),
  },
  ensureImageCached: vi.fn(async () => true),
}))

// Mock signalr service to provide missing hooks (e.g., onScanJobUpdate)
vi.mock('@/services/signalr', () => ({
  signalRService: {
    connect: vi.fn(async () => undefined),
    onQueueUpdate: vi.fn(() => () => undefined),
    onFilesRemoved: vi.fn(() => () => undefined),
    onToast: vi.fn(() => () => undefined),
    onAudiobookUpdate: vi.fn(() => () => undefined),
    onDownloadUpdate: vi.fn(() => () => undefined),
    onDownloadsList: vi.fn(() => () => undefined),
    onScanJobUpdate: vi.fn(() => () => undefined),
  },
}))

describe('AudiobookDetailView image recache behavior', () => {
  beforeEach(() => {
    const pinia = createPinia()
    setActivePinia(pinia)
    vi.clearAllMocks()
    routeParams.id = '5'
    vi.mocked(apiService.getAudiobook)
      .mockReset()
      .mockResolvedValue(undefined as never)
  })

  it('calls ensureImageCached for the audiobook cover on load', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const imagePath = `${API_BASE_PATH}/images/ASIN000005`
    const store = useLibraryStore()
    store.audiobooks = [
      { id: 5, title: 'Detail Book', imageUrl: imagePath, files: [] },
    ] as unknown as ReturnType<typeof useLibraryStore>['audiobooks']

    store.fetchLibrary = vi.fn(async () => undefined)

    mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await new Promise((r) => setTimeout(r, 10))

    expect(ensureImageCached).toHaveBeenCalled()
    const ensureImageCachedMock = ensureImageCached as unknown as {
      mock: { calls: Array<[string]> }
    }
    expect(ensureImageCachedMock.mock.calls[0]?.[0]).toBe(imagePath)
  })

  it('navigates to the author, narrator, publisher, series, and genre collections when their tags are clicked', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 5,
        title: 'Detail Book',
        authors: ['Brandon Sanderson'],
        narrators: ['Michael Kramer'],
        publisher: 'Tor Audio',
        series: 'Mistborn',
        genres: ['Fantasy'],
        files: [],
      },
    ] as unknown as ReturnType<typeof useLibraryStore>['audiobooks']

    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await new Promise((r) => setTimeout(r, 10))

    const authorTag = wrapper
      .findAll('.detail-link-tag')
      .find((tag) => tag.text().includes('Brandon Sanderson'))
    const narratorTag = wrapper
      .findAll('.detail-link-tag')
      .find((tag) => tag.text().includes('Michael Kramer'))
    const publisherTag = wrapper
      .findAll('.detail-link-tag')
      .find((tag) => tag.text().includes('Tor Audio'))
    const seriesTag = wrapper
      .findAll('.detail-link-tag')
      .find((tag) => tag.text().includes('Mistborn'))
    const genreTag = wrapper
      .findAll('.detail-link-tag')
      .find((tag) => tag.text().includes('Fantasy'))

    expect(authorTag).toBeTruthy()
    expect(narratorTag).toBeTruthy()
    expect(publisherTag).toBeTruthy()
    expect(seriesTag).toBeTruthy()
    expect(genreTag).toBeTruthy()

    await authorTag!.trigger('click')

    expect(routerPushMock).toHaveBeenCalledWith('/collection/author/Brandon%20Sanderson')

    await narratorTag!.trigger('click')

    expect(routerPushMock).toHaveBeenCalledWith('/collection/narrator/Michael%20Kramer')

    await publisherTag!.trigger('click')

    expect(routerPushMock).toHaveBeenCalledWith('/collection/publisher/Tor%20Audio')

    await seriesTag!.trigger('click')

    expect(routerPushMock).toHaveBeenCalledWith('/collection/series/Mistborn')

    await genreTag!.trigger('click')

    expect(routerPushMock).toHaveBeenCalledWith('/collection/genre/Fantasy')
  })

  it('opens the edit metadata modal from the detail view action', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 5,
        title: 'Detail Book',
        authors: ['Author One'],
        files: [],
      },
    ] as unknown as ReturnType<typeof useLibraryStore>['audiobooks']

    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(AudiobookDetailViewCmp, {
      global: {
        plugins: [pinia],
        stubs: {
          EditAudiobookModal: {
            name: 'EditAudiobookModal',
            props: ['isOpen'],
            template: '<div class="edit-audiobook-modal-stub" :data-open="String(isOpen)" />',
          },
        },
      },
    })
    await new Promise((r) => setTimeout(r, 10))

    const editButton = wrapper.find('button[aria-label="Edit Metadata"]')
    expect(editButton.exists()).toBe(true)

    await editButton.trigger('click')
    await new Promise((r) => setTimeout(r, 0))

    expect(wrapper.find('.edit-audiobook-modal-stub').attributes('data-open')).toBe('true')
  })

  it('updates the Files tab scan status from the shared scan state', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryStore()
    const scanNotificationsStore = useScanNotificationsStore()
    store.audiobooks = [{ id: 5, title: 'Detail Book', files: [] }] as unknown as ReturnType<
      typeof useLibraryStore
    >['audiobooks']
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await new Promise((r) => setTimeout(r, 10))

    const filesTab = wrapper.findAll('.tab').find((tab) => tab.text().includes('Files'))
    expect(filesTab).toBeTruthy()
    await filesTab!.trigger('click')

    scanNotificationsStore.applyUpdate({
      jobId: 'internal-scan-5',
      audiobookId: 5,
      status: 'Processing',
    })
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.scan-job-status').exists()).toBe(false)

    scanNotificationsStore.registerManualScan('scan-job-5', 5)
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.scan-job-status').text()).toContain('scan-job-5')
    expect(wrapper.find('.scan-job-status').text()).toContain('Queued')

    scanNotificationsStore.applyUpdate({
      jobId: 'scan-job-5',
      audiobookId: 5,
      status: 'Completed',
      found: 2,
      created: 1,
    })
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.scan-job-status').text()).toContain('Completed')
    expect(wrapper.find('.scan-job-status').text()).not.toContain('Queued / Processing')

    scanNotificationsStore.clearFinished()
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.scan-job-status').exists()).toBe(false)
  })

  it('shows the newest visible manual scan for the audiobook', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryStore()
    const scanNotificationsStore = useScanNotificationsStore()
    store.audiobooks = [{ id: 5, title: 'Detail Book', files: [] }] as unknown as ReturnType<
      typeof useLibraryStore
    >['audiobooks']
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await new Promise((r) => setTimeout(r, 10))

    const filesTab = wrapper.findAll('.tab').find((tab) => tab.text().includes('Files'))
    await filesTab!.trigger('click')

    scanNotificationsStore.registerManualScan('older-scan', 5)
    scanNotificationsStore.applyUpdate({
      jobId: 'older-scan',
      audiobookId: 5,
      status: 'Completed',
    })
    await new Promise((resolve) => setTimeout(resolve, 2))
    scanNotificationsStore.registerManualScan('newer-scan', 5)
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.scan-job-status').text()).toContain('newer-scan')
    expect(wrapper.find('.scan-job-status').text()).toContain('Queued')
    expect(wrapper.find('.scan-job-status').text()).not.toContain('older-scan')
  })

  it('registers an accepted Scan Folder job for global notification progress', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    useFilesystemReadinessStore().readiness = {
      isReady: true,
      status: 'ready',
      databaseConnected: true,
      migrationsCurrent: true,
      errorCode: null,
      filesystemReady: true,
      filesystemStatus: 'Ready',
      filesystemPhase: null,
      filesystemErrorCode: null,
      filesystemErrorMessage: null,
    }
    const store = useLibraryStore()
    const scanNotificationsStore = useScanNotificationsStore()
    store.audiobooks = [{ id: 5, title: 'Detail Book', files: [] }] as unknown as ReturnType<
      typeof useLibraryStore
    >['audiobooks']
    store.fetchLibrary = vi.fn(async () => undefined)
    vi.mocked(apiService.scanAudiobook).mockResolvedValue({
      message: 'Scan enqueued',
      found: 0,
      created: 0,
      jobId: 'scan-job-5',
    })

    const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await new Promise((r) => setTimeout(r, 10))

    const scanButton = wrapper.find('button[aria-label="Scan Folder"]')
    expect(scanButton.exists()).toBe(true)
    await scanButton.trigger('click')
    await new Promise((r) => setTimeout(r, 0))

    expect(apiService.scanAudiobook).toHaveBeenCalledWith(5)
    expect(scanNotificationsStore.jobs).toHaveLength(1)
    expect(scanNotificationsStore.jobs[0]).toMatchObject({
      jobId: 'scan-job-5',
      audiobookId: 5,
      status: 'Queued',
      visible: true,
    })
  })

  it('keeps the newest audiobook detail response when terminal scans overlap', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryStore()
    const initial = { id: 5, title: 'Initial details', files: [] }
    store.audiobooks = [initial] as unknown as typeof store.audiobooks
    const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await flushPromises()
    type Detail = Awaited<ReturnType<typeof apiService.getAudiobook>>
    let resolveOlder!: (book: Detail) => void
    let resolveNewer!: (book: Detail) => void
    vi.mocked(apiService.getAudiobook)
      .mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            resolveOlder = resolve
          }),
      )
      .mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            resolveNewer = resolve
          }),
      )
    const scans = useScanNotificationsStore()
    scans.registerManualScan('older-detail', 5)
    scans.applyUpdate({ jobId: 'older-detail', audiobookId: 5, status: 'Completed' })
    await flushPromises()
    scans.registerManualScan('newer-detail', 5)
    scans.applyUpdate({ jobId: 'newer-detail', audiobookId: 5, status: 'Completed' })
    await flushPromises()

    resolveNewer({
      ...initial,
      title: 'Newest details',
      files: [{ id: 12, path: 'new.m4b' }],
    } as Detail)
    await flushPromises()
    resolveOlder({
      ...initial,
      title: 'Obsolete details',
      files: [{ id: 11, path: 'old.m4b' }],
    } as Detail)
    await flushPromises()

    expect(wrapper.text()).toContain('Newest details')
    expect(wrapper.text()).not.toContain('Obsolete details')
    const detail = (wrapper.vm as unknown as { audiobook: Detail }).audiobook
    expect(detail?.files?.map((file) => file.id)).toEqual([12])
    wrapper.unmount()
  })

  it.each(['route', 'unmount'] as const)(
    'ignores a pending scan detail response after %s invalidates the view',
    async (invalidate) => {
      const pinia = createPinia()
      setActivePinia(pinia)
      const store = useLibraryStore()
      const initial = { id: 5, title: 'Initial details', files: [] }
      store.audiobooks = [initial] as unknown as typeof store.audiobooks
      const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
      await flushPromises()
      type Detail = Awaited<ReturnType<typeof apiService.getAudiobook>>
      let resolveDetail!: (book: Detail) => void
      vi.mocked(apiService.getAudiobook).mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            resolveDetail = resolve
          }),
      )
      const scans = useScanNotificationsStore()
      scans.registerManualScan('obsolete-view', 5)
      scans.applyUpdate({ jobId: 'obsolete-view', audiobookId: 5, status: 'Completed' })
      await flushPromises()
      const missingFileCalls = vi.mocked(apiService.getWeakStorageMissingFiles).mock.calls.length
      const view = wrapper.vm as unknown as { audiobook: Detail }
      if (invalidate === 'route') routeParams.id = '6'
      else wrapper.unmount()

      resolveDetail({ ...initial, title: 'Obsolete details' } as Detail)
      await flushPromises()

      expect(view.audiobook?.title).toBe('Initial details')
      expect(apiService.getWeakStorageMissingFiles).toHaveBeenCalledTimes(missingFileCalls)
      if (invalidate === 'route') wrapper.unmount()
    },
  )

  it('keeps the newest weak-storage missing-file response when refreshes overlap', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryStore()
    store.audiobooks = [{ id: 5, title: 'Detail Book', files: [] }] as unknown as ReturnType<
      typeof useLibraryStore
    >['audiobooks']
    store.fetchLibrary = vi.fn(async () => undefined)
    vi.mocked(apiService.getWeakStorageMissingFiles).mockResolvedValueOnce({ items: [] })

    const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await new Promise((resolve) => setTimeout(resolve, 10))

    const scanNotificationsStore = useScanNotificationsStore()

    let resolveOlder!: (value: {
      scanToken: string
      items: Array<{ id: string; audiobookFileId: number; path: string }>
    }) => void
    let resolveNewer!: typeof resolveOlder
    const older = new Promise<Parameters<typeof resolveOlder>[0]>((resolve) => {
      resolveOlder = resolve
    })
    const newer = new Promise<Parameters<typeof resolveNewer>[0]>((resolve) => {
      resolveNewer = resolve
    })
    vi.mocked(apiService.getWeakStorageMissingFiles)
      .mockImplementationOnce(() => older)
      .mockImplementationOnce(() => newer)

    scanNotificationsStore.registerManualScan('older-scan', 5)
    scanNotificationsStore.applyUpdate({
      jobId: 'older-scan',
      audiobookId: 5,
      status: 'Completed',
    })
    await new Promise((resolve) => setTimeout(resolve, 2))
    scanNotificationsStore.registerManualScan('newer-scan', 5)
    scanNotificationsStore.applyUpdate({
      jobId: 'newer-scan',
      audiobookId: 5,
      status: 'Completed',
    })

    resolveNewer({
      scanToken: 'new-token',
      items: [{ id: 'new', audiobookFileId: 12, path: 'newer.m4b' }],
    })
    await Promise.resolve()
    resolveOlder({
      scanToken: 'old-token',
      items: [{ id: 'old', audiobookFileId: 11, path: 'older.m4b' }],
    })
    await new Promise((resolve) => setTimeout(resolve, 0))

    const filesTab = wrapper.findAll('.tab').find((tab) => tab.text().includes('Files'))
    await filesTab!.trigger('click')
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.weak-storage-missing-files').text()).toContain('newer.m4b')
    expect(wrapper.find('.weak-storage-missing-files').text()).not.toContain('older.m4b')
  })

  it('disables Scan Folder while library filesystem initialization is incomplete', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryStore()
    store.audiobooks = [{ id: 5, title: 'Detail Book', files: [] }] as unknown as ReturnType<
      typeof useLibraryStore
    >['audiobooks']
    store.fetchLibrary = vi.fn(async () => undefined)
    useFilesystemReadinessStore().readiness = {
      isReady: true,
      status: 'ready',
      databaseConnected: true,
      migrationsCurrent: true,
      errorCode: null,
      filesystemReady: false,
      filesystemStatus: 'Running',
      filesystemPhase: 'AudiobookFileIdentities',
      filesystemErrorCode: null,
      filesystemErrorMessage: null,
    }

    const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await new Promise((resolve) => setTimeout(resolve, 10))

    const scanButton = wrapper.get('button[aria-label="Scan Folder"]')
    expect(scanButton.attributes('disabled')).toBeDefined()
    expect(scanButton.attributes('title')).toContain('filesystem initialization')
    await scanButton.trigger('click')
    expect(apiService.scanAudiobook).not.toHaveBeenCalled()
  })
})
