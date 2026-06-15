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
import { afterEach, describe, it, expect, vi, beforeEach } from 'vitest'
import { enableAutoUnmount, mount } from '@vue/test-utils'
import { computed, ref } from 'vue'
import { createPinia, setActivePinia } from 'pinia'
import { delay, flushAsync } from '@/test/utils/wait'

enableAutoUnmount(afterEach)

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
    onToast: vi.fn(() => () => undefined),
    onDownloadUpdate: vi.fn(() => () => undefined),
    onDownloadsList: vi.fn(() => () => undefined),
    onNotification: vi.fn(() => () => undefined),
  },
}))

vi.mock('@/router', () => ({
  preloadRoute: vi.fn(async () => undefined),
}))

// Mock API calls used during mount - only return what tests need
vi.mock('@/services/api', () => ({
  apiService: {
    getQueue: vi.fn(async () => []),
    getServiceHealth: vi.fn(async () => ({ version: '0.0.0' })),
    getBootstrapConfig: vi.fn(async () => ({ authenticationRequired: false })),
    getStartupConfig: vi.fn(async () => ({ authenticationRequired: false })),
    getLibrary: vi.fn(async () => []),
  },
}))

import { createRouter, createMemoryHistory } from 'vue-router'

describe('App.vue activity badge', () => {
  beforeEach(() => {
    // reset mocks between tests
    vi.resetModules()
    setActivePinia(createPinia())
  })

  // Ensure localStorage APIs exist in the test environment for App.vue session debug helpers
  if (typeof (globalThis as any as { localStorage?: unknown }).localStorage === 'undefined') {
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

    const wrapper = mount(AppComponent, {
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
    })

    // Wait a tick for computed properties in mounted hook
    // Allow async onMounted tasks (SignalR/connect, api fetches) to settle
    await delay(20)

    const vm = wrapper.vm as any as { activityCount: number }
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

    const wrapper = mount(AppComponent, {
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
    })

    // Allow async onMounted tasks to settle
    await delay(20)

    const vm = wrapper.vm as any as { activityCount: number }
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

    const wrapper = mount(AppComponent, {
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
    })

    // Allow async onMounted tasks (SignalR/connect, api fetches) to settle
    await delay(20)

    const vm = wrapper.vm as any as { activityCount: number }
    // With zero active downloads and two queue items, activityCount should reflect the queue
    expect(vm.activityCount).toBe(2)
  }, 20000)

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

    const wrapper = mount(AppComponent, {
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
    })

    await delay(20)

    const vm = wrapper.vm as any as { wantedCount: number }
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

    const wrapper = mount(AppComponent, {
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
    })

    await delay(20)

    expect(getLibrary).toHaveBeenCalledTimes(1)
    expect(connectedCallbacks).toHaveLength(1)

    connectedCallbacks[0]!()
    await delay(20)

    const vm = wrapper.vm as any as { wantedCount: number }
    expect(getLibrary).toHaveBeenCalledTimes(2)
    expect(vm.wantedCount).toBe(1)
  })

  it('hides app chrome on the login route', async () => {
    vi.resetModules()
    vi.doMock('@/services/startupConfigCache', () => ({
      getStartupConfigCached: vi.fn(async () => ({ authenticationRequired: true })),
    }))

    const { default: AppComponent } = await import('@/App.vue')
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        {
          path: '/',
          name: 'home',
          component: { template: '<div />' },
          meta: { requiresAuth: true },
        },
        {
          path: '/login',
          name: 'login',
          component: { template: '<div />' },
          meta: { hideLayout: true },
        },
      ],
    })
    await router.push('/login')
    await router.isReady().catch(() => {})

    const wrapper = mount(AppComponent, {
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
    })
    await flushAsync()

    expect(wrapper.find('header.top-nav').exists()).toBe(false)
    expect(wrapper.find('main.main-content.full-page').exists()).toBe(true)
  })

  it('redirects from a protected route when auth state becomes unauthenticated', async () => {
    vi.resetModules()
    const { reactive } = await import('vue')
    const authStore = {
      user: reactive({ authenticated: true }),
      loadCurrentUser: vi.fn(async () => undefined),
      logout: vi.fn(async () => {
        authStore.user.authenticated = false
      }),
    }

    vi.doMock('@/stores/auth', () => ({
      useAuthStore: () => authStore,
    }))
    vi.doMock('@/services/startupConfigCache', () => ({
      getStartupConfigCached: vi.fn(async () => ({ authenticationRequired: true })),
    }))
    vi.doMock('@/services/api', () => ({
      apiService: {
        getQueue: async () => [],
        getServiceHealth: async () => ({ version: '0.0.0' }),
        getBootstrapConfig: async () => ({ authenticationRequired: true }),
        getStartupConfig: async () => ({ authenticationRequired: true }),
        getLibrary: async () => [],
      },
    }))

    const { default: AppComponent } = await import('@/App.vue')
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        {
          path: '/',
          name: 'home',
          component: { template: '<div />' },
          meta: { requiresAuth: true },
        },
        {
          path: '/settings',
          name: 'settings',
          component: { template: '<div />' },
          meta: { requiresAuth: true },
        },
        {
          path: '/login',
          name: 'login',
          component: { template: '<div />' },
          meta: { hideLayout: true },
        },
      ],
    })
    await router.push('/settings')
    await router.isReady().catch(() => {})

    mount(AppComponent, {
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
    })
    await flushAsync()

    authStore.user.authenticated = false
    await flushAsync()

    expect(router.currentRoute.value.name).toBe('login')
    expect(router.currentRoute.value.query.redirect).toBe('/settings')
  })

  it('does not force login when auth is disabled and shows the disabled-auth banner', async () => {
    vi.resetModules()
    const { reactive } = await import('vue')
    const authStore = {
      user: reactive({ authenticated: true }),
      loadCurrentUser: vi.fn(async () => undefined),
      logout: vi.fn(async () => {
        authStore.user.authenticated = false
      }),
    }

    vi.doMock('@/stores/auth', () => ({
      useAuthStore: () => authStore,
    }))
    vi.doMock('@/services/startupConfigCache', () => ({
      getStartupConfigCached: vi.fn(async () => ({ authenticationRequired: false })),
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
      routes: [
        {
          path: '/',
          name: 'home',
          component: { template: '<div />' },
          meta: { requiresAuth: true },
        },
        {
          path: '/settings',
          name: 'settings',
          component: { template: '<div />' },
          meta: { requiresAuth: true },
        },
        {
          path: '/login',
          name: 'login',
          component: { template: '<div />' },
          meta: { hideLayout: true },
        },
      ],
    })
    await router.push('/settings')
    await router.isReady().catch(() => {})

    const wrapper = mount(AppComponent, {
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
    })
    await flushAsync()

    authStore.user.authenticated = false
    await flushAsync()

    expect(router.currentRoute.value.name).toBe('settings')
    expect(wrapper.find('.security-warning-banner').exists()).toBe(true)
  })

  it('logout button calls auth logout and uses app auth navigation', async () => {
    vi.resetModules()
    const { reactive } = await import('vue')
    const authStore = {
      user: reactive({ authenticated: true }),
      loadCurrentUser: vi.fn(async () => undefined),
      logout: vi.fn(async () => {
        authStore.user.authenticated = false
      }),
    }

    vi.doMock('@/stores/auth', () => ({
      useAuthStore: () => authStore,
    }))
    vi.doMock('@/services/startupConfigCache', () => ({
      getStartupConfigCached: vi.fn(async () => ({ authenticationRequired: true })),
    }))
    vi.doMock('@/services/api', () => ({
      apiService: {
        getQueue: async () => [],
        getServiceHealth: async () => ({ version: '0.0.0' }),
        getBootstrapConfig: async () => ({ authenticationRequired: true }),
        getStartupConfig: async () => ({ authenticationRequired: true }),
        getLibrary: async () => [],
      },
    }))

    const { default: AppComponent } = await import('@/App.vue')
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        {
          path: '/',
          name: 'home',
          component: { template: '<div />' },
          meta: { requiresAuth: true },
        },
        {
          path: '/settings',
          name: 'settings',
          component: { template: '<div />' },
          meta: { requiresAuth: true },
        },
        {
          path: '/login',
          name: 'login',
          component: { template: '<div />' },
          meta: { hideLayout: true },
        },
      ],
    })
    await router.push('/settings')
    await router.isReady().catch(() => {})

    const wrapper = mount(AppComponent, {
      global: { stubs: ['RouterLink', 'RouterView'], plugins: [createPinia(), router] },
    })
    await flushAsync()

    await wrapper.find('.nav-user-btn').trigger('click')
    await wrapper.find('.user-menu-item').trigger('click')
    await flushAsync()

    expect(authStore.logout).toHaveBeenCalledTimes(1)
    expect(router.currentRoute.value.name).toBe('login')
    expect(router.currentRoute.value.query.redirect).toBe('/settings')
  })
})
