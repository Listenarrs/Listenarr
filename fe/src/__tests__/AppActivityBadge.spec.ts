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
import { mount, type VueWrapper } from '@vue/test-utils'
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
    onNotification: vi.fn(() => () => undefined),
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

  it('shows active move progress in the notification dropdown', async () => {
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
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
    })
    await new Promise((resolve) => setTimeout(resolve, 20))

    await wrapper.find('.notification-wrapper .nav-btn').trigger('click')

    const dropdown = wrapper.find('.notification-dropdown')
    expect(dropdown.exists()).toBe(true)
    expect(dropdown.text()).toContain('Moving audiobook')
    expect(dropdown.text()).toContain('Verifying source')
    expect(dropdown.text()).toContain('42%')
    expect(dropdown.find('.progress-fill').attributes('style')).toContain('width: 42.4%')
  })

  it('updates folder scan progress in one notification without a fake percentage', async () => {
    const { default: AppComponent } = await import('@/App.vue')
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', name: 'home', component: { template: '<div />' } }],
    })
    await router.push('/')
    await router.isReady().catch(() => {})

    wrapper = mount(AppComponent, {
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
    })
    await new Promise((resolve) => setTimeout(resolve, 20))
    expect(scanSignalRMock.callback).not.toBeNull()

    scanSignalRMock.callback?.({
      jobId: 'internal-scan',
      audiobookId: 42,
      status: 'Processing',
    })
    await wrapper.vm.$nextTick()
    await wrapper.find('.notification-wrapper .nav-btn').trigger('click')
    expect(wrapper.find('.notification-dropdown').text()).not.toContain('Scanning audiobook folder')

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
    expect(scanNotification?.find('.progress-fill').classes()).toContain('indeterminate')
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
    expect(scanNotification?.find('.progress-fill').classes()).toContain('indeterminate')
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
    expect(scanNotification?.find('.progress-fill').exists()).toBe(false)
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
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
    })
    await new Promise((resolve) => setTimeout(resolve, 20))

    scanSignalRMock.callback?.({
      jobId: 'scan-reconcile',
      audiobookId: 42,
      status: 'Queued',
    })
    await new Promise((resolve) => setTimeout(resolve, 10))

    expect(scanJobStatusMock).toHaveBeenCalledWith('scan-reconcile')

    await wrapper.find('.notification-wrapper .nav-btn').trigger('click')
    const dropdown = wrapper.find('.notification-dropdown')
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
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
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
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
    })
    await new Promise((resolve) => setTimeout(resolve, 20))

    scanSignalRMock.callback?.({
      jobId: 'scan-lost',
      audiobookId: 42,
      status: 'Queued',
    })
    await new Promise((resolve) => setTimeout(resolve, 10))

    await wrapper.find('.notification-wrapper .nav-btn').trigger('click')
    const dropdown = wrapper.find('.notification-dropdown')
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
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
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
    await wrapper.find('.notification-wrapper .nav-btn').trigger('click')
    expect(wrapper.find('.notification-dropdown').text()).not.toContain('scan-fast')
    expect(wrapper.find('.notification-dropdown').text()).not.toContain('Scan complete')

    scanSignalRMock.callback?.({ jobId: 'scan-fast', audiobookId: 42, status: 'Queued' })
    await wrapper.vm.$nextTick()

    const dropdown = wrapper.find('.notification-dropdown')
    expect(dropdown.text()).toContain('Scan complete: audiobook folder')
    expect(dropdown.text()).toContain('1 file found · 1 added')
    expect(dropdown.text()).not.toContain('Scan queued')
  })

  it('shows library delete progress in notifications instead of Activity', async () => {
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
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
    })
    await new Promise((resolve) => setTimeout(resolve, 20))

    await wrapper.find('.notification-wrapper .nav-btn').trigger('click')

    const dropdown = wrapper.find('.notification-dropdown')
    expect(dropdown.text()).toContain('Notifications')
    expect(dropdown.text()).toContain('Deleting 4 audiobooks')
    expect(dropdown.text()).toContain('2/4 · Second Book')
    expect(dropdown.text()).toContain('50%')
  })

  it('shows a single delete as indeterminate progress without a fake percentage', async () => {
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
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
    })
    await new Promise((resolve) => setTimeout(resolve, 20))

    await wrapper.find('.notification-wrapper .nav-btn').trigger('click')

    const dropdown = wrapper.find('.notification-dropdown')
    expect(dropdown.text()).toContain('Deleting Slow Delete')
    expect(dropdown.text()).toContain('Removing audiobook from library')
    expect(dropdown.text()).not.toContain('35%')
    expect(dropdown.find('.progress-fill').classes()).toContain('indeterminate')
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
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
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
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
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
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
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
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
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
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
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
})
