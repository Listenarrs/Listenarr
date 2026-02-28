import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'
import Checkbox from '@/components/form/Checkbox.vue'

describe('SearchSettingsSection', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
  })

  it('emits update:settings for checkboxes and numeric inputs', async () => {
    const { default: SearchSettingsSection } = await import('@/components/settings/SearchSettingsSection.vue')
    const wrapper = mount(SearchSettingsSection, {
      props: { settings: { enableOpenLibrarySearch: false } },
      global: { components: { Checkbox } },
    })

    const checks = wrapper.findAll('input[type="checkbox"]')
    await checks[0].setValue(true)
    let last = wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.enableOpenLibrarySearch).toBe(true)

  })
})