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
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { apiService } from '@/services/api'
import type { SearchResult } from '@/types'
import LibraryImportSearchModal from '@/components/domain/audiobook/LibraryImportSearchModal.vue'

const getProtectedImageSrc = vi.fn(() => 'https://example.com/protected.jpg')

vi.mock('@/composables/useProtectedImages', () => ({
  useProtectedImages: () => ({
    getProtectedImageSrc,
  }),
}))

describe('LibraryImportSearchModal', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    setActivePinia(createPinia())
    vi.spyOn(apiService, 'getApplicationSettings').mockResolvedValue({
      defaultSearchRegion: 'fr',
    } as unknown as Awaited<ReturnType<typeof apiService.getApplicationSettings>>)
    vi.spyOn(apiService, 'advancedSearch').mockResolvedValue([
      {
        asin: 'B000APXZHK',
        title: 'Alchemised',
        imageUrl: '/api/v1/images/B000APXZHK',
        authors: [{ name: 'SenLinYu' }],
      } as unknown as SearchResult,
    ])
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('displays narrator when result includes narrator data', async () => {
    vi.spyOn(apiService, 'advancedSearch').mockResolvedValue([
      {
        asin: 'B000APXZHK',
        title: 'Alchemised',
        imageUrl: '/api/v1/images/B000APXZHK',
        authors: [{ name: 'SenLinYu' }],
        narrator: 'John Smith',
      } as unknown as SearchResult,
    ])

    const wrapper = mount(LibraryImportSearchModal, {
      props: {
        item: {
          id: 'C:\\incoming\\Alchemised.m4b',
          fullPath: 'C:\\incoming\\Alchemised.m4b',
          sourceFiles: ['C:\\incoming\\Alchemised.m4b'],
          folderPath: 'C:\\incoming',
          relativePath: 'Alchemised',
          folderName: 'Alchemised',
          detectedTitle: 'Alchemised',
          detectedAuthor: 'SenLinYu',
          format: 'M4B',
          fileCount: 1,
          selectedMatch: null,
          hasSearched: false,
          isSearching: false,
          selected: false,
        },
      },
    })

    await new Promise((resolve) => setTimeout(resolve, 0))
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.result-narrator').text()).toContain('John Smith')
  })

  it('routes result thumbnails through the protected image helper', async () => {
    const wrapper = mount(LibraryImportSearchModal, {
      props: {
        item: {
          id: 'C:\\incoming\\Alchemised.m4b',
          fullPath: 'C:\\incoming\\Alchemised.m4b',
          sourceFiles: ['C:\\incoming\\Alchemised.m4b'],
          folderPath: 'C:\\incoming',
          relativePath: 'Alchemised',
          folderName: 'Alchemised',
          detectedTitle: 'Alchemised',
          detectedAuthor: 'SenLinYu',
          format: 'M4B',
          fileCount: 1,
          selectedMatch: null,
          hasSearched: false,
          isSearching: false,
          selected: false,
        },
      },
    })

    await new Promise((resolve) => setTimeout(resolve, 0))
    await wrapper.vm.$nextTick()

    expect(getProtectedImageSrc).toHaveBeenCalledWith(
      '/api/v1/images/B000APXZHK',
      '/placeholder.svg',
    )
    expect(wrapper.find('img').attributes('src')).toBe('https://example.com/protected.jpg')
  })

  it('searches the configured region first, then the region the user picks, which the match keeps', async () => {
    const search = vi.mocked(apiService.advancedSearch)
    const wrapper = mount(LibraryImportSearchModal, {
      props: {
        item: {
          id: '/incoming/PH4/01.mp3',
          fullPath: '/incoming/PH4/01.mp3',
          sourceFiles: ['/incoming/PH4/01.mp3'],
          folderPath: '/incoming/PH4',
          relativePath: 'PH4',
          folderName: 'Primal Hunter 4',
          detectedTitle: 'The Primal Hunter 4',
          detectedAuthor: 'Zogarth',
          format: 'MP3',
          fileCount: 1,
          selectedMatch: null,
          hasSearched: false,
          isSearching: false,
          selected: false,
        },
      },
    })
    await flushPromises()
    expect(search).toHaveBeenLastCalledWith(
      expect.objectContaining({ region: 'fr' }),
      expect.any(AbortSignal),
    )
    expect(
      (wrapper.get('[data-testid="find-match-region"]').element as HTMLSelectElement).value,
    ).toBe('fr')

    search.mockResolvedValue([
      {
        asin: 'B0BK9W82PQ',
        title: 'Primal Hunter 4',
        authors: [{ name: 'Zogarth' }],
      } as unknown as SearchResult,
    ])
    await wrapper.get('[data-testid="find-match-region"]').setValue('us')
    await flushPromises()
    expect(search).toHaveBeenLastCalledWith(
      expect.objectContaining({ region: 'us' }),
      expect.any(AbortSignal),
    )

    await wrapper.get('.result-item').trigger('click')
    expect(wrapper.emitted('select')?.[0]?.[0]).toEqual(
      expect.objectContaining({ asin: 'B0BK9W82PQ', region: 'us' }),
    )
  })

  it('keeps the results of the region the user picked when the earlier search answers last', async () => {
    const search = vi.mocked(apiService.advancedSearch)
    const pending: Record<string, (results: SearchResult[]) => void> = {}
    const signals: Record<string, AbortSignal | undefined> = {}
    search.mockImplementation(
      (params, signal) =>
        new Promise<SearchResult[]>((resolve) => {
          pending[params.region!] = resolve
          signals[params.region!] = signal
        }),
    )
    const wrapper = mount(LibraryImportSearchModal, {
      props: {
        item: {
          id: '/incoming/PH4/01.mp3',
          fullPath: '/incoming/PH4/01.mp3',
          sourceFiles: ['/incoming/PH4/01.mp3'],
          folderPath: '/incoming/PH4',
          relativePath: 'PH4',
          folderName: 'Primal Hunter 4',
          detectedTitle: 'The Primal Hunter 4',
          detectedAuthor: 'Zogarth',
          format: 'MP3',
          fileCount: 1,
          selectedMatch: null,
          hasSearched: false,
          isSearching: false,
          selected: false,
        },
      },
    })
    await flushPromises()
    expect(pending.fr).toBeDefined()

    // The French search is still running when the user switches to US
    await wrapper.get('[data-testid="find-match-region"]').setValue('us')
    await flushPromises()
    expect(signals.fr?.aborted).toBe(true)

    pending.us!([
      { asin: 'B0BK9W82PQ', title: 'Primal Hunter 4', authors: [{ name: 'Zogarth' }] },
    ] as unknown as SearchResult[])
    await flushPromises()
    pending.fr!([
      { asin: 'B0HM45F8MW', title: 'Primal Hunter, Vol. 7', authors: [{ name: 'Zogarth' }] },
    ] as unknown as SearchResult[])
    await flushPromises()

    const items = wrapper.findAll('.result-item')
    expect(items).toHaveLength(1)
    expect(items[0]!.text()).toContain('Primal Hunter 4')
    expect(wrapper.find('.search-spinner').exists()).toBe(false)
    await items[0]!.trigger('click')
    expect(wrapper.emitted('select')?.[0]?.[0]).toEqual(
      expect.objectContaining({ asin: 'B0BK9W82PQ', region: 'us' }),
    )
  })

  it('does not offer the previous results while a new search runs', async () => {
    const search = vi.mocked(apiService.advancedSearch)
    const wrapper = mount(LibraryImportSearchModal, {
      props: {
        item: {
          id: '/incoming/Alchemised.m4b',
          fullPath: '/incoming/Alchemised.m4b',
          sourceFiles: ['/incoming/Alchemised.m4b'],
          folderPath: '/incoming',
          relativePath: 'Alchemised',
          folderName: 'Alchemised',
          detectedTitle: 'Alchemised',
          detectedAuthor: 'SenLinYu',
          format: 'M4B',
          fileCount: 1,
          selectedMatch: null,
          hasSearched: false,
          isSearching: false,
          selected: false,
        },
      },
    })
    await flushPromises()
    expect(wrapper.findAll('.result-item')).toHaveLength(1)

    search.mockImplementation(() => new Promise<SearchResult[]>(() => {}))
    await wrapper.get('[data-testid="find-match-region"]').setValue('us')
    await flushPromises()

    expect(wrapper.findAll('.result-item')).toHaveLength(0)
    expect(wrapper.find('.search-spinner').exists()).toBe(true)
  })

  it('closes quietly while a search is running: the cancelled request is not reported', async () => {
    const unhandled = vi.fn()
    process.on('unhandledRejection', unhandled)
    try {
      // Like fetch: the request fails once its signal is aborted (the API wraps it as Network error)
      vi.mocked(apiService.advancedSearch).mockImplementation(
        (_params, signal) =>
          new Promise<SearchResult[]>((_resolve, reject) => {
            signal?.addEventListener('abort', () => reject(new Error('Network error')))
          }),
      )
      const wrapper = mount(LibraryImportSearchModal, {
        props: {
          item: {
            id: '/incoming/PH4/01.mp3',
            fullPath: '/incoming/PH4/01.mp3',
            sourceFiles: ['/incoming/PH4/01.mp3'],
            folderPath: '/incoming/PH4',
            relativePath: 'PH4',
            folderName: 'Primal Hunter 4',
            detectedTitle: 'The Primal Hunter 4',
            detectedAuthor: 'Zogarth',
            format: 'MP3',
            fileCount: 1,
            selectedMatch: null,
            hasSearched: false,
            isSearching: false,
            selected: false,
          },
        },
      })
      await flushPromises()
      expect(wrapper.find('.search-spinner').exists()).toBe(true)

      wrapper.unmount()
      await flushPromises()
      await new Promise((resolve) => setTimeout(resolve, 0))

      expect(unhandled).not.toHaveBeenCalled()
    } finally {
      process.off('unhandledRejection', unhandled)
    }
  })

  it('tells the import a search is running until it ends, also when the modal closes during it', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    let answer: (results: SearchResult[]) => void = () => {}
    vi.mocked(apiService.advancedSearch).mockImplementation(
      (_params, signal) =>
        new Promise<SearchResult[]>((resolve, reject) => {
          answer = resolve
          signal?.addEventListener('abort', () => reject(new Error('Network error')))
        }),
    )
    const item = {
      id: '/incoming/PH4/01.mp3',
      fullPath: '/incoming/PH4/01.mp3',
      sourceFiles: ['/incoming/PH4/01.mp3'],
      folderPath: '/incoming/PH4',
      relativePath: 'PH4',
      folderName: 'Primal Hunter 4',
      detectedTitle: 'The Primal Hunter 4',
      detectedAuthor: 'Zogarth',
      format: 'MP3',
      fileCount: 1,
      selectedMatch: null,
      hasSearched: false,
      isSearching: false,
      selected: false,
    }
    const store = useLibraryImportStore()

    const first = mount(LibraryImportSearchModal, { props: { item } })
    await flushPromises()
    expect(store.findMatchSearching).toBe(true)
    answer([])
    await flushPromises()
    expect(store.findMatchSearching).toBe(false)
    first.unmount()

    const second = mount(LibraryImportSearchModal, { props: { item } })
    await flushPromises()
    expect(store.findMatchSearching).toBe(true)
    second.unmount()
    await flushPromises()
    expect(store.findMatchSearching).toBe(false)
  })
})
