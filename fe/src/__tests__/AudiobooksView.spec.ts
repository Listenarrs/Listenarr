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
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import AudiobooksView from '@/views/library/AudiobooksView.vue'
import { useLibraryStore } from '@/stores/library'
// apiService stubbed in vi.mock below if needed

const { mockGetAudiobookDeleteCapabilities, mockGetAuthorLookup } = vi.hoisted(() => ({
  mockGetAudiobookDeleteCapabilities: vi.fn(async () => ({
    canRemoveFromLibrary: true,
    canDeleteTrackedFiles: true,
    canDeleteFolder: true,
    reason: null,
    fallbackAction: 'RemoveFromLibraryOnly' as const,
  })),
  mockGetAuthorLookup: vi.fn(async () => null),
}))

vi.mock('@/services/api', () => ({
  apiService: {
    getQualityProfiles: vi.fn(async () => []),
    getImageUrl: vi.fn((url: string) => url || 'https://via.placeholder.com/300x450?text=No+Image'),
    getBootstrapConfig: vi.fn(async () => ({})),
    getStartupConfig: vi.fn(async () => ({})),
    getApplicationSettings: vi.fn(async () => ({})),
    getAudiobookDeleteCapabilities: mockGetAudiobookDeleteCapabilities,
    getAuthorLookup: mockGetAuthorLookup,
  },
}))

type AudiobooksVm = {
  setGroupBy?: (value: string) => Promise<void> | void
  groupedCollections?: Array<{ name: string; count: number; coverUrl?: string }>
  showItemDetails?: boolean
  groupBy?: string
  visibleRange?: { start: number; end: number }
  confirmDelete?: (audiobook: import('@/types').Audiobook) => Promise<void>
  deleteTarget?: import('@/types').Audiobook | null
  deleteCapabilities?: import('@/types').AudiobookDeleteCapabilities | null
  showDeleteDialog?: boolean
}

const getVm = (wrapper: ReturnType<typeof mount>) => wrapper.vm as unknown as AudiobooksVm

describe('AudiobooksView', () => {
  beforeEach(() => {
    const pinia = createPinia()
    setActivePinia(pinia)
    mockGetAudiobookDeleteCapabilities.mockReset()
    mockGetAudiobookDeleteCapabilities.mockResolvedValue({
      canRemoveFromLibrary: true,
      canDeleteTrackedFiles: true,
      canDeleteFolder: true,
      reason: null,
      fallbackAction: 'RemoveFromLibraryOnly',
    })
  })

  it('shows extra details in grid view when showItemDetails is enabled', async () => {
    // ensure ResizeObserver is defined for the mount in vtu
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    // Minimal WebSocket stub so SignalRService doesn't throw during tests
    if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }
    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 123,
        title: 'The Test Book',
        authors: ['Test Author'],
        narrators: ['Test Narrator'],
        publisher: 'Test Publisher',
        publishYear: 2020,
        imageUrl: 'https://example.com/cover.jpg',
        files: [],
      },
    ] as unknown as import('@/types').Audiobook[]

    // Persist 'showItemDetails' so component mounts with details on
    localStorage.setItem('listenarr.showItemDetails', 'true')
    // Prevent real fetchLibrary from running during mount (we set audiobooks directly)
    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    // Find the rendered extra details block under the poster in the grid
    const bottomDetails = wrapper.find('.grid-bottom-details')
    expect(bottomDetails.exists()).toBe(true)
    expect(wrapper.text()).toContain('The Test Book')
    expect(wrapper.text()).toContain('Test Author')
    expect(wrapper.text()).toContain('Test Narrator')
    expect(wrapper.text()).toContain('Test Publisher')
    expect(wrapper.text()).toContain('2020')
  })

  it('ignores stale delete capabilities when a newer audiobook becomes the target', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const books = [
      { id: 1, title: 'First', authors: ['Author'], files: [] },
      { id: 2, title: 'Second', authors: ['Author'], files: [] },
    ] as unknown as import('@/types').Audiobook[]
    const store = useLibraryStore()
    store.audiobooks = books
    store.fetchLibrary = vi.fn(async () => undefined)

    let resolveFirst!: (value: import('@/types').AudiobookDeleteCapabilities) => void
    let resolveSecond!: (value: import('@/types').AudiobookDeleteCapabilities) => void
    mockGetAudiobookDeleteCapabilities
      .mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            resolveFirst = resolve
          }),
      )
      .mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            resolveSecond = resolve
          }),
      )

    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((resolve) => setTimeout(resolve, 0))
    const vm = getVm(wrapper)

    const firstRequest = vm.confirmDelete?.(books[0]!)
    const secondRequest = vm.confirmDelete?.(books[1]!)
    resolveSecond({
      canRemoveFromLibrary: true,
      canDeleteTrackedFiles: false,
      canDeleteFolder: false,
      reason: 'Second target is protected.',
      fallbackAction: 'RemoveFromLibraryOnly',
    })
    await secondRequest
    resolveFirst({
      canRemoveFromLibrary: true,
      canDeleteTrackedFiles: true,
      canDeleteFolder: true,
      reason: 'Stale first target.',
      fallbackAction: 'RemoveFromLibraryOnly',
    })
    await firstRequest

    expect(vm.deleteTarget?.id).toBe(2)
    expect(vm.deleteCapabilities?.reason).toBe('Second target is protected.')
    expect(vm.showDeleteDialog).toBe(true)
  })
})

describe('AudiobooksView Grouping', () => {
  beforeEach(() => {
    const pinia = createPinia()
    setActivePinia(pinia)
  })

  it('groups audiobooks by author when groupBy is authors', async () => {
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }

    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 1,
        title: 'Book 1',
        authors: ['Author A'],
        series: 'Series 1',
        imageUrl: 'cover1.jpg',
        files: [],
      },
      {
        id: 2,
        title: 'Book 2',
        authors: ['Author A'],
        series: 'Series 1',
        imageUrl: 'cover2.jpg',
        files: [],
      },
      {
        id: 3,
        title: 'Book 3',
        authors: ['Author B'],
        series: 'Series 2',
        imageUrl: 'cover3.jpg',
        files: [],
      },
    ] as unknown as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    // Set groupBy to authors
    const vm = getVm(wrapper)
    await vm.setGroupBy?.('authors')
    await wrapper.vm.$nextTick()

    const groupedCollections = vm.groupedCollections ?? []
    expect(groupedCollections).toHaveLength(2)
    expect(groupedCollections.find((g) => g.name === 'Author A')).toEqual({
      name: 'Author A',
      count: 2,
      coverUrl: undefined,
    })
    expect(groupedCollections.find((g) => g.name === 'Author B')).toEqual({
      name: 'Author B',
      count: 1,
      coverUrl: undefined,
    })

    // Default sorting when grouped by authors should be author-last ascending
    expect((vm as unknown).sortKey).toBe('author-last')
    expect((vm as unknown).sortOrder).toBe('asc')
  })

  const mountGroupedView = async (
    initialViewMode: 'grid' | 'list' = 'grid',
    attachToBody = false,
  ) => {
    try {
      // Both are persisted by the component and survive between tests in this file. Left
      // alone, an earlier test's grouping makes the grouped GRID render on mount and do the
      // author-cover work before the mode under test is applied.
      localStorage.setItem('listenarr.viewMode', initialViewMode)
      localStorage.setItem('listenarr.groupBy', 'books')
    } catch {}

    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }

    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      { id: 1, title: 'Book 1', authors: ['Author A'], imageUrl: 'c1.jpg', files: [] },
      { id: 2, title: 'Book 2', authors: ['Author A'], imageUrl: 'c2.jpg', files: [] },
      { id: 3, title: 'Book 3', authors: ['Author B'], imageUrl: 'c3.jpg', files: [] },
    ] as unknown as import('@/types').Audiobook[]
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(AudiobooksView, {
      // observeAuthorCards reaches for the grouped nodes through `document`, so the component
      // has to actually be in the document for those tests to say anything.
      ...(attachToBody ? { attachTo: document.body } : {}),
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    // onMounted reads the stored mode behind an await, so pin it here too: otherwise the
    // grouped GRID can render first and do the author-cover work the list is meant to do,
    // and a test about the list would pass against a list that never asked for anything.
    ;(wrapper.vm as unknown as { viewMode: 'grid' | 'list' }).viewMode = initialViewMode
    await wrapper.vm.$nextTick()

    mockGetAuthorLookup.mockClear()
    await getVm(wrapper).setGroupBy?.('authors')
    await wrapper.vm.$nextTick()
    return wrapper
  }

  it('renders authors as list rows when the view mode is list', async () => {
    const wrapper = await mountGroupedView()

    ;(wrapper.vm as unknown as { viewMode: string }).viewMode = 'list'
    await wrapper.vm.$nextTick()

    const rows = wrapper.findAll('.collection-list-item')
    expect(rows).toHaveLength(2)
    expect(rows.map((row) => row.text())).toEqual(
      expect.arrayContaining([
        expect.stringContaining('Author A'),
        expect.stringContaining('Author B'),
      ]),
    )
    expect(wrapper.find('.collection-list-item').text()).toContain('book')

    // The grid must be gone, or the toggle has added a second layout rather than switching.
    expect(wrapper.findAll('.collection-card')).toHaveLength(0)

    wrapper.unmount()
  })

  it('renders authors as cards when the view mode is grid', async () => {
    // The control. Without it, a test asserting the list rows would also pass against a
    // component that ignored viewMode and always rendered the list.
    const wrapper = await mountGroupedView()

    ;(wrapper.vm as unknown as { viewMode: string }).viewMode = 'grid'
    await wrapper.vm.$nextTick()

    expect(wrapper.findAll('.collection-card')).toHaveLength(2)
    expect(wrapper.findAll('.collection-list-item')).toHaveLength(0)

    wrapper.unmount()
  })

  it('fetches author covers in the grouped list view, not only in the grid', async () => {
    // The grouped grid and the grouped list are v-if siblings, so the list is the only markup
    // in the DOM when the list is showing. observeAuthorCards has to reach it, or grouping by
    // author in list view shows a placeholder for every author and never asks for the real
    // cover. jsdom has no IntersectionObserver, so the function takes its direct fallback.
    const wrapper = await mountGroupedView('list', true)

    expect(wrapper.findAll('.collection-list-item').length).toBe(2)
    expect(mockGetAuthorLookup.mock.calls.map((call) => call[0]).sort()).toEqual([
      'Author A',
      'Author B',
    ])

    wrapper.unmount()
    try {
      localStorage.removeItem('listenarr.viewMode')
    } catch {}
  })

  it('re-observes the grouped cards after the view mode switches back to grid', async () => {
    // Switching layout destroys the observed nodes and mounts fresh ones. groupedCollections
    // has not changed, so its watcher stays quiet and nothing else re-observes them.
    const observed: HTMLElement[] = []
    const previous = (globalThis as unknown as { IntersectionObserver?: unknown })
      .IntersectionObserver
    ;(globalThis as unknown as Record<string, unknown>).IntersectionObserver = class {
      observe(element: HTMLElement) {
        observed.push(element)
      }
      unobserve() {}
      disconnect() {}
    }

    try {
      const wrapper = await mountGroupedView('grid', true)
      const vm = wrapper.vm as unknown as { viewMode: string }

      vm.viewMode = 'list'
      await wrapper.vm.$nextTick()
      await wrapper.vm.$nextTick()

      observed.length = 0

      vm.viewMode = 'grid'
      await wrapper.vm.$nextTick()
      await wrapper.vm.$nextTick()

      // Scope to this wrapper: an attached mount left behind by a failing earlier test would
      // otherwise show up here and turn a cascade into a second, misleading failure.
      const mine = observed.filter((element) => wrapper.element.contains(element))
      expect(mine.map((element) => element.dataset.authorName).sort()).toEqual([
        'Author A',
        'Author B',
      ])
      expect(
        mine.every((element) => element.classList.contains('audiobook-poster-container')),
      ).toBe(true)

      wrapper.unmount()
    } finally {
      if (previous === undefined) {
        delete (globalThis as unknown as Record<string, unknown>).IntersectionObserver
      } else {
        ;(globalThis as unknown as Record<string, unknown>).IntersectionObserver = previous
      }
      try {
        localStorage.removeItem('listenarr.viewMode')
      } catch {}
    }
  })

  it('remembers a view mode chosen while the library is already grouped', async () => {
    // The watcher that persists viewMode was registered only by the virtual scroller, which
    // bails out when there is no scroll container. A library that loads already grouped never
    // has one, so the toggle worked for the session and was forgotten on the next load.
    // This mounts straight into the grouped state rather than switching into it, because
    // passing through Books registers the watcher and hides the problem.
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }

    localStorage.setItem('listenarr.groupBy', 'authors')
    localStorage.setItem('listenarr.viewMode', 'grid')

    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      { id: 1, title: 'Book 1', authors: ['Author A'], imageUrl: 'c1.jpg', files: [] },
    ] as unknown as import('@/types').Audiobook[]
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    expect(getVm(wrapper).groupBy).toBe('authors')
    ;(wrapper.vm as unknown as { toggleViewMode: () => void }).toggleViewMode()
    await wrapper.vm.$nextTick()

    expect(localStorage.getItem('listenarr.viewMode')).toBe('list')

    wrapper.unmount()
    try {
      localStorage.removeItem('listenarr.viewMode')
      localStorage.removeItem('listenarr.groupBy')
    } catch {}
  })

  it('groups audiobooks by series when groupBy is series', async () => {
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }

    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 1,
        title: 'Book 1',
        authors: ['Author A'],
        series: 'Series 1',
        imageUrl: 'cover1.jpg',
        files: [],
      },
      {
        id: 2,
        title: 'Book 2',
        authors: ['Author A'],
        series: 'Series 1',
        imageUrl: 'cover2.jpg',
        files: [],
      },
      {
        id: 3,
        title: 'Book 3',
        authors: ['Author B'],
        series: 'Series 2',
        imageUrl: 'cover3.jpg',
        files: [],
      },
    ] as unknown as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    // Set groupBy to series
    const vm = getVm(wrapper)
    await vm.setGroupBy?.('series')
    await wrapper.vm.$nextTick()

    const groupedCollections = vm.groupedCollections ?? []
    expect(groupedCollections).toHaveLength(2)
    expect(groupedCollections.find((g) => g.name === 'Series 1')).toEqual({
      name: 'Series 1',
      count: 2,
      coverUrls: ['cover1.jpg', 'cover2.jpg'],
    })
    expect(groupedCollections.find((g) => g.name === 'Series 2')).toEqual({
      name: 'Series 2',
      count: 1,
      coverUrls: ['cover3.jpg'],
    })
  })

  it('updates toolbar sort options and sorts grouped collections by count/name depending on grouping', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      { id: 1, title: 'A1', authors: ['Author A'], series: 'Series X', imageUrl: 'c1', files: [] },
      { id: 2, title: 'A2', authors: ['Author A'], series: 'Series X', imageUrl: 'c2', files: [] },
      { id: 3, title: 'B1', authors: ['Author B'], series: 'Series Y', imageUrl: 'c3', files: [] },
    ] as unknown as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    const vm = wrapper.vm as unknown as unknown

    // Switch to authors grouping and verify sortOptions exposed for collections
    await vm.setGroupBy('authors')
    await wrapper.vm.$nextTick()

    const optValues = (vm.sortOptions || []).map((o: unknown) => o.value)
    expect(optValues).toContain('author-last')
    expect(optValues).toContain('author-first')
    expect(optValues).toContain('count')

    // Default sorting when grouped by authors should be author-last ascending
    expect((vm as unknown).sortKey).toBe('author-last')
    expect((vm as unknown).sortOrder).toBe('asc')

    // CustomSelect should not be marked "active" for the default author sort
    const csStub = wrapper.find('custom-select-stub')
    expect(csStub.exists()).toBe(true)
    expect(csStub.attributes('active')).toBe('false')

    // Sort collections by count descending (non-default) — control should become active
    vm.sortKey = 'count'
    vm.sortOrder = 'desc'
    await wrapper.vm.$nextTick()
    expect(wrapper.find('custom-select-stub').attributes('active')).toBe('true')
    expect(vm.groupedCollections[0].name).toBe('Author A')

    // Sort collections by author-last ascending (back to default) — control should be inactive
    vm.sortKey = 'author-last'
    vm.sortOrder = 'asc'
    await wrapper.vm.$nextTick()
    expect(wrapper.find('custom-select-stub').attributes('active')).toBe('false')
    expect(vm.groupedCollections[0].name).toBe('Author A')

    // Switch to series grouping and verify options
    await vm.setGroupBy('series')
    await wrapper.vm.$nextTick()
    const seriesOpt = (vm.sortOptions || []).map((o: unknown) => o.value)
    expect(seriesOpt).toContain('title')
    expect(seriesOpt).toContain('count')
    expect(seriesOpt).not.toContain('author-last')

    // Series default should be `title` ascending and the control should NOT be active
    expect((vm as unknown).sortKey).toBe('title')
    expect((vm as unknown).sortOrder).toBe('asc')
    expect(wrapper.find('custom-select-stub').attributes('active')).toBe('false')

    // Sort series by count ascending (non-default)
    vm.sortKey = 'count'
    vm.sortOrder = 'asc'
    await wrapper.vm.$nextTick()
    expect(wrapper.find('custom-select-stub').attributes('active')).toBe('true')
    expect(vm.groupedCollections[0].name).toBe('Series Y')
  })

  it('shows individual books when groupBy is books', async () => {
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }

    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 1,
        title: 'Book 1',
        authors: ['Author A'],
        series: 'Series 1',
        imageUrl: 'cover1.jpg',
        files: [],
      },
    ] as unknown as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    // Ensure groupBy is 'books'
    localStorage.setItem('listenarr.groupBy', 'books')
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    // groupBy defaults to 'books'
    const vm = getVm(wrapper)
    const groupedCollections = vm.groupedCollections ?? []
    expect(groupedCollections).toHaveLength(0)
  })

  it("'Clear Filters' button resets search, custom filter and builtin filters", async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    // single audiobook that would be shown when no filters/search applied
    store.audiobooks = [
      { id: 1, title: 'Visible Book', authors: ['Author A'], imageUrl: 'c1', files: [] },
    ] as unknown as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })

    const vm = wrapper.vm as unknown as unknown

    // Apply a search that yields no results and a custom filter selection
    vm.searchQuery = 'no-match-query'
    vm.selectedFilterId = 'custom-1'
    vm.filterMonitored = 'monitored'
    await wrapper.vm.$nextTick()

    // Should show the 'No audiobooks match your filters' empty state
    expect(wrapper.text()).toContain('No audiobooks match your filters')

    // Click the Clear Filters button and verify everything resets
    const clearBtn = wrapper.find('button.btn.btn-primary')
    expect(clearBtn.exists()).toBe(true)
    expect(clearBtn.text()).toContain('Clear Filters')

    await clearBtn.trigger('click')
    await wrapper.vm.$nextTick()

    expect(vm.searchQuery).toBe('')
    expect(vm.selectedFilterId).toBeNull()
    expect(vm.filterMonitored).toBe('all')

    // After clearing, the audiobook should be visible again
    expect(wrapper.text()).toContain('Visible Book')
  })

  it('route query group parameter overrides stored preference on initial load', async () => {
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }

    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    // Simulate previous preference saved as 'series'
    localStorage.setItem('listenarr.groupBy', 'series')
    // Navigate to audiobooks with explicit group=books in URL
    await router.push({ path: '/audiobooks', query: { group: 'books' } })
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 1,
        title: 'Book 1',
        authors: ['Author A'],
        series: 'Series 1',
        imageUrl: 'cover1.jpg',
        files: [],
      },
    ] as unknown as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    // Expect the component to use the route query 'books' despite stored 'series'
    expect((wrapper.vm as unknown as { groupBy: string }).groupBy).toBe('books')
  })

  it('resets the virtual range when returning to books grouping', async () => {
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }

    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push({ path: '/audiobooks', query: { group: 'authors' } })
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = Array.from({ length: 50 }, (_, index) => ({
      id: index + 1,
      title: `Book ${index + 1}`,
      authors: [`Author ${index % 5}`],
      imageUrl: `cover${index + 1}.jpg`,
      files: [],
    })) as unknown as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    const vm = getVm(wrapper)
    vm.visibleRange = { start: 40, end: 50 }
    await vm.setGroupBy?.('books')
    await wrapper.vm.$nextTick()

    expect(vm.groupBy).toBe('books')
    expect(vm.visibleRange?.start).toBe(0)
  })

  it('clears selection when changing grouping mode', async () => {
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }

    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 1,
        title: 'Book 1',
        authors: ['Author A'],
        series: 'Series 1',
        imageUrl: 'cover1.jpg',
        files: [],
      },
      {
        id: 2,
        title: 'Book 2',
        authors: ['Author B'],
        series: 'Series 2',
        imageUrl: 'cover2.jpg',
        files: [],
      },
    ] as unknown as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    // Select one item
    store.toggleSelection(1)
    expect(store.selectedIds.size).toBeGreaterThan(0)

    // Switch group and expect selection cleared
    const vm = getVm(wrapper)
    await vm.setGroupBy?.('authors')
    await wrapper.vm.$nextTick()
    expect(store.selectedIds.size).toBe(0)
  })

  it('series bottom placard is only visible when showItemDetails is enabled', async () => {
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }

    // Ensure persisted item details are cleared for this test (deterministic)
    localStorage.setItem('listenarr.showItemDetails', 'false')

    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 1,
        title: 'Book 1',
        authors: ['Author A'],
        series: 'Series 1',
        imageUrl: 'cover1.jpg',
        files: [],
      },
      {
        id: 2,
        title: 'Book 2',
        authors: ['Author B'],
        series: 'Series 2',
        imageUrl: 'cover2.jpg',
        files: [],
      },
    ] as unknown as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    // Set groupBy to series
    const vm = getVm(wrapper)
    await vm.setGroupBy?.('series')
    await wrapper.vm.$nextTick()

    // By default, details should be hidden and placard not present
    expect(vm.showItemDetails).toBe(false)
    expect(wrapper.find('.series-bottom-placard').exists()).toBe(false)

    // Enable details and confirm placard is shown
    if (vm) {
      vm.showItemDetails = true
    }
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.series-bottom-placard').exists()).toBe(true)
  })
})
