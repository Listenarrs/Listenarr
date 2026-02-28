import { describe, it, expect, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'

// We'll mock getIndexers so we can control its resolution during the test
describe('IndexersTab', () => {
  it('shows loading state while fetching indexers', async () => {
    vi.resetModules()
    const pinia = createPinia()
    setActivePinia(pinia)

    let resolveFn: (value: unknown) => void = () => {}

    // Use doMock so the module is mocked for subsequent imports in this test
    vi.doMock('@/services/api', async (importOriginal) => {
      const actual = await importOriginal()
      return {
        ...(actual as any),
        getIndexers: vi.fn(() => new Promise((res) => (resolveFn = res))),
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

    const wrapper = mount(IndexersTab, { global: { plugins: [pinia] } })

    // Allow Vue to flush lifecycle effects
    await wrapper.vm.$nextTick()

    // While the promise is pending the loading indicator should be visible
    expect(wrapper.find('.loading-state').exists()).toBe(true)

    // Resolve the pending API call and wait for the DOM to update
    resolveFn([])
    await new Promise((r) => setTimeout(r, 0))
    await wrapper.vm.$nextTick()

    // After resolution, the empty-state should be shown (no indexers)
    expect(wrapper.find('.empty-state').exists()).toBe(true)
  })
})
