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
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { apiService } from '@/services/api'
import type { SearchResult } from '@/types'
import LibraryImportSearchModal from '@/components/domain/audiobook/LibraryImportSearchModal.vue'
import { modalStubs } from '@/test/stubs'
import { flushAsync } from '@/test/utils/wait'

const getProtectedImageSrc = vi.fn(() => 'https://example.com/protected.jpg')

vi.mock('@/composables/useProtectedImages', () => ({
  useProtectedImages: () => ({
    getProtectedImageSrc,
  }),
}))

describe('LibraryImportSearchModal', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.spyOn(apiService, 'advancedSearch').mockResolvedValue([
      {
        asin: 'B000APXZHK',
        title: 'Alchemised',
        imageUrl: '/api/v1/images/B000APXZHK',
        authors: [{ name: 'SenLinYu' }],
      } as any as SearchResult,
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
      global: {
        stubs: modalStubs,
      },
    })

    await flushAsync()
    await wrapper.vm.$nextTick()

    expect(getProtectedImageSrc).toHaveBeenCalledWith(
      '/api/v1/images/B000APXZHK',
      '/placeholder.svg',
    )
    expect(wrapper.find('img').attributes('src')).toBe('https://example.com/protected.jpg')
  })
})
