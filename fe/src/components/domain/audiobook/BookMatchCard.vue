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
<!-- One book and the Audible record it should point at: its current and new records, the ASIN,
     Title, Author and Region fields, and the search under the book (Repair Books and Find Match).
     The bar on its left tells its state: grey ignored, green matched, red error. -->
<template>
  <div
    class="form-control-card match-card"
    :class="`match-card-${barState}`"
    :data-match-state="barState"
  >
    <div class="match-header">
      <span class="match-heading">
        <slot name="heading" />
      </span>
      <span class="match-header-right">
        <span class="match-meta" data-testid="match-summary">
          {{ currentSummary }}
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
          @change="asinEntered = true"
          @keydown.enter.prevent="onAsinEntered"
        />
      </label>
      <label class="match-field">
        <span class="match-field-label">Title</span>
        <input
          ref="titleEl"
          v-model="match.query"
          class="form-input"
          data-testid="match-title"
          @input="onSearchChanged('title')"
          @keydown.enter.prevent="search()"
        />
      </label>
      <label class="match-field">
        <span class="match-field-label">Author</span>
        <input
          v-model="match.author"
          class="form-input"
          data-testid="match-author"
          @input="onSearchChanged('author')"
          @keydown.enter.prevent="search()"
        />
      </label>
      <label class="match-field">
        <span class="match-field-label">Region</span>
        <select
          v-model="match.region"
          class="form-input"
          data-testid="match-region"
          :disabled="regionLocked"
          :title="
            regionLocked ? 'Wait for the search to end: changing the region searches' : undefined
          "
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
        v-else-if="retryKind"
        type="button"
        class="btn btn-secondary match-search-btn"
        data-testid="match-retry"
        :disabled="!canSearchFor(retryKind)"
        :title="
          searchLocked ? 'Another book is being searched' : 'Search again with the same values'
        "
        @click="search(retryKind)"
      >
        <PhArrowCounterClockwise :size="16" />
        Retry
      </button>
      <button
        v-else
        type="button"
        class="btn btn-secondary match-search-btn"
        data-testid="match-search"
        :disabled="!canSearchFor(searchKind)"
        :title="
          searchLocked
            ? 'Another book is being searched'
            : 'The search Add New and Library Import use, with the same rules and priorities'
        "
        @click="search(searchKind)"
      >
        <PhMagnifyingGlass :size="16" />
        Search
      </button>
    </div>

    <!-- What the book points at today, and the record it will point at -->
    <dl class="match-records">
      <dt>Current</dt>
      <dd data-testid="match-current">
        {{ currentSummary || 'None' }}
      </dd>
      <dt>New</dt>
      <dd v-if="match.picked" data-testid="match-picked">
        {{
          bookSummary({
            asin: match.picked.asin,
            title: match.picked.title,
            subtitle: match.picked.subtitle,
            author: match.picked.authors?.[0]?.name,
            region: match.picked.region ?? match.region,
          })
        }}
        <span class="match-status">
          <PhCheckCircle :size="14" weight="fill" />
          checked
        </span>
      </dd>
      <dd v-else-if="match.asinError" class="match-new-error" data-testid="match-asin-error">
        <PhXCircle :size="14" weight="fill" />
        <span>{{ match.asinError }}</span>
      </dd>
      <dd v-else data-testid="match-none">
        No new record yet: search the book and pick it, or type its ASIN
      </dd>
    </dl>

    <!-- The rules, where they apply -->
    <p v-if="clearedBy && !match.asin" class="match-hint" data-testid="match-asin-cleared">
      ASIN cleared: the {{ clearedBy }} changed, search again.
    </p>
    <p v-if="asinEntered && !asinValid(match)" class="match-hint" data-testid="match-invalid-asin">
      An ASIN is 10 letters or digits.
    </p>

    <!-- The search under the book: pending, failed (Search is then Retry), or its results -->
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
      v-else-if="state.status === 'failed' && state.kind === 'title'"
      class="match-search-state match-search-error"
      data-testid="match-search-error"
    >
      <PhWarningCircle :size="16" />
      <span>{{ state.error }}</span>
    </p>
    <template v-else-if="state.status === 'done'">
      <FindMatchResults
        v-if="state.results.length"
        class="match-search-results"
        :results="state.results"
        :selected="selectedRecord"
        @select="pick"
      />
      <p v-else class="match-search-state" data-testid="match-search-empty">
        No results for "{{ match.query.trim() }}"{{
          match.author.trim() ? ` by "${match.author.trim()}"` : ''
        }}
        in {{ regionLabel(match.region) }}.
      </p>
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import {
  PhArrowCounterClockwise,
  PhCheckCircle,
  PhMagnifyingGlass,
  PhSpinner,
  PhWarningCircle,
  PhX,
  PhXCircle,
} from '@phosphor-icons/vue'
import FindMatchResults from './FindMatchResults.vue'
import { useLibraryImportStore } from '@/stores/libraryImport'
import { orderedSearchRegionOptions } from '@/utils/languageMapping'
import {
  FALLBACK_REGION,
  asinValid,
  bookSummary,
  isEdited,
  matchState,
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
    // The book as it is today, summed up with its current record next to Revert and on Current
    book: { title?: string | null; subtitle?: string | null; author?: string | null }
    // Another book is being searched: this one waits for it
    searchLocked?: boolean
    // Left out by the user (Repair's checkbox): a matched book then shows as ignored
    excluded?: boolean
    // Search as soon as the card shows, and focus the title
    searchOnMount?: boolean
  }>(),
  { searchLocked: false, excluded: false, searchOnMount: false },
)

const match = defineModel<BookMatch>('match', { required: true })

const emit = defineEmits<{
  // A search starts or ends, however it ends (except by unmounting the card)
  searching: [searching: boolean]
  // A record is now the book's new one, picked in the results or found for its ASIN
  matched: [record: SearchResult]
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
// The field whose change emptied the ASIN, said under the fields until the ASIN is filled again
const clearedBy = ref<'title' | 'author' | null>(null)
// The ASIN was entered (left, or Enter): a malformed one is flagged from then on, not while typed
const asinEntered = ref(false)
let controller: AbortController | null = null

// Every Audible region stays available, ordered by usefulness: the book's region when the card
// opened first, then the configured one and the US, then the others alphabetically
const regionOptions = computed(() =>
  orderedSearchRegionOptions([match.value.initial.region, props.configuredRegion, FALLBACK_REGION]),
)

const barState = computed(() => {
  const matched = matchState(match.value)
  return matched === 'matched' && props.excluded ? 'ignored' : matched
})

// Search looks the ASIN up when it is the only thing to search (a typed ASIN empties the title),
// else searches the title and author
const searchKind = computed<SearchKind>(() =>
  !match.value.query.trim() && match.value.asin ? 'asin' : 'title',
)

// ASIN - Title: Subtitle - Author - Region of the book as it is today
const currentSummary = computed(() =>
  bookSummary({
    ...props.book,
    asin: match.value.current.asin,
    region: match.value.current.region,
  }),
)

// The record shown as selected in the results: the new one once there is one, else the one the
// book points at today
const selectedRecord = computed(() => {
  const { picked, current } = match.value
  if (picked?.asin) return { asin: picked.asin, region: picked.region ?? match.value.region }
  return current.asin ? { asin: current.asin, region: current.region } : null
})

// Changing the region looks the ASIN up there: not while a search runs, here or on another book.
// One search at a time keeps answers from racing each other.
const regionLocked = computed(() => props.searchLocked || state.value.status === 'searching')

// The last search, with the values still in the fields, found nothing or failed: Search becomes
// Retry. Editing a field forgets that search, and Search comes back. An ASIN its region has no
// record for keeps Search: searching the title there is what comes next.
const retryKind = computed<SearchKind | null>(() => {
  const { status, results, kind } = state.value
  if (status === 'failed' || (status === 'done' && results.length === 0)) return kind
  return null
})

function canSearchFor(kind: SearchKind): boolean {
  if (props.searchLocked || state.value.status === 'searching') return false
  return kind === 'asin' ? asinValid(match.value) : !!match.value.query.trim()
}

function idle(): SearchState {
  return { status: 'idle', kind: 'title', results: [], error: null }
}

function setMatched(record: SearchResult) {
  match.value.picked = record
  match.value.asinError = null
  clearedBy.value = null
  emit('matched', record)
}

// Search the title and author (or an ASIN typed as the title) in the region, the results opening
// under the book; or look the ASIN up in its region: one record is the book's new one, several are
// offered to pick from, none is an error
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
      // One record: the book's, selected in the results; its title and author fill the fields
      const record = found[0]!
      match.value.query = record.title || match.value.query
      match.value.author = record.authors?.[0]?.name || match.value.author
      setMatched(record)
      state.value = { status: 'done', kind, results: found, error: null }
    } else if (found.length > 1) {
      state.value = { status: 'done', kind, results: found, error: null }
    } else {
      match.value.asinError = noRecordError(asin, region)
      state.value = idle()
    }
  } catch (e) {
    if (controller !== current) return
    const error = timedOut
      ? `The search took longer than ${SEARCH_TIMEOUT_MS / 1000} seconds and was stopped.`
      : `The search failed: ${(e as Error)?.message ?? 'unknown error'}`
    // An ASIN that could not be looked up stays unusable until it is (its error shows on the New
    // line)
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

// The record picked in the results: its ASIN in the region it was found in. The results stay,
// with the selection moved to it.
function pick(result: SearchResult) {
  if (!result.asin) return
  match.value.asin = result.asin.toUpperCase()
  match.value.region = result.region || match.value.region
  asinEntered.value = false
  setMatched({ ...result, region: match.value.region })
}

function forgetRecord() {
  match.value.picked = null
  match.value.asinError = null
  cancelSearch()
  state.value = idle()
}

// Another title or author describes another record: the ASIN no longer matches, so it is emptied
// until the book is searched and picked again (or its ASIN is typed)
function onSearchChanged(field: 'title' | 'author') {
  if (match.value.asin) clearedBy.value = field
  match.value.asin = ''
  asinEntered.value = false
  forgetRecord()
}

// Another region keeps the ASIN and looks it up there right away
function onRegionChanged() {
  forgetRecord()
  void search('asin')
}

// A typed ASIN is another record: it is no longer the new one nor the rejected one, and the
// title and author, which described the previous one, are emptied. Enter or Search looks it up.
function onAsinTyped() {
  asinEntered.value = false
  clearedBy.value = null
  match.value.query = ''
  match.value.author = ''
  forgetRecord()
}

function onAsinEntered() {
  if (!match.value.asin || match.value.picked) return
  asinEntered.value = true
  void search('asin')
}

watch(
  () => state.value.status === 'searching',
  (searching) => emit('searching', searching),
)

function revert() {
  cancelSearch()
  Object.assign(match.value, match.value.initial)
  clearedBy.value = null
  asinEntered.value = false
  forgetRecord()
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
/* The state bar, the whole height of the card: grey ignored, green matched, red error */
.match-card {
  border-left: 4px solid var(--match-bar-ignored, #6b7280);
}

.match-card-matched {
  border-left-color: var(--match-bar-matched, #22c55e);
}

.match-card-error {
  border-left-color: var(--match-bar-error, #ef4444);
}

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

.btn.match-revert-btn {
  min-width: 0;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 1.75rem;
  height: 1.75rem;
  padding: 0;
}

.match-meta {
  font-size: 0.8rem;
  color: #aaa;
}

/* Current and New read like the summary next to Revert; only their state is coloured */
.match-records {
  display: grid;
  grid-template-columns: auto 1fr;
  align-items: center;
  gap: 0.3rem 0.75rem;
  margin: 0.75rem 0 0;
  font-size: 0.8rem;
  color: #aaa;
}

.match-records dt {
  color: #aaa;
}

.match-records dd {
  color: #aaa;
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 0.4rem;
  margin: 0;
  min-width: 0;
}

.match-status {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  color: #22c55e;
}

.match-records dd.match-new-error {
  color: #ef4444;
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

.match-search-results {
  margin-top: 0.75rem;
}

.match-hint {
  margin: 0.5rem 0 0;
  font-size: 0.8rem;
  color: #ffb74d;
}
</style>
