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

    await flushPromises()

    // a 10-letter title is searched as a title, not looked up as an ASIN
    expect(apiService.advancedSearch).toHaveBeenCalledWith(
      expect.objectContaining({ title: 'Alchemised', author: 'SenLinYu', region: 'fr' }),
    )
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
    expect(search).toHaveBeenLastCalledWith(expect.objectContaining({ region: 'fr' }))
    expect((wrapper.get('[data-testid="record-region"]').element as HTMLSelectElement).value).toBe(
      'fr',
    )

    search.mockResolvedValue([
      {
        asin: 'B0BK9W82PQ',
        title: 'Primal Hunter 4',
        authors: [{ name: 'Zogarth' }],
      } as unknown as SearchResult,
    ])
    await wrapper.get('[data-testid="record-region"]').setValue('us')
    await flushPromises()
    expect(search).toHaveBeenLastCalledWith(expect.objectContaining({ region: 'us' }))

    await wrapper.get('[data-testid="record-result"]').trigger('click')
    expect(wrapper.emitted('select')?.[0]?.[0]).toEqual(
      expect.objectContaining({ asin: 'B0BK9W82PQ', region: 'us' }),
    )
  })

  function itemWithMatch(match: Partial<SearchResult> | null) {
    return {
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
      selectedMatch: match as SearchResult | null,
      hasSearched: true,
      isSearching: false,
      selected: true,
    }
  }

  const fieldValue = (wrapper: ReturnType<typeof mount>, testid: string) =>
    (wrapper.get(`[data-testid="${testid}"]`).element as HTMLInputElement).value

  it('fills the title with the folder name and searches it, showing the current match as picked', async () => {
    const search = vi.mocked(apiService.advancedSearch)
    search.mockResolvedValue([
      {
        asin: 'B0HM45F8MW',
        title: 'Primal Hunter, Vol. 7',
        authors: [{ name: 'Zogarth' }],
      } as unknown as SearchResult,
      {
        asin: 'B0BK9W82PQ',
        title: 'Primal Hunter 4',
        authors: [{ name: 'Zogarth' }],
      } as unknown as SearchResult,
    ])
    const wrapper = mount(LibraryImportSearchModal, {
      props: {
        item: {
          ...itemWithMatch({ asin: 'B0HM45F8MW', title: 'Primal Hunter, Vol. 7', region: 'fr' }),
          sourceFiles: ['/incoming/PH4/01.mp3', '/incoming/PH4/02.mp3'],
          fileCount: 2,
        },
      },
    })
    await flushPromises()

    expect(fieldValue(wrapper, 'record-title')).toBe('Primal Hunter 4')
    expect(fieldValue(wrapper, 'record-asin')).toBe('B0HM45F8MW')
    expect(search).toHaveBeenCalledTimes(1)
    expect(search).toHaveBeenCalledWith({
      title: 'Primal Hunter 4',
      author: 'Zogarth',
      region: 'fr',
      cap: 8,
    })
    const results = wrapper.findAll('[data-testid="record-result"]')
    expect(results).toHaveLength(2)
    expect(results[0]!.classes()).toContain('picked')
    expect(results[1]!.classes()).not.toContain('picked')
  })

  it('fills the title with the file name for a single-file book', async () => {
    const wrapper = mount(LibraryImportSearchModal, {
      props: {
        item: {
          ...itemWithMatch(null),
          fullPath: '/incoming/Zelazny/Jack of Shadows.m4b',
          sourceFiles: ['/incoming/Zelazny/Jack of Shadows.m4b'],
          folderName: 'Zelazny',
          detectedTitle: 'Amber',
        },
      },
    })
    await flushPromises()

    expect(fieldValue(wrapper, 'record-title')).toBe('Jack of Shadows')
    expect(apiService.advancedSearch).toHaveBeenCalledWith(
      expect.objectContaining({ title: 'Jack of Shadows' }),
    )
  })

  it('looks the ASIN up again when the region changes, and says when that region has no record', async () => {
    const search = vi.mocked(apiService.advancedSearch)
    search.mockResolvedValue([])
    const wrapper = mount(LibraryImportSearchModal, {
      props: {
        item: itemWithMatch({ asin: 'B0HM45F8MW', title: 'Primal Hunter, Vol. 7', region: 'fr' }),
      },
    })
    await flushPromises()

    await wrapper.get('[data-testid="record-region"]').setValue('us')
    await flushPromises()

    expect(search).toHaveBeenLastCalledWith({ asin: 'B0HM45F8MW', region: 'us', cap: 5 })
    expect(wrapper.get('[data-testid="record-asin-not-found"]').text()).toContain('B0HM45F8MW')
  })

  it('looks a typed ASIN up, fills the title and author, and selects the record with its region', async () => {
    const search = vi.mocked(apiService.advancedSearch)
    const wrapper = mount(LibraryImportSearchModal, {
      props: { item: itemWithMatch(null) },
    })
    await flushPromises()

    search.mockResolvedValue([
      {
        asin: 'B0BK9W82PQ',
        title: 'Primal Hunter 4',
        authors: [{ name: 'Zogarth' }],
      } as unknown as SearchResult,
    ])
    await wrapper.get('[data-testid="record-region"]').setValue('us')
    await flushPromises()
    await wrapper.get('[data-testid="record-asin"]').setValue('B0BK9W82PQ')
    await flushPromises()

    expect(search).toHaveBeenLastCalledWith({ asin: 'B0BK9W82PQ', region: 'us', cap: 5 })
    expect(fieldValue(wrapper, 'record-title')).toBe('Primal Hunter 4')
    expect(fieldValue(wrapper, 'record-author')).toBe('Zogarth')

    await wrapper.get('[data-testid="record-result"]').trigger('click')
    expect(wrapper.emitted('select')?.[0]?.[0]).toEqual(
      expect.objectContaining({ asin: 'B0BK9W82PQ', region: 'us' }),
    )
    expect(wrapper.emitted('close')).toBeTruthy()
  })
})
