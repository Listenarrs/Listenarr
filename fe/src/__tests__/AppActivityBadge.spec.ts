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
import { mount, RouterLinkStub, type VueWrapper } from '@vue/test-utils'
import { computed, reactive, ref } from 'vue'
import { createPinia, setActivePinia } from 'pinia'

const deleteOperationsMock = vi.hoisted(() => ({
  operations: [] as Array<{
    id: string
    kind: 'single' | 'bulk'
    title: string
    audiobookId?: number
    status: 'deleting' | 'completed' | 'failed'
    progress: number
    total: number
    processed: number
    deleted: number
    failed: number
    currentTitle?: string
    startedAt: string
    error?: string
    dismissed?: boolean
  }>,
  dismiss: vi.fn(),
  clearFinished: vi.fn(),
}))

const scanSignalRMock = vi.hoisted(() => ({
  callback: null as
    | ((job: {
        jobId: string
        audiobookId?: number | null
        status: string
        found?: number
        created?: number
        error?: string
      }) => void)
    | null,
}))

const notificationSignalRMock = vi.hoisted(() => ({
  callback: null as
    | ((notification: { id: string; title: string; message: string; timestamp: string }) => void)
    | null,
}))

const scanJobStatusMock = vi.hoisted(() =>
  vi.fn(async (jobId: string) => ({
    id: jobId,
    audiobookId: 42,
    status: 'Queued',
    enqueuedAt: '2026-08-08T12:00:00Z',
    canRequeue: true,
  })),
)

const moveJobsMock = vi.hoisted(() => ({
  trackedJobs: [] as Array<{
    jobId: string
    audiobookId?: number
    status: string
    progress: number
    phase?: string
    target?: string
  }>,
  start: vi.fn(),
  stop: vi.fn(),
  loadActiveJobs: vi.fn(async () => undefined),
}))

type ScanTaskMock = {
  jobId: string | null
  rootFolderId: number
  folderName: string | null
  status: 'starting' | 'running' | 'done' | 'failed' | 'canceled'
  startedAt: string
  finishedAt: string | null
  error: string | null
  itemCount: number | null
}

// The Library Import store, as far as the bell uses it: its scan task, kept reactive so the
// notification follows the task as it changes. Made once per file (resetModules keeps mocks).
const libraryImportMock = vi.hoisted(() => ({
  store: null as null | {
    scanTask: ScanTaskMock | null
    readonly scanTaskActive: boolean
    restoreScanTask: ReturnType<typeof vi.fn>
    dismissScanTask: ReturnType<typeof vi.fn>
  },
}))

vi.mock('@/stores/libraryImport', async () => {
  const { reactive } = await import('vue')
  const store = reactive({
    scanTask: null as ScanTaskMock | null,
    get scanTaskActive(): boolean {
      return this.scanTask?.status === 'starting' || this.scanTask?.status === 'running'
    },
    restoreScanTask: vi.fn(),
    dismissScanTask: vi.fn(() => {
      if (!store.scanTaskActive) store.scanTask = null
    }),
  })
  libraryImportMock.store = store
  return { useLibraryImportStore: () => store }
})

vi.mock('@/stores/moveJobs', () => ({
  useMoveJobsStore: () => moveJobsMock,
}))

vi.mock('@/stores/libraryDeleteOperations', () => ({
  useLibraryDeleteOperationsStore: () => deleteOperationsMock,
}))

// Mock the downloads store so App.vue picks up the activeDownloads correctly
vi.mock('@/stores/downloads', () => ({
  useDownloadsStore: () => ({
    // activeDownloads is a computed-like value in the real store
    activeDownloads: computed(() => []),
    loadDownloads: vi.fn(async () => undefined),
  }),
}))

// Mock auth so the component proceeds through its authenticated path
vi.mock('@/stores/auth', () => ({
  useAuthStore: () => ({
    user: { authenticated: true },
    loadCurrentUser: vi.fn(async () => undefined),
    logout: vi.fn(async () => undefined),
  }),
}))

// Minimal signalR service stub (no-op event registrations)
vi.mock('@/services/signalr', () => ({
  signalRService: {
    connect: vi.fn(async () => undefined),
    onConnected: vi.fn(() => () => undefined),
    onQueueUpdate: vi.fn(() => () => undefined),
    onFilesRemoved: vi.fn(() => () => undefined),
    onScanJobUpdate: vi.fn((callback) => {
      scanSignalRMock.callback = callback
      return () => {
        if (scanSignalRMock.callback === callback) scanSignalRMock.callback = null
      }
    }),
    onToast: vi.fn(() => () => undefined),
    onDownloadUpdate: vi.fn(() => () => undefined),
    onDownloadsList: vi.fn(() => () => undefined),
    onNotification: vi.fn((callback) => {
      notificationSignalRMock.callback = callback
      return () => undefined
    }),
  },
}))

// Mock API calls used during mount - only return what tests need
vi.mock('@/services/api', () => ({
  apiService: {
    getQueue: vi.fn(async () => []),
    getServiceHealth: vi.fn(async () => ({ version: '0.0.0' })),
    getBootstrapConfig: vi.fn(async () => ({ authenticationRequired: false })),
    getStartupConfig: vi.fn(async () => ({ authenticationRequired: false })),
    getLibrary: vi.fn(async () => []),
    getScanJobStatus: scanJobStatusMock,
  },
}))

vi.mock('@/router', () => ({
  preloadRoute: vi.fn(),
}))

import { createRouter, createMemoryHistory } from 'vue-router'

// Background jobs are under the cog; the bell keeps downloads and messages
const JOBS_BUTTON = '[data-testid="jobs-menu"] .nav-btn'
const JOBS_LIST = '[data-testid="jobs-menu"] .notification-dropdown'
const BELL_BUTTON = '[data-testid="notifications-menu"] .nav-btn'
const BELL_LIST = '[data-testid="notifications-menu"] .notification-dropdown'

describe('App.vue activity badge', () => {
  let wrapper: VueWrapper | undefined

  beforeEach(() => {
    // reset mocks between tests
    vi.resetModules()
    moveJobsMock.trackedJobs.length = 0
    deleteOperationsMock.operations.length = 0
    scanSignalRMock.callback = null
    scanJobStatusMock.mockReset()
    scanJobStatusMock.mockImplementation(async (jobId: string) => ({
      id: jobId,
      audiobookId: 42,
      status: 'Queued',
      enqueuedAt: '2026-08-08T12:00:00Z',
      canRequeue: true,
    }))
    deleteOperationsMock.dismiss.mockReset()
    deleteOperationsMock.clearFinished.mockReset()
    if (libraryImportMock.store) libraryImportMock.store.scanTask = null
    setActivePinia(createPinia())
  })

  afterEach(() => {
    wrapper?.unmount()
    wrapper = undefined
    vi.clearAllMocks()
  })

  // Ensure localStorage APIs exist in the test environment for App.vue session debug helpers
  if (typeof (globalThis as unknown as { localStorage?: unknown }).localStorage === 'undefined') {
    Object.defineProperty(globalThis, 'localStorage', {
      value: {
        _store: {} as Record<string, string>,
        getItem(key: string) {
          return this._store[key] ?? null
        },
        setItem(key: string, value: string) {
          this._store[key] = value + ''
        },
        removeItem(key: string) {
          delete this._store[key]
        },
      },
      configurable: true,
    })
  }

  it('shows active move progress in the jobs menu', async () => {
    moveJobsMock.trackedJobs.push({
      jobId: 'move-1',
      audiobookId: 98,
      status: 'Running',
      progress: 42.4,
      phase: 'Verifying source',
      target: 'D:\\Listenarr Test\\Book',
    })

    const { default: AppComponent } = await import('@/App.vue')
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', name: 'home', component: { template: '<div />' } }],
    })
    await router.push('/')
    await router.isReady().catch(() => {})

    wrapper = mount(AppComponent, {
      global: {
        stubs: { RouterLink: RouterLinkStub, RouterView: true },
        plugins: [createPinia(), router],
      },
    })
    await new Promise((resolve) => setTimeout(resolve, 20))

    await wrapper.find(JOBS_BUTTON).trigger('click')

    const dropdown = wrapper.find(JOBS_LIST)
    expect(dropdown.exists()).toBe(true)
    expect(dropdown.text()).toContain('Moving audiobook')
    expect(dropdown.text()).toContain('Verifying source')
    expect(dropdown.text()).toContain('42%')
    expect(dropdown.find('.progress-fill').attributes('style')).toContain('width: 42.4%')
  })

  it('updates folder scan progress in one job, with a spinner and no fake percentage', async () => {
    const { default: AppComponent } = await import('@/App.vue')
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', name: 'home', component: { template: '<div />' } }],
    })
    await router.push('/')
    await router.isReady().catch(() => {})

    wrapper = mount(AppComponent, {
      global: {
        stubs: { RouterLink: RouterLinkStub, RouterView: true },
        plugins: [createPinia(), router],
      },
    })
    await new Promise((resolve) => setTimeout(resolve, 20))
    expect(scanSignalRMock.callback).not.toBeNull()

    scanSignalRMock.callback?.({
      jobId: 'internal-scan',
      audiobookId: 42,
      status: 'Processing',
    })
    await wrapper.vm.$nextTick()
    await wrapper.find(JOBS_BUTTON).trigger('click')
    expect(wrapper.find(JOBS_LIST).text()).not.toContain('Scanning audiobook folder')

    scanSignalRMock.callback?.({
      jobId: 'scan-1',
      audiobookId: 42,
      status: 'Queued',
    })
    await wrapper.vm.$nextTick()

    let scanNotification = wrapper
      .findAll('.notification-item')
      .find((item) => item.text().includes('Scan queued: audiobook folder'))
    expect(scanNotification?.text()).toContain('Waiting to scan folder')
    expect(scanNotification?.find('.notif-icon .ph-spin').exists()).toBe(true)
    expect(scanNotification?.find('.progress-fill').exists()).toBe(false)
    expect(scanNotification?.text()).not.toMatch(/\d+%/)
    expect(scanNotification?.find('.dismiss-btn').exists()).toBe(false)

    scanSignalRMock.callback?.({
      jobId: 'scan-1',
      audiobookId: 42,
      status: 'Processing',
    })
    await wrapper.vm.$nextTick()

    scanNotification = wrapper
      .findAll('.notification-item')
      .find((item) => item.text().includes('Scanning audiobook folder'))
    expect(scanNotification?.text()).toContain('Scanning folder')
    expect(scanNotification?.find('.notif-icon .ph-spin').exists()).toBe(true)
    expect(scanNotification?.text()).not.toMatch(/\d+%/)
    expect(scanNotification?.find('.dismiss-btn').exists()).toBe(false)

    scanSignalRMock.callback?.({
      jobId: 'scan-1',
      audiobookId: 42,
      status: 'Completed',
      found: 3,
      created: 2,
    })
    await wrapper.vm.$nextTick()

    scanNotification = wrapper
      .findAll('.notification-item')
      .find((item) => item.text().includes('Scan complete: audiobook folder'))
    expect(scanNotification?.text()).toContain('3 files found · 2 added')
    expect(scanNotification?.find('.ph-spin').exists()).toBe(false)
    expect(scanNotification?.find('.dismiss-btn').exists()).toBe(true)
  })

  it('reconciles a missed terminal scan update from the authoritative status endpoint', async () => {
    scanJobStatusMock.mockResolvedValue({
      id: 'scan-reconcile',
      audiobookId: 42,
      status: 'Completed',
      enqueuedAt: '2026-08-08T12:00:00Z',
      canRequeue: true,
    })

    const { default: AppComponent } = await import('@/App.vue')
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', name: 'home', component: { template: '<div />' } }],
    })
    await router.push('/')
    await router.isReady().catch(() => {})

    wrapper = mount(AppComponent, {
      global: {
        stubs: { RouterLink: RouterLinkStub, RouterView: true },
        plugins: [createPinia(), router],
      },
    })
    await new Promise((resolve) => setTimeout(resolve, 20))

    scanSignalRMock.callback?.({
      jobId: 'scan-reconcile',
      audiobookId: 42,
      status: 'Queued',
    })
    await new Promise((resolve) => setTimeout(resolve, 10))

    expect(scanJobStatusMock).toHaveBeenCalledWith('scan-reconcile')

    await wrapper.find(JOBS_BUTTON).trigger('click')
    const dropdown = wrapper.find(JOBS_LIST)
    expect(dropdown.text()).toContain('Scan complete: audiobook folder')
    expect(dropdown.text()).toContain('Folder scan completed')
    expect(dropdown.text()).not.toContain('Waiting to scan folder')
    expect(dropdown.text()).not.toContain('0 files found')
  })

  it('stops scan status reconciliation while logged out and resumes after re-authentication', async () => {
    const authState = reactive({
      user: { authenticated: true },
      loadCurrentUser: vi.fn(async () => undefined),
      logout: vi.fn(async () => undefined),
    })
    vi.doMock('@/stores/auth', () => ({
      useAuthStore: () => authState,
    }))

    const { default: AppComponent } = await import('@/App.vue')
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', name: 'home', component: { template: '<div />' } }],
    })
    await router.push('/')
    await router.isReady().catch(() => {})

    wrapper = mount(AppComponent, {
      global: {
        stubs: { RouterLink: RouterLinkStub, RouterView: true },
        plugins: [createPinia(), router],
      },
    })
    await new Promise((resolve) => setTimeout(resolve, 20))

    scanSignalRMock.callback?.({
      jobId: 'scan-auth-lifecycle',
      audiobookId: 42,
      status: 'Queued',
    })
    await new Promise((resolve) => setTimeout(resolve, 20))
    expect(scanJobStatusMock).toHaveBeenCalled()

    const callsBeforeLogout = scanJobStatusMock.mock.calls.length
    authState.user.authenticated = false
    await wrapper.vm.$nextTick()
    await new Promise((resolve) => setTimeout(resolve, 1600))
    expect(scanJobStatusMock).toHaveBeenCalledTimes(callsBeforeLogout)

    authState.user.authenticated = true
    await wrapper.vm.$nextTick()
    await new Promise((resolve) => setTimeout(resolve, 20))
    expect(scanJobStatusMock.mock.calls.length).toBeGreaterThan(callsBeforeLogout)
  })

  it('fails closed when the authoritative scan job no longer exists', async () => {
    scanJobStatusMock.mockRejectedValue(Object.assign(new Error('not found'), { status: 404 }))

    const { default: AppComponent } = await import('@/App.vue')
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', name: 'home', component: { template: '<div />' } }],
    })
    await router.push('/')
    await router.isReady().catch(() => {})

    wrapper = mount(AppComponent, {
      global: {
        stubs: { RouterLink: RouterLinkStub, RouterView: true },
        plugins: [createPinia(), router],
      },
    })
    await new Promise((resolve) => setTimeout(resolve, 20))

    scanSignalRMock.callback?.({
      jobId: 'scan-lost',
      audiobookId: 42,
      status: 'Queued',
    })
    await new Promise((resolve) => setTimeout(resolve, 10))

    await wrapper.find(JOBS_BUTTON).trigger('click')
    const dropdown = wrapper.find(JOBS_LIST)
    expect(dropdown.text()).toContain('Scan failed: audiobook folder')
    expect(dropdown.text()).toContain('Scan status is no longer available')
    expect(dropdown.text()).not.toContain('Waiting to scan folder')
  })

  it('does not regress a fast manual scan when completion arrives before queued', async () => {
    const { default: AppComponent } = await import('@/App.vue')
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', name: 'home', component: { template: '<div />' } }],
    })
    await router.push('/')
    await router.isReady().catch(() => {})

    wrapper = mount(AppComponent, {
      global: {
        stubs: { RouterLink: RouterLinkStub, RouterView: true },
        plugins: [createPinia(), router],
      },
    })
    await new Promise((resolve) => setTimeout(resolve, 20))

    scanSignalRMock.callback?.({ jobId: 'scan-fast', audiobookId: 42, status: 'Processing' })
    scanSignalRMock.callback?.({
      jobId: 'scan-fast',
      audiobookId: 42,
      status: 'Completed',
      found: 1,
      created: 1,
    })
    await wrapper.vm.$nextTick()
    await wrapper.find(JOBS_BUTTON).trigger('click')
    expect(wrapper.find(JOBS_LIST).text()).not.toContain('scan-fast')
    expect(wrapper.find(JOBS_LIST).text()).not.toContain('Scan complete')

    scanSignalRMock.callback?.({ jobId: 'scan-fast', audiobookId: 42, status: 'Queued' })
    await wrapper.vm.$nextTick()

    const dropdown = wrapper.find(JOBS_LIST)
    expect(dropdown.text()).toContain('Scan complete: audiobook folder')
    expect(dropdown.text()).toContain('1 file found · 1 added')
    expect(dropdown.text()).not.toContain('Scan queued')
  })

  it('shows library delete progress in the jobs menu instead of Activity', async () => {
    deleteOperationsMock.operations.push({
      id: 'delete-bulk-1',
      kind: 'bulk',
      title: 'Deleting 4 audiobooks',
      status: 'deleting',
      progress: 50,
      total: 4,
      processed: 2,
      deleted: 2,
      failed: 0,
      currentTitle: 'Second Book',
      startedAt: '2026-08-08T12:00:00Z',
    })

    const { default: AppComponent } = await import('@/App.vue')
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', name: 'home', component: { template: '<div />' } }],
    })
    await router.push('/')
    await router.isReady().catch(() => {})

    wrapper = mount(AppComponent, {
      global: {
        stubs: { RouterLink: RouterLinkStub, RouterView: true },
        plugins: [createPinia(), router],
      },
    })
    await new Promise((resolve) => setTimeout(resolve, 20))

    await wrapper.find(JOBS_BUTTON).trigger('click')

    const dropdown = wrapper.find(JOBS_LIST)
    expect(dropdown.text()).toContain('Jobs')
    expect(dropdown.text()).toContain('Deleting 4 audiobooks')
    expect(dropdown.text()).toContain('2/4 · Second Book')
    expect(dropdown.text()).toContain('50%')
  })

  it('shows a single delete with a spinner and no fake percentage', async () => {
    deleteOperationsMock.operations.push({
      id: 'delete-single-1',
      kind: 'single',
      title: 'Slow Delete',
      audiobookId: 42,
      status: 'deleting',
      progress: 35,
      total: 1,
      processed: 0,
      deleted: 0,
      failed: 0,
      startedAt: '2026-08-08T12:00:00Z',
    })

    const { default: AppComponent } = await import('@/App.vue')
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', name: 'home', component: { template: '<div />' } }],
    })
    await router.push('/')
    await router.isReady().catch(() => {})

    wrapper = mount(AppComponent, {
      global: {
        stubs: { RouterLink: RouterLinkStub, RouterView: true },
        plugins: [createPinia(), router],
      },
    })
    await new Promise((resolve) => setTimeout(resolve, 20))

    await wrapper.find(JOBS_BUTTON).trigger('click')

    const dropdown = wrapper.find(JOBS_LIST)
    expect(dropdown.text()).toContain('Deleting Slow Delete')
    expect(dropdown.text()).toContain('Removing audiobook from library')
    expect(dropdown.text()).not.toContain('35%')
    expect(dropdown.find('.notif-icon .ph-spin').exists()).toBe(true)
    expect(dropdown.find('.progress-fill').exists()).toBe(false)
  })

  it('counts active downloads correctly even when statuses are lowercase', async () => {
    // replace the downloads mock with one that returns a lowercased status
    const active = ref([
      {
        id: 'dl-1',
        status: 'downloading',
        downloadClientId: 'DDL',
        startedAt: new Date().toISOString(),
      },
    ])

    vi.doMock('@/stores/downloads', () => ({
      useDownloadsStore: () => ({
        activeDownloads: computed(() => active.value),
        loadDownloads: vi.fn(async () => undefined),
      }),
    }))

    // remock API queue to be empty
    vi.doMock('@/services/api', () => ({
      apiService: {
        getQueue: async () => [],
        getServiceHealth: async () => ({ version: '0.0.0' }),
        getBootstrapConfig: async () => ({ authenticationRequired: false }),
        getStartupConfig: async () => ({ authenticationRequired: false }),
        getLibrary: async () => [],
      },
    }))

    // Import App again after changing mocks
    const { default: AppComponent } = await import('@/App.vue')

    // Ensure a router exists so useRoute() injections succeed
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', name: 'home', component: { template: '<div />' } }],
    })
    await router.push('/')
    await router.isReady().catch(() => {})

    wrapper = mount(AppComponent, {
      global: {
        stubs: { RouterLink: RouterLinkStub, RouterView: true },
        plugins: [createPinia(), router],
      },
    })

    // Wait a tick for computed properties in mounted hook
    // Allow async onMounted tasks (SignalR/connect, api fetches) to settle
    await new Promise((r) => setTimeout(r, 20))

    const vm = wrapper.vm as unknown as { activityCount: number }
    // The badge should reflect the single active DDL download
    expect(vm.activityCount).toBe(1)
  })

  it('counts DDL downloads regardless of downloadClientId casing', async () => {
    // downloads list contains a DDL downloadClientId in lowercase
    const active = ref([
      {
        id: 'dl-2',
        status: 'Downloading',
        downloadClientId: 'ddl',
        startedAt: new Date().toISOString(),
      },
    ])

    vi.doMock('@/stores/downloads', () => ({
      useDownloadsStore: () => ({
        activeDownloads: computed(() => active.value),
        loadDownloads: vi.fn(async () => undefined),
      }),
    }))

    vi.doMock('@/services/api', () => ({
      apiService: {
        getQueue: async () => [],
        getServiceHealth: async () => ({ version: '0.0.0' }),
        getBootstrapConfig: async () => ({ authenticationRequired: false }),
        getStartupConfig: async () => ({ authenticationRequired: false }),
        getLibrary: async () => [],
      },
    }))

    const { default: AppComponent } = await import('@/App.vue')

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', name: 'home', component: { template: '<div />' } }],
    })
    await router.push('/')
    await router.isReady().catch(() => {})

    wrapper = mount(AppComponent, {
      global: {
        stubs: { RouterLink: RouterLinkStub, RouterView: true },
        plugins: [createPinia(), router],
      },
    })

    // Allow async onMounted tasks to settle
    await new Promise((r) => setTimeout(r, 20))

    const vm = wrapper.vm as unknown as { activityCount: number }
    expect(vm.activityCount).toBe(1)
  })

  it('prefers queue count when there are no downloads', async () => {
    // downloads empty
    const active = ref([])

    // Mock API and SignalR so both the initial fetch and the real-time push contain the two queue items
    vi.doMock('@/services/api', () => ({
      apiService: {
        getQueue: async () => [
          { id: 'q1', status: 'queued' },
          { id: 'q2', status: 'queued' },
        ],
        getServiceHealth: async () => ({ version: '0.0.0' }),
        getBootstrapConfig: async () => ({ authenticationRequired: false }),
        getStartupConfig: async () => ({ authenticationRequired: false }),
        getLibrary: async () => [],
      },
    }))
    // Also mock SignalR to push the same items
    vi.doMock('@/services/signalr', () => ({
      signalRService: {
        connect: vi.fn(async () => undefined),
        onConnected: vi.fn(() => () => undefined),
        onQueueUpdate: (cb: (items: unknown[]) => void) => {
          cb([
            { id: 'q1', status: 'queued' },
            { id: 'q2', status: 'queued' },
          ])
          return () => undefined
        },
        onFilesRemoved: vi.fn(() => () => undefined),
        onScanJobUpdate: vi.fn(() => () => undefined),
        onToast: vi.fn(() => () => undefined),
        onDownloadUpdate: vi.fn(() => () => undefined),
        onDownloadsList: vi.fn(() => () => undefined),
        onNotification: vi.fn(() => () => undefined),
      },
    }))

    vi.doMock('@/stores/downloads', () => ({
      useDownloadsStore: () => ({
        activeDownloads: computed(() => active.value),
        loadDownloads: vi.fn(async () => undefined),
      }),
    }))

    const { default: AppComponent } = await import('@/App.vue')
    // Ensure a router exists so useRoute() injections succeed
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', name: 'home', component: { template: '<div />' } }],
    })
    await router.push('/')
    await router.isReady().catch(() => {})

    wrapper = mount(AppComponent, {
      global: {
        stubs: { RouterLink: RouterLinkStub, RouterView: true },
        plugins: [createPinia(), router],
      },
    })

    // Allow async onMounted tasks (SignalR/connect, api fetches) to settle
    await new Promise((r) => setTimeout(r, 20))

    const vm = wrapper.vm as unknown as { activityCount: number }
    // With zero active downloads and two queue items, activityCount should reflect the queue
    expect(vm.activityCount).toBe(2)
  })

  it('derives wantedCount from the hydrated library store without polling timers', async () => {
    const setIntervalSpy = vi.spyOn(window, 'setInterval')

    vi.doMock('@/services/api', () => ({
      apiService: {
        getQueue: async () => [],
        getServiceHealth: async () => ({ version: '0.0.0' }),
        getBootstrapConfig: async () => ({ authenticationRequired: false }),
        getStartupConfig: async () => ({ authenticationRequired: false }),
        getLibrary: async () => [
          { id: 1, title: 'Wanted Book', wanted: true },
          { id: 2, title: 'Present Book', wanted: false },
        ],
      },
    }))

    const { default: AppComponent } = await import('@/App.vue')

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', name: 'home', component: { template: '<div />' } }],
    })
    await router.push('/')
    await router.isReady().catch(() => {})

    wrapper = mount(AppComponent, {
      global: {
        stubs: { RouterLink: RouterLinkStub, RouterView: true },
        plugins: [createPinia(), router],
      },
    })

    await new Promise((r) => setTimeout(r, 20))

    const vm = wrapper.vm as unknown as { wantedCount: number }
    expect(vm.wantedCount).toBe(1)
    expect(setIntervalSpy).not.toHaveBeenCalled()

    setIntervalSpy.mockRestore()
  })

  it('retries library sync on SignalR reconnect even when the initial hydrate fails', async () => {
    const getLibrary = vi
      .fn()
      .mockRejectedValueOnce(new Error('initial load failed'))
      .mockResolvedValueOnce([{ id: 1, title: 'Recovered Book', wanted: true }])
    const connectedCallbacks: Array<() => void> = []

    vi.doMock('@/services/api', () => ({
      apiService: {
        getQueue: async () => [],
        getServiceHealth: async () => ({ version: '0.0.0' }),
        getBootstrapConfig: async () => ({ authenticationRequired: false }),
        getStartupConfig: async () => ({ authenticationRequired: false }),
        getLibrary,
      },
    }))

    vi.doMock('@/services/signalr', () => ({
      signalRService: {
        connect: vi.fn(async () => undefined),
        onConnected: vi.fn((cb: () => void) => {
          connectedCallbacks.push(cb)
          return () => undefined
        }),
        onQueueUpdate: vi.fn(() => () => undefined),
        onFilesRemoved: vi.fn(() => () => undefined),
        onScanJobUpdate: vi.fn(() => () => undefined),
        onToast: vi.fn(() => () => undefined),
        onDownloadUpdate: vi.fn(() => () => undefined),
        onDownloadsList: vi.fn(() => () => undefined),
        onNotification: vi.fn(() => () => undefined),
      },
    }))

    const { default: AppComponent } = await import('@/App.vue')

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', name: 'home', component: { template: '<div />' } }],
    })
    await router.push('/')
    await router.isReady().catch(() => {})

    wrapper = mount(AppComponent, {
      global: {
        stubs: { RouterLink: RouterLinkStub, RouterView: true },
        plugins: [createPinia(), router],
      },
    })

    await new Promise((r) => setTimeout(r, 20))

    expect(getLibrary).toHaveBeenCalledTimes(1)
    expect(connectedCallbacks).toHaveLength(1)

    connectedCallbacks[0]!()
    await new Promise((r) => setTimeout(r, 20))

    const vm = wrapper.vm as unknown as { wantedCount: number }
    expect(getLibrary).toHaveBeenCalledTimes(2)
    expect(vm.wantedCount).toBe(1)
  })

  describe('Library Import scan', () => {
    function scanTask(overrides: Partial<ScanTaskMock> = {}): ScanTaskMock {
      return {
        jobId: 'unmatched-1',
        rootFolderId: 7,
        folderName: 'Audiobooks A',
        status: 'running',
        startedAt: '2026-08-08T10:40:00Z',
        finishedAt: null,
        error: null,
        itemCount: null,
        ...overrides,
      }
    }

    async function mountApp(task: ScanTaskMock | null = null, { open = true } = {}) {
      const { useLibraryImportStore } = await import('@/stores/libraryImport')
      useLibraryImportStore().scanTask = task
      const { default: AppComponent } = await import('@/App.vue')
      const router = createRouter({
        history: createMemoryHistory(),
        routes: [
          { path: '/', name: 'home', component: { template: '<div />' } },
          { path: '/library-import', name: 'library-import', component: { template: '<div />' } },
          { path: '/:pathMatch(.*)*', name: 'other', component: { template: '<div />' } },
        ],
      })
      await router.push('/')
      await router.isReady().catch(() => {})
      wrapper = mount(AppComponent, {
        global: { stubs: ['RouterView'], plugins: [createPinia(), router] },
      })
      await new Promise((resolve) => setTimeout(resolve, 20))
      if (open) await wrapper.find(JOBS_BUTTON).trigger('click')
      return { wrapper, router }
    }

    function scanItem(w: VueWrapper) {
      return w.find('[data-testid="notification-library-import-scan"]')
    }

    it('shows a running scan with a spinner, and no dismiss button', async () => {
      const { wrapper: w } = await mountApp(scanTask({ status: 'starting', jobId: null }))

      const item = scanItem(w)
      expect(item.exists()).toBe(true)
      expect(item.text()).toContain('Scanning Audiobooks A for unmatched files')
      expect(item.find('.notif-icon .ph-spin').exists()).toBe(true)
      expect(item.find('.progress-fill').exists()).toBe(false)
      expect(item.find('.dismiss-btn').exists()).toBe(false)
    })

    it('updates the same notification as the scan ends, and stops the spinner', async () => {
      const { wrapper: w } = await mountApp(scanTask())
      const store = libraryImportMock.store!

      store.scanTask = scanTask({
        status: 'done',
        finishedAt: '2026-08-08T10:42:00Z',
        itemCount: 14,
      })
      await w.vm.$nextTick()
      expect(w.findAll('[data-testid="notification-library-import-scan"]')).toHaveLength(1)
      expect(scanItem(w).text()).toContain('Scan of Audiobooks A finished')
      expect(scanItem(w).text()).toContain('14 unmatched books')
      expect(scanItem(w).find('.ph-spin').exists()).toBe(false)
      expect(scanItem(w).find('.dismiss-btn').exists()).toBe(true)

      store.scanTask = scanTask({ status: 'failed', error: 'The root folder is unavailable' })
      await w.vm.$nextTick()
      expect(scanItem(w).text()).toContain('Scan of Audiobooks A failed')
      expect(scanItem(w).text()).toContain('The root folder is unavailable')

      store.scanTask = scanTask({ status: 'canceled', finishedAt: '2026-08-08T10:41:00Z' })
      await w.vm.$nextTick()
      expect(scanItem(w).text()).toContain('Scan of Audiobooks A canceled')
    })

    it('says "1 unmatched book" for a single result', async () => {
      const { wrapper: w } = await mountApp(scanTask({ status: 'done', itemCount: 1 }))

      expect(scanItem(w).text()).toContain('1 unmatched book')
      expect(scanItem(w).text()).not.toContain('1 unmatched books')
    })

    it('brings the user back to the folder in Library Import when its name is clicked', async () => {
      const { wrapper: w, router } = await mountApp(scanTask())

      await scanItem(w).find('a.notif-title').trigger('click')
      await new Promise((resolve) => setTimeout(resolve, 0))

      expect(router.currentRoute.value.name).toBe('library-import')
      expect(router.currentRoute.value.query).toEqual({ rootFolder: '7' })
      expect(w.find(JOBS_LIST).exists()).toBe(false)
    })

    it('forgets a finished scan when it is dismissed', async () => {
      const { wrapper: w } = await mountApp(scanTask({ status: 'done', itemCount: 3 }))

      await scanItem(w).find('.dismiss-btn').trigger('click')

      expect(libraryImportMock.store!.dismissScanTask).toHaveBeenCalled()
      expect(scanItem(w).exists()).toBe(false)
    })

    it('forgets a finished scan on Clear, but keeps a running one', async () => {
      const { wrapper: w } = await mountApp(scanTask({ status: 'canceled' }))

      await w.find(`${JOBS_LIST} .clear-btn`).trigger('click')
      expect(scanItem(w).exists()).toBe(false)

      libraryImportMock.store!.scanTask = scanTask({ status: 'running' })
      await w.vm.$nextTick()
      await w.find(`${JOBS_LIST} .clear-btn`).trigger('click')
      expect(scanItem(w).exists()).toBe(true)
    })

    it("brings back the session's scan when the app starts, whatever the page", async () => {
      await mountApp()

      expect(libraryImportMock.store!.restoreScanTask).toHaveBeenCalled()
    })

    it('keeps messages under the bell, and jobs out of it', async () => {
      // Earlier tests replace the signalR mock: this one needs the message callback
      vi.doMock('@/services/signalr', () => ({
        signalRService: {
          connect: vi.fn(async () => undefined),
          onConnected: vi.fn(() => () => undefined),
          onQueueUpdate: vi.fn(() => () => undefined),
          onFilesRemoved: vi.fn(() => () => undefined),
          onScanJobUpdate: vi.fn(() => () => undefined),
          onToast: vi.fn(() => () => undefined),
          onDownloadUpdate: vi.fn(() => () => undefined),
          onDownloadsList: vi.fn(() => () => undefined),
          onNotification: vi.fn((callback) => {
            notificationSignalRMock.callback = callback
            return () => undefined
          }),
        },
      }))
      const { wrapper: w } = await mountApp(scanTask(), { open: false })
      notificationSignalRMock.callback?.({
        id: 'n1',
        title: 'Import finished',
        message: 'Dune was imported',
        timestamp: '2026-08-08T10:00:00Z',
      })
      await w.vm.$nextTick()

      await w.find(BELL_BUTTON).trigger('click')
      const bell = w.find(BELL_LIST)
      expect(bell.find('.dropdown-header').text()).toContain('Notifications')
      expect(bell.text()).toContain('Import finished')
      expect(bell.text()).not.toContain('Scanning Audiobooks A')
      expect(w.find(JOBS_LIST).exists()).toBe(false)

      await w.find(JOBS_BUTTON).trigger('click')
      expect(w.find(JOBS_LIST).text()).toContain('Scanning Audiobooks A for unmatched files')
      expect(w.find(JOBS_LIST).text()).not.toContain('Import finished')
    })

    it('lists Jobs under System in the sidebar', async () => {
      const { wrapper: w } = await mountApp(null, { open: false })

      const jobsLink = w.find('.sidebar a.nav-subitem[href="/jobs"]')
      expect(jobsLink.exists()).toBe(true)
      expect(jobsLink.text()).toBe('Jobs')
      expect(jobsLink.element.closest('.nav-sub')?.previousElementSibling?.textContent).toContain(
        'System',
      )
    })

    it('counts unread jobs on the cog, apart from the bell', async () => {
      sessionStorage.clear()
      const { wrapper: w } = await mountApp(scanTask(), { open: false })

      expect(w.find('[data-testid="jobs-menu"] .notification-badge').text()).toBe('1')
      expect(w.find('[data-testid="notifications-menu"] .notification-badge').exists()).toBe(false)

      await w.find(JOBS_BUTTON).trigger('click')
      expect(w.find('[data-testid="jobs-menu"] .notification-badge').exists()).toBe(false)
    })

    it('spins the cog while a job runs', async () => {
      const { wrapper: w } = await mountApp(scanTask(), { open: false })
      expect(w.find('[data-testid="jobs-menu"] .header-menu-icon').classes()).toContain('ph-spin')

      libraryImportMock.store!.scanTask = scanTask({ status: 'done', itemCount: 2 })
      await w.vm.$nextTick()
      expect(w.find('[data-testid="jobs-menu"] .header-menu-icon').classes()).not.toContain(
        'ph-spin',
      )
    })

    it('names a folder it does not know as "root folder"', async () => {
      const { wrapper: w } = await mountApp(scanTask({ folderName: null }))

      expect(scanItem(w).text()).toContain('Scanning root folder for unmatched files')
    })
  })
})
