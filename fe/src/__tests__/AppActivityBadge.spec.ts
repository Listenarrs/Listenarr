import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'
import { computed, ref } from 'vue'

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
    onQueueUpdate: vi.fn(() => () => undefined),
    onFilesRemoved: vi.fn(() => () => undefined),
    onToast: vi.fn(() => () => undefined),
    onAudiobookUpdate: vi.fn(() => () => undefined),
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
    getStartupConfig: vi.fn(async () => ({ authenticationRequired: false })),
    getLibrary: vi.fn(async () => []),
  },
}))

import { createRouter, createMemoryHistory } from 'vue-router'

describe('App.vue activity badge', () => {
  beforeEach(() => {
    // reset mocks between tests
    vi.resetModules()
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

    const wrapper = mount(AppComponent, {
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [router] },
    })

    // Wait a tick for computed properties in mounted hook
    // Allow async onMounted tasks (SignalR/connect, api fetches) to settle
    await new Promise((r) => setTimeout(r, 20))

    const vm = wrapper.vm as unknown as { activityCount: number }
    // The badge should reflect the single active DDL download
    expect(vm.activityCount).toBe(1)
  }, 20000)

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

    const wrapper = mount(AppComponent, {
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [router] },
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
        getStartupConfig: async () => ({ authenticationRequired: false }),
        getLibrary: async () => [],
      },
    }))
    // Also mock SignalR to push the same items
    vi.doMock('@/services/signalr', () => ({
      signalRService: {
        connect: vi.fn(async () => undefined),
        onQueueUpdate: (cb: (items: unknown[]) => void) => {
          cb([
            { id: 'q1', status: 'queued' },
            { id: 'q2', status: 'queued' },
          ])
          return () => undefined
        },
        onFilesRemoved: vi.fn(() => () => undefined),
        onToast: vi.fn(() => () => undefined),
        onAudiobookUpdate: vi.fn(() => () => undefined),
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

    const wrapper = mount(AppComponent, {
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [router] },
    })

    // Allow async onMounted tasks (SignalR/connect, api fetches) to settle
    await new Promise((r) => setTimeout(r, 20))

    const vm = wrapper.vm as unknown as { activityCount: number }
    // With zero active downloads and two queue items, activityCount should reflect the queue
    expect(vm.activityCount).toBe(2)
  }, 20000)
})
