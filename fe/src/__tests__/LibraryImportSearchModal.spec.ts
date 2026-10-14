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
    expect(search).toHaveBeenLastCalledWith(expect.objectContaining({ region: 'fr' }))
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
    expect(search).toHaveBeenLastCalledWith(expect.objectContaining({ region: 'us' }))

    await wrapper.get('.result-item').trigger('click')
    expect(wrapper.emitted('select')?.[0]?.[0]).toEqual(
      expect.objectContaining({ asin: 'B0BK9W82PQ', region: 'us' }),
    )
  })
})
