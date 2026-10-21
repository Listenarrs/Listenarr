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
<!-- One book and the Audible record it should point at: ASIN, Title, Author and Region, with the
     search under the book (Repair Books and Find Match) -->
<template>
  <div class="form-control-card match-card">
    <div class="match-header">
      <span class="match-heading">
        <slot name="heading" />
      </span>
      <span class="match-header-right">
        <span class="match-meta">
          <slot name="meta" />
        </span>
        <button
          type="button"
          class="btn btn-secondary match-revert-btn"
          title="Revert"
          aria-label="Revert to the values before your changes"
          data-testid="match-revert"
          :disabled="!isEdited(match)"
          @click.prevent.stop="revert"
        >
          <PhArrowCounterClockwise :size="14" />
        </button>
      </span>
    </div>

    <div class="match-fields">
      <label class="match-field match-field-asin">
        <span class="match-field-label">ASIN</span>
        <input
          v-model.trim="match.asin"
          class="form-input"
          maxlength="10"
          data-testid="match-asin"
          @input="onAsinTyped"
        />
      </label>
      <label class="match-field">
        <span class="match-field-label">Title</span>
        <input
          ref="titleEl"
          v-model="match.query"
          class="form-input"
          data-testid="match-title"
          @input="onSearchChanged"
          @keydown.enter.prevent="search()"
        />
      </label>
      <label class="match-field">
        <span class="match-field-label">Author</span>
        <input
          v-model="match.author"
          class="form-input"
          data-testid="match-author"
          @input="onSearchChanged"
          @keydown.enter.prevent="search()"
        />
      </label>
      <label class="match-field">
        <span class="match-field-label">Region</span>
        <select
          v-model="match.region"
          class="form-input"
          data-testid="match-region"
          @change="onRegionChanged"
        >
          <option v-for="o in regionOptions" :key="o.value" :value="o.value">
            {{ o.label }}
          </option>
        </select>
      </label>
      <button
        v-if="state.status === 'searching'"
        type="button"
        class="btn btn-danger match-search-btn"
        data-testid="match-cancel-search"
        @click="cancelSearch"
      >
        <PhX :size="16" />
        Cancel search
      </button>
      <button
        v-else
        type="button"
        class="btn btn-secondary match-search-btn"
        data-testid="match-search"
        :disabled="!canSearch"
        :title="searchLocked ? 'Another book is being searched' : undefined"
        @click="search()"
      >
        <PhMagnifyingGlass :size="16" />
        Search
      </button>
    </div>

    <!-- The search under the book: pending, failed with Retry, or its results to pick from -->
    <p
      v-if="state.status === 'searching'"
      class="match-search-state"
      data-testid="match-search-pending"
    >
      <PhSpinner class="ph-spin" :size="16" />
      <template v-if="state.kind === 'asin'">
        Checking {{ match.asin.trim().toUpperCase() }} in {{ regionLabel(match.region) }}…
      </template>
      <template v-else>
        Searching "{{ match.query.trim() }}" in {{ regionLabel(match.region) }}…
      </template>
    </p>
    <p
      v-else-if="state.status === 'failed'"
      class="match-search-state match-search-error"
      data-testid="match-search-error"
    >
      <PhWarningCircle :size="16" />
      <span>{{ state.error }}</span>
      <button
        type="button"
        class="btn btn-secondary match-retry-btn"
        data-testid="match-search-retry"
        :disabled="!canSearchFor(state.kind)"
        @click="search(state.kind)"
      >
        <PhArrowCounterClockwise :size="14" />
        Retry
      </button>
    </p>
    <template v-else-if="state.status === 'done'">
      <FindMatchResults
        v-if="state.results.length"
        class="match-search-results"
        :results="state.results"
        :selected-asin="match.picked?.asin"
        @select="pick"
      />
      <p v-else class="match-search-state" data-testid="match-search-empty">
        No results for "{{ match.query.trim() }}"{{
          match.author.trim() ? ` by "${match.author.trim()}"` : ''
        }}
        in {{ regionLabel(match.region) }}.
      </p>
    </template>
    <p v-if="match.picked" class="match-picked" data-testid="match-picked">
      New record: {{ match.picked.title ?? match.picked.asin }}
    </p>
    <p
      v-if="checkAsin && !match.asinError && asinInvalid(match)"
      class="match-hint"
      data-testid="match-invalid-asin"
    >
      {{
        match.asin.trim()
          ? 'An ASIN is 10 letters or digits: type it, or search the book.'
          : 'No ASIN: search the book and pick it, or type its ASIN.'
      }}
    </p>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import {
  PhArrowCounterClockwise,
  PhMagnifyingGlass,
  PhSpinner,
  PhWarningCircle,
  PhX,
} from '@phosphor-icons/vue'
import FindMatchResults from './FindMatchResults.vue'
import { useLibraryImportStore } from '@/stores/libraryImport'
import { orderedSearchRegionOptions } from '@/utils/languageMapping'
import {
  FALLBACK_REGION,
  asinInvalid,
  isEdited,
  asinValid,
  noRecordError,
  regionLabel,
  searchParams,
  type BookMatch,
} from '@/utils/bookMatch'
import type { SearchResult } from '@/types'

// A search slower than this is stopped and reported, with Retry
const SEARCH_TIMEOUT_MS = 30_000

const props = withDefaults(
  defineProps<{
    configuredRegion: string
    // Another book is being searched: this one waits for it
    searchLocked?: boolean
    // Flag an ASIN that is missing or malformed
    checkAsin?: boolean
    // Search as soon as the card shows, and focus the title
    searchOnMount?: boolean
  }>(),
  { searchLocked: false, checkAsin: true, searchOnMount: false },
)

const match = defineModel<BookMatch>('match', { required: true })

const emit = defineEmits<{
  // A search starts or ends, however it ends (except by unmounting the card)
  searching: [searching: boolean]
  picked: [result: SearchResult]
}>()

// A search for the title and author, or the check of the ASIN in its region
type SearchKind = 'title' | 'asin'

interface SearchState {
  status: 'idle' | 'searching' | 'done' | 'failed'
  kind: SearchKind
  results: SearchResult[]
  error: string | null
}

const libraryImportStore = useLibraryImportStore()
const titleEl = ref<HTMLInputElement | null>(null)
const state = ref<SearchState>(idle())
let controller: AbortController | null = null

// Every Audible region stays available, ordered by usefulness: the book's region when the card
// opened first, then the configured one and the US, then the others alphabetically
const regionOptions = computed(() =>
  orderedSearchRegionOptions([match.value.initial.region, props.configuredRegion, FALLBACK_REGION]),
)

const canSearch = computed(() => canSearchFor('title'))

function canSearchFor(kind: SearchKind): boolean {
  if (props.searchLocked || state.value.status === 'searching') return false
  return kind === 'asin' ? asinValid(match.value) : !!match.value.query.trim()
}

function idle(): SearchState {
  return { status: 'idle', kind: 'title', results: [], error: null }
}

// Search the title and author (or an ASIN typed as the title) in the region, the results opening
// under the book; or check the ASIN in its region, which then vouches for it or rejects it
async function search(kind: SearchKind = 'title') {
  if (!canSearchFor(kind)) return
  const asin = match.value.asin.trim().toUpperCase()
  const region = match.value.region
  const current = new AbortController()
  controller = current
  let timedOut = false
  const timer = setTimeout(() => {
    timedOut = true
    current.abort()
  }, SEARCH_TIMEOUT_MS)
  state.value = { status: 'searching', kind, results: [], error: null }
  try {
    const params = kind === 'asin' ? { asin, cap: 5 } : searchParams(match.value)
    const results = await libraryImportStore.searchInRegion(params, region, current.signal)
    // A canceled search answers to nobody: the card already went back to its fields
    if (controller !== current) return
    if (kind === 'title') {
      state.value = { status: 'done', kind, results, error: null }
      return
    }
    const found = results
      .filter((r) => r.asin?.toUpperCase() === asin)
      .map((r) => ({ ...r, region: r.region || region }))
    if (found.length === 1) {
      // One record: it is the book's, selected in the results, and its title and author fill the
      // fields
      const record = found[0]!
      match.value.picked = record
      match.value.query = record.title || match.value.query
      match.value.author = record.authors?.[0]?.name || match.value.author
      state.value = { status: 'done', kind, results: found, error: null }
    } else if (found.length > 1) {
      // Several records for one ASIN: the user picks
      state.value = { status: 'done', kind, results: found, error: null }
    } else {
      match.value.asinError = noRecordError(asin, region)
      state.value = { status: 'failed', kind, results: [], error: match.value.asinError }
    }
  } catch (e) {
    if (controller !== current) return
    const error = timedOut
      ? `The search took longer than ${SEARCH_TIMEOUT_MS / 1000} seconds and was stopped.`
      : `The search failed: ${(e as Error)?.message ?? 'unknown error'}`
    // An ASIN its region could not be checked for stays unusable until checked
    if (kind === 'asin') match.value.asinError = error
    state.value = { status: 'failed', kind, results: [], error }
  } finally {
    clearTimeout(timer)
    if (controller === current) controller = null
  }
}

// Back to the fields, nothing to report
function cancelSearch() {
  controller?.abort()
  controller = null
  if (state.value.status === 'searching') state.value = idle()
}

// The record picked in the results: its ASIN in the region it was found in; the results close
function pick(result: SearchResult) {
  if (!result.asin) return
  match.value.asin = result.asin.toUpperCase()
  match.value.region = result.region || match.value.region
  match.value.picked = { ...result, region: result.region || match.value.region }
  match.value.asinError = null
  state.value = idle()
  emit('picked', result)
}

// Another title or author describes another record: the ASIN no longer matches, so it is emptied
// until the book is searched and picked again (or its ASIN is typed). A search running for the
// previous values is stopped, and their results no longer apply.
function onSearchChanged() {
  match.value.asin = ''
  onSearchReset()
}

function onSearchReset() {
  match.value.picked = null
  match.value.asinError = null
  cancelSearch()
  state.value = idle()
}

// Another region keeps the ASIN and checks it there right away: the record picked in the previous
// one no longer vouches for it, and the previous search and its results no longer apply
function onRegionChanged() {
  onSearchReset()
  void search('asin')
}

// A typed ASIN is no longer the picked record, nor the one rejected in the region
function onAsinTyped() {
  match.value.picked = null
  match.value.asinError = null
  if (state.value.kind === 'asin') {
    cancelSearch()
    state.value = idle()
  }
}

watch(
  () => state.value.status === 'searching',
  (searching) => emit('searching', searching),
)

function revert() {
  cancelSearch()
  Object.assign(match.value, match.value.initial)
  match.value.picked = null
  match.value.asinError = null
  state.value = idle()
}

onMounted(() => {
  if (!props.searchOnMount) return
  titleEl.value?.focus()
  titleEl.value?.select()
  void search()
})

onBeforeUnmount(cancelSearch)

defineExpose({ search, cancelSearch })
</script>

<style scoped>
.match-header {
  display: flex;
  justify-content: space-between;
  gap: 0.75rem;
  flex-wrap: wrap;
}

.match-heading {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 600;
}

.match-header-right {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.match-revert-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 1.75rem;
  height: 1.75rem;
  padding: 0;
}

.match-meta {
  font-size: 0.8rem;
  opacity: 0.75;
}

/* ASIN, Title, Author, Region and Search always share the whole row */
.match-fields {
  display: grid;
  grid-template-columns:
    minmax(8rem, 1fr) minmax(0, 2.5fr) minmax(0, 1.5fr) minmax(12rem, 1.3fr)
    auto;
  align-items: end;
  gap: 0.75rem;
  margin-top: 0.75rem;
}

.match-field {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
  min-width: 0;
}

.match-field .form-input {
  width: 100%;
  box-sizing: border-box;
}

.match-field-asin input {
  font-family: monospace;
}

.match-field-label {
  font-size: 0.8rem;
  color: #aaa;
}

/* Search and Cancel search take the same width, so the row doesn't move when they swap */
.match-search-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 0.4rem;
  width: 11rem;
  white-space: nowrap;
}

@media (max-width: 720px) {
  .match-fields {
    grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  }

  .match-search-btn {
    grid-column: 1 / -1;
    width: auto;
  }
}

.match-search-state {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 0.5rem;
  margin: 0.75rem 0 0;
  font-size: 0.85rem;
  color: #aaa;
}

.match-search-error {
  color: #ef4444;
}

.match-retry-btn {
  display: inline-flex;
  align-items: center;
  gap: 0.3rem;
  padding: 0.25rem 0.6rem;
  font-size: 0.8rem;
}

.match-search-results {
  margin-top: 0.75rem;
}

.match-picked {
  margin: 0.5rem 0 0;
  font-size: 0.85rem;
  color: #22c55e;
}

.match-hint {
  margin: 0.5rem 0 0;
  font-size: 0.8rem;
  color: #ffb74d;
}
</style>
