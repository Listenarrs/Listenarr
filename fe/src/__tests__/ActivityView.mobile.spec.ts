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
import { describe, it, beforeEach, expect, vi } from 'vitest'
import { mount } from '@vue/test-utils'

describe('ActivityView mobile virtualization', () => {
  beforeEach(() => {
    vi.resetModules()
    vi.clearAllMocks()
    vi.stubGlobal(
      'matchMedia',
      vi.fn().mockImplementation(() => ({
        matches: true,
        media: '(max-width: 768px)',
        onchange: null,
        addListener: vi.fn(),
        removeListener: vi.fn(),
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
        dispatchEvent: vi.fn(),
      })),
    )
  })

  it('renders the full activity list without virtualization on mobile', async () => {
    const queueItems = Array.from({ length: 25 }, (_, index) => ({
      id: `queue-${index + 1}`,
      title: `Queued Item ${index + 1}`,
      status: 'downloading',
      progress: 42,
      size: 4096,
      downloaded: 2048,
      downloadClientId: 'qbittorrent',
      downloadClient: 'qBittorrent',
      canRemove: true,
    }))

    vi.spyOn(globalThis, 'setInterval').mockReturnValue(
      1 as unknown as ReturnType<typeof setInterval>,
    )
    vi.spyOn(globalThis, 'clearInterval').mockImplementation(() => undefined)

    vi.doMock('@/services/signalr', () => ({
      signalRService: {
        onQueueUpdate: vi.fn(() => () => undefined),
      },
    }))

    vi.doMock('@/services/api', () => ({
      apiService: {
        getQueue: vi.fn(async () => queueItems),
      },
    }))

    vi.doMock('@/stores/configuration', () => ({
      useConfigurationStore: () => ({
        applicationSettings: { showCompletedExternalDownloads: false },
        loadApplicationSettings: vi.fn(async () => undefined),
      }),
    }))

    vi.doMock('@/stores/library', () => ({
      useLibraryStore: () => ({
        audiobooks: [],
      }),
    }))

    vi.doMock('@/stores/downloads', () => ({
      useDownloadsStore: () => ({
        activeDownloads: [],
        completedDownloads: [],
        failedDownloads: [],
        loadDownloads: vi.fn(async () => undefined),
      }),
    }))

    vi.doMock('@/stores/moveJobs', () => ({
      useMoveJobsStore: () => ({
        trackedJobs: [],
        start: vi.fn(),
      }),
    }))

    vi.doMock('@/services/errorTracking', () => ({
      errorTracking: {
        captureException: vi.fn(),
      },
    }))

    const { default: ActivityView } = await import('@/views/activity/ActivityView.vue')
    const wrapper = mount(ActivityView, {
      global: {
        stubs: {
          CustomSelect: true,
          EmptyState: true,
          LoadingState: true,
          ProgressBar: true,
        },
      },
    })

    await new Promise((resolve) => setTimeout(resolve, 10))

    expect(wrapper.find('.queue-grid-container').classes()).toContain('is-static')
    expect(wrapper.find('.queue-body.is-static').exists()).toBe(true)
    expect(wrapper.findAll('.queue-row')).toHaveLength(25)

    wrapper.unmount()
  })
})
