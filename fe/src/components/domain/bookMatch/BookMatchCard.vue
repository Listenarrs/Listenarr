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
     or no result, red for a failed request, and breathes in blue while a search runs. While another
     book is searched, the whole card waits: nothing in it can be edited, searched or picked. -->
<template>
  <div
    class="form-control-card match-card"
    :class="[
      `match-card-${barState}`,
      { 'match-card-locked': searchLocked, 'match-card-narrow': narrow },
    ]"
    :data-match-state="barState"
    :title="searchLocked ? 'Another book is being searched' : undefined"
  >
    <fieldset class="match-lock" :disabled="searchLocked" data-testid="match-lock">
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

      <BookMatchFields
        ref="fieldsEl"
        v-model:match="match"
        :configured-region="configuredRegion"
        :action="fieldsAction"
        :can-search="canSearchFor(retryKind ?? searchKind)"
        :search-locked="searchLocked"
        :region-locked="regionLocked"
        :narrow="narrow"
        @edited="edited = true"
        @search="searchByPriority"
        @cancel="cancelSearch"
        @region-changed="onRegionChanged"
      />

      <BookMatchRecords :current="currentRecord" :picked="pickedAsRecord" :narrow="narrow" />

      <BookMatchResults
        ref="resultsEl"
        :results="state.results"
        :header="searchHeader(state)"
        :reason="noResultsReason(state)"
        :searching="state.status === 'searching'"
        :pending-text="pendingText"
        :locked="searchLocked"
        :revealing="reveal.revealing.value"
        :placeholders="placeholders"
        :selected="pickedRecord"
        :current="match.current"
        @select="pick"
      />
    </fieldset>

    <StatusBar
      :tone="BAR_TONES[barState]"
      :breathing="barState === 'searching'"
      :title="pendingText ?? undefined"
      data-testid="match-bar"
    />
    <span class="match-sr-only" role="status">
      <span v-if="pendingText" data-testid="match-search-pending">{{ pendingText }}</span>
    </span>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, inject, watch } from 'vue'
import { PhArrowCounterClockwise } from '@phosphor-icons/vue'
import StatusBar, { type StatusTone } from '@/components/base/StatusBar.vue'
import BookMatchFields from './BookMatchFields.vue'
import BookMatchRecords from './BookMatchRecords.vue'
import BookMatchResults from './BookMatchResults.vue'
import { useAbortableRequest } from '@/composables/useAbortableRequest'
import { useResultsReveal } from '@/composables/useResultsReveal'
import {
  asinValid,
  INVALID_ASIN_MESSAGE,
  bookSummary,
  isEdited,
  matchState,
  noRecordError,
  recordFromResult,
  addNewQuery,
  searchCacheKey,
  searchLikeAddNew,
  SEARCH_CACHE,
  type BookMatch,
  type BookRecord,
} from '@/utils/bookMatch'
import {
  SEARCH_TIMEOUT_MS,
  cardBarState,
  idleSearch,
  noResultsReason,
  pendingSearchText,
  recordsForAsin,
  searchErrorText,
  searchHeader,
  type CardBarState,
  type SearchKind,
  type SearchState,
  type Searched,
} from '@/utils/bookSearch'
import type { SearchResult } from '@/types'

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
    // The dialog is short of room (Repair measures its footer): the fields take the narrow
    // screen's layout before the screen itself is narrow
    narrow?: boolean
    // The results list's room kept from the start, at its full height whatever it holds. For a
    // card alone in its dialog (Find Match, Repair of one book), so nothing moves when a search
    // answers; with several books, the results open under each one when found
    placeholders?: boolean
  }>(),
  {
    searchLocked: false,
    excluded: false,
    searchOnMount: false,
    narrow: false,
    placeholders: false,
  },
)

const match = defineModel<BookMatch>('match', { required: true })

const emit = defineEmits<{
  // A search starts or ends, however it ends (except by unmounting the card)
  searching: [searching: boolean]
  // A record is now the book's new one, picked in the results or found for its ASIN
  matched: [record: SearchResult]
}>()

// The bar's colour for each state of the card
const BAR_TONES: Record<CardBarState, StatusTone> = {
  searching: 'progress',
  error: 'error',
  warning: 'warning',
  matched: 'success',
  ignored: 'idle',
}

const fieldsEl = ref<InstanceType<typeof BookMatchFields> | null>(null)
const resultsEl = ref<InstanceType<typeof BookMatchResults> | null>(null)
const state = ref<SearchState>(idleSearch())
// The search running, if any
const running = ref<Searched | null>(null)
// The kind of the last search started (answered, failed or canceled), and whether a field was
// edited since: Retry until one is
const lastSearchKind = ref<SearchKind | null>(null)
const edited = ref(false)
// The dialog's answered searches, when it keeps them
const searchCache = inject(SEARCH_CACHE, null)
const request = useAbortableRequest(SEARCH_TIMEOUT_MS)
const reveal = useResultsReveal()

const barState = computed(() =>
  cardBarState(state.value, matchState(match.value) === 'matched', props.excluded),
)

// A filled ASIN has priority: a search looks it up on its own, leaving the title and author out.
// With the ASIN field empty, it looks for the title and author
const searchKind = computed<SearchKind>(() => (match.value.asin.trim() ? 'asin' : 'title'))

// The book as it is today, as a record
const currentRecord = computed<BookRecord>(() => ({
  ...props.book,
  asin: match.value.current.asin,
  region: match.value.current.region,
}))

const pendingText = computed(() => pendingSearchText(running.value))

// The record picked, green in the results; the current one only wears a tag there
const pickedRecord = computed(() => {
  const { picked } = match.value
  return picked?.asin ? { asin: picked.asin, region: picked.region ?? match.value.region } : null
})

const pickedAsRecord = computed(() =>
  match.value.picked ? recordFromResult(match.value.picked, match.value.region) : null,
)

// Changing the region searches there: not while a search runs, here or on another book.
// One search at a time keeps answers from racing each other.
const regionLocked = computed(() => props.searchLocked || state.value.status === 'searching')

// Search becomes Retry once a search ran, whatever its outcome, until a field is edited: from then
// on it is Search, even if the field is changed back. Retry asks Audible again (a new request),
// where Search may be served by the dialog's answered searches
const retryKind = computed<SearchKind | null>(() => {
  if (edited.value || state.value.status === 'searching') return null
  return lastSearchKind.value
})

const fieldsAction = computed(() =>
  state.value.status === 'searching' ? 'cancel' : retryKind.value ? 'retry' : 'search',
)

function canSearchFor(kind: SearchKind): boolean {
  if (props.searchLocked || state.value.status === 'searching') return false
  // An ASIN that isn't one can still be searched: Search then says what an ASIN is
  return kind === 'asin' ? !!match.value.asin.trim() : !!match.value.query.trim()
}

function setMatched(record: SearchResult) {
  match.value.picked = record
  match.value.asinError = null
  emit('matched', record)
}

// The results on screen go: the empty list keeps their height
function keepResultsHeight() {
  if (state.value.results.length) resultsEl.value?.keepHeight()
}

// A search starts: with no results on screen, the empty list opens one result high
function openResultsRoom() {
  if (!state.value.results.length) resultsEl.value?.openRoom()
}

// The answer to a search, under the book: a title search's results, or the records found for the
// ASIN (none is an error)
function showResults(
  kind: SearchKind,
  searched: Searched,
  results: SearchResult[],
  requestMs: number | null,
) {
  // The records found for an ASIN are offered to pick from, like a title search's: nothing is
  // picked for the user
  const found = kind === 'title' ? results : recordsForAsin(results, searched)
  // An ASIN that found nothing (orange): searching the title there is what comes next
  if (kind === 'asin' && !found.length)
    match.value.asinError = noRecordError(searched.asin, searched.region)
  state.value = { status: 'done', kind, results: found, error: null, searched, requestMs }
  if (found.length) reveal.start()
}

// Search the title and author in the region, or look the ASIN up there; the results open under
// the book, to pick from. An ASIN its region has no record for is an error. Values already
// answered while the dialog is open are served without a request, unless fresh (Retry)
async function search(kind: SearchKind = 'title', fresh = false) {
  if (!canSearchFor(kind)) return
  if (kind === 'asin' && !asinValid(match.value)) {
    // A field error, not a request one: nothing is sent. The previous results go, so the error
    // shows in their room
    match.value.picked = null
    reveal.end()
    keepResultsHeight()
    openResultsRoom()
    state.value = {
      ...state.value,
      status: 'invalid',
      kind,
      results: [],
      error: INVALID_ASIN_MESSAGE,
    }
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
  const query = kind === 'asin' ? asin : addNewQuery(match.value)
  const cacheKey = searchCacheKey(query, region)
  lastSearchKind.value = kind
  edited.value = false
  // A new search starts over: the record picked in the previous results is no longer selected, and
  // New goes back to no record (nothing stays picked that the user can't see)
  match.value.picked = null
  match.value.asinError = null
  reveal.end()
  openResultsRoom()
  const answered = fresh ? undefined : searchCache?.get(cacheKey)
  if (answered) {
    showResults(kind, searched, answered, null)
    return
  }
  // The previous results stay on screen until this search answers
  state.value = { ...state.value, status: 'searching', kind, error: null }
  running.value = searched
  const startedAt = performance.now()
  const outcome = await request.run((signal) => searchLikeAddNew(query, region, signal))
  // A canceled search answers to nobody: the card already went back to its fields
  if (outcome.status === 'stale') return
  running.value = null
  if (outcome.status === 'done') {
    searchCache?.set(cacheKey, outcome.value)
    showResults(kind, searched, outcome.value, performance.now() - startedAt)
    return
  }
  const error = searchErrorText(outcome.error, outcome.timedOut)
  // An ASIN that could not be looked up stays unusable until it is (its error shows over the
  // results' room)
  if (kind === 'asin') match.value.asinError = error
  keepResultsHeight()
  state.value = { status: 'failed', kind, results: [], error, searched, requestMs: null }
}

// Stops the running search, if any, leaving the card as it is
function stopSearch() {
  request.stop()
  running.value = null
  reveal.end()
}

// Cancel search: the search stops, and the card says it was canceled (orange)
function cancelSearch() {
  if (state.value.status !== 'searching') return
  const canceled = running.value
  keepResultsHeight()
  stopSearch()
  // The previous results go: they don't answer the search canceled, which the header names
  state.value = {
    ...state.value,
    status: 'canceled',
    results: [],
    error: 'Search canceled.',
    searched: canceled ?? state.value.searched,
  }
}

// The record picked in the results: its ASIN in the region it was found in. The results stay,
// with the selection moved to it. Clicking the selected row again unpicks it: no new record.
function pick(result: SearchResult) {
  // Not while a search runs (the results shown are the previous search's), nor while new ones are
  // revealed
  if (
    !result.asin ||
    props.searchLocked ||
    state.value.status === 'searching' ||
    reveal.revealing.value
  )
    return
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

// Typing only prepares the next search: the results, the record and any error stay until it runs.
// Search, Enter in any field and a region change all start the same search: the ASIN when it is
// filled, the title and author otherwise. With no field edited since the last search, that is
// Retry: a new request
function searchByPriority() {
  const retry = retryKind.value
  void (retry ? search(retry, true) : search(searchKind.value))
}

// Another region keeps the fields and searches there right away, by the same priority: the one
// change that searches by itself
function onRegionChanged() {
  forgetRecord()
  // A field changed: a search by priority, not a Retry of the previous one
  edited.value = true
  searchByPriority()
}

function revert() {
  Object.assign(match.value, match.value.initial)
  edited.value = true
  forgetRecord()
}

watch(
  () => state.value.status === 'searching',
  (searching) => emit('searching', searching),
)

onMounted(() => {
  if (!props.searchOnMount) return
  fieldsEl.value?.focusTitle()
  void search()
})

onBeforeUnmount(stopSearch)

defineExpose({ search, cancelSearch })
</script>

<style scoped>
/* Room at the bottom for the state bar */
.form-control-card.match-card {
  position: relative;
  overflow: hidden;
  padding-bottom: calc(1rem + 3px);
}

/* Only disables what it holds: the card keeps its own layout */
.match-lock {
  display: contents;
}

.match-card-locked {
  opacity: 0.6;
}

.match-card-locked :deep(.result-item) {
  cursor: not-allowed;
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
</style>
