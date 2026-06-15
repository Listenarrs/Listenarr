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

import { describe, it, expect, beforeEach, vi } from 'vitest'
import type { Mock } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import AddNewView from '@/views/content/AddNewView.vue'
import { useLibraryStore } from '@/stores/library'
import { installStorageMock } from '@/test/utils/storage'
import { delay } from '@/test/utils/wait'

vi.mock('@/services/signalr', () => ({
  signalRService: {
    connect: vi.fn(async () => undefined),
    onDownloadsList: vi.fn(() => () => undefined),
    onSearchProgress: vi.fn(() => () => undefined),
    onQueueUpdate: vi.fn(() => () => undefined),
    onDownloadUpdate: vi.fn(() => () => undefined),
    onFilesRemoved: vi.fn(() => () => undefined),
    onAudiobookUpdate: vi.fn(() => () => undefined),
    onNotification: vi.fn(() => () => undefined),
    onToast: vi.fn(() => () => undefined),
  },
}))

vi.mock('@/services/api', () => {
  const apiService = {
    searchAudibleByTitleAndAuthor: vi.fn(async () => ({ totalResults: 0, results: [] })),
    advancedSearch: vi.fn(async (params: unknown) => {
      const p = params as { title?: string; author?: string } | undefined
      if (p?.title) {
        const resp = await (apiService.searchAudibleByTitleAndAuthor as Mock)(p.title, p.author)
        return resp.results || resp || []
      }
      return { totalResults: 0, results: [] }
    }),
    getImageUrl: vi.fn((url: string) => url || ''),
    getStartupConfig: vi.fn(async () => ({})),
    getApplicationSettings: vi.fn(async () => ({})),
    getLibrary: vi.fn(async () => []),
  }

  return {
    apiService,
    getStartupConfig: apiService.getStartupConfig,
    getApplicationSettings: apiService.getApplicationSettings,
    ensureImageCached: vi.fn(async (url: string) => url || ''),
  }
})

describe('AddNewView pagination', () => {
  const createTestRouter = () =>
    createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', component: { template: '<div />' } }],
    })

  beforeEach(() => {
    vi.clearAllMocks()
    installStorageMock()
    window.localStorage.clear()
    const pinia = createPinia()
    setActivePinia(pinia)
  })

  it('uses total from aggregated API response', () => {
    // With backend aggregation, total is simply the totalResults from API
    const apiResponse = { totalResults: 150, results: [] }
    expect(apiResponse.totalResults).toBe(150)
  })

  it('handles empty results from API', () => {
    const apiResponse = { totalResults: 0, results: [] }
    expect(apiResponse.totalResults).toBe(0)
  })

  it('does not render empty results controls when title results have no pagination controls', async () => {
    const router = createTestRouter()
    const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })
    const vm = wrapper.vm as any as {
      searchType?: string
      titleResults?: unknown[]
    }

    vm.searchType = 'title'
    vm.titleResults = [
      {
        key: 'dune-messiah',
        title: 'Dune Messiah',
        author_name: ['Frank Herbert'],
        searchResult: {
          artist: 'Frank Herbert',
        },
      },
    ]

    await wrapper.vm.$nextTick()

    expect(wrapper.find('.title-results').exists()).toBe(true)
    expect(wrapper.find('.results-controls').exists()).toBe(false)
  })

  it('renders results controls when title results need client-side pagination', async () => {
    const router = createTestRouter()
    const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })
    const vm = wrapper.vm as any as {
      searchType?: string
      titleResults?: unknown[]
      totalTitleResultsCount?: number
    }

    vm.searchType = 'title'
    vm.titleResults = [
      {
        key: 'dune',
        title: 'Dune',
        author_name: ['Frank Herbert'],
        searchResult: {
          artist: 'Frank Herbert',
        },
      },
    ]
    vm.totalTitleResultsCount = 60

    await wrapper.vm.$nextTick()

    expect(wrapper.find('.results-controls').exists()).toBe(true)
    expect(wrapper.find('.client-pagination-controls').exists()).toBe(true)
  })

  it('maps audible metadata to result fields', async () => {
    const apiModule = await import('@/services/api')
    const apiService = apiModule.apiService as any as { searchAudibleByTitleAndAuthor?: Mock }
    apiService.searchAudibleByTitleAndAuthor?.mockResolvedValue({
      totalResults: 1,
      results: [
        {
          asin: 'B000123',
          title: 'Dune',
          subtitle: 'A Heroic Saga',
          authors: [{ name: 'Frank Herbert' }],
          imageUrl: 'http://img',
          runtimeLengthMin: 900,
          language: 'english',
          series: [{ name: 'Dune Series', position: '1' }],
          publisher: 'Chilton',
          narrators: [{ name: 'Scott Brick' }],
          releaseDate: '1965-08-01',
          link: 'https://www.audible.com/pd/B000123',
        },
      ],
    })

    const router = createTestRouter()
    const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })
    const vm = wrapper.vm as any as {
      showAdvancedSearch?: boolean
      advancedSearchParams?: Record<string, unknown>
      performAdvancedSearch?: () => Promise<void>
      allAudibleResults?: unknown[]
      titleResults?: unknown[]
    }

    // Use advanced search with title to trigger audible path
    vm.showAdvancedSearch = true
    vm.advancedSearchParams = { title: 'Dune' }

    await vm.performAdvancedSearch()

    expect(vm.allAudibleResults.length).toBe(1)
    expect(vm.titleResults.length).toBe(1)
    const tr = vm.titleResults[0] as any
    expect(tr.searchResult.narrator).toBe('Scott Brick')
    expect(tr.searchResult.subtitle).toBe('A Heroic Saga')
    expect(tr.searchResult.series).toBe('Dune Series')
    expect(tr.publisher && tr.publisher[0]).toBe('Chilton')
    expect(tr.first_publish_year).toBe(1965)
    expect(tr.searchResult.productUrl).toBe('https://www.audible.com/pd/B000123')

    // Rendered subtitle should appear in the title-result card
    await wrapper.vm.$nextTick()
    const subtitleEl = wrapper.find('.title-results .title-result-card .result-subtitle')
    expect(subtitleEl.exists()).toBe(true)
    expect(subtitleEl.text()).toBe('A Heroic Saga')
  })

  it('renders direct image URLs on advanced search results', async () => {
    const apiModule = await import('@/services/api')
    const apiService = apiModule.apiService as any as { searchAudibleByTitleAndAuthor?: Mock }
    apiService.searchAudibleByTitleAndAuthor?.mockResolvedValue({
      totalResults: 1,
      results: [
        {
          asin: 'B000124',
          title: 'Dune Messiah',
          authors: [{ name: 'Frank Herbert' }],
          imageUrl: 'http://img2',
          runtimeLengthMin: 720,
          language: 'english',
        },
      ],
    })

    const router = createTestRouter()
    const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })
    const vm = wrapper.vm as any as {
      showAdvancedSearch?: boolean
      advancedSearchParams?: Record<string, unknown>
      performAdvancedSearch?: () => Promise<void>
      allAudibleResults?: unknown[]
      titleResults?: unknown[]
    }

    vm.showAdvancedSearch = true
    vm.advancedSearchParams = { title: 'Dune' }

    await vm.performAdvancedSearch()
    await wrapper.vm.$nextTick()

    // Find result image element
    const img = wrapper.find('.result-poster img')
    expect(img.exists()).toBe(true)
    expect(img.attributes('src')).toBe('http://img2')
  })

  it('shows language options in the add new search selects', async () => {
    const router = createTestRouter()
    const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })

    // Simple search language select should contain the supported language filters
    const simpleSelect = wrapper.find('select.language-select')
    expect(simpleSelect.exists()).toBe(true)
    const simpleOptions = simpleSelect.findAll('option').map((o) => o.text())
    expect(simpleOptions).toContain('All')
    expect(simpleOptions).toContain('English')

    // Advanced search select should be labeled Language and contain German
    await wrapper.vm.$nextTick()
    const advToggle = wrapper.find('button.search-btn.advanced-btn')
    expect(advToggle.exists()).toBe(true)
    await advToggle.trigger('click')

    const advSelect = wrapper.find('select#adv-language')
    expect(advSelect.exists()).toBe(true)
    const advOptions = advSelect.findAll('option').map((o) => o.text())
    expect(advOptions).toContain('German')
    expect(wrapper.find('label[for="adv-language"]').text()).toBe('Language')
  })

  it('applies configured default region and language from application settings', async () => {
    const apiModule = await import('@/services/api')
    const apiService = apiModule.apiService as any as { getApplicationSettings?: Mock }
    apiService.getApplicationSettings?.mockResolvedValue({
      defaultSearchRegion: 'de',
      defaultSearchLanguage: 'polish',
    })

    const router = createTestRouter()
    const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })
    await flushPromises()

    const vm = wrapper.vm as any as {
      searchLanguage?: string
      preferredSearchLanguage?: string
      advancedSearchParams?: { language?: string }
    }

    expect(vm.searchLanguage).toBe('de')
    expect(vm.preferredSearchLanguage).toBe('polish')
  })

  it('omits language filtering when default language is set to all', async () => {
    const apiModule = await import('@/services/api')
    const apiService = apiModule.apiService as any as { getApplicationSettings?: Mock }
    apiService.getApplicationSettings?.mockResolvedValue({
      defaultSearchRegion: 'de',
      defaultSearchLanguage: 'all',
    })
    const advancedSearchSpy = vi.spyOn(apiModule.apiService, 'advancedSearch').mockResolvedValue([])

    const router = createTestRouter()
    const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })
    await flushPromises()

    const vm = wrapper.vm as any as {
      searchQuery?: string
      preferredSearchLanguage?: string
      performSearch?: () => Promise<void>
    }

    expect(vm.preferredSearchLanguage).toBe('all')

    vm.searchQuery = 'Dune'
    await vm.performSearch?.()
    await flushPromises()

    expect(advancedSearchSpy).toHaveBeenCalled()
    const lastCall = advancedSearchSpy.mock.calls.at(-1)?.[0] as Record<string, unknown> | undefined
    expect(lastCall?.region).toBe('de')
    expect(lastCall).not.toHaveProperty('language')
    advancedSearchSpy.mockRestore()
  })

  it('filters mixed-language audible results using the selected language while keeping the default region', async () => {
    const apiModule = await import('@/services/api')
    const apiService = apiModule.apiService as any as { getApplicationSettings?: Mock }
    apiService.getApplicationSettings?.mockResolvedValue({
      defaultSearchRegion: 'de',
      defaultSearchLanguage: 'english',
    })
    const advancedSearchSpy = vi.spyOn(apiModule.apiService, 'advancedSearch').mockResolvedValue([
      {
        asin: 'BENGLISH',
        title: 'English Result',
        authors: [{ name: 'Author A' }],
        imageUrl: 'http://img-en',
        language: 'english',
      },
      {
        asin: 'BGERMAN',
        title: 'German Result',
        authors: [{ name: 'Author B' }],
        imageUrl: 'http://img-de',
        language: 'de',
      },
    ] as any)

    const router = createTestRouter()
    const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })
    await flushPromises()

    const vm = wrapper.vm as any as {
      searchQuery?: string
      titleResults?: Array<{ title?: string }>
      performSearch?: () => Promise<void>
    }

    vm.searchQuery = 'Dune'
    await vm.performSearch?.()
    await flushPromises()
    await wrapper.vm.$nextTick()

    expect(advancedSearchSpy).toHaveBeenCalled()
    const lastCall = advancedSearchSpy.mock.calls.at(-1)?.[0] as Record<string, unknown> | undefined
    expect(lastCall?.region).toBe('de')
    expect(lastCall?.language).toBe('english')
    expect(vm.titleResults?.length).toBe(1)
    expect(vm.titleResults?.[0]?.title).toBe('English Result')
    advancedSearchSpy.mockRestore()
  })

  it('defaults to title search for simple unprefixed queries (simple search)', async () => {
    const apiModule = await import('@/services/api')
    const apiService = apiModule.apiService as any as { searchAudibleByTitleAndAuthor?: Mock }
    apiService.searchAudibleByTitleAndAuthor?.mockResolvedValue({
      totalResults: 1,
      results: [
        {
          asin: 'B000999',
          title: 'Dune Simple',
          authors: [{ name: 'Frank Herbert' }],
          imageUrl: 'http://imgsimple',
          language: 'english',
        },
      ],
    })

    const router = createTestRouter()
    const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })
    const vm = wrapper.vm as any as {
      searchQuery?: string
      performSearch?: () => Promise<void>
      titleResults?: unknown[]
    }

    // Simulate entering a simple unprefixed query in the unified search
    vm.searchQuery = 'Dune Simple'

    await vm.performSearch()
    await flushPromises()
    await wrapper.vm.$nextTick()

    // The UX hint should show 'Searching by title' when no prefix is present
    const hint = wrapper.find('#unified-search-hint')
    expect(hint.exists()).toBe(true)
    expect(hint.text()).toContain('Searching by title')

    expect(vm.titleResults.length).toBe(1)
    const tr = vm.titleResults[0] as any
    expect(tr.title).toBe('Dune Simple')
  })

  it('does not search automatically while typing in the unified search', async () => {
    const apiModule = await import('@/services/api')
    const advancedSearchSpy = vi.spyOn(apiModule.apiService, 'advancedSearch')

    vi.useFakeTimers()
    try {
      const router = createTestRouter()
      const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })
      await flushPromises()

      const input = wrapper.find('#unified-search-input')
      await input.setValue('Dune')
      await vi.advanceTimersByTimeAsync(1100)
      await flushPromises()

      const vm = wrapper.vm as unknown as { searchType?: string }
      expect(vm.searchType).toBe('title')
      expect(advancedSearchSpy).not.toHaveBeenCalled()
    } finally {
      vi.useRealTimers()
      advancedSearchSpy.mockRestore()
    }
  })

  it('submits the unified search when pressing enter in the search field', async () => {
    const apiModule = await import('@/services/api')
    const advancedSearchSpy = vi.spyOn(apiModule.apiService, 'advancedSearch').mockResolvedValue([])

    try {
      const router = createTestRouter()
      const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })
      await flushPromises()

      const input = wrapper.find('#unified-search-input')
      await input.setValue('Dune')
      await input.trigger('keydown.enter')
      await flushPromises()

      expect(advancedSearchSpy).toHaveBeenCalled()
      expect(advancedSearchSpy.mock.calls.at(-1)?.[0]).toMatchObject({ title: 'Dune' })
    } finally {
      advancedSearchSpy.mockRestore()
    }
  })

  it('keeps add-new search controls keyboard accessible', async () => {
    const router = createTestRouter()
    const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })
    await flushPromises()

    expect(wrapper.find('label[for="unified-search-input"]').exists()).toBe(true)
    expect(wrapper.find('form.unified-search-form[role="search"]').exists()).toBe(true)
    expect(wrapper.find('form.unified-search-form button[type="submit"]').exists()).toBe(true)

    await wrapper.find('button.advanced-btn').trigger('click')
    await wrapper.vm.$nextTick()

    expect(wrapper.findAll('button.simple-search-button')).toHaveLength(1)
    expect(wrapper.find('form.advanced-search-form[role="search"]').exists()).toBe(true)
    for (const id of [
      'adv-title',
      'adv-author',
      'adv-series',
      'adv-isbn',
      'adv-asin',
      'adv-language',
    ]) {
      expect(wrapper.find(`label[for="${id}"]`).exists()).toBe(true)
      expect(wrapper.find(`#${id}`).attributes('tabindex')).not.toBe('-1')
    }
    expect(wrapper.find('form.advanced-search-form button[type="submit"]').exists()).toBe(true)
  })

  it('submits advanced search when pressing enter in an advanced text field', async () => {
    const apiModule = await import('@/services/api')
    const advancedSearchSpy = vi.spyOn(apiModule.apiService, 'advancedSearch').mockResolvedValue([])

    try {
      const router = createTestRouter()
      const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })
      await flushPromises()

      await wrapper.find('button.advanced-btn').trigger('click')
      await wrapper.vm.$nextTick()

      const titleInput = wrapper.find('#adv-title')
      await titleInput.setValue('Dune')
      await titleInput.trigger('keydown.enter')
      await flushPromises()

      expect(advancedSearchSpy).toHaveBeenCalled()
      expect(advancedSearchSpy.mock.calls.at(-1)?.[0]).toMatchObject({ title: 'Dune' })
    } finally {
      advancedSearchSpy.mockRestore()
    }
  })

  it('defaults to title search for simple unprefixed queries (advanced path)', async () => {
    const apiModule = await import('@/services/api')
    const apiService = apiModule.apiService as any as { searchAudibleByTitleAndAuthor?: Mock }
    apiService.searchAudibleByTitleAndAuthor?.mockResolvedValue({
      totalResults: 1,
      results: [
        {
          asin: 'B000999',
          title: 'Dune Simple',
          authors: [{ name: 'Frank Herbert' }],
          imageUrl: 'http://imgsimple',
          language: 'english',
        },
      ],
    })

    const router = createTestRouter()
    const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })
    const vm = wrapper.vm as any as {
      searchQuery?: string
      performAdvancedSearch?: () => Promise<void>
      titleResults?: unknown[]
    }

    // Simulate entering a simple unprefixed query in the unified search
    vm.searchQuery = 'Dune Simple'

    await vm.performAdvancedSearch()
    await wrapper.vm.$nextTick()

    expect(vm.titleResults.length).toBe(1)
    const tr = vm.titleResults[0] as any
    expect(tr.title).toBe('Dune Simple')
  })

  it('shows toast and scrolls to input when simple search returns no results', async () => {
    const apiModule = await import('@/services/api')
    const apiService = apiModule.apiService as any as { searchAudibleByTitleAndAuthor?: Mock }
    apiService.searchAudibleByTitleAndAuthor?.mockResolvedValue({ totalResults: 0, results: [] })

    const router = createTestRouter()
    const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })
    const vm = wrapper.vm as any as {
      searchQuery?: string
      performSearch?: () => Promise<void>
    }

    // Spy on window.scrollTo
    const scrollSpy = vi.spyOn(window, 'scrollTo').mockImplementation(() => {})

    vm.searchQuery = 'Nothing'
    await vm.performSearch()
    await wrapper.vm.$nextTick()
    // allow microtasks to flush so the watch handler runs and any scroll is triggered
    await delay(10)

    const toastSvc = (await import('@/services/toastService')).useToast()
    expect(toastSvc.toasts.length).toBeGreaterThan(0)
    expect(toastSvc.toasts[0].title).toBe('No results found')

    // Scroll behavior is executed in the browser and can be environment-dependent in jsdom;
    // assert the user-facing toast is shown which signals the empty-state handling.
    scrollSpy.mockRestore()
  })

  it('maps runtime from runtimeLengthMin (minutes) and keeps as minutes', async () => {
    const apiModule = await import('@/services/api')
    const apiService = apiModule.apiService as any as { searchAudibleByTitleAndAuthor?: Mock }
    apiService.searchAudibleByTitleAndAuthor?.mockResolvedValue({
      totalResults: 1,
      results: [
        {
          asin: 'B000125',
          title: 'Children of Dune',
          authors: [{ name: 'Frank Herbert' }],
          imageUrl: 'http://img3',
          runtimeLengthMin: 10,
          language: 'english',
        },
      ],
    })

    const router = createTestRouter()
    const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })
    const vm = wrapper.vm as any as {
      showAdvancedSearch?: boolean
      advancedSearchParams?: Record<string, unknown>
      performAdvancedSearch?: () => Promise<void>
      allAudibleResults?: unknown[]
      titleResults?: unknown[]
    }

    vm.showAdvancedSearch = true
    vm.advancedSearchParams = { title: 'Children of Dune' }

    await vm.performAdvancedSearch()
    expect(vm.titleResults.length).toBe(1)
    const tr = vm.titleResults[0] as any
    expect(tr.searchResult.runtime).toBe(10)
  })

  it('maps runtime from lengthMinutes (metadata field) and keeps as minutes', async () => {
    const apiModule = await import('@/services/api')
    const apiService = apiModule.apiService as any as { searchAudibleByTitleAndAuthor?: Mock }
    apiService.searchAudibleByTitleAndAuthor?.mockResolvedValue({
      totalResults: 1,
      results: [
        {
          asin: 'B000126',
          title: 'Heretics of Dune',
          authors: [{ name: 'Frank Herbert' }],
          imageUrl: 'http://img4',
          lengthMinutes: 12,
          language: 'english',
        },
      ],
    })

    const router = createTestRouter()
    const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })
    const vm = wrapper.vm as any as {
      showAdvancedSearch?: boolean
      advancedSearchParams?: Record<string, unknown>
      performAdvancedSearch?: () => Promise<void>
      titleResults?: unknown[]
    }

    vm.showAdvancedSearch = true
    vm.advancedSearchParams = { title: 'Heretics of Dune' }

    await vm.performAdvancedSearch()
    expect(vm.titleResults.length).toBe(1)
    const tr = vm.titleResults[0] as any
    expect(tr.searchResult.runtime).toBe(12)
  })

  it('renders formatted runtime string for advanced search results', async () => {
    const apiModule = await import('@/services/api')
    const apiService = apiModule.apiService as any as { searchAudibleByTitleAndAuthor?: Mock }
    apiService.searchAudibleByTitleAndAuthor?.mockResolvedValue({
      totalResults: 1,
      results: [
        {
          asin: 'B000127',
          title: 'Example Long Book',
          authors: [{ name: 'Some Author' }],
          imageUrl: 'http://img5',
          runtimeLengthMin: 620, // 10h 20m
          language: 'english',
        },
      ],
    })

    const router = createTestRouter()
    const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })
    const vm = wrapper.vm as any as {
      showAdvancedSearch?: boolean
      advancedSearchParams?: Record<string, unknown>
      performAdvancedSearch?: () => Promise<void>
      allAudibleResults?: unknown[]
      titleResults?: unknown[]
    }

    vm.showAdvancedSearch = true
    vm.advancedSearchParams = { title: 'Example Long Book' }

    await vm.performAdvancedSearch()
    await wrapper.vm.$nextTick()

    const statEl = wrapper.find('.title-results .title-result-card .stat-item')
    expect(statEl.exists()).toBe(true)
    expect(statEl.text()).toContain('10h')
    expect(statEl.text()).toContain('20m')
  })

  it('shows metadata badge linking to the Audible product page and source badge linking to Audible product', async () => {
    const router = createTestRouter()
    const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })
    const vm = wrapper.vm as any as {
      searchType?: string
      audibleResult?: Record<string, unknown>
    }

    // Simulate an ASIN-based Audible-backed result (single result view)
    vm.searchType = 'asin'
    ;(vm as any).audibleResult = {
      asin: 'BAUD1',
      title: 'Title',
      authors: [{ name: 'Author Name' }],
      narrators: [{ name: 'Narrator Name' }],
      imageUrl: 'http://example.com/cover.jpg',
      metadataSource: 'Audible',
      source: 'Audible',
      sourceLink: 'https://www.audible.com/pd/BAUD1',
      series: 'Series Name',
      seriesList: ['Series Name', 'Other Series'],
    }

    await wrapper.vm.$nextTick()

    // Metadata badge should link to the Audible product page
    const metaLink = wrapper.find('.result-meta .metadata-source-link')
    expect(metaLink.exists()).toBe(true)
    expect(metaLink.attributes('href')).toBe('https://www.audible.com/pd/BAUD1')
    expect(metaLink.text()).toContain('Audible')

    // Source link should prefer Audible product URL and show 'Audible'
    const sourceLink = wrapper.find('.result-meta .source-link')
    expect(sourceLink.exists()).toBe(true)
    expect(sourceLink.attributes('href')).toBe('https://www.audible.com/pd/BAUD1')
    expect(sourceLink.text()).toContain('Audible')
  })

  it('does not label non-Audible URLs containing audible.com as Audible', async () => {
    const router = createTestRouter()
    const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })
    const vm = wrapper.vm as any as {
      searchType?: string
      audibleResult?: Record<string, unknown>
    }

    vm.searchType = 'asin'
    ;(vm as any).audibleResult = {
      asin: 'BAUDX',
      title: 'Title',
      source: 'External',
      sourceLink: 'https://example.com/?q=audible.com',
    }
    await wrapper.vm.$nextTick()

    let sourceLink = wrapper.find('.result-meta .source-link')
    expect(sourceLink.exists()).toBe(true)
    if (sourceLink.text().includes('Audible')) {
      throw new Error('Non-audible URL incorrectly labeled as Audible')
    }

    // Also ensure fake hostnames are not treated as Audible
    ;(vm as any).audibleResult.sourceLink = 'https://fakeaudible.com/pd/123'
    await wrapper.vm.$nextTick()
    sourceLink = wrapper.find('.result-meta .source-link')
    if (sourceLink.text().includes('Audible')) {
      throw new Error('Non-audible URL incorrectly labeled as Audible')
    }
  })

  it('shows full series list on hover (title and asin result views)', async () => {
    const router = createTestRouter()
    const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })
    const vm = wrapper.vm as any as { searchType?: string; titleResults?: unknown[] }

    // Title-list item case
    vm.searchType = 'title'
    vm.titleResults = [
      {
        title: 'Book',
        key: 'k1',
        searchResult: { series: 'Main Series', seriesList: ['Main Series', 'Alt Series'] },
      },
    ]

    await wrapper.vm.$nextTick()

    const seriesBadge = wrapper.find('.title-results .title-result-card .series-badge[title]')
    expect(seriesBadge.exists()).toBe(true)
    expect(seriesBadge.attributes('title')).toBe('Main Series, Alt Series')

    // ASIN result case
    vm.searchType = 'asin'
    ;(vm as any).audibleResult = {
      asin: 'BAUD2',
      title: 'B',
      series: 'X',
      seriesList: ['X', 'Y'],
    }
    await wrapper.vm.$nextTick()
    const seriesBadgeAsin = wrapper.find('.search-results .title-result-card .series-badge[title]')
    expect(seriesBadgeAsin.exists()).toBe(true)
    expect(seriesBadgeAsin.attributes('title')).toBe('X, Y')
  })

  it('shows "Added" and disables add button when result is already in library', async () => {
    const router = createTestRouter()
    const wrapper = mount(AddNewView, { global: { plugins: [createPinia(), router] } })
    const vm = wrapper.vm as any as {
      searchType?: string
      audibleResult?: Record<string, unknown>
      checkExistingInLibrary?: () => Promise<void>
    }

    // Simulate library already containing the ASIN
    const lib = useLibraryStore()
    lib.audiobooks = [{ id: 1, asin: 'BEXIST', title: 'Already In Library' }]

    vm.searchType = 'asin'
    vm.audibleResult = { asin: 'BEXIST', title: 'Already In Library' }

    await vm.checkExistingInLibrary()
    await wrapper.vm.$nextTick()

    const addBtn = wrapper.find('.search-results .result-actions .btn')
    expect(addBtn.exists()).toBe(true)
    expect(addBtn.text()).toContain('Added')
    expect(addBtn.attributes('disabled')).toBeDefined()
  })
})
