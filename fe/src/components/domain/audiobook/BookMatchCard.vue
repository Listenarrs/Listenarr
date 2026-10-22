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
     The bar along its bottom tells its state: grey ignored, green matched, orange for a field error
     or no result, red for a failed request, and breathes in blue while a search runs. -->
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
          {{ bookSummary(currentRecord) }}
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
        <span class="match-clearable">
          <input
            v-model.trim="match.asin"
            class="form-input"
            maxlength="10"
            data-testid="match-asin"
            @input="onAsinTyped"
            @keydown.enter.prevent="onAsinEntered"
          />
          <button
            v-if="match.asin"
            type="button"
            class="match-clear-btn"
            title="Empty the ASIN field"
            aria-label="Empty the ASIN field"
            data-testid="match-asin-clear"
            @click.prevent="clearField('asin')"
          >
            <PhBackspace :size="18" />
          </button>
        </span>
      </label>
      <label class="match-field">
        <span class="match-field-label">Title</span>
        <span class="match-clearable">
          <input
            ref="titleEl"
            v-model="match.query"
            class="form-input"
            data-testid="match-title"
            @input="onEdited"
            @keydown.enter.prevent="search(searchKind)"
          />
          <button
            v-if="match.query"
            type="button"
            class="match-clear-btn"
            title="Empty the Title field"
            aria-label="Empty the Title field"
            data-testid="match-title-clear"
            @click.prevent="clearField('query')"
          >
            <PhBackspace :size="18" />
          </button>
        </span>
      </label>
      <label class="match-field">
        <span class="match-field-label">Author</span>
        <span class="match-clearable">
          <input
            v-model="match.author"
            class="form-input"
            data-testid="match-author"
            @input="onEdited"
            @keydown.enter.prevent="search(searchKind)"
          />
          <button
            v-if="match.author"
            type="button"
            class="match-clear-btn"
            title="Empty the Author field"
            aria-label="Empty the Author field"
            data-testid="match-author-clear"
            @click.prevent="clearField('author')"
          >
            <PhBackspace :size="18" />
          </button>
        </span>
      </label>
      <label class="match-field">
        <span class="match-field-label">Region</span>
        <select
          v-model="match.region"
          class="form-input"
          data-testid="match-region"
          :disabled="regionLocked"
          :title="
            regionLocked
              ? 'Wait for the search to end: changing the region searches'
              : regionLabel(match.region)
          "
          @change="onRegionChanged"
        >
          <option v-for="o in regionOptions" :key="o.value" :value="o.value" :title="o.label">
            {{ o.value.toUpperCase() }}
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
        {{ bookSummary(currentRecord) || 'None' }}
      </dd>
      <dt>New</dt>
      <dd v-if="match.picked" class="match-new-picked" data-testid="match-picked">
        {{ bookSummary(recordFromResult(match.picked, match.region)) }}
        <span class="match-status">
          <PhCheckCircle :size="14" weight="fill" />
          checked
        </span>
      </dd>
      <dd
        v-else-if="match.asinError"
        :class="state.status === 'failed' ? 'match-new-error' : 'match-new-warning'"
        data-testid="match-asin-error"
      >
        <PhXCircle :size="14" weight="fill" />
        <span>{{ match.asinError }}</span>
      </dd>
      <dd v-else data-testid="match-none">
        No new record yet: search the book and pick it, or type its ASIN
      </dd>
    </dl>

    <!-- The rules, where they apply -->

    <!-- The search under the book: its message (failed in red; invalid or canceled in orange), and
         its results, which stay until a new search on this book answers. A running search only
         shows on the bottom bar, so nothing moves under the book; its text is for screen
         readers. -->
    <p
      v-if="
        (state.status === 'failed' && state.kind === 'title') ||
        state.status === 'invalid' ||
        state.status === 'canceled'
      "
      class="match-search-state"
      :class="state.status === 'failed' ? 'match-search-error' : 'match-search-warning'"
      data-testid="match-search-error"
    >
      <PhWarningCircle :size="16" />
      <span>{{ state.error }}</span>
    </p>
    <p
      v-if="state.results.length && state.searched"
      class="match-results-header"
      data-testid="match-results-header"
    >
      Results for {{ describeSearch(state.searched) }} in
      {{ regionLabel(state.searched.region) }}
    </p>
    <FindMatchResults
      v-if="state.results.length"
      class="match-search-results"
      :results="state.results"
      :selected="pickedRecord"
      :current="match.current"
      @select="pick"
    />
    <p
      v-else-if="state.status === 'done' && state.kind === 'title' && state.searched"
      class="match-search-state match-search-warning"
      data-testid="match-search-empty"
    >
      No results for {{ describeSearch(state.searched) }} in
      {{ regionLabel(state.searched.region) }}.
    </p>

    <div class="match-bar" :title="pendingText ?? undefined" />
    <span class="match-sr-only" role="status">
      <span v-if="pendingText" data-testid="match-search-pending">{{ pendingText }}</span>
    </span>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import {
  PhArrowCounterClockwise,
  PhCheckCircle,
  PhBackspace,
  PhMagnifyingGlass,
  PhWarningCircle,
  PhX,
  PhXCircle,
} from '@phosphor-icons/vue'
import FindMatchResults from './FindMatchResults.vue'
import { orderedSearchRegionOptions } from '@/utils/languageMapping'
import {
  FALLBACK_REGION,
  asinValid,
  INVALID_ASIN_MESSAGE,
  bookSummary,
  isEdited,
  matchState,
  noRecordError,
  regionLabel,
  recordFromResult,
  addNewQuery,
  searchLikeAddNew,
  type BookMatch,
  type BookRecord,
} from '@/utils/bookMatch'
import type { SearchResult } from '@/types'

// A search slower than this is stopped and reported, with Retry
const SEARCH_TIMEOUT_MS = 30_000

const props = withDefaults(
  defineProps<{
    configuredRegion: string
    // The book as it is today: with its current ASIN and region, the record next to Revert and on
    // Current
    book: Pick<BookRecord, 'title' | 'subtitle' | 'authors'>
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

// invalid: Search was asked for something it can't search (an ASIN that isn't one), a field error;
// canceled: the user stopped the search. results: the last answer, kept until a new one replaces it
interface SearchState {
  status: 'idle' | 'searching' | 'done' | 'failed' | 'invalid' | 'canceled'
  kind: SearchKind
  results: SearchResult[]
  error: string | null
  // What the results shown were searched with, for their header
  searched: Searched | null
}

// The parameters of one search, taken when it starts: typing afterwards doesn't change them
interface Searched {
  kind: SearchKind
  asin: string
  title: string
  author: string
  region: string
}

// What a search looked for: the ASIN (which has priority when filled), or the title and author
function describeSearch(searched: Searched): string {
  if (searched.kind === 'asin') return `ASIN "${searched.asin}"`
  return `"${searched.title}"${searched.author ? ` by "${searched.author}"` : ''}`
}

const titleEl = ref<HTMLInputElement | null>(null)
const state = ref<SearchState>(idle())
// The search running, if any
const running = ref<Searched | null>(null)
// A field changed since the last search: Retry goes back to Search
const edited = ref(false)
let controller: AbortController | null = null

// Every Audible region stays available, in canary's order (as in Add to Library): the book's
// region, then the preferred (configured) one and the US, then the others alphabetically by name
// (each option's tooltip; the field shows the codes)
const regionOptions = computed(() =>
  orderedSearchRegionOptions([match.value.initial.region, props.configuredRegion, FALLBACK_REGION]),
)

// Blue while searching; red when the request failed; orange for a field error or a search that
// found nothing; green once matched (grey when left out); grey otherwise
// Matched (green) is only a picked record, the one state Repair counts; a search that found records
// with nothing picked yet stays ignored (grey)
const barState = computed(() => {
  const { status, results } = state.value
  if (status === 'searching') return 'searching'
  if (status === 'failed') return 'error'
  if (status === 'invalid' || status === 'canceled' || (status === 'done' && results.length === 0))
    return 'warning'
  return matchState(match.value) === 'matched' && !props.excluded ? 'matched' : 'ignored'
})

// A filled ASIN has priority: Search looks it up on its own, leaving the title and author out.
// With the ASIN field empty, Search looks for the title and author
const searchKind = computed<SearchKind>(() => (match.value.asin.trim() ? 'asin' : 'title'))

// The book as it is today, as a record
const currentRecord = computed<BookRecord>(() => ({
  ...props.book,
  asin: match.value.current.asin,
  region: match.value.current.region,
}))

// What the running search does, on the bar's tooltip and for screen readers
const pendingText = computed(() =>
  running.value
    ? `Searching ${describeSearch(running.value)} in ${regionLabel(running.value.region)}…`
    : null,
)

// The record picked, green in the results; the current one only wears a tag there
const pickedRecord = computed(() => {
  const { picked } = match.value
  return picked?.asin ? { asin: picked.asin, region: picked.region ?? match.value.region } : null
})

// Changing the region looks the ASIN up there: not while a search runs, here or on another book.
// One search at a time keeps answers from racing each other.
const regionLocked = computed(() => props.searchLocked || state.value.status === 'searching')

// The last search, with the values still in the fields, found nothing or failed: Search becomes
// Retry. Editing a field forgets that search, and Search comes back. An ASIN its region has no
// record for keeps Search: searching the title there is what comes next.
const retryKind = computed<SearchKind | null>(() => {
  if (edited.value) return null
  const { status, results, kind } = state.value
  if (status === 'failed' || status === 'canceled') return kind
  if (status === 'done' && results.length === 0 && kind === 'title') return kind
  return null
})

function canSearchFor(kind: SearchKind): boolean {
  if (props.searchLocked || state.value.status === 'searching') return false
  // An ASIN that isn't one can still be searched: Search then says what an ASIN is
  return kind === 'asin' ? !!match.value.asin.trim() : !!match.value.query.trim()
}

function idle(): SearchState {
  return { status: 'idle', kind: 'title', results: [], error: null, searched: null }
}

function setMatched(record: SearchResult) {
  match.value.picked = record
  match.value.asinError = null
  emit('matched', record)
}

// Search the title and author in the region, or look the ASIN up there; the results open under
// the book, to pick from. An ASIN its region has no record for is an error
async function search(kind: SearchKind = 'title') {
  if (!canSearchFor(kind)) return
  if (kind === 'asin' && !asinValid(match.value)) {
    // A field error, not a request one: nothing is sent
    state.value = { ...state.value, status: 'invalid', kind, error: INVALID_ASIN_MESSAGE }
    return
  }
  const asin = match.value.asin.trim().toUpperCase()
  const region = match.value.region
  const searched: Searched = {
    kind,
    asin,
    title: match.value.query.trim(),
    author: match.value.author.trim(),
    region,
  }
  const current = new AbortController()
  controller = current
  let timedOut = false
  const timer = setTimeout(() => {
    timedOut = true
    current.abort()
  }, SEARCH_TIMEOUT_MS)
  // The previous results stay on screen until this search answers
  state.value = { ...state.value, status: 'searching', kind, error: null }
  running.value = searched
  edited.value = false
  // A new search starts over: the record picked in the previous results is no longer selected, and
  // New goes back to no record (nothing stays picked that the user can't see)
  match.value.picked = null
  match.value.asinError = null
  try {
    const query = kind === 'asin' ? asin : addNewQuery(match.value)
    const results = await searchLikeAddNew(query, region, current.signal)
    // A canceled search answers to nobody: the card already went back to its fields
    if (controller !== current) return
    if (kind === 'title') {
      state.value = { status: 'done', kind, results, error: null, searched }
      return
    }
    const found = results
      .filter((r) => r.asin?.toUpperCase() === asin)
      .map((r) => ({ ...r, region: r.region || region }))
    // The records found are offered to pick from, like a title search's: nothing is picked for
    // the user
    if (found.length) {
      state.value = { status: 'done', kind, results: found, error: null, searched }
    } else {
      // Found nothing (orange): a title search is what comes next, so Search stays
      match.value.asinError = noRecordError(asin, region)
      state.value = { status: 'done', kind, results: [], error: null, searched }
    }
  } catch (e) {
    if (controller !== current) return
    const error = timedOut
      ? `The search took longer than ${SEARCH_TIMEOUT_MS / 1000} seconds and was stopped.`
      : `The search failed: ${(e as Error)?.message ?? 'unknown error'}`
    // An ASIN that could not be looked up stays unusable until it is (its error shows on the New
    // line)
    if (kind === 'asin') match.value.asinError = error
    state.value = { status: 'failed', kind, results: [], error, searched }
  } finally {
    clearTimeout(timer)
    if (controller === current) {
      controller = null
      running.value = null
    }
  }
}

// Back to the fields, nothing to report
// Stops the running search, if any, leaving the card as it is
function stopSearch() {
  controller?.abort()
  controller = null
  running.value = null
}

// Cancel search: the search stops, its results stay, and the card says it was canceled (orange)
function cancelSearch() {
  if (state.value.status !== 'searching') return
  stopSearch()
  state.value = { ...state.value, status: 'canceled', error: 'Search canceled.' }
}

// The record picked in the results: its ASIN in the region it was found in. The results stay,
// with the selection moved to it. Clicking the selected row again unpicks it: no new record.
function pick(result: SearchResult) {
  if (!result.asin) return
  const picked = pickedRecord.value
  if (
    picked &&
    picked.asin.toUpperCase() === result.asin.toUpperCase() &&
    (!result.region || result.region === picked.region)
  ) {
    match.value.picked = null
    return
  }
  // The record only: the fields keep the search as typed, so a wrong pick undone leaves them as
  // they were
  setMatched({ ...result, region: result.region || match.value.region })
}

// The record and its error no longer apply; the results stay until a new search replaces them
function forgetRecord() {
  match.value.picked = null
  match.value.asinError = null
  stopSearch()
  state.value = {
    ...state.value,
    status: state.value.results.length ? 'done' : 'idle',
    error: null,
  }
}

// Typing only prepares the next search: the results, the record and any error stay until it runs
function onEdited() {
  edited.value = true
}

// Another region keeps the ASIN and looks it up there right away: the one change that searches by
// itself
function onRegionChanged() {
  forgetRecord()
  void search('asin')
}

// The erase buttons of the ASIN, Title and Author fields: like typing, emptying a field only
// prepares the next search (an empty ASIN lets Search look for the title and author)
function clearField(field: 'asin' | 'query' | 'author') {
  match.value[field] = ''
  edited.value = true
}

// A typed ASIN only prepares the next search too: Enter or Search then looks it up
function onAsinTyped() {
  edited.value = true
}

function onAsinEntered() {
  if (!match.value.asin.trim()) return
  void search('asin')
}

watch(
  () => state.value.status === 'searching',
  (searching) => emit('searching', searching),
)

function revert() {
  Object.assign(match.value, match.value.initial)
  edited.value = false
  forgetRecord()
}

onMounted(() => {
  if (!props.searchOnMount) return
  titleEl.value?.focus()
  titleEl.value?.select()
  void search()
})

onBeforeUnmount(stopSearch)

defineExpose({ search, cancelSearch })
</script>

<style scoped>
/* The state bar along the bottom of the card, with the library's colours and thickness (its
   cards' bottom bar): grey ignored, green matched, red error, and blue breathing while a search
   runs (a search has no progress to show) */
.form-control-card.match-card {
  position: relative;
  overflow: hidden;
  padding-bottom: calc(1rem + 3px);
}

.match-bar {
  position: absolute;
  left: 0;
  right: 0;
  bottom: 0;
  height: 3px;
  background: var(--status-idle);
}

.match-card-matched .match-bar {
  background: var(--status-success);
}

.match-card-warning .match-bar {
  background: var(--status-warning);
}

.match-card-error .match-bar {
  background: var(--status-error);
}

.match-card-searching .match-bar {
  background: var(--status-progress);
  animation: match-bar-breathe 1.6s ease-in-out infinite alternate;
}

@keyframes match-bar-breathe {
  from {
    opacity: 0.2;
  }
  to {
    opacity: 1;
  }
}

@media (prefers-reduced-motion: reduce) {
  .match-card-searching .match-bar {
    animation: none;
  }
}

.match-sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
  white-space: nowrap;
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
  color: #e0e0e0;
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

/* Current and New read like the summary next to Revert; only their state is coloured. Each keeps
   room for the lines a record summary usually takes (one, two on a phone), so the card doesn't
   grow or shrink when the new record changes */
.match-records {
  display: grid;
  grid-template-columns: auto 1fr;
  align-items: start;
  line-height: 1.4;
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
  align-content: flex-start;
  flex-wrap: wrap;
  gap: 0.4rem;
  margin: 0;
  min-width: 0;
  min-height: 1.4em;
}

/* The new record flows as text, so "checked" follows its last line instead of taking a line of its own */
.match-records dd.match-new-picked {
  display: block;
}

.match-status {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  margin-left: 0.4rem;
  vertical-align: middle;
  white-space: nowrap;
  color: var(--status-success);
}

.match-records dd.match-new-warning {
  color: var(--status-warning);
}

.match-records dd.match-new-error {
  color: var(--status-error);
}

/* ASIN, Title, Author, Region and Search always share the whole row */
.match-fields {
  display: grid;
  grid-template-columns:
    minmax(10.5rem, 1.2fr) minmax(0, 2.5fr) minmax(0, 1.5fr) minmax(5rem, 0.5fr)
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

/* The erase button sits inside its field, on the right, while the field holds something. Its room
   is always kept, so showing it doesn't change the field's size nor move the others */
.match-clearable {
  position: relative;
  display: block;
}

.match-clearable .form-input {
  padding-right: 2.2rem;
}

.match-clear-btn {
  position: absolute;
  top: 50%;
  right: 0.4rem;
  transform: translateY(-50%);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  padding: 0.25rem;
  border: none;
  border-radius: 4px;
  background: transparent;
  color: #aaa;
  cursor: pointer;
}

.match-clear-btn:hover {
  color: var(--status-error);
  background: rgba(255, 255, 255, 0.06);
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

  .match-records dd {
    min-height: 2.8em;
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

.match-search-warning {
  color: var(--status-warning);
}

.match-search-error {
  color: var(--status-error);
}

.match-results-header {
  margin: 0.75rem 0 0;
  font-size: 0.8rem;
  color: #aaa;
}

.match-search-results {
  margin-top: 0.4rem;
}

.match-hint {
  margin: 0.5rem 0 0;
  font-size: 0.8rem;
  color: #ffb74d;
}
</style>
