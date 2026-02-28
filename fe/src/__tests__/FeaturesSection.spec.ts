import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'
import Checkbox from '@/components/form/Checkbox.vue'

describe('FeaturesSection', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
  })

  it('emits update:settings when feature checkboxes change', async () => {
    const { default: FeaturesSection } = await import('@/components/settings/FeaturesSection.vue')
    const wrapper = mount(FeaturesSection, {
      props: { settings: { enableMetadataProcessing: false, enableCoverArtDownload: false, enableNotifications: false, showCompletedExternalDownloads: false } },
      global: { components: { Checkbox } },
    })

    const checks = wrapper.findAll('input[type="checkbox"]')
    await checks[0].setValue(true)
    let last = wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.enableMetadataProcessing).toBe(true)

    await checks[1].setValue(true)
    last = wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.enableCoverArtDownload).toBe(true)
  })
})