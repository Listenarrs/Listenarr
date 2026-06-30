<!--
  Listenarr - Audiobook Management System
  Copyright (C) 2024-2026 Listenarr Contributors

  This program is free software: you can redistribute it and/or modify
  it under the terms of the GNU Affero General Public License as published
  by the Free Software Foundation, either version 3 of the License, or
  (at your option) any later version.

  This program is distributed in the hope that it will be useful,
  but WITHOUT ANY WARRANTY; without even the implied warranty of
  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
  GNU Affero General Public License for more details.

  You should have received a copy of the GNU Affero General Public License
  along with this program. If not, see <https://www.gnu.org/licenses/>.
-->
<!--
  Unified library search results — the single page that lists everything the user
  owns matching a query, grouped by Books / Series / Authors / Narrators. Distinct
  from content/SearchView.vue, which searches external metadata to add new books.

  Tabs (with counts) double as a result summary and let the user isolate one
  category; the "All" tab is a capped overview so a large Books result never
  buries the other categories. A grid/list toggle and category-appropriate sort
  mirror the controls on the library and collection pages.
-->
<template>
  <div class="library-search-view">
    <header class="search-header">
      <h1><PhMagnifyingGlass /> Search results</h1>
      <p v-if="query" class="search-subtitle">
        for <strong>"{{ query }}"</strong>
      </p>
    </header>

    <div v-if="!query" class="search-state">Type a query to search your library.</div>

    <div v-else-if="libraryStore.loading && libraryStore.audiobooks.length === 0" class="search-state">
      Loading your library…
    </div>

    <div v-else-if="totalResults === 0" class="search-state">
      No matches in your library for "{{ query }}".
    </div>

    <template v-else>
      <div class="search-toolbar">
        <!-- Tabs double as the result summary: each shows its count. -->
        <div class="search-tabs" role="tablist">
          <button
            v-for="tab in tabs"
            :key="tab.key"
            class="search-tab"
            :class="{ active: activeTab === tab.key }"
            role="tab"
            :aria-selected="activeTab === tab.key"
            @click="activeTab = tab.key"
          >
            {{ tab.label }}
            <span class="search-tab-count">{{ tab.count }}</span>
          </button>
        </div>

        <div class="search-view-controls">
          <div v-if="activeTab !== 'all'" class="sort-control">
            <label class="sr-only" :for="sortSelectId">Sort by</label>
            <select :id="sortSelectId" v-model="sortKey" class="sort-select">
              <option v-for="opt in sortOptions" :key="opt.value" :value="opt.value">
                {{ opt.label }}
              </option>
            </select>
            <button
              class="icon-btn"
              type="button"
              :title="sortDir === 'asc' ? 'Ascending' : 'Descending'"
              :aria-label="sortDir === 'asc' ? 'Sort ascending' : 'Sort descending'"
              @click="sortDir = sortDir === 'asc' ? 'desc' : 'asc'"
            >
              <PhArrowUp v-if="sortDir === 'asc'" />
              <PhArrowDown v-else />
            </button>
          </div>
          <div class="view-toggle" role="group" aria-label="View mode">
            <button
              class="icon-btn"
              type="button"
              :class="{ active: viewMode === 'grid' }"
              title="Grid view"
              aria-label="Grid view"
              @click="setViewMode('grid')"
            >
              <PhSquaresFour />
            </button>
            <button
              class="icon-btn"
              type="button"
              :class="{ active: viewMode === 'list' }"
              title="List view"
              aria-label="List view"
              @click="setViewMode('list')"
            >
              <PhListBullets />
            </button>
          </div>
        </div>
      </div>

      <section v-for="section in visibleSections" :key="section.kind" class="result-section">
        <div class="result-section-head">
          <h2 class="result-section-title">
            {{ section.label }} <span class="section-count">{{ section.total }}</span>
          </h2>
          <button
            v-if="section.truncated"
            class="see-all-link"
            type="button"
            @click="activeTab = section.kind"
          >
            See all {{ section.total }} →
          </button>
        </div>

        <!-- Books -->
        <template v-if="section.kind === 'book'">
          <div v-if="viewMode === 'grid'" class="card-grid">
            <RouterLink
              v-for="book in section.books"
              :key="`book-${book.id}`"
              class="result-card"
              :to="{ name: 'audiobook-detail', params: { id: String(book.id) } }"
            >
              <img
                class="card-cover"
                :src="getProtectedImageSrc(book.imageUrl, getPlaceholderUrl())"
                :alt="book.title"
                loading="lazy"
                decoding="async"
                @error="handleImageError"
              />
              <div class="card-label" :title="book.title">{{ book.title }}</div>
              <div v-if="book.author" class="card-sub" :title="book.author">{{ book.author }}</div>
            </RouterLink>
          </div>
          <div v-else class="result-list">
            <RouterLink
              v-for="book in section.books"
              :key="`book-row-${book.id}`"
              class="result-row"
              :to="{ name: 'audiobook-detail', params: { id: String(book.id) } }"
            >
              <img
                class="row-thumb"
                :src="getProtectedImageSrc(book.imageUrl, getPlaceholderUrl())"
                :alt="book.title"
                loading="lazy"
                decoding="async"
                @error="handleImageError"
              />
              <div class="row-main">
                <div class="row-title">{{ book.title }}</div>
                <div class="row-sub">{{ [book.author, book.year].filter(Boolean).join(' · ') }}</div>
              </div>
            </RouterLink>
          </div>
        </template>

        <!-- Series / Authors / Narrators -->
        <template v-else>
          <div v-if="viewMode === 'grid'" class="card-grid">
            <RouterLink
              v-for="facet in section.facets"
              :key="`${section.kind}-${facet.normKey}`"
              class="result-card"
              :to="`/collection/${section.kind}/${encodeURIComponent(facet.name)}`"
            >
              <img
                class="card-cover"
                :src="getProtectedImageSrc(facet.imageUrl || '', getPlaceholderUrl())"
                :alt="facet.name"
                loading="lazy"
                decoding="async"
                @error="handleImageError"
              />
              <div class="card-label" :title="facet.name">{{ facet.name }}</div>
              <div class="card-sub">{{ facet.count }} book{{ facet.count !== 1 ? 's' : '' }}</div>
            </RouterLink>
          </div>
          <div v-else class="result-list">
            <RouterLink
              v-for="facet in section.facets"
              :key="`${section.kind}-row-${facet.normKey}`"
              class="result-row"
              :to="`/collection/${section.kind}/${encodeURIComponent(facet.name)}`"
            >
              <img
                class="row-thumb"
                :src="getProtectedImageSrc(facet.imageUrl || '', getPlaceholderUrl())"
                :alt="facet.name"
                loading="lazy"
                decoding="async"
                @error="handleImageError"
              />
              <div class="row-main">
                <div class="row-title">{{ facet.name }}</div>
                <div class="row-sub">{{ facet.count }} book{{ facet.count !== 1 ? 's' : '' }}</div>
              </div>
            </RouterLink>
          </div>
        </template>
      </section>
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { RouterLink } from 'vue-router'
import {
  PhMagnifyingGlass,
  PhSquaresFour,
  PhListBullets,
  PhArrowUp,
  PhArrowDown,
} from '@phosphor-icons/vue'
import { useLibraryStore } from '@/stores/library'
import { useProtectedImages } from '@/composables/useProtectedImages'
import { getPlaceholderUrl } from '@/utils/placeholder'
import { handleImageError } from '@/utils/imageFallback'
import {
  buildLibraryFacets,
  matchLibraryBooks,
  matchLibraryFacets,
  type LibraryBookMatch,
  type LibraryFacetMatch,
} from '@/utils/librarySearch'

type FacetKind = 'series' | 'author' | 'narrator'
type TabKey = 'all' | 'book' | FacetKind
type SortDir = 'asc' | 'desc'

// In the "All" overview, cap each section so a large Books result can't bury the
// other categories — a "See all" link jumps to that category's tab for the rest.
const ALL_PREVIEW_LIMIT = 12
const VIEWMODE_KEY = 'listenarr.search.viewMode'

const route = useRoute()
const libraryStore = useLibraryStore()
const { getProtectedImageSrc } = useProtectedImages()

const sortSelectId = 'library-search-sort'
const activeTab = ref<TabKey>('all')
const viewMode = ref<'grid' | 'list'>('grid')
const bookSortKey = ref<'title' | 'author' | 'year'>('title')
const facetSortKey = ref<'count' | 'name'>('count')
const sortDir = ref<SortDir>('asc')

const query = computed(() => {
  const q = route.query.q
  return (typeof q === 'string' ? q : Array.isArray(q) ? q[0] || '' : '').trim()
})

const results = computed(() => {
  const q = query.value
  if (!q) {
    return {
      books: [] as LibraryBookMatch[],
      series: [] as LibraryFacetMatch[],
      authors: [] as LibraryFacetMatch[],
      narrators: [] as LibraryFacetMatch[],
    }
  }
  const books = libraryStore.audiobooks
  const facets = buildLibraryFacets(books)
  return {
    books: matchLibraryBooks(books, q),
    series: matchLibraryFacets(facets.series, q),
    authors: matchLibraryFacets(facets.authors, q),
    narrators: matchLibraryFacets(facets.narrators, q),
  }
})

const totalResults = computed(
  () =>
    results.value.books.length +
    results.value.series.length +
    results.value.authors.length +
    results.value.narrators.length,
)

const tabs = computed(() => {
  const out: { key: TabKey; label: string; count: number }[] = [
    { key: 'all', label: 'All', count: totalResults.value },
  ]
  if (results.value.books.length) out.push({ key: 'book', label: 'Books', count: results.value.books.length })
  if (results.value.series.length) out.push({ key: 'series', label: 'Series', count: results.value.series.length })
  if (results.value.authors.length) out.push({ key: 'author', label: 'Authors', count: results.value.authors.length })
  if (results.value.narrators.length)
    out.push({ key: 'narrator', label: 'Narrators', count: results.value.narrators.length })
  return out
})

// If a query change empties the active category, fall back to the overview.
watch(tabs, (list) => {
  if (!list.some((t) => t.key === activeTab.value)) activeTab.value = 'all'
})

const sortOptions = computed(() =>
  activeTab.value === 'book'
    ? [
        { value: 'title', label: 'Title' },
        { value: 'author', label: 'Author' },
        { value: 'year', label: 'Year' },
      ]
    : [
        { value: 'count', label: 'Books' },
        { value: 'name', label: 'Name' },
      ],
)

// One v-model that proxies to whichever per-category key applies, so each tab
// remembers its own sort. Switching to a category seeds a sensible default dir.
const sortKey = computed<string>({
  get: () => (activeTab.value === 'book' ? bookSortKey.value : facetSortKey.value),
  set: (value) => {
    if (activeTab.value === 'book') bookSortKey.value = value as 'title' | 'author' | 'year'
    else facetSortKey.value = value as 'count' | 'name'
  },
})

function sortBooks(list: LibraryBookMatch[], byDefault: boolean): LibraryBookMatch[] {
  const key = byDefault ? 'title' : bookSortKey.value
  const dir = byDefault ? 1 : sortDir.value === 'asc' ? 1 : -1
  return [...list].sort((a, b) => {
    if (key === 'year') {
      const ay = a.year ?? -Infinity
      const by = b.year ?? -Infinity
      return ay === by ? a.title.localeCompare(b.title) : (ay - by) * dir
    }
    const av = key === 'author' ? a.author : a.title
    const bv = key === 'author' ? b.author : b.title
    return av.localeCompare(bv) * dir || a.title.localeCompare(b.title)
  })
}

function sortFacets(list: LibraryFacetMatch[], byDefault: boolean): LibraryFacetMatch[] {
  const key = byDefault ? 'count' : facetSortKey.value
  const dir = byDefault ? 1 : sortDir.value === 'asc' ? 1 : -1
  return [...list].sort((a, b) => {
    if (key === 'count') {
      // Default (byDefault) keeps the most-books-first ranking the util produced.
      return (b.count - a.count) * (byDefault ? 1 : dir === 1 ? 1 : -1) || a.name.localeCompare(b.name)
    }
    return a.name.localeCompare(b.name) * dir || b.count - a.count
  })
}

interface VisibleSection {
  kind: TabKey & ('book' | FacetKind)
  label: string
  total: number
  truncated: boolean
  books?: LibraryBookMatch[]
  facets?: LibraryFacetMatch[]
}

const visibleSections = computed<VisibleSection[]>(() => {
  const all = activeTab.value === 'all'
  const out: VisibleSection[] = []
  const wantBooks = all || activeTab.value === 'book'
  const wantSeries = all || activeTab.value === 'series'
  const wantAuthors = all || activeTab.value === 'author'
  const wantNarrators = all || activeTab.value === 'narrator'

  if (wantBooks && results.value.books.length) {
    const sorted = sortBooks(results.value.books, all)
    out.push({
      kind: 'book',
      label: 'Books',
      total: results.value.books.length,
      truncated: all && sorted.length > ALL_PREVIEW_LIMIT,
      books: all ? sorted.slice(0, ALL_PREVIEW_LIMIT) : sorted,
    })
  }
  const facetSection = (kind: FacetKind, label: string, list: LibraryFacetMatch[]) => {
    if (!((all || activeTab.value === kind) && list.length)) return
    const sorted = sortFacets(list, all)
    out.push({
      kind,
      label,
      total: list.length,
      truncated: all && sorted.length > ALL_PREVIEW_LIMIT,
      facets: all ? sorted.slice(0, ALL_PREVIEW_LIMIT) : sorted,
    })
  }
  if (wantSeries) facetSection('series', 'Series', results.value.series)
  if (wantAuthors) facetSection('author', 'Authors', results.value.authors)
  if (wantNarrators) facetSection('narrator', 'Narrators', results.value.narrators)
  return out
})

function setViewMode(mode: 'grid' | 'list') {
  viewMode.value = mode
  try {
    localStorage.setItem(VIEWMODE_KEY, mode)
  } catch {
    /* ignore localStorage errors (e.g. privacy mode) */
  }
}

onMounted(() => {
  try {
    const stored = localStorage.getItem(VIEWMODE_KEY)
    if (stored === 'grid' || stored === 'list') viewMode.value = stored
  } catch {
    /* ignore */
  }
  if (libraryStore.audiobooks.length === 0) {
    void libraryStore.fetchLibrary()
  }
})
</script>

<style scoped>
.library-search-view {
  padding: 24px;
  max-width: 1400px;
  margin: 0 auto;
}

.search-header {
  margin-bottom: 16px;
}

.search-header h1 {
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 1.6rem;
  margin: 0;
}

.search-subtitle {
  margin: 6px 0 0;
  color: var(--text-secondary, #bfc8cf);
}

.search-state {
  padding: 48px 12px;
  text-align: center;
  color: var(--text-secondary, #9aa0a6);
}

.search-toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 20px;
  padding-bottom: 12px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.06);
}

.search-tabs {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.search-tab {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 6px 12px;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 18px;
  background: transparent;
  color: var(--text-secondary, #bfc8cf);
  font-size: 0.88rem;
  cursor: pointer;
}

.search-tab:hover {
  background: rgba(255, 255, 255, 0.04);
}

.search-tab.active {
  background: #2196f3;
  border-color: #2196f3;
  color: #fff;
}

.search-tab-count {
  font-size: 0.75rem;
  font-weight: 600;
  background: rgba(0, 0, 0, 0.18);
  border-radius: 10px;
  padding: 0 7px;
}

.search-tab.active .search-tab-count {
  background: rgba(255, 255, 255, 0.22);
}

.search-view-controls {
  display: flex;
  align-items: center;
  gap: 10px;
}

.sort-control {
  display: flex;
  align-items: center;
  gap: 4px;
}

.sort-select {
  background: rgba(255, 255, 255, 0.04);
  color: var(--text-primary, #e6eef6);
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 6px;
  padding: 6px 8px;
  font-size: 0.85rem;
}

.icon-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary, #bfc8cf);
  cursor: pointer;
}

.icon-btn:hover {
  background: rgba(255, 255, 255, 0.04);
}

.icon-btn.active {
  background: #2196f3;
  border-color: #2196f3;
  color: #fff;
}

.view-toggle {
  display: flex;
  gap: 4px;
}

.result-section {
  margin-bottom: 32px;
}

.result-section-head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 12px;
}

.result-section-title {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 1.05rem;
  margin: 0;
}

.section-count {
  font-size: 0.8rem;
  font-weight: 600;
  color: var(--text-secondary, #8a929a);
  background: rgba(255, 255, 255, 0.06);
  border-radius: 10px;
  padding: 1px 8px;
}

.see-all-link {
  background: transparent;
  border: none;
  color: #2196f3;
  font-size: 0.85rem;
  font-weight: 500;
  cursor: pointer;
}

.see-all-link:hover {
  text-decoration: underline;
}

.card-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));
  gap: 16px;
}

.result-card {
  display: flex;
  flex-direction: column;
  text-decoration: none;
  color: inherit;
  border-radius: 8px;
  transition: transform 120ms ease;
}

.result-card:hover {
  transform: translateY(-2px);
}

.card-cover {
  width: 100%;
  aspect-ratio: 1 / 1;
  object-fit: cover;
  border-radius: 8px;
  background: #2a2a2a;
}

.card-label {
  margin-top: 8px;
  font-weight: 500;
  font-size: 0.9rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.card-sub {
  font-size: 0.8rem;
  color: var(--text-secondary, #bfc8cf);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.result-list {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.result-row {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 8px 10px;
  border-radius: 8px;
  text-decoration: none;
  color: inherit;
}

.result-row:hover {
  background: rgba(255, 255, 255, 0.03);
}

.row-thumb {
  width: 48px;
  height: 48px;
  object-fit: cover;
  border-radius: 6px;
  flex-shrink: 0;
  background: #2a2a2a;
}

.row-main {
  min-width: 0;
}

.row-title {
  font-weight: 500;
  font-size: 0.92rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.row-sub {
  font-size: 0.82rem;
  color: var(--text-secondary, #bfc8cf);
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border: 0;
}
</style>
