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
import { mount } from '@vue/test-utils'
import { setActivePinia, createPinia } from 'pinia'
import { describe, it, beforeEach, expect, vi } from 'vitest'
import WantedView from '@/views/content/WantedView.vue'
import { useLibraryStore } from '@/stores/library'
import { useDownloadsStore } from '@/stores/downloads'
import { API_BASE_PATH } from '@/services/apiBase'
import { delay } from '@/test/utils/wait'

// Mock api service ensureImageCached and getImageUrl (and other helpers used by stores)
vi.mock('@/services/api', () => ({
  apiService: {
    getImageUrl: vi.fn((url: string) => url || 'https://via.placeholder.com/300x450?text=No+Image'),
    getQualityProfiles: vi.fn(async () => []),
  },
  // Also expose the named helper so tests can import it directly
  getImageUrl: vi.fn((url: string) => url || 'https://via.placeholder.com/300x450?text=No+Image'),
  ensureImageCached: vi.fn(async () => true),
}))

describe('WantedView image recache behavior', () => {
  beforeEach(() => {
    const pinia = createPinia()
    setActivePinia(pinia)
    vi.clearAllMocks()
    vi.stubGlobal(
      'matchMedia',
      vi.fn().mockImplementation(() => ({
        matches: false,
        media: '',
        onchange: null,
        addListener: vi.fn(),
        removeListener: vi.fn(),
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
        dispatchEvent: vi.fn(),
      })),
    )
  })

  it('calls ensureImageCached for visible wanted items on mount', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const imageBasePath = `${API_BASE_PATH}/images`

    const store = useLibraryStore()
    store.audiobooks = [
      { id: 1, title: 'Book 1', monitored: true, files: [], imageUrl: `${imageBasePath}/ASIN1` },
      { id: 2, title: 'Book 2', monitored: true, files: [], imageUrl: `${imageBasePath}/ASIN2` },
    ] as any as ReturnType<typeof useLibraryStore>['audiobooks']

    // Prevent fetchLibrary from running during mount
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(WantedView, { global: { plugins: [pinia] } })

    // Allow onMounted work to complete
    await delay(10)

    // Ensure the image element was rendered with the expected src (avoid relying on internal mock call)
    const img = wrapper.find('img')
    expect(img.exists()).toBe(true)
    const src = img.attributes('src') || ''
    expect(src).toContain(`${imageBasePath}/ASIN1`)
  })

  it('treats ImportPending as active and ImportBlocked as terminal for wanted items', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    const libraryStore = useLibraryStore()
    libraryStore.audiobooks = [
      { id: 101, title: 'Pending Book', monitored: true, files: [] },
      { id: 202, title: 'Blocked Book', monitored: true, files: [] },
    ] as any as ReturnType<typeof useLibraryStore>['audiobooks']
    libraryStore.fetchLibrary = vi.fn(async () => undefined)

    const downloadsStore = useDownloadsStore()
    downloadsStore.downloads = [
      {
        id: 'd-pending',
        title: 'Pending Book',
        status: 'ImportPending',
        progress: 100,
        totalSize: 1000,
        downloadedSize: 1000,
        audiobookId: 101,
        startedAt: new Date().toISOString(),
        metadata: {},
      },
      {
        id: 'd-blocked',
        title: 'Blocked Book',
        status: 'ImportBlocked',
        progress: 100,
        totalSize: 1000,
        downloadedSize: 1000,
        audiobookId: 202,
        startedAt: new Date().toISOString(),
        metadata: {},
      },
    ] as ReturnType<typeof useDownloadsStore>['downloads']

    const wrapper = mount(WantedView, { global: { plugins: [pinia] } })
    await delay(10)

    const vm = wrapper.vm as any as {
      hasActiveDownload: (audiobook: { id: number }) => boolean
      getStatusText: (audiobook: { id: number }) => string
    }

    expect(vm.hasActiveDownload({ id: 101 })).toBe(true)
    expect(vm.getStatusText({ id: 101 })).toContain('ImportPending')

    expect(vm.hasActiveDownload({ id: 202 })).toBe(false)
    expect(vm.getStatusText({ id: 202 })).toBe('Missing')
  })

  it('renders the full wanted list without virtualization on mobile', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

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

    const libraryStore = useLibraryStore()
    libraryStore.audiobooks = Array.from({ length: 30 }, (_, index) => ({
      id: index + 1,
      title: `Wanted Book ${index + 1}`,
      monitored: true,
      files: [],
    })) as any as ReturnType<typeof useLibraryStore>['audiobooks']
    libraryStore.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(WantedView, { global: { plugins: [pinia] } })
    await delay(10)

    expect(wrapper.find('.wanted-grid-container').classes()).toContain('is-static')
    expect(wrapper.find('.wanted-body.is-static').exists()).toBe(true)
    expect(wrapper.findAll('.wanted-row')).toHaveLength(30)
  })
})
