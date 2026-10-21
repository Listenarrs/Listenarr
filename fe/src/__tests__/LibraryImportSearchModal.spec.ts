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

const alchemised = {
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
}

const primalHunter = {
  ...alchemised,
  id: '/incoming/PH4/01.mp3',
  fullPath: '/incoming/PH4/01.mp3',
  sourceFiles: ['/incoming/PH4/01.mp3'],
  folderPath: '/incoming/PH4',
  relativePath: 'PH4',
  folderName: 'Primal Hunter 4',
  detectedTitle: 'The Primal Hunter 4',
  detectedAuthor: 'Zogarth',
  format: 'MP3',
}

const ph4 = {
  asin: 'B0BK9W82PQ',
  title: 'Primal Hunter 4',
  authors: [{ name: 'Zogarth' }],
} as unknown as SearchResult

function mountModal(item: typeof alchemised = alchemised) {
  return mount(LibraryImportSearchModal, { props: { item } })
}

function field(wrapper: ReturnType<typeof mountModal>, testId: string) {
  return wrapper.get(`[data-testid="${testId}"]`)
}

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

  it('shows the book in the same card as Repair: ASIN, Title, Author, Region, without a checkbox', async () => {
    const wrapper = mountModal()
    await flushPromises()

    expect(wrapper.findAll('.match-field-label').map((l) => l.text())).toEqual([
      'ASIN',
      'Title',
      'Author',
      'Region',
    ])
    expect((field(wrapper, 'match-title').element as HTMLInputElement).value).toBe('Alchemised')
    expect((field(wrapper, 'match-author').element as HTMLInputElement).value).toBe('SenLinYu')
    expect(wrapper.find('input[type="checkbox"]').exists()).toBe(false)
  })

  it('searches as it opens and shows the results under the book, with their narrator', async () => {
    vi.mocked(apiService.advancedSearch).mockResolvedValue([
      {
        asin: 'B000APXZHK',
        title: 'Alchemised',
        imageUrl: '/api/v1/images/B000APXZHK',
        authors: [{ name: 'SenLinYu' }],
        narrator: 'John Smith',
      } as unknown as SearchResult,
    ])
    const wrapper = mountModal()
    await flushPromises()

    expect(wrapper.findAll('[data-testid="find-match-result"]')).toHaveLength(1)
    expect(wrapper.find('.result-narrator').text()).toContain('John Smith')
  })

  it('routes result thumbnails through the protected image helper', async () => {
    const wrapper = mountModal()
    await flushPromises()

    expect(getProtectedImageSrc).toHaveBeenCalledWith(
      '/api/v1/images/B000APXZHK',
      '/placeholder.svg',
    )
    expect(wrapper.find('img').attributes('src')).toBe('https://example.com/protected.jpg')
  })

  it('searches the configured region first, then the region the user picks, which the match keeps', async () => {
    const search = vi.mocked(apiService.advancedSearch)
    const wrapper = mountModal(primalHunter)
    await flushPromises()
    expect(search).toHaveBeenLastCalledWith(
      expect.objectContaining({ region: 'fr' }),
      expect.any(AbortSignal),
    )
    expect((field(wrapper, 'match-region').element as HTMLSelectElement).value).toBe('fr')

    search.mockResolvedValue([ph4])
    await field(wrapper, 'match-region').setValue('us')
    await field(wrapper, 'match-search').trigger('click')
    await flushPromises()
    expect(search).toHaveBeenLastCalledWith(
      expect.objectContaining({ region: 'us' }),
      expect.any(AbortSignal),
    )

    await wrapper.get('[data-testid="find-match-result"]').trigger('click')
    expect(field(wrapper, 'match-picked').text()).toContain('Primal Hunter 4')
    expect(wrapper.emitted('select')).toBeUndefined()

    await field(wrapper, 'find-match-confirm').trigger('click')
    await flushPromises()
    expect(wrapper.emitted('select')?.[0]?.[0]).toEqual(
      expect.objectContaining({ asin: 'B0BK9W82PQ', region: 'us' }),
    )
    expect(wrapper.emitted('close')).toHaveLength(1)
  })

  it('stops the running search when the region changes: its late answer is not shown', async () => {
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
    const wrapper = mountModal(primalHunter)
    await flushPromises()
    expect(field(wrapper, 'match-search-pending').text()).toContain('France')

    await field(wrapper, 'match-region').setValue('us')
    expect(signals.fr?.aborted).toBe(true)
    expect(wrapper.find('[data-testid="match-search-pending"]').exists()).toBe(false)

    await field(wrapper, 'match-search').trigger('click')
    pending.us!([ph4])
    await flushPromises()
    pending.fr!([
      { asin: 'B0HM45F8MW', title: 'Primal Hunter, Vol. 7', authors: [{ name: 'Zogarth' }] },
    ] as unknown as SearchResult[])
    await flushPromises()

    const items = wrapper.findAll('[data-testid="find-match-result"]')
    expect(items).toHaveLength(1)
    expect(items[0]!.text()).toContain('Primal Hunter 4')
  })

  it('offers no match until a new record is picked or a valid ASIN typed', async () => {
    const wrapper = mountModal()
    await flushPromises()
    expect(field(wrapper, 'find-match-confirm').attributes('disabled')).toBeDefined()

    await field(wrapper, 'match-asin').setValue('B0BK9')
    expect(field(wrapper, 'match-invalid-asin').text()).toContain('10 letters or digits')
    expect(field(wrapper, 'find-match-confirm').attributes('disabled')).toBeDefined()

    await field(wrapper, 'match-asin').setValue('B0BK9W82PQ')
    expect(field(wrapper, 'find-match-confirm').attributes('disabled')).toBeUndefined()
  })

  it('checks a typed ASIN in its region before using it', async () => {
    const search = vi.mocked(apiService.advancedSearch)
    const wrapper = mountModal(primalHunter)
    await flushPromises()

    search.mockResolvedValue([])
    await field(wrapper, 'match-asin').setValue('B0BK9W82PQ')
    await field(wrapper, 'find-match-confirm').trigger('click')
    await flushPromises()
    expect(search).toHaveBeenLastCalledWith(
      expect.objectContaining({ asin: 'B0BK9W82PQ', region: 'fr' }),
    )
    expect(field(wrapper, 'find-match-error').text()).toContain(
      'Audible France (FR) has no record for B0BK9W82PQ',
    )
    expect(wrapper.emitted('select')).toBeUndefined()

    search.mockResolvedValue([ph4])
    await field(wrapper, 'find-match-confirm').trigger('click')
    await flushPromises()
    expect(wrapper.emitted('select')?.[0]?.[0]).toEqual(
      expect.objectContaining({ asin: 'B0BK9W82PQ', region: 'fr' }),
    )
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
      const wrapper = mountModal(primalHunter)
      await flushPromises()
      expect(wrapper.find('[data-testid="match-search-pending"]').exists()).toBe(true)

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
    const store = useLibraryImportStore()

    const first = mountModal(primalHunter)
    await flushPromises()
    expect(store.findMatchSearching).toBe(true)
    answer([])
    await flushPromises()
    expect(store.findMatchSearching).toBe(false)
    first.unmount()

    const second = mountModal(primalHunter)
    await flushPromises()
    expect(store.findMatchSearching).toBe(true)
    second.unmount()
    await flushPromises()
    expect(store.findMatchSearching).toBe(false)
  })
})
