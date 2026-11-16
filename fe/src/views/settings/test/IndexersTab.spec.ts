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
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { modalStubs } from '@/test/stubs'
import { flushAsync } from '@/test/utils/wait'

// We'll mock getIndexers so we can control its resolution during the test
describe('IndexersTab', () => {
  beforeEach(() => {
    vi.resetModules()
    // Mock signalR before any module imports to prevent the auto-connect at module
    // load time (signalr.ts calls signalRService.connect() immediately), which
    // attempts a real WebSocket connection and schedules retries that hang the test.
    vi.doMock('@/services/signalr', () => ({
      signalRService: { onIndexersUpdated: vi.fn(() => () => {}) },
    }))
  })

  it('shows loading state while fetching indexers', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    let resolveFn: (value: unknown) => void = () => {}

    // Use doMock so the module is mocked for subsequent imports in this test
    vi.doMock('@/services/api', async (importOriginal) => {
      const actual = await importOriginal()
      return {
        ...(actual as any),
        getIndexers: vi.fn(() => new Promise((res) => (resolveFn = res))),
        getProwlarrImportSettings: vi.fn().mockResolvedValue({
          url: '',
          port: undefined,
          tagFilter: '',
          hasSavedApiKey: false,
        }),
      }
    })

    // ensure signalRService has the onIndexersUpdated helper (some test setup
    // imports the module earlier so we patch the existing export)
    const sr = await import('@/services/signalr')
    // provide a no-op subscription function
    if (!sr.signalRService || typeof sr.signalRService.onIndexersUpdated !== 'function') {
      ;(sr as any).signalRService = { onIndexersUpdated: () => () => {} } as any
    }

    const IndexersTab = (await import('@/views/settings/IndexersTab.vue')).default

    const wrapper = mount(IndexersTab, { global: { plugins: [pinia], stubs: modalStubs } })

    // Allow Vue to flush lifecycle effects
    await wrapper.vm.$nextTick()

    // While the promise is pending the loading indicator should be visible
    expect(wrapper.find('.loading-state').exists()).toBe(true)

    // Resolve the pending API call and wait for the DOM to update
    resolveFn([])
    await flushAsync()
    await wrapper.vm.$nextTick()

    // After resolution, the empty-state should be shown (no indexers)
    expect(wrapper.find('.empty-state').exists()).toBe(true)
  })

  it('loads saved Prowlarr connection fields from the API', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    vi.doMock('@/services/api', async (importOriginal) => {
      const actual = await importOriginal()
      return {
        ...(actual as any),
        getIndexers: vi.fn().mockResolvedValue([]),
        getProwlarrImportSettings: vi.fn().mockResolvedValue({
          url: 'http://localhost',
          port: 9696,
          tagFilter: 'audiobooks',
          hasSavedApiKey: true,
        }),
      }
    })

    const sr = await import('@/services/signalr')
    if (!sr.signalRService || typeof sr.signalRService.onIndexersUpdated !== 'function') {
      ;(sr as any).signalRService = { onIndexersUpdated: () => () => {} } as any
    }

    const IndexersTab = (await import('@/views/settings/IndexersTab.vue')).default

    const wrapper = mount(IndexersTab, {
      attachTo: document.body,
      global: { plugins: [pinia], stubs: modalStubs },
    })

    ;(wrapper.vm as any as { openProwlarrImport: () => void }).openProwlarrImport()
    await wrapper.vm.$nextTick()

    await flushAsync()
    await wrapper.vm.$nextTick()

    expect((wrapper.get('#prowlarr-url').element as HTMLInputElement).value).toBe(
      'http://localhost',
    )
    expect((wrapper.get('#prowlarr-port').element as HTMLInputElement).value).toBe('9696')
    expect((wrapper.get('#prowlarr-tag-filter').element as HTMLInputElement).value).toBe(
      'audiobooks',
    )
    expect((wrapper.get('#prowlarr-key').element as HTMLInputElement).value).toBe('')
    expect(wrapper.text()).toContain('saved API key is already stored securely on the server')

    wrapper.unmount()
  })

  it('sends clearPort when the saved Prowlarr port is cleared before import', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    const importProwlarrIndexers = vi.fn().mockResolvedValue({
      addedCount: 0,
      skippedCount: 0,
    })

    vi.doMock('@/services/api', async (importOriginal) => {
      const actual = await importOriginal()
      return {
        ...(actual as any),
        getIndexers: vi.fn().mockResolvedValue([]),
        getProwlarrImportSettings: vi.fn().mockResolvedValue({
          url: 'http://localhost',
          port: 9696,
          tagFilter: '',
          hasSavedApiKey: true,
        }),
        importProwlarrIndexers,
      }
    })

    const sr = await import('@/services/signalr')
    if (!sr.signalRService || typeof sr.signalRService.onIndexersUpdated !== 'function') {
      ;(sr as any).signalRService = { onIndexersUpdated: () => () => {} } as any
    }

    const IndexersTab = (await import('@/views/settings/IndexersTab.vue')).default

    const wrapper = mount(IndexersTab, {
      attachTo: document.body,
      global: { plugins: [pinia], stubs: modalStubs },
    })

    ;(wrapper.vm as any as { openProwlarrImport: () => void }).openProwlarrImport()
    await wrapper.vm.$nextTick()
    await flushAsync()
    await wrapper.vm.$nextTick()

    await wrapper.get('#prowlarr-port').setValue('')

    await (wrapper.vm as any as { importFromProwlarr: () => Promise<void> }).importFromProwlarr()

    expect(importProwlarrIndexers).toHaveBeenCalledWith({
      url: 'http://localhost',
      clearPort: true,
      tagFilter: '',
    })

    wrapper.unmount()
  })

  it('handles numeric prowlarrPort state when importing', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    const importProwlarrIndexers = vi.fn().mockResolvedValue({
      addedCount: 0,
      skippedCount: 0,
    })

    vi.doMock('@/services/api', async (importOriginal) => {
      const actual = await importOriginal()
      return {
        ...(actual as any),
        getIndexers: vi.fn().mockResolvedValue([]),
        getProwlarrImportSettings: vi.fn().mockResolvedValue({
          url: 'http://localhost',
          port: 9696,
          tagFilter: '',
          hasSavedApiKey: true,
        }),
        importProwlarrIndexers,
      }
    })

    const sr = await import('@/services/signalr')
    if (!sr.signalRService || typeof sr.signalRService.onIndexersUpdated !== 'function') {
      ;(sr as any).signalRService = { onIndexersUpdated: () => () => {} } as any
    }

    const IndexersTab = (await import('@/views/settings/IndexersTab.vue')).default

    const wrapper = mount(IndexersTab, {
      attachTo: document.body,
      global: { plugins: [pinia], stubs: modalStubs },
    })

    ;(wrapper.vm as any as { openProwlarrImport: () => void }).openProwlarrImport()
    await wrapper.vm.$nextTick()
    await flushAsync()
    await wrapper.vm.$nextTick()
    ;(wrapper.vm as any as { prowlarrPort: number }).prowlarrPort = 9696

    await (wrapper.vm as any as { importFromProwlarr: () => Promise<void> }).importFromProwlarr()

    expect(importProwlarrIndexers).toHaveBeenCalledWith({
      url: 'http://localhost',
      port: 9696,
      clearPort: false,
      tagFilter: '',
    })

    wrapper.unmount()
  })
})
