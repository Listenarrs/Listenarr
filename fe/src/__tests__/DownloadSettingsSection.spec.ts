import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'

describe('DownloadSettingsSection', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
  })

  it('emits update:settings when numerical inputs change', async () => {
    const { default: DownloadSettingsSection } = await import('@/components/settings/DownloadSettingsSection.vue')
    const wrapper = mount(DownloadSettingsSection, {
      props: { settings: { maxConcurrentDownloads: 2, pollingIntervalSeconds: 30, downloadCompletionStabilitySeconds: 5, missingSourceRetryInitialDelaySeconds: 2, missingSourceMaxRetries: 3 } },
    })

    const inputs = wrapper.findAll('input[type="number"]')
    // Max concurrent
    await inputs[0].setValue('4')
    let last = wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.maxConcurrentDownloads).toBe(4)

    // Polling interval
    await inputs[1].setValue('60')
    last = wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.pollingIntervalSeconds).toBe(60)

    // Stability
    await inputs[2].setValue('10')
    last = wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.downloadCompletionStabilitySeconds).toBe(10)

    // Missing-source delay
    await inputs[3].setValue('3')
    last = wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.missingSourceRetryInitialDelaySeconds).toBe(3)

    // Missing-source retries
    await inputs[4].setValue('5')
    last = wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.missingSourceMaxRetries).toBe(5)
  })
})
