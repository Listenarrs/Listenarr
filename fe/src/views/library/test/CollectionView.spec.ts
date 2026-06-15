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
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import CollectionView from '@/views/library/CollectionView.vue'
import { useLibraryStore } from '@/stores/library'
import { flushAsync } from '@/test/utils/wait'

const {
  mockGetLibrary,
  mockGetAuthorCatalog,
  mockGetAuthorLookup,
  mockGetSeriesCatalog,
  mockGetSeriesLookup,
  mockGetApplicationSettings,
  mockGetAuthorMonitoringStatus,
  mockGetSeriesMonitoringStatus,
  mockMonitorAuthor,
  mockMonitorSeries,
  mockUnmonitorAuthor,
  mockUnmonitorSeries,
  mockToastSuccess,
  mockToastWarning,
  mockToastError,
} = vi.hoisted(() => ({
  mockGetLibrary: vi.fn(async () => []),
  mockGetAuthorCatalog: vi.fn(async () => null),
  mockGetAuthorLookup: vi.fn(async () => null),
  mockGetSeriesCatalog: vi.fn(async () => null),
  mockGetSeriesLookup: vi.fn(async () => null),
  mockGetApplicationSettings: vi.fn(async () => ({})),
  mockGetAuthorMonitoringStatus: vi.fn(async () => ({
    isMonitored: false,
    monitoredAuthor: null,
  })),
  mockGetSeriesMonitoringStatus: vi.fn(async () => ({
    isMonitored: false,
    monitoredSeries: null,
  })),
  mockMonitorAuthor: vi.fn(async () => ({
    message: 'Author monitoring enabled',
    monitoredAuthor: {
      id: 1,
      authorName: 'Test Author',
      region: 'us',
      language: 'english',
      createdAt: '2026-03-18T00:00:00Z',
      updatedAt: '2026-03-18T00:00:00Z',
    },
    addedCount: 0,
    existingCount: 0,
    failedCount: 0,
  })),
  mockMonitorSeries: vi.fn(async () => ({
    message: 'Series monitoring enabled',
    monitoredSeries: {
      id: 1,
      seriesName: 'Test Series',
      region: 'us',
      language: 'english',
      createdAt: '2026-03-18T00:00:00Z',
      updatedAt: '2026-03-18T00:00:00Z',
    },
    addedCount: 0,
    existingCount: 0,
    failedCount: 0,
  })),
  mockUnmonitorAuthor: vi.fn(async () => ({ message: 'Author monitoring disabled' })),
  mockUnmonitorSeries: vi.fn(async () => ({ message: 'Series monitoring disabled' })),
  mockToastSuccess: vi.fn(),
  mockToastWarning: vi.fn(),
  mockToastError: vi.fn(),
}))

vi.mock('@/services/api', () => ({
  apiService: {
    getImageUrl: vi.fn((url: string) => url || 'https://via.placeholder.com/300x450?text=No+Image'),
    getBootstrapConfig: vi.fn(async () => ({})),
    getLibrary: mockGetLibrary,
    getStartupConfig: vi.fn(async () => ({})),
    getApplicationSettings: mockGetApplicationSettings,
    getAuthorCatalog: mockGetAuthorCatalog,
    getAuthorLookup: mockGetAuthorLookup,
    getSeriesCatalog: mockGetSeriesCatalog,
    getSeriesLookup: mockGetSeriesLookup,
    getAuthorMonitoringStatus: mockGetAuthorMonitoringStatus,
    getSeriesMonitoringStatus: mockGetSeriesMonitoringStatus,
    monitorAuthor: mockMonitorAuthor,
    monitorSeries: mockMonitorSeries,
    unmonitorAuthor: mockUnmonitorAuthor,
    unmonitorSeries: mockUnmonitorSeries,
  },
}))

vi.mock('@/services/toastService', () => ({
  useToast: () => ({
    success: mockToastSuccess,
    warning: mockToastWarning,
    error: mockToastError,
    info: vi.fn(),
  }),
}))

describe('CollectionView', () => {
  beforeEach(() => {
    const pinia = createPinia()
    setActivePinia(pinia)
    mockGetLibrary.mockReset()
    mockGetLibrary.mockResolvedValue([])
    mockGetAuthorCatalog.mockReset()
    mockGetAuthorCatalog.mockResolvedValue(null)
    mockGetAuthorLookup.mockReset()
    mockGetAuthorLookup.mockResolvedValue(null)
    mockGetSeriesCatalog.mockReset()
    mockGetSeriesCatalog.mockResolvedValue(null)
    mockGetSeriesLookup.mockReset()
    mockGetSeriesLookup.mockResolvedValue(null)
    mockGetApplicationSettings.mockReset()
    mockGetApplicationSettings.mockResolvedValue({})
    mockGetAuthorMonitoringStatus.mockReset()
    mockGetAuthorMonitoringStatus.mockResolvedValue({
      isMonitored: false,
      monitoredAuthor: null,
    })
    mockGetSeriesMonitoringStatus.mockReset()
    mockGetSeriesMonitoringStatus.mockResolvedValue({
      isMonitored: false,
      monitoredSeries: null,
    })
    mockMonitorAuthor.mockReset()
    mockMonitorAuthor.mockResolvedValue({
      message: 'Author monitoring enabled',
      monitoredAuthor: {
        id: 1,
        authorName: 'Test Author',
        region: 'us',
        language: 'english',
        createdAt: '2026-03-18T00:00:00Z',
        updatedAt: '2026-03-18T00:00:00Z',
      },
      addedCount: 0,
      existingCount: 0,
      failedCount: 0,
    } as any)
    mockMonitorSeries.mockReset()
    mockMonitorSeries.mockResolvedValue({
      message: 'Series monitoring enabled',
      monitoredSeries: {
        id: 1,
        seriesName: 'Test Series',
        region: 'us',
        language: 'english',
        createdAt: '2026-03-18T00:00:00Z',
        updatedAt: '2026-03-18T00:00:00Z',
      },
      addedCount: 0,
      existingCount: 0,
      failedCount: 0,
    } as any)
    mockUnmonitorAuthor.mockReset()
    mockUnmonitorAuthor.mockResolvedValue({ message: 'Author monitoring disabled' })
    mockUnmonitorSeries.mockReset()
    mockUnmonitorSeries.mockResolvedValue({ message: 'Series monitoring disabled' })
    mockToastSuccess.mockReset()
    mockToastWarning.mockReset()
    mockToastError.mockReset()
  })

  it('shows collection content details', async () => {
    if (typeof (globalThis as any as { ResizeObserver?: unknown }).ResizeObserver === 'undefined') {
      ;(globalThis as any as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    if (typeof (globalThis as any as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as any as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }

    const pinia = createPinia()
    setActivePinia(pinia)

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/collection/:type/:name', name: 'collection', component: CollectionView },
      ],
    })
    await router.push('/collection/series/Series%201')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 1,
        title: 'Book A',
        authors: ['Author A'],
        series: 'Series 1',
        imageUrl: 'c1.jpg',
        files: [],
      },
      {
        id: 2,
        title: 'Book B',
        authors: ['Author B'],
        series: 'Series 1',
        imageUrl: 'c2.jpg',
        files: [],
      },
    ] as any as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(CollectionView, {
      global: {
        plugins: [pinia, router],
        stubs: ['EditAudiobookModal', 'CustomSelect', 'AddLibraryModal'],
      },
    })
    await flushAsync()

    // ensure grid view
    expect(wrapper.vm.viewMode).toBe('grid')

    // collection cards should show title and author in collection-content
    const collectionCards = wrapper.findAll('.collection-card')
    expect(collectionCards.length).toBe(2)

    // Check first card has title and author
    const firstCard = collectionCards[0]
    expect(firstCard.find('.collection-title').text()).toBe('Book A')
    expect(firstCard.find('.collection-author').text()).toBe('Author A')
  })

  it('shows other audiobooks in a genre collection', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/collection/:type/:name', name: 'collection', component: CollectionView },
      ],
    })
    await router.push('/collection/genre/Fantasy')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 1,
        title: 'The Final Empire',
        authors: ['Brandon Sanderson'],
        genres: ['Fantasy', 'Epic Fantasy'],
        imageUrl: 'fantasy-1.jpg',
        files: [],
      },
      {
        id: 2,
        title: 'Project Hail Mary',
        authors: ['Andy Weir'],
        genres: ['Science Fiction'],
        imageUrl: 'scifi-1.jpg',
        files: [],
      },
      {
        id: 3,
        title: 'The Way of Kings',
        authors: ['Brandon Sanderson'],
        genres: ['Fantasy'],
        imageUrl: 'fantasy-2.jpg',
        files: [],
      },
    ] as any as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(CollectionView, {
      global: {
        plugins: [pinia, router],
        stubs: ['EditAudiobookModal', 'CustomSelect', 'AddLibraryModal'],
      },
    })
    await flushAsync()

    const collectionCards = wrapper.findAll('.collection-card')
    expect(collectionCards).toHaveLength(2)
    expect(wrapper.text()).toContain('The Final Empire')
    expect(wrapper.text()).toContain('The Way of Kings')
    expect(wrapper.text()).not.toContain('Project Hail Mary')
  })

  it('shows other audiobooks in a narrator collection', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/collection/:type/:name', name: 'collection', component: CollectionView },
      ],
    })
    await router.push('/collection/narrator/Scott%20Brick')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 1,
        title: 'Dune',
        authors: ['Frank Herbert'],
        narrators: ['Scott Brick'],
        imageUrl: 'dune.jpg',
        files: [],
      },
      {
        id: 2,
        title: 'Hyperion',
        authors: ['Dan Simmons'],
        narrators: ['George Guidall'],
        imageUrl: 'hyperion.jpg',
        files: [],
      },
      {
        id: 3,
        title: 'Children of Dune',
        authors: ['Frank Herbert'],
        narrators: ['Scott Brick', 'Cast'],
        imageUrl: 'children.jpg',
        files: [],
      },
    ] as any as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(CollectionView, {
      global: {
        plugins: [pinia, router],
        stubs: ['EditAudiobookModal', 'CustomSelect', 'AddLibraryModal'],
      },
    })
    await flushAsync()

    const collectionCards = wrapper.findAll('.collection-card')
    expect(collectionCards).toHaveLength(2)
    expect(wrapper.text()).toContain('Dune')
    expect(wrapper.text()).toContain('Children of Dune')
    expect(wrapper.text()).not.toContain('Hyperion')
  })

  it('shows other audiobooks in a publisher collection', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/collection/:type/:name', name: 'collection', component: CollectionView },
      ],
    })
    await router.push('/collection/publisher/Penguin')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 1,
        title: 'Book One',
        authors: ['Author A'],
        publisher: 'Penguin',
        imageUrl: 'book-1.jpg',
        files: [],
      },
      {
        id: 2,
        title: 'Book Two',
        authors: ['Author B'],
        publisher: 'HarperAudio',
        imageUrl: 'book-2.jpg',
        files: [],
      },
      {
        id: 3,
        title: 'Book Three',
        authors: ['Author C'],
        publisher: 'Penguin',
        imageUrl: 'book-3.jpg',
        files: [],
      },
    ] as any as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(CollectionView, {
      global: {
        plugins: [pinia, router],
        stubs: ['EditAudiobookModal', 'CustomSelect', 'AddLibraryModal'],
      },
    })
    await flushAsync()

    const collectionCards = wrapper.findAll('.collection-card')
    expect(collectionCards).toHaveLength(2)
    expect(wrapper.text()).toContain('Book One')
    expect(wrapper.text()).toContain('Book Three')
    expect(wrapper.text()).not.toContain('Book Two')
  })

  it('shows toolbar on author collection detail page', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/collection/:type/:name', name: 'collection', component: CollectionView },
      ],
    })

    await router.push('/collection/author/Author%201')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      { id: 1, title: 'Book A', authors: ['Author A'], imageUrl: 'a.jpg', files: [] },
    ] as any as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(CollectionView, {
      global: {
        plugins: [pinia, router],
        stubs: ['EditAudiobookModal', 'CustomSelect', 'AddLibraryModal'],
      },
    })

    await flushAsync()

    const toolbar = wrapper.find('.toolbar')
    expect(toolbar.exists()).toBe(true)

    // Back button should be present and search should be removed
    expect(toolbar.find('.toolbar-btn').exists()).toBe(true)
    expect(toolbar.find('.toolbar-search').exists()).toBe(false)
  })

  it('renders an audiobook-detail-style hero for author collections', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    mockGetApplicationSettings.mockResolvedValue({
      defaultSearchRegion: 'us',
      defaultSearchLanguage: 'english',
    })
    mockGetAuthorLookup.mockResolvedValue({
      asin: 'AUTHOR123',
      name: 'Andy Weir',
      image: 'andy-weir.jpg',
      cachedPath: '/config/cache/images/temp/andy-weir.jpg',
      description:
        'Andy Weir built a two-decade career as a software engineer before becoming a full-time novelist.',
      similarAuthors: [
        { asin: 'B001H6U8X0', name: 'Blake Crouch' },
        { asin: 'B004XRR8Z6', name: 'Ernest Cline' },
      ],
    })
    mockGetAuthorCatalog.mockResolvedValue({
      author: {
        asin: 'AUTHOR123',
        name: 'Andy Weir',
        image: 'andy-weir.jpg',
      },
      totalBooks: 3,
      books: [
        {
          asin: 'B000LOCAL01',
          title: 'Project Hail Mary',
          authors: ['Andy Weir'],
          imageUrl: 'local.jpg',
          language: 'english',
          metadataSource: 'Audible',
        },
        {
          asin: 'B000REMOTE1',
          title: 'The Martian',
          authors: ['Andy Weir'],
          imageUrl: 'remote.jpg',
          language: 'english',
          metadataSource: 'Audible',
        },
      ],
    })

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/collection/:type/:name', name: 'collection', component: CollectionView },
      ],
    })

    await router.push('/collection/author/Andy%20Weir')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    const localLibrary = [
      {
        id: 1,
        title: 'Project Hail Mary',
        authors: ['Andy Weir'],
        asin: 'B000LOCAL01',
        imageUrl: 'local.jpg',
        files: [],
      },
    ] as any as import('@/types').Audiobook[]
    store.audiobooks = localLibrary
    mockGetLibrary.mockResolvedValue(localLibrary)
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(CollectionView, {
      global: {
        plugins: [pinia, router],
        stubs: ['EditAudiobookModal', 'CustomSelect', 'AddLibraryModal'],
      },
    })

    await flushPromises()

    const hero = wrapper.find('.author-hero-section')
    expect(hero.exists()).toBe(true)
    expect(hero.text()).toContain('Andy Weir')
    expect(hero.text()).toContain('United States (US) / English')
    expect(hero.text()).toContain('Andy Weir built a two-decade career as a software engineer')
    expect(hero.text()).toContain('Related Authors')
    expect(hero.text()).toContain('Blake Crouch')
    expect(wrapper.find('.author-hero-poster').attributes('src')).toContain('andy-weir.jpg')
    expect(wrapper.find('.top-nav').exists()).toBe(false)
    expect(mockGetAuthorLookup).toHaveBeenCalledWith('Andy Weir', 'us', 'AUTHOR123', false)
  })

  it('renders a detail-style hero and filtered not-added books for series collections', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    mockGetApplicationSettings.mockResolvedValue({
      defaultSearchRegion: 'us',
      defaultSearchLanguage: 'english',
    })
    mockGetSeriesLookup.mockResolvedValue({
      asin: 'SERIES123',
      name: 'Mistborn',
      image: 'mistborn.jpg',
      cachedPath: '/config/cache/images/series/SERIES123.jpg',
      description: 'The Mistborn saga spans empires, rebellions, and evolving eras.',
      totalBooks: 3,
    })
    mockGetSeriesCatalog.mockResolvedValue({
      series: {
        asin: 'SERIES123',
        name: 'Mistborn',
        image: 'mistborn.jpg',
        description: 'The Mistborn saga spans empires, rebellions, and evolving eras.',
      },
      totalBooks: 3,
      books: [
        {
          asin: 'BOOK1',
          title: 'The Final Empire',
          authors: ['Brandon Sanderson'],
          imageUrl: 'book1.jpg',
          language: 'english',
          metadataSource: 'Audible',
          series: 'Mistborn',
          seriesNumber: '1',
        },
        {
          asin: 'BOOK2',
          title: 'The Well of Ascension',
          authors: ['Brandon Sanderson'],
          imageUrl: 'book2.jpg',
          language: 'english',
          metadataSource: 'Audible',
          series: 'Mistborn',
          seriesNumber: '2',
        },
        {
          asin: 'BOOK3',
          title: 'Hero of Ages',
          authors: ['Brandon Sanderson'],
          imageUrl: 'book3.jpg',
          language: 'german',
          metadataSource: 'Audible',
          series: 'Mistborn',
          seriesNumber: '3',
        },
      ],
    })

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/collection/:type/:name', name: 'collection', component: CollectionView },
      ],
    })

    await router.push('/collection/series/Mistborn')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    const localLibrary = [
      {
        id: 1,
        title: 'The Final Empire',
        authors: ['Brandon Sanderson'],
        series: 'Mistborn',
        asin: 'BOOK1',
        imageUrl: 'book1.jpg',
        files: [],
      },
    ] as any as import('@/types').Audiobook[]
    store.audiobooks = localLibrary
    mockGetLibrary.mockResolvedValue(localLibrary)
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(CollectionView, {
      global: {
        plugins: [pinia, router],
        stubs: ['EditAudiobookModal', 'CustomSelect', 'AddLibraryModal'],
      },
    })

    await flushPromises()

    expect(wrapper.find('.series-hero-section').exists()).toBe(true)
    expect(wrapper.text()).toContain('Mistborn')
    expect(wrapper.text()).toContain('The Mistborn saga spans empires')
    expect(wrapper.text()).toContain('In Library')
    expect(wrapper.text()).toContain('Not Added')
    expect(wrapper.text()).toContain('The Final Empire')
    expect(wrapper.text()).toContain('The Well of Ascension')
    expect(wrapper.text()).not.toContain('Hero of Ages')
    expect(wrapper.find('.series-hero-poster-card').exists()).toBe(true)
    expect(wrapper.findAll('.series-hero-cover-item')).toHaveLength(2)
    expect(wrapper.findAll('.series-hero-cover-item.is-not-added')).toHaveLength(1)
    expect(wrapper.find('.top-nav').exists()).toBe(false)
    expect(mockGetSeriesLookup).toHaveBeenCalledWith('Mistborn', 'us', 'SERIES123', false)
  })

  it('sorts a series collection by position within owned, then within not-added (#626)', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    mockGetApplicationSettings.mockResolvedValue({
      defaultSearchRegion: 'us',
      defaultSearchLanguage: 'english',
    })
    mockGetSeriesLookup.mockResolvedValue({
      asin: 'SERIES123',
      name: 'Mistborn',
      totalBooks: 4,
    })
    mockGetSeriesCatalog.mockResolvedValue({
      series: { asin: 'SERIES123', name: 'Mistborn' },
      totalBooks: 4,
      books: [
        {
          asin: 'BOOK1',
          title: 'The Final Empire',
          authors: ['Brandon Sanderson'],
          language: 'english',
          metadataSource: 'Audible',
          series: 'Mistborn',
          seriesNumber: '1',
        },
        {
          asin: 'BOOK2',
          title: 'The Well of Ascension',
          authors: ['Brandon Sanderson'],
          language: 'english',
          metadataSource: 'Audible',
          series: 'Mistborn',
          seriesNumber: '2',
        },
        {
          asin: 'BOOK3',
          title: 'Hero of Ages',
          authors: ['Brandon Sanderson'],
          language: 'english',
          metadataSource: 'Audible',
          series: 'Mistborn',
          seriesNumber: '3',
        },
        {
          asin: 'BOOK4',
          title: 'The Alloy of Law',
          authors: ['Brandon Sanderson'],
          language: 'english',
          metadataSource: 'Audible',
          series: 'Mistborn',
          seriesNumber: '4',
        },
      ],
    })

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/collection/:type/:name', name: 'collection', component: CollectionView },
      ],
    })
    await router.push('/collection/series/Mistborn')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    // Owns #1 and #3; expected order is owned-by-position (#1, #3) then not-added (#2, #4).
    const localLibrary = [
      {
        id: 3,
        title: 'Hero of Ages',
        authors: ['Brandon Sanderson'],
        series: 'Mistborn',
        seriesNumber: '3',
        asin: 'BOOK3',
        files: [],
      },
      {
        id: 1,
        title: 'The Final Empire',
        authors: ['Brandon Sanderson'],
        series: 'Mistborn',
        seriesNumber: '1',
        asin: 'BOOK1',
        files: [],
      },
    ] as unknown as import('@/types').Audiobook[]
    store.audiobooks = localLibrary
    mockGetLibrary.mockResolvedValue(localLibrary)
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(CollectionView, {
      global: {
        plugins: [pinia, router],
        stubs: ['EditAudiobookModal', 'CustomSelect', 'AddLibraryModal'],
      },
    })
    await flushPromises()

    const sorted = (
      wrapper.vm as unknown as {
        audiobooks: Array<{ title: string; seriesNumber?: string; inLibrary?: boolean }>
      }
    ).audiobooks

    expect(sorted.map((book) => book.title)).toEqual([
      'The Final Empire', // #1, owned
      'Hero of Ages', // #3, owned
      'The Well of Ascension', // #2, not added
      'The Alloy of Law', // #4, not added
    ])
    expect(sorted.map((book) => Boolean(book.inLibrary))).toEqual([true, true, false, false])
  })

  // Mounts a series collection of owned books (one group → pure position order) and returns
  // the rendered order of titles from the component's sorted `audiobooks` list.
  async function seriesPositionOrder(
    collection: string,
    books: Array<{ id: number; title: string; seriesNumber?: string }>,
  ): Promise<string[]> {
    const pinia = createPinia()
    setActivePinia(pinia)

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/collection/:type/:name', name: 'collection', component: CollectionView },
      ],
    })
    await router.push(`/collection/series/${encodeURIComponent(collection)}`)
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    const localLibrary = books.map((book) => ({
      ...book,
      authors: ['Brandon Sanderson'],
      series: collection,
      files: [],
    })) as unknown as import('@/types').Audiobook[]
    store.audiobooks = localLibrary
    mockGetLibrary.mockResolvedValue(localLibrary)
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(CollectionView, {
      global: {
        plugins: [pinia, router],
        stubs: ['EditAudiobookModal', 'CustomSelect', 'AddLibraryModal'],
      },
    })
    await flushPromises()

    return (wrapper.vm as unknown as { audiobooks: Array<{ title: string }> }).audiobooks.map(
      (book) => book.title,
    )
  }

  it('sorts books with no series position last (#660)', async () => {
    const order = await seriesPositionOrder('Mistborn', [
      { id: 1, title: 'Second', seriesNumber: '2' },
      { id: 2, title: 'First', seriesNumber: '1' },
      { id: 3, title: 'Unplaced' }, // no seriesNumber → must sort last, not first
    ])
    expect(order).toEqual(['First', 'Second', 'Unplaced'])
  })

  it('orders multi-digit series positions numerically, not lexically (#660)', async () => {
    const order = await seriesPositionOrder('Mistborn', [
      { id: 1, title: 'Tenth', seriesNumber: '10' },
      { id: 2, title: 'Second', seriesNumber: '2' },
    ])
    expect(order).toEqual(['Second', 'Tenth'])
  })

  it('does not treat a non-numeric position like "1-2" as #1 (#660)', async () => {
    const order = await seriesPositionOrder('Mistborn', [
      { id: 1, title: 'Second', seriesNumber: '2' },
      { id: 2, title: 'Combined', seriesNumber: '1-2' },
      { id: 3, title: 'First', seriesNumber: '1' },
    ])
    // "1-2" must not collapse onto #1's key; it sorts after the numeric positions.
    expect(order).toEqual(['First', 'Second', 'Combined'])
  })

  it('shows a loading state immediately when navigating to a similar author', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    mockGetApplicationSettings.mockResolvedValue({
      defaultSearchRegion: 'us',
      defaultSearchLanguage: 'english',
    })

    let resolveLookup: ((value: import('@/types').AuthorLookupResponse | null) => void) | undefined
    mockGetAuthorLookup.mockImplementation(
      () =>
        new Promise((resolve) => {
          resolveLookup = resolve
        }),
    )
    mockGetAuthorCatalog.mockResolvedValue({
      author: {
        asin: 'AUTHOR123',
        name: 'Andy Weir',
        image: 'andy-weir.jpg',
      },
      totalBooks: 1,
      books: [
        {
          asin: 'B000LOCAL01',
          title: 'Project Hail Mary',
          authors: ['Andy Weir'],
          imageUrl: 'local.jpg',
          language: 'english',
          metadataSource: 'Audible',
        },
      ],
    })

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/collection/:type/:name', name: 'collection', component: CollectionView },
      ],
    })

    await router.push('/collection/author/Andy%20Weir')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    const localLibrary = [
      {
        id: 1,
        title: 'Project Hail Mary',
        authors: ['Andy Weir'],
        asin: 'B000LOCAL01',
        imageUrl: 'local.jpg',
        files: [],
      },
    ] as any as import('@/types').Audiobook[]
    store.audiobooks = localLibrary
    mockGetLibrary.mockResolvedValue(localLibrary)
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(CollectionView, {
      global: {
        plugins: [pinia, router],
        stubs: ['EditAudiobookModal', 'CustomSelect', 'AddLibraryModal'],
      },
    })

    await flushPromises()

    resolveLookup?.({
      asin: 'AUTHOR123',
      name: 'Andy Weir',
      image: 'andy-weir.jpg',
      cachedPath: '/config/cache/images/temp/andy-weir.jpg',
      description: 'Author bio',
      similarAuthors: [{ asin: 'SIM123', name: 'Blake Crouch' }],
    })
    await flushPromises()

    mockGetAuthorCatalog.mockResolvedValue({
      author: {
        asin: 'SIM123',
        name: 'Blake Crouch',
        image: 'blake-crouch.jpg',
      },
      totalBooks: 1,
      books: [],
    })

    let resolveNextLookup:
      | ((value: import('@/types').AuthorLookupResponse | null) => void)
      | undefined
    mockGetAuthorLookup.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveNextLookup = resolve
        }),
    )

    const similarAuthorButton = wrapper.find('.author-similar-chip')
    expect(similarAuthorButton.exists()).toBe(true)

    await similarAuthorButton.trigger('click')
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.loading-state').exists()).toBe(true)

    resolveNextLookup?.({
      asin: 'SIM123',
      name: 'Blake Crouch',
      image: 'blake-crouch.jpg',
      cachedPath: '/config/cache/images/temp/blake-crouch.jpg',
      description: 'Next author bio',
      similarAuthors: [],
    })
  })

  it('shows remote author books as dimmed addable cards without duplicating library matches', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    mockGetAuthorCatalog.mockResolvedValue({
      author: { asin: 'AUTHOR123', name: 'SenLinYu' },
      totalBooks: 2,
      books: [
        {
          asin: 'B000LOCAL01',
          title: 'Manacled',
          authors: ['SenLinYu'],
          imageUrl: 'local.jpg',
          language: 'english',
          metadataSource: 'Audible',
        },
        {
          asin: 'B000REMOTE1',
          title: 'Let the Dark In',
          authors: ['SenLinYu'],
          imageUrl: 'remote.jpg',
          language: 'english',
          metadataSource: 'Audible',
        },
      ],
    })

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/collection/:type/:name', name: 'collection', component: CollectionView },
      ],
    })

    await router.push('/collection/author/SenLinYu')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    const localLibrary = [
      {
        id: 1,
        title: 'Manacled',
        authors: ['SenLinYu'],
        asin: 'B000LOCAL01',
        imageUrl: 'local.jpg',
        files: [],
      },
    ] as any as import('@/types').Audiobook[]
    store.audiobooks = localLibrary
    mockGetLibrary.mockResolvedValue(localLibrary)
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(CollectionView, {
      global: {
        plugins: [pinia, router],
        stubs: ['EditAudiobookModal', 'CustomSelect', 'AddLibraryModal'],
      },
    })

    await flushPromises()

    const collectionCards = wrapper.findAll('.collection-card')
    expect(collectionCards).toHaveLength(2)
    expect(wrapper.findAll('.collection-card.not-in-library')).toHaveLength(1)
    const gridSectionHeaders = wrapper.findAll('.collection-section-header')
    expect(gridSectionHeaders).toHaveLength(2)
    expect(gridSectionHeaders[0]?.text()).toContain('In Library')
    expect(gridSectionHeaders[1]?.text()).toContain('Not Added')
    expect(wrapper.findAll('.add-btn-small')).toHaveLength(1)
    expect(wrapper.text()).toContain('Manacled')
    expect(wrapper.text()).toContain('Let the Dark In')

    const toggleViewButton = wrapper.find('button[title="Toggle view"]')
    expect(toggleViewButton.exists()).toBe(true)
    await toggleViewButton.trigger('click')
    await flushPromises()

    const listSectionHeaders = wrapper.findAll('.list-section-header')
    expect(listSectionHeaders).toHaveLength(2)
    expect(listSectionHeaders[0]?.text()).toContain('In Library')
    expect(listSectionHeaders[1]?.text()).toContain('Not Added')
  })

  it('filters remote not-added author books by language while keeping library books visible', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    mockGetApplicationSettings.mockResolvedValue({
      defaultSearchLanguage: 'english',
    })
    mockGetAuthorCatalog.mockResolvedValue({
      author: { asin: 'AUTHOR123', name: 'SenLinYu' },
      totalBooks: 3,
      books: [
        {
          asin: 'B000LOCAL01',
          title: 'Manacled',
          authors: ['SenLinYu'],
          imageUrl: 'local.jpg',
          language: 'german',
          metadataSource: 'Audible',
        },
        {
          asin: 'B000REMOTE1',
          title: 'Let the Dark In',
          authors: ['SenLinYu'],
          imageUrl: 'remote-en.jpg',
          language: 'en-us',
          metadataSource: 'Audible',
        },
        {
          asin: 'B000REMOTE2',
          title: 'Manacled German Edition',
          authors: ['SenLinYu'],
          imageUrl: 'remote-de.jpg',
          language: 'de',
          metadataSource: 'Audible',
        },
        {
          asin: 'B000REMOTE3',
          title: 'Unknown Language Fallback',
          authors: ['SenLinYu'],
          imageUrl: 'remote-unknown.jpg',
          metadataSource: 'Audible Fallback',
        },
      ],
    })

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/collection/:type/:name', name: 'collection', component: CollectionView },
      ],
    })

    await router.push('/collection/author/SenLinYu')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    const localLibrary = [
      {
        id: 1,
        title: 'Manacled',
        authors: ['SenLinYu'],
        asin: 'B000LOCAL01',
        imageUrl: 'local.jpg',
        files: [],
      },
    ] as any as import('@/types').Audiobook[]
    store.audiobooks = localLibrary
    mockGetLibrary.mockResolvedValue(localLibrary)
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(CollectionView, {
      global: {
        plugins: [pinia, router],
        stubs: ['EditAudiobookModal', 'CustomSelect', 'AddLibraryModal'],
      },
    })

    await flushPromises()

    const collectionCards = wrapper.findAll('.collection-card')
    expect(collectionCards).toHaveLength(2)
    expect(wrapper.findAll('.collection-card.not-in-library')).toHaveLength(1)
    expect(wrapper.text()).toContain('Manacled')
    expect(wrapper.text()).toContain('Let the Dark In')
    expect(wrapper.text()).not.toContain('Unknown Language Fallback')
    expect(wrapper.text()).not.toContain('Manacled German Edition')
  })

  it('deduplicates repeated author catalog matches that point to the same library book', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    mockGetAuthorCatalog.mockResolvedValue({
      author: { asin: 'AUTHOR123', name: 'Andy Weir' },
      totalBooks: 3,
      books: [
        {
          asin: 'B000LOCAL01',
          title: 'Project Hail Mary',
          authors: ['Andy Weir'],
          imageUrl: 'local-1.jpg',
          language: 'english',
          metadataSource: 'Audible',
        },
        {
          asin: 'B000LOCAL02',
          title: 'Project Hail Mary',
          authors: ['Andy Weir'],
          imageUrl: 'local-2.jpg',
          language: 'english',
          metadataSource: 'Audible',
        },
        {
          asin: 'B000REMOTE1',
          title: 'The Martian',
          authors: ['Andy Weir'],
          imageUrl: 'remote.jpg',
          language: 'english',
          metadataSource: 'Audible',
        },
      ],
    })

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/collection/:type/:name', name: 'collection', component: CollectionView },
      ],
    })

    await router.push('/collection/author/Andy%20Weir')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    const localLibrary = [
      {
        id: 1,
        title: 'Project Hail Mary',
        authors: ['Andy Weir'],
        asin: 'B000LOCAL01',
        imageUrl: 'local.jpg',
        files: [],
      },
    ] as any as import('@/types').Audiobook[]
    store.audiobooks = localLibrary
    mockGetLibrary.mockResolvedValue(localLibrary)
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(CollectionView, {
      global: {
        plugins: [pinia, router],
        stubs: ['EditAudiobookModal', 'CustomSelect', 'AddLibraryModal'],
      },
    })

    await flushPromises()

    const collectionCards = wrapper.findAll('.collection-card')
    expect(collectionCards).toHaveLength(2)
    expect(collectionCards.filter((card) => !card.classes('not-in-library'))).toHaveLength(1)
    expect(wrapper.findAll('.collection-card.not-in-library')).toHaveLength(1)
    expect(wrapper.text()).toContain('Project Hail Mary')
    expect(wrapper.text()).toContain('The Martian')
  })

  it('monitors an author using the configured region and language defaults', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    mockGetApplicationSettings.mockResolvedValue({
      defaultSearchRegion: 'uk',
      defaultSearchLanguage: 'german',
    })
    mockGetAuthorCatalog.mockResolvedValue({
      author: { asin: 'AUTHOR123', name: 'Andy Weir' },
      totalBooks: 1,
      books: [
        {
          asin: 'B000REMOTE1',
          title: 'Project Hail Mary',
          authors: ['Andy Weir'],
          imageUrl: 'remote.jpg',
          language: 'de',
          metadataSource: 'Audible',
        },
      ],
    })
    mockMonitorAuthor.mockResolvedValue({
      message: 'Author monitoring enabled',
      monitoredAuthor: {
        id: 7,
        authorName: 'Andy Weir',
        authorAsin: 'AUTHOR123',
        region: 'uk',
        language: 'german',
        createdAt: '2026-03-18T00:00:00Z',
        updatedAt: '2026-03-18T00:00:00Z',
      },
      addedCount: 1,
      existingCount: 0,
      failedCount: 0,
    } as any)

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/collection/:type/:name', name: 'collection', component: CollectionView },
      ],
    })

    await router.push('/collection/author/Andy%20Weir')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [] as any as import('@/types').Audiobook[]
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(CollectionView, {
      global: {
        plugins: [pinia, router],
        stubs: ['EditAudiobookModal', 'CustomSelect', 'AddLibraryModal'],
      },
    })

    await flushPromises()

    expect(mockGetAuthorCatalog).toHaveBeenCalledWith('Andy Weir', 'uk', false)
    expect(mockGetAuthorMonitoringStatus).toHaveBeenCalledWith('Andy Weir', 'uk', 'german')
    expect(wrapper.find('.author-hero-meta').text()).toContain('United Kingdom (UK) / German')

    const monitorButton = wrapper.find('.author-monitor-btn')
    expect(monitorButton.text()).toContain('Monitor Author')

    await monitorButton.trigger('click')
    await flushPromises()

    expect(mockMonitorAuthor).toHaveBeenCalledWith({
      name: 'Andy Weir',
      asin: 'AUTHOR123',
      region: 'uk',
      language: 'german',
    })
    expect(mockToastSuccess).toHaveBeenCalled()
  })

  it('monitors a series using the configured region and language defaults', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    mockGetApplicationSettings.mockResolvedValue({
      defaultSearchRegion: 'uk',
      defaultSearchLanguage: 'german',
    })
    mockGetSeriesCatalog.mockResolvedValue({
      series: { asin: 'SERIES123', name: 'Mistborn' },
      totalBooks: 1,
      books: [
        {
          asin: 'BOOK1',
          title: 'The Final Empire',
          authors: ['Brandon Sanderson'],
          imageUrl: 'remote.jpg',
          language: 'de',
          metadataSource: 'Audible',
          series: 'Mistborn',
          seriesNumber: '1',
        },
      ],
    })
    mockMonitorSeries.mockResolvedValue({
      message: 'Series monitoring enabled',
      monitoredSeries: {
        id: 8,
        seriesName: 'Mistborn',
        seriesAsin: 'SERIES123',
        region: 'uk',
        language: 'german',
        createdAt: '2026-03-18T00:00:00Z',
        updatedAt: '2026-03-18T00:00:00Z',
      },
      addedCount: 1,
      existingCount: 0,
      failedCount: 0,
    } as any)

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/collection/:type/:name', name: 'collection', component: CollectionView },
      ],
    })

    await router.push('/collection/series/Mistborn')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [] as any as import('@/types').Audiobook[]
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(CollectionView, {
      global: {
        plugins: [pinia, router],
        stubs: ['EditAudiobookModal', 'CustomSelect', 'AddLibraryModal'],
      },
    })

    await flushPromises()

    expect(mockGetSeriesCatalog).toHaveBeenCalledWith('Mistborn', 'uk', false)
    expect(mockGetSeriesMonitoringStatus).toHaveBeenCalledWith('Mistborn', 'uk', 'german')
    expect(wrapper.find('.author-hero-meta').text()).toContain('United Kingdom (UK) / German')

    const monitorButton = wrapper.find('.author-monitor-btn')
    expect(monitorButton.text()).toContain('Monitor Series')

    await monitorButton.trigger('click')
    await flushPromises()

    expect(mockMonitorSeries).toHaveBeenCalledWith({
      name: 'Mistborn',
      asin: 'SERIES123',
      region: 'uk',
      language: 'german',
    })
    expect(mockToastSuccess).toHaveBeenCalled()
  })

  it('refreshes author metadata on demand with forced author lookups and catalog reloads', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    mockGetApplicationSettings.mockResolvedValue({
      defaultSearchRegion: 'us',
      defaultSearchLanguage: 'english',
    })
    mockGetAuthorLookup.mockResolvedValue({
      asin: 'AUTHOR123',
      name: 'Andy Weir',
      image: 'andy-weir.jpg',
      cachedPath: '/config/cache/images/authors/AUTHOR123.jpg',
      description: 'Fresh biography',
      similarAuthors: [{ asin: 'SIM123', name: 'Blake Crouch' }],
    })
    mockGetAuthorCatalog.mockResolvedValue({
      author: {
        asin: 'AUTHOR123',
        name: 'Andy Weir',
        image: 'andy-weir.jpg',
      },
      totalBooks: 2,
      books: [
        {
          asin: 'B000LOCAL01',
          title: 'Project Hail Mary',
          authors: ['Andy Weir'],
          imageUrl: 'local.jpg',
          language: 'english',
          metadataSource: 'Audible',
        },
        {
          asin: 'B000REMOTE1',
          title: 'The Martian',
          authors: ['Andy Weir'],
          imageUrl: 'remote.jpg',
          language: 'english',
          metadataSource: 'Audible',
        },
      ],
    })

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/collection/:type/:name', name: 'collection', component: CollectionView },
      ],
    })

    await router.push('/collection/author/Andy%20Weir')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [] as any as import('@/types').Audiobook[]
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(CollectionView, {
      global: {
        plugins: [pinia, router],
        stubs: ['EditAudiobookModal', 'CustomSelect', 'AddLibraryModal'],
      },
    })

    await flushPromises()

    mockGetAuthorCatalog.mockClear()
    mockGetAuthorLookup.mockClear()

    const refreshButton = wrapper.find('.author-refresh-btn')
    expect(refreshButton.exists()).toBe(true)

    await refreshButton.trigger('click')
    await flushPromises()

    expect(mockGetAuthorCatalog).toHaveBeenCalledWith('Andy Weir', 'us', true)
    expect(mockGetAuthorLookup).toHaveBeenCalledWith('Andy Weir', 'us', 'AUTHOR123', true)
    expect(mockToastSuccess).toHaveBeenCalledWith(
      'Author metadata refreshed',
      'Updated the author image, description, related authors, and catalog.',
    )
  })

  it('renders the per-collection series position for a book matched via a non-primary membership', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/collection/:type/:name', name: 'collection', component: CollectionView },
      ],
    })
    await router.push('/collection/series/Mistborn')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 1,
        title: 'Shadows of Self',
        authors: ['Brandon Sanderson'],
        // Primary series (and number) point at a DIFFERENT series...
        series: 'Other Series',
        seriesNumber: '5',
        // ...but the book also belongs to Mistborn at position 2.
        seriesMemberships: [
          { seriesName: 'Other Series', seriesNumber: '5', isPrimary: true },
          { seriesName: 'Mistborn', seriesNumber: '2', isPrimary: false },
        ],
        imageUrl: 'c1.jpg',
        files: [],
      },
    ] as unknown as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(CollectionView, {
      global: {
        plugins: [pinia, router],
        stubs: ['EditAudiobookModal', 'CustomSelect', 'AddLibraryModal'],
      },
    })
    await flushPromises()

    const card = wrapper.find('.collection-card')
    expect(card.exists()).toBe(true)
    // Shows the Mistborn position (#2 from the membership), not the primary 'Other Series' #5.
    expect(card.text()).toContain('#2')
    expect(card.text()).not.toContain('#5')
  })
})
