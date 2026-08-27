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

const { mockGetAudiobookDeleteCapabilities } = vi.hoisted(() => ({
  mockGetAudiobookDeleteCapabilities: vi.fn(async () => ({
    canRemoveFromLibrary: true,
    canDeleteTrackedFiles: true,
    canDeleteFolder: true,
    reason: null,
    fallbackAction: 'RemoveFromLibraryOnly' as const,
  })),
}))

vi.mock('@/services/api', () => ({
  apiService: {
    getQualityProfiles: vi.fn(async () => []),
    getImageUrl: vi.fn((url: string) => url || 'https://via.placeholder.com/300x450?text=No+Image'),
    getBootstrapConfig: vi.fn(async () => ({})),
    getStartupConfig: vi.fn(async () => ({})),
    getApplicationSettings: vi.fn(async () => ({})),
    getAudiobookDeleteCapabilities: mockGetAudiobookDeleteCapabilities,
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

describe('AudiobooksView list-view virtual scroll', () => {
  type ScrollVm = {
    viewMode: 'grid' | 'list'
    showItemDetails: boolean
    measuredRowHeight: number | null
    visibleRange: { start: number; end: number }
    totalHeight: number
    getRowHeight: () => number
    updateVisibleRange: () => void
    onScroll: () => void
    sortKey: string
    sortOrder: 'asc' | 'desc'
  }

  const ensureBrowserGlobals = () => {
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        unobserve() {}
        disconnect() {}
      }
    }
    if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }
  }

  const mountListView = async (count: number) => {
    ensureBrowserGlobals()
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
    store.audiobooks = Array.from({ length: count }, (_, index) => ({
      id: index + 1,
      title: `Book ${index + 1}`,
      authors: [`Author ${index % 5}`],
      imageUrl: `cover${index + 1}.jpg`,
      files: [],
    })) as unknown as import('@/types').Audiobook[]
    store.fetchLibrary = vi.fn(async () => undefined)

    localStorage.setItem('listenarr.groupBy', 'books')
    localStorage.setItem('listenarr.showItemDetails', 'false')

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

    const vm = wrapper.vm as unknown as ScrollVm
    vm.viewMode = 'list'
    await new Promise((r) => setTimeout(r, 0))
    return { wrapper, vm, store }
  }

  beforeEach(() => {
    localStorage.clear()
  })

  it('uses a fixed list row height and ignores a stale grid measuredRowHeight (no oversized scroll area)', async () => {
    const { vm } = await mountListView(50)

    // Simulate an inflated grid measurement leaking into list view via the shared ref.
    vm.measuredRowHeight = 240

    expect(vm.viewMode).toBe('list')
    // Must be the fixed list constant, not the leaked 240px grid measurement.
    expect(vm.getRowHeight()).toBe(80)
  })

  it('uses a taller fixed row height when show-details is enabled', async () => {
    const { vm } = await mountListView(10)

    vm.showItemDetails = true
    await new Promise((r) => setTimeout(r, 0))

    expect(vm.getRowHeight()).toBe(120)
  })

  it('reserves space for the list header in totalHeight so the last row stays reachable', async () => {
    const { vm } = await mountListView(50)

    // n rows (80px) plus the always-present column header (40px). Without
    // reserving the header height the last row sits below the scrollable area
    // and cannot be fully scrolled into view.
    expect(vm.totalHeight).toBe(80 * 50 + 40)
  })

  it('keeps the list scroll geometry stable when the sort order changes (sort-then-scroll)', async () => {
    const { vm } = await mountListView(50)

    // Simulate a tall row having been sampled, then the user re-sorts (e.g. Author
    // Z->A). The reorder must not let that stale sample — or the reshuffle of which
    // row renders first — change the fixed list row height or the scroll area; that
    // interaction was what locked up the page on the next scroll.
    vm.measuredRowHeight = 240
    vm.sortKey = 'author-last'
    vm.sortOrder = 'desc'
    await new Promise((r) => setTimeout(r, 0))

    expect(vm.sortOrder).toBe('desc')
    expect(vm.getRowHeight()).toBe(80)
    expect(vm.totalHeight).toBe(80 * 50 + 40)
  })

  it('does not reassign visibleRange when the computed range is unchanged', async () => {
    const { vm } = await mountListView(50)

    vm.updateVisibleRange()
    const firstRange = vm.visibleRange
    vm.updateVisibleRange()
    const secondRange = vm.visibleRange

    // Stable identity → no needless watcher fires / re-renders on every scroll tick.
    expect(secondRange).toBe(firstRange)
  })

  it('coalesces rapid scroll events into a single animation frame', async () => {
    const { vm } = await mountListView(50)

    const rafCallbacks: FrameRequestCallback[] = []
    const rafSpy = vi
      .spyOn(globalThis, 'requestAnimationFrame')
      .mockImplementation((cb: FrameRequestCallback) => {
        rafCallbacks.push(cb)
        return rafCallbacks.length
      })

    try {
      vm.onScroll()
      vm.onScroll()
      vm.onScroll()
      // Three scroll events, one scheduled frame.
      expect(rafSpy).toHaveBeenCalledTimes(1)

      // Flushing the frame allows the next scroll to schedule again.
      rafCallbacks[0]?.(0)
      vm.onScroll()
      expect(rafSpy).toHaveBeenCalledTimes(2)
    } finally {
      rafSpy.mockRestore()
    }
  })

  it('widens the visible slice to the rows scrolled into view', async () => {
    const { wrapper, vm } = await mountListView(50)
    const el = wrapper.find('.audiobooks-scroll-container').element as HTMLElement
    // jsdom has no layout, so force the scroll geometry the math reads.
    Object.defineProperty(el, 'clientHeight', { value: 800, configurable: true })
    Object.defineProperty(el, 'scrollTop', { value: 800, configurable: true })

    vm.updateVisibleRange()

    // scrollTop 800 / 80px rows = row 10, ±BUFFER_ROWS(2), 10 viewport rows.
    expect(vm.visibleRange).toEqual({ start: 8, end: 22 })
  })

  it('applies the new range only when the scheduled animation frame runs', async () => {
    const { wrapper, vm } = await mountListView(50)
    const el = wrapper.find('.audiobooks-scroll-container').element as HTMLElement
    Object.defineProperty(el, 'clientHeight', { value: 800, configurable: true })
    Object.defineProperty(el, 'scrollTop', { value: 800, configurable: true })

    const frames: FrameRequestCallback[] = []
    const rafSpy = vi
      .spyOn(globalThis, 'requestAnimationFrame')
      .mockImplementation((cb: FrameRequestCallback) => {
        frames.push(cb)
        return frames.length
      })

    try {
      const before = { ...vm.visibleRange }
      vm.onScroll()
      // Deferred: nothing changes until the frame fires.
      expect(vm.visibleRange).toEqual(before)

      frames[0]?.(0)
      expect(vm.visibleRange).toEqual({ start: 8, end: 22 })
    } finally {
      rafSpy.mockRestore()
    }
  })

  it('cancels a pending scroll animation frame on unmount', async () => {
    const { wrapper, vm } = await mountListView(50)

    const rafSpy = vi
      .spyOn(globalThis, 'requestAnimationFrame')
      .mockImplementation(() => 123 as unknown as number)
    const cancelSpy = vi.spyOn(globalThis, 'cancelAnimationFrame').mockImplementation(() => {})

    try {
      vm.onScroll() // schedules frame 123 (mock never runs it)
      wrapper.unmount()
      expect(cancelSpy).toHaveBeenCalledWith(123)
    } finally {
      rafSpy.mockRestore()
      cancelSpy.mockRestore()
    }
  })
})
