import { describe, it, expect, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import RootFoldersSettings from '@/components/settings/RootFoldersSettings.vue'
import { useRootFoldersStore } from '@/stores/rootFolders'

describe('RootFoldersSettings', () => {
  it('shows header spinner and loading state when store.loading is true', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    const store = useRootFoldersStore()

    // Make the underlying API call pending so store.loading remains true while mounted
    const api = await import('@/services/api')
    let resolveFn: (value: unknown) => void = () => {}
    // spy on the apiService instance method (module-level named export is not present in TS types)
    vi.spyOn((api as any).apiService, 'getRootFolders').mockImplementation(
      () => new Promise((res) => {
        resolveFn = res
      }) as any,
    )

    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    // Wait for onMounted to run and for store.load() to set loading=true
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.loading-state').exists()).toBe(true)
    expect(wrapper.find('.section-header .small-inline-spinner').exists()).toBe(true)

    // Resolve API and ensure UI updates
    resolveFn([])
    await new Promise((r) => setTimeout(r, 0))
    await wrapper.vm.$nextTick()
  })
})