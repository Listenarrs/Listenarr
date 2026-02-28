import { describe, it, expect, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'

describe('QualityProfilesTab', () => {
  it('shows loading state while fetching profiles', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    // Spy the API so we can control resolution
    const api = await import('@/services/api')
    let resolveFn: (value: unknown) => void = () => {}
    vi.spyOn(api, 'getQualityProfiles').mockImplementation(
      () => new Promise((res) => {
        resolveFn = res
      }) as any,
    )

    const QualityProfilesTab = (await import('@/views/settings/QualityProfilesTab.vue')).default
    const wrapper = mount(QualityProfilesTab, { global: { plugins: [pinia] } })

    // debug: inspect rendered HTML during pending state
    await wrapper.vm.$nextTick()
    // console.log(wrapper.html())

    // initial pending state should show a loading indicator
    expect(wrapper.find('.loading-state').exists()).toBe(true)

    // resolve API and assert empty-state appears
    resolveFn([])
    await new Promise((r) => setTimeout(r, 0))
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.empty-state').exists()).toBe(true)
  })
})