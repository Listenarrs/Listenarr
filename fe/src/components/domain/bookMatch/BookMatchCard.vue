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
      { 'match-card-locked': isSearchLocked, 'match-card-narrow': isNarrow },
    ]"
    :data-match-state="barState"
    :title="isSearchLocked ? 'Another book is being searched' : undefined"
  >
    <fieldset class="match-lock" :disabled="isSearchLocked" data-testid="match-lock">
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
            :disabled="!isEdited(bookMatch)"
            @click.prevent.stop="revert"
          >
            <PhArrowCounterClockwise :size="14" />
          </button>
        </span>
      </div>

      <BookMatchFields
        ref="fieldsRef"
        v-model:book-match="bookMatch"
        :configured-region="configuredRegion"
        :search-action="searchAction"
        :can-search="canSearchFor(retryKind ?? searchKind)"
        :is-search-locked="isSearchLocked"
        :is-region-locked="isRegionLocked"
        :is-narrow="isNarrow"
        @edited="isEditedSinceSearch = true"
        @search="searchByPriority"
        @cancel="cancelSearch"
        @region-changed="onRegionChanged"
      />

      <BookMatchRecords
        :current-record="currentRecord"
        :picked-record="pickedRecord"
        :is-narrow="isNarrow"
      />

      <BookMatchResults
        ref="resultsRef"
        :search-results="searchState.searchResults"
        :header="searchHeader(searchState)"
        :no-results-reason="noResultsReason(searchState)"
        :is-searching="searchState.status === 'searching'"
        :pending-text="pendingText"
        :is-search-locked="isSearchLocked"
        :is-revealing="resultsReveal.isRevealing.value"
        :should-keep-results-room="shouldKeepResultsRoom"
        :selected-record-key="pickedRecordKey"
        :current-record-key="bookMatch.currentRecordKey"
        :is-provider-record-required="isProviderRecordRequired"
        @select="pick"
      />
    </fieldset>

    <StatusBar
      :tone="BAR_TONE_BY_STATE[barState]"
      :is-breathing="barState === 'searching'"
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
  isAsinValid,
  isResultOfRecord,
  type RecordKey,
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
  type SearchParameters,
} from '@/utils/bookSearch'
import type { SearchResult } from '@/types'

const props = withDefaults(
  defineProps<{
    configuredRegion: string
    // The book as it is today: with its current ASIN and region, the record next to Revert and on
    // Current
    book: Pick<BookRecord, 'title' | 'subtitle' | 'authors'>
    // Another book is being searched: this one waits for it
    isSearchLocked?: boolean
    // Left out by the user (Repair's checkbox): a matched book then shows as ignored
    isExcluded?: boolean
    // Search as soon as the card shows, and focus the title
    shouldSearchOnMount?: boolean
    // Repair: the new record must be one a metadata provider can read again (its ASIN, Audible
    // today); Find Match also takes a record without one (another source), as Library Import can
    // add a book from it
    isProviderRecordRequired?: boolean
    // The dialog is short of room (Repair measures its footer): the fields take the narrow
    // screen's layout before the screen itself is narrow
    isNarrow?: boolean
    // The results list's room kept from the start, at its full height whatever it holds. For a
    // card alone in its dialog (Find Match, Repair of one book), so nothing moves when a search
    // answers; with several books, the results open under each one when found
    shouldKeepResultsRoom?: boolean
  }>(),
  {
    isSearchLocked: false,
    isExcluded: false,
    shouldSearchOnMount: false,
    isProviderRecordRequired: false,
    isNarrow: false,
    shouldKeepResultsRoom: false,
  },
)

const bookMatch = defineModel<BookMatch>('bookMatch', { required: true })

const emit = defineEmits<{
  // A search starts or ends, however it ends (except by unmounting the card)
  searching: [isSearching: boolean]
  // A record is now the book's new one, picked in the results or found for its ASIN
  matched: [searchResult: SearchResult]
}>()

// The bar's colour for each state of the card
const BAR_TONE_BY_STATE: Record<CardBarState, StatusTone> = {
  searching: 'progress',
  error: 'error',
  warning: 'warning',
  matched: 'success',
  ignored: 'idle',
}

const fieldsRef = ref<InstanceType<typeof BookMatchFields> | null>(null)
const resultsRef = ref<InstanceType<typeof BookMatchResults> | null>(null)
const searchState = ref<SearchState>(idleSearch())
// The search running, if any
const runningSearch = ref<SearchParameters | null>(null)
// The kind of the last search started (answered, failed or canceled), and whether a field was
// edited since: Retry until one is
const lastSearchKind = ref<SearchKind | null>(null)
const isEditedSinceSearch = ref(false)
// The dialog's answered searches, when it keeps them
const searchCache = inject(SEARCH_CACHE, null)
const searchRequest = useAbortableRequest(SEARCH_TIMEOUT_MS)
const resultsReveal = useResultsReveal()

const barState = computed(() =>
  cardBarState(searchState.value, matchState(bookMatch.value) === 'matched', props.isExcluded),
)

// A filled ASIN has priority: a search looks it up on its own, leaving the title and author out.
// With the ASIN field empty, it looks for the title and author
const searchKind = computed<SearchKind>(() => (bookMatch.value.asin.trim() ? 'asin' : 'title'))

// The book as it is today, as a record
const currentRecord = computed<BookRecord>(() => ({
  ...props.book,
  asin: bookMatch.value.currentRecordKey.asin,
  region: bookMatch.value.currentRecordKey.region,
}))

const pendingText = computed(() => pendingSearchText(runningSearch.value))

// The record picked, green in the results; the current one only wears a tag there
const pickedRecordKey = computed<RecordKey | null>(() => {
  const { pickedSearchResult } = bookMatch.value
  if (!pickedSearchResult) return null
  return pickedSearchResult.asin
    ? { asin: pickedSearchResult.asin, region: pickedSearchResult.region ?? bookMatch.value.region }
    : { asin: '', id: pickedSearchResult.id ?? pickedSearchResult.title ?? null }
})

const pickedRecord = computed(() =>
  bookMatch.value.pickedSearchResult
    ? recordFromResult(bookMatch.value.pickedSearchResult, bookMatch.value.region)
    : null,
)

// Changing the region searches there: not while a search runs, here or on another book.
// One search at a time keeps answers from racing each other.
const isRegionLocked = computed(
  () => props.isSearchLocked || searchState.value.status === 'searching',
)

// Search becomes Retry once a search ran, whatever its outcome, until a field is edited: from then
// on it is Search, even if the field is changed back. Retry asks Audible again (a new request),
// where Search may be served by the dialog's answered searches
const retryKind = computed<SearchKind | null>(() => {
  if (isEditedSinceSearch.value || searchState.value.status === 'searching') return null
  return lastSearchKind.value
})

const searchAction = computed(() =>
  searchState.value.status === 'searching' ? 'cancel' : retryKind.value ? 'retry' : 'search',
)

function canSearchFor(kind: SearchKind): boolean {
  if (props.isSearchLocked || searchState.value.status === 'searching') return false
  // An ASIN that isn't one can still be searched: Search then says what an ASIN is
  return kind === 'asin' ? !!bookMatch.value.asin.trim() : !!bookMatch.value.query.trim()
}

function setMatched(searchResult: SearchResult) {
  bookMatch.value.pickedSearchResult = searchResult
  bookMatch.value.asinError = null
  emit('matched', searchResult)
}

// The results on screen go: the empty list keeps their height
function keepResultsHeight() {
  if (searchState.value.searchResults.length) resultsRef.value?.keepHeight()
}

// A search starts: with no results on screen, the empty list opens one result high
function openResultsRoom() {
  if (!searchState.value.searchResults.length) resultsRef.value?.openRoom()
}

// The answer to a search, under the book: a title search's results, or the records found for the
// ASIN (none is an error)
function showResults(
  kind: SearchKind,
  searchParameters: SearchParameters,
  searchResults: SearchResult[],
  requestMs: number | null,
) {
  // The records found for an ASIN are offered to pick from, like a title search's: nothing is
  // picked for the user
  const foundResults =
    kind === 'title' ? searchResults : recordsForAsin(searchResults, searchParameters)
  // An ASIN that found nothing (orange): searching the title there is what comes next
  if (kind === 'asin' && !foundResults.length)
    bookMatch.value.asinError = noRecordError(searchParameters.asin, searchParameters.region)
  searchState.value = {
    status: 'done',
    kind,
    searchResults: foundResults,
    error: null,
    searchParameters,
    requestMs,
  }
  if (foundResults.length) resultsReveal.start()
}

// Search the title and author in the region, or look the ASIN up there; the results open under
// the book, to pick from. An ASIN its region has no record for is an error. Values already
// answered while the dialog is open are served without a request, unless fresh (Retry)
async function search(kind: SearchKind = 'title', fresh = false) {
  if (!canSearchFor(kind)) return
  if (kind === 'asin' && !isAsinValid(bookMatch.value)) {
    // A field error, not a request one: nothing is sent. The previous results go, so the error
    // shows in their room
    bookMatch.value.pickedSearchResult = null
    resultsReveal.end()
    keepResultsHeight()
    openResultsRoom()
    searchState.value = {
      ...searchState.value,
      status: 'invalid',
      kind,
      searchResults: [],
      error: INVALID_ASIN_MESSAGE,
    }
    return
  }
  const asin = bookMatch.value.asin.trim().toUpperCase()
  const region = bookMatch.value.region
  const searchParameters: SearchParameters = {
    kind,
    asin,
    title: bookMatch.value.query.trim(),
    author: bookMatch.value.author.trim(),
    region,
  }
  const query = kind === 'asin' ? asin : addNewQuery(bookMatch.value)
  const cacheKey = searchCacheKey(query, region)
  lastSearchKind.value = kind
  isEditedSinceSearch.value = false
  // A new search starts over: the record picked in the previous results is no longer selected, and
  // New goes back to no record (nothing stays picked that the user can't see)
  bookMatch.value.pickedSearchResult = null
  bookMatch.value.asinError = null
  resultsReveal.end()
  openResultsRoom()
  const cachedSearchResults = fresh ? undefined : searchCache?.get(cacheKey)
  if (cachedSearchResults) {
    showResults(kind, searchParameters, cachedSearchResults, null)
    return
  }
  // The previous results stay on screen until this search answers
  searchState.value = { ...searchState.value, status: 'searching', kind, error: null }
  runningSearch.value = searchParameters
  const startedAt = performance.now()
  const searchOutcome = await searchRequest.run((signal) => searchLikeAddNew(query, region, signal))
  // A canceled search answers to nobody: the card already went back to its fields
  if (searchOutcome.status === 'stale') return
  runningSearch.value = null
  if (searchOutcome.status === 'done') {
    searchCache?.set(cacheKey, searchOutcome.value)
    showResults(kind, searchParameters, searchOutcome.value, performance.now() - startedAt)
    return
  }
  const errorText = searchErrorText(searchOutcome.error, searchOutcome.hasTimedOut)
  // An ASIN that could not be looked up stays unusable until it is (its error shows over the
  // results' room)
  if (kind === 'asin') bookMatch.value.asinError = errorText
  keepResultsHeight()
  searchState.value = {
    status: 'failed',
    kind,
    searchResults: [],
    error: errorText,
    searchParameters,
    requestMs: null,
  }
}

// Stops the running search, if any, leaving the card as it is
function stopSearch() {
  searchRequest.stop()
  runningSearch.value = null
  resultsReveal.end()
}

// Cancel search: the search stops, and the card says it was canceled (orange)
function cancelSearch() {
  if (searchState.value.status !== 'searching') return
  const canceledSearch = runningSearch.value
  keepResultsHeight()
  stopSearch()
  // The previous results go: they don't answer the search canceled, which the header names
  searchState.value = {
    ...searchState.value,
    status: 'canceled',
    searchResults: [],
    error: 'Search canceled.',
    searchParameters: canceledSearch ?? searchState.value.searchParameters,
  }
}

// The record picked in the results: its ASIN in the region it was found in. The results stay,
// with the selection moved to it. Clicking the selected row again unpicks it: no new record.
function pick(searchResult: SearchResult) {
  // Not while a search runs (the results shown are the previous search's), nor while new ones are
  // revealed
  if (
    (!searchResult.asin && props.isProviderRecordRequired) ||
    props.isSearchLocked ||
    searchState.value.status === 'searching' ||
    resultsReveal.isRevealing.value
  )
    return
  // The picked record again: unpicked
  if (isResultOfRecord(searchResult, pickedRecordKey.value)) {
    bookMatch.value.pickedSearchResult = null
    return
  }
  // The record only: the fields keep the search as typed, so a wrong pick undone leaves them as
  // they were
  setMatched({ ...searchResult, region: searchResult.region || bookMatch.value.region })
}

// The record and its error no longer apply; the results stay until a new search replaces them
function forgetRecord() {
  bookMatch.value.pickedSearchResult = null
  bookMatch.value.asinError = null
  stopSearch()
  searchState.value = {
    ...searchState.value,
    status: searchState.value.searchResults.length ? 'done' : 'idle',
    error: null,
  }
}

// Typing only prepares the next search: the results, the record and any error stay until it runs.
// Search, Enter in any field and a region change all start the same search: the ASIN when it is
// filled, the title and author otherwise. With no field edited since the last search, that is
// Retry: a new request
function searchByPriority() {
  const kindToRetry = retryKind.value
  void (kindToRetry ? search(kindToRetry, true) : search(searchKind.value))
}

// Another region keeps the fields and searches there right away, by the same priority: the one
// change that searches by itself
function onRegionChanged() {
  forgetRecord()
  // A field changed: a search by priority, not a Retry of the previous one
  isEditedSinceSearch.value = true
  searchByPriority()
}

function revert() {
  Object.assign(bookMatch.value, bookMatch.value.initialValues)
  isEditedSinceSearch.value = true
  forgetRecord()
}

watch(
  () => searchState.value.status === 'searching',
  (isSearching) => emit('searching', isSearching),
)

onMounted(() => {
  if (!props.shouldSearchOnMount) return
  fieldsRef.value?.focusTitle()
  // By the same priority as Search: a filled ASIN on its own
  void search(searchKind.value)
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
