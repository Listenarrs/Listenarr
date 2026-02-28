import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { useConfigurationStore } from '@/stores/configuration'

describe('NotificationsTab', () => {
  it('shows loading state and header spinner while application settings are loading', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    const cfg = useConfigurationStore()
    cfg.isLoading = true

    const NotificationsTab = (await import('@/views/settings/NotificationsTab.vue')).default
    const wrapper = mount(NotificationsTab, {
      props: { settings: null },
      global: { plugins: [pinia] },
    })

    await wrapper.vm.$nextTick()

    expect(wrapper.find('.loading-state').exists()).toBe(true)
    expect(wrapper.find('.section-header .small-inline-spinner').exists()).toBe(true)
  })
})