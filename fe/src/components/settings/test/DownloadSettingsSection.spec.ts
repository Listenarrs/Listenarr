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

describe('DownloadSettingsSection', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
  })

  it('emits update:settings when numerical inputs change', async () => {
    const { default: DownloadSettingsSection } =
      await import('@/components/settings/DownloadSettingsSection.vue')
    const wrapper = mount(DownloadSettingsSection, {
      props: {
        settings: {
          maxConcurrentDownloads: 2,
          unmatchedScanConcurrency: 2,
          pollingIntervalSeconds: 30,
          downloadCompletionStabilitySeconds: 5,
          missingSourceRetryInitialDelaySeconds: 2,
          missingSourceMaxRetries: 3,
        },
      },
    })

    const inputs = wrapper.findAll('input[type="number"]')
    expect(inputs).toHaveLength(6)

    // Max concurrent
    await inputs[0].setValue('4')
    let last =
      wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.maxConcurrentDownloads).toBe(4)

    // Unmatched scan concurrency
    await inputs[1].setValue('3')
    last =
      wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.unmatchedScanConcurrency).toBe(3)

    // Polling interval
    await inputs[2].setValue('60')
    last =
      wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.pollingIntervalSeconds).toBe(60)

    // Stability
    await inputs[3].setValue('10')
    last =
      wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.downloadCompletionStabilitySeconds).toBe(10)

    // Missing-source delay
    await inputs[4].setValue('3')
    last =
      wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.missingSourceRetryInitialDelaySeconds).toBe(3)

    // Missing-source retries
    await inputs[5].setValue('5')
    last =
      wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.missingSourceMaxRetries).toBe(5)
  })
})
