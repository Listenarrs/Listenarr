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
import { API_BASE_PATH } from '@/services/apiBase'
import { useLibraryStore } from '@/stores/library'
import { ensureImageCached } from '@/services/api'
import { delay, flushAsync } from '@/test/utils/wait'
import AudiobookDetailViewCmp from '@/views/library/AudiobookDetailView.vue'
const routerPushMock = vi.fn()
// Mock useRoute to provide params for the detail view
vi.mock('vue-router', () => ({
  useRoute: () => ({ params: { id: '5' } }),
  useRouter: () => ({ push: routerPushMock }),
}))

// Mock api service ensureImageCached and getImageUrl
vi.mock('@/services/api', () => ({
  apiService: {
    getImageUrl: vi.fn((url: string) => url || 'https://via.placeholder.com/300x450?text=No+Image'),
    getQualityProfiles: vi.fn(async () => []),
    getLibrary: vi.fn(async () => []),
  },
  ensureImageCached: vi.fn(async () => true),
}))

// Mock signalr service to provide missing hooks (e.g., onScanJobUpdate)
vi.mock('@/services/signalr', () => ({
  signalRService: {
    connect: vi.fn(async () => undefined),
    onQueueUpdate: vi.fn(() => () => undefined),
    onFilesRemoved: vi.fn(() => () => undefined),
    onToast: vi.fn(() => () => undefined),
    onAudiobookUpdate: vi.fn(() => () => undefined),
    onDownloadUpdate: vi.fn(() => () => undefined),
    onDownloadsList: vi.fn(() => () => undefined),
    onScanJobUpdate: vi.fn(() => () => undefined),
  },
}))

describe('AudiobookDetailView image recache behavior', () => {
  beforeEach(() => {
    const pinia = createPinia()
    setActivePinia(pinia)
    vi.clearAllMocks()
  })

  it('calls ensureImageCached for the audiobook cover on load', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const imagePath = `${API_BASE_PATH}/images/ASIN000005`
    const store = useLibraryStore()
    store.audiobooks = [
      { id: 5, title: 'Detail Book', imageUrl: imagePath, files: [] },
    ] as any as ReturnType<typeof useLibraryStore>['audiobooks']

    store.fetchLibrary = vi.fn(async () => undefined)

    mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await delay(10)

    expect(ensureImageCached).toHaveBeenCalled()
    const ensureImageCachedMock = ensureImageCached as any as {
      mock: { calls: Array<[string]> }
    }
    expect(ensureImageCachedMock.mock.calls[0]?.[0]).toBe(imagePath)
  })

  it('navigates to the author, narrator, publisher, series, and genre collections when their tags are clicked', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 5,
        title: 'Detail Book',
        authors: ['Brandon Sanderson'],
        narrators: ['Michael Kramer'],
        publisher: 'Tor Audio',
        series: 'Mistborn',
        genres: ['Fantasy'],
        files: [],
      },
    ] as any as ReturnType<typeof useLibraryStore>['audiobooks']

    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })
    await delay(10)

    const authorTag = wrapper
      .findAll('.detail-link-tag')
      .find((tag) => tag.text().includes('Brandon Sanderson'))
    const narratorTag = wrapper
      .findAll('.detail-link-tag')
      .find((tag) => tag.text().includes('Michael Kramer'))
    const publisherTag = wrapper
      .findAll('.detail-link-tag')
      .find((tag) => tag.text().includes('Tor Audio'))
    const seriesTag = wrapper
      .findAll('.detail-link-tag')
      .find((tag) => tag.text().includes('Mistborn'))
    const genreTag = wrapper
      .findAll('.detail-link-tag')
      .find((tag) => tag.text().includes('Fantasy'))

    expect(authorTag).toBeTruthy()
    expect(narratorTag).toBeTruthy()
    expect(publisherTag).toBeTruthy()
    expect(seriesTag).toBeTruthy()
    expect(genreTag).toBeTruthy()

    await authorTag!.trigger('click')

    expect(routerPushMock).toHaveBeenCalledWith('/collection/author/Brandon%20Sanderson')

    await narratorTag!.trigger('click')

    expect(routerPushMock).toHaveBeenCalledWith('/collection/narrator/Michael%20Kramer')

    await publisherTag!.trigger('click')

    expect(routerPushMock).toHaveBeenCalledWith('/collection/publisher/Tor%20Audio')

    await seriesTag!.trigger('click')

    expect(routerPushMock).toHaveBeenCalledWith('/collection/series/Mistborn')

    await genreTag!.trigger('click')

    expect(routerPushMock).toHaveBeenCalledWith('/collection/genre/Fantasy')
  })

  it('opens the edit metadata modal from the detail view action', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 5,
        title: 'Detail Book',
        authors: ['Author One'],
        files: [],
      },
    ] as any as ReturnType<typeof useLibraryStore>['audiobooks']

    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(AudiobookDetailViewCmp, {
      global: {
        plugins: [pinia],
        stubs: {
          EditAudiobookModal: {
            name: 'EditAudiobookModal',
            props: ['isOpen'],
            template: '<div class="edit-audiobook-modal-stub" :data-open="String(isOpen)" />',
          },
        },
      },
    })
    await delay(10)

    const editButton = wrapper.find('button[aria-label="Edit Metadata"]')
    expect(editButton.exists()).toBe(true)

    await editButton.trigger('click')
    await flushAsync()

    expect(wrapper.find('.edit-audiobook-modal-stub').attributes('data-open')).toBe('true')
  })
})
