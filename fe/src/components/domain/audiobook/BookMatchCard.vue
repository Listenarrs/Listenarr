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
<!-- One book and the Audible record it should point at: a search box read like Add New's and a
     region, the search under the book, and its current and new records (Repair Books and Find
     Match).
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

      <div class="match-fields">
        <label class="match-field">
          <span class="match-field-label">Search</span>
          <span class="match-clearable">
            <input
              ref="queryEl"
              v-model="match.query"
              class="form-input"
              placeholder="Title, ASIN, or TITLE:… AUTHOR:… SERIES:…"
              title="As in Add New: a title, an ASIN, or TITLE:, AUTHOR:, ASIN:, SERIES: and ISBN: tokens"
              data-testid="match-query"
              @input="onEdited"
              @keydown.enter.prevent="searchByPriority"
            />
            <button
              v-if="match.query"
              type="button"
              class="match-clear-btn"
              title="Empty the Search field"
              aria-label="Empty the Search field"
              data-testid="match-query-clear"
              @click.prevent="clearQuery"
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
          v-else-if="retry"
          type="button"
          class="btn btn-secondary match-search-btn"
          data-testid="match-retry"
          :disabled="!canSearch"
          :title="
            searchLocked
              ? 'Another book is being searched'
              : 'Ask Audible again with the same values (a new request)'
          "
          @click="searchByPriority"
        >
          <PhArrowCounterClockwise :size="16" />
          Retry
        </button>
        <button
          v-else
          type="button"
          class="btn btn-secondary match-search-btn"
          data-testid="match-search"
          :disabled="!canSearch"
          :title="
            searchLocked
              ? 'Another book is being searched'
              : 'The search Add New uses, with the same rules and priorities'
          "
          @click="searchByPriority"
        >
          <PhMagnifyingGlass :size="16" />
          Search
        </button>
      </div>

      <!-- What the book points at today, and the record picked in the results shown (no record
           otherwise: why a search found none is said over the results' room) -->
      <dl class="match-records">
        <dt>Current</dt>
        <!-- With a new record picked, the parts it replaces in orange -->
        <dd data-testid="match-current">
          <BookRecordDiff
            v-if="match.picked"
            :record="currentRecord"
            :base="recordFromResult(match.picked, match.region)"
            replaced
          />
          <template v-else>{{ bookSummary(currentRecord) || 'None' }}</template>
        </dd>
        <dt>New</dt>
        <dd v-if="match.picked" class="match-new-picked" data-testid="match-picked">
          <BookRecordDiff
            :record="recordFromResult(match.picked, match.region)"
            :base="currentRecord"
          />
          <span class="match-status">
            <PhCheckCircle :size="14" weight="fill" />
            checked
          </span>
        </dd>
        <dd v-else data-testid="match-none">
          No new record yet: search the book and pick it, or type its ASIN
        </dd>
      </dl>

      <!-- The rules, where they apply -->

      <!-- The search under the book: its header line says how it went (failed in red; nothing
         found, invalid or canceled in orange), over its results, which stay until a new search on
         this book answers. A running search only shows on the bottom bar and over the results, so
         nothing moves under the book; its text is for screen readers. -->
      <!-- The last results stay while a new search runs, so nothing moves; they are greyed out
           under a spinning wheel and can't be picked until it answers. The same while another
           book is searched: one search at a time -->
      <div
        class="match-results"
        :class="{
          'match-results-placeholders': placeholders,
          'match-results-pending': resultsPending,
          'match-results-revealing': revealing && !resultsPending,
        }"
        :style="{
          '--match-reveal-hold': `${resultsReveal.holdMs}ms`,
          '--match-reveal-fade': `${resultsReveal.fadeMs}ms`,
        }"
        :aria-busy="resultsPending || revealing"
        data-testid="match-results"
      >
        <!-- Always there: "No search request done" until a search, then what was found or not,
             and where the answer comes from -->
        <p
          class="match-results-header"
          :class="resultsPending ? undefined : resultsHeader.tone"
          :data-testid="resultsHeader.testId"
        >
          <PhWarningCircle v-if="resultsHeader.tone === 'match-search-error'" :size="14" />
          {{ resultsHeader.text }}
        </p>
        <!-- The list and its empty room share one place; from the first search on (several books),
             an unseen result with every line a result can have keeps that place at least one
             result high, so a short answer doesn't move what is under the book -->
        <div class="match-results-cell">
          <FindMatchResults
            v-if="state.results.length"
            ref="resultsListEl"
            class="match-search-results"
            :results="state.results"
            :selected="pickedRecord"
            :current="match.current"
            @select="pick"
          />
          <!-- No results: the empty list, at its full height when the card keeps its room, else at
             the height of the results a failed or canceled search took away, or of one result from
             the first search on (the spinner and the reveal need something to cover) -->
          <!-- Why there are no results, in the middle of that room: once the spinner is gone -->
          <div
            v-else-if="emptyList"
            class="match-results-empty"
            :style="placeholders || keptHeight === null ? undefined : { height: `${keptHeight}px` }"
            data-testid="match-results-placeholder"
          >
            <p
              v-if="noResultsReason && !resultsPending"
              class="match-results-reason"
              :class="noResultsReason.tone"
              data-testid="match-results-reason"
            >
              <PhWarningCircle :size="16" />
              {{ noResultsReason.text }}
            </p>
          </div>
          <FindMatchResults
            v-if="oneResultRoom"
            class="match-search-results match-results-sizer"
            :results="[ROOM_SAMPLE]"
            aria-hidden="true"
            data-testid="match-results-sizer"
          />
        </div>
        <span
          v-if="resultsPending"
          class="match-results-spinner"
          data-testid="match-results-pending"
        >
          <PhSpinner class="ph-spin" :size="28" />
          <span class="match-results-pending-text" data-testid="match-results-pending-text">
            {{ pendingResultsText }}
          </span>
        </span>
        <!-- The answer: the spinner's line says what was found, then fades out to the results -->
        <span
          v-else-if="revealing"
          class="match-results-spinner match-results-found"
          data-testid="match-results-found"
        >
          <PhCheckCircle :size="28" weight="fill" />
          <span class="match-results-pending-text">{{ foundText }}</span>
        </span>
      </div>
    </fieldset>

    <div class="match-bar" :title="pendingText ?? undefined" />
    <span class="match-sr-only" role="status">
      <span v-if="pendingText" data-testid="match-search-pending">{{ pendingText }}</span>
    </span>
  </div>
</template>

<script setup lang="ts">
import {
  computed,
  inject,
  onBeforeUnmount,
  onMounted,
  ref,
  watch,
  type ComponentPublicInstance,
} from 'vue'
import {
  PhArrowCounterClockwise,
  PhCheckCircle,
  PhBackspace,
  PhMagnifyingGlass,
  PhSpinner,
  PhWarningCircle,
  PhX,
} from '@phosphor-icons/vue'
import FindMatchResults from './FindMatchResults.vue'
import BookRecordDiff from './BookRecordDiff.vue'
import { resultsReveal, revealMs } from '@/utils/resultsReveal'
import { orderedSearchRegionOptions } from '@/utils/languageMapping'
import {
  FALLBACK_REGION,
  INVALID_ASIN_MESSAGE,
  bookSummary,
  answerSource,
  isEdited,
  matchState,
  noRecordError,
  regionLabel,
  recordFromResult,
  searchCacheKey,
  searchLikeAddNew,
  SEARCH_CACHE,
  type BookMatch,
  type BookRecord,
} from '@/utils/bookMatch'
import type { SearchResult } from '@/types'
import { queryAsin } from '@/services/audiobookSearch'

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
    // Search as soon as the card shows, and focus the search box
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

// What the search box holds: an ASIN alone (bare or ASIN:), looked up in the region, or any other
// query (a title, TITLE:/AUTHOR:/… tokens), searched like Add New
type SearchKind = 'query' | 'asin'

// invalid: Search was asked for something it can't search (an ASIN that isn't one), a field error;
// canceled: the user stopped the search. results: the last answer, kept until a new one replaces it
interface SearchState {
  status: 'idle' | 'searching' | 'done' | 'failed' | 'invalid' | 'canceled'
  kind: SearchKind
  results: SearchResult[]
  error: string | null
  // What the results shown were searched with, for their header
  searched: Searched | null
  // How long their request took, or null when the dialog had them already (answered before)
  requestMs: number | null
}

// The parameters of one search, taken when it starts: typing afterwards doesn't change them
interface Searched {
  kind: SearchKind
  asin: string
  query: string
  region: string
}

// What a search looked for: the ASIN, or the search box as typed
function describeSearch(searched: Searched): string {
  return searched.kind === 'asin' ? `ASIN "${searched.asin}"` : `"${searched.query}"`
}

const queryEl = ref<HTMLInputElement | null>(null)
const state = ref<SearchState>(idle())
// The search running, if any
const running = ref<Searched | null>(null)
// Whether a search ran (answered, failed or canceled), and whether the search box or region
// changed since: Retry until one does
const hasSearched = ref(false)
const edited = ref(false)
// The dialog's answered searches, when it keeps them
const searchCache = inject(SEARCH_CACHE, null)
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

// The results shown are waiting for a search: this book's (they are the previous search's, stale)
// or another book's (one search at a time). Not to be picked until it answers
const resultsPending = computed(
  () =>
    (state.value.results.length > 0 || emptyList.value) &&
    (state.value.status === 'searching' || props.searchLocked),
)

// The results' header line, always there: what was searched, where, and where the answer comes
// from (orange when it found nothing, red when it failed). Why there are no results is said in the
// middle of their room. A failed, canceled or refused search shows no results: the empty list
// again, as before any search
const resultsHeader = computed<{ text: string; tone?: string; testId: string }>(() => {
  const { searched, results, status, requestMs } = state.value
  const warning = 'match-search-warning'
  if (status === 'invalid')
    return { text: 'No search request done', tone: warning, testId: 'match-search-error' }
  if (!searched) return { text: 'No search request done', testId: 'match-results-header' }
  const what = `${describeSearch(searched)} in ${regionLabel(searched.region)}`
  if (status === 'canceled')
    return { text: `No results for ${what}`, tone: warning, testId: 'match-search-error' }
  if (status === 'failed')
    return {
      text: `No results for ${what}`,
      tone: 'match-search-error',
      testId: 'match-search-error',
    }
  if (results.length)
    return {
      text: `Results for ${what} - ${answerSource(requestMs)}`,
      testId: 'match-results-header',
    }
  return {
    text: `No results for ${what} - ${answerSource(requestMs)}`,
    tone: warning,
    testId: 'match-search-empty',
  }
})

// Why a search left no results, in the middle of their room: a failed request (red), or nothing
// found, the search canceled, an ASIN that isn't one (orange). Nothing before any search
const noResultsReason = computed<{ text: string; tone: string } | null>(() => {
  const { searched, results, status, error } = state.value
  const warning = 'match-search-warning'
  if (status === 'invalid') return { text: error ?? '', tone: warning }
  if (status === 'canceled') return { text: 'Search canceled', tone: warning }
  if (status === 'failed') return { text: error ?? '', tone: 'match-search-error' }
  if (status !== 'done' || results.length || !searched) return null
  return {
    text:
      searched.kind === 'asin'
        ? noRecordError(searched.asin, searched.region)
        : 'Nothing found for this search',
    tone: warning,
  }
})

// Results just answered, shown under an overlay fading out ("Found 3 results"): not pickable
// until it is gone
const revealing = ref(false)
let revealTimer: ReturnType<typeof setTimeout> | null = null

const foundText = computed(() => {
  const count = state.value.results.length
  return `Found ${count} ${count === 1 ? 'result' : 'results'}`
})

function startReveal() {
  endReveal()
  if (revealMs() <= 0 || !state.value.results.length) return
  revealing.value = true
  revealTimer = setTimeout(endReveal, revealMs())
}

function endReveal() {
  if (revealTimer) clearTimeout(revealTimer)
  revealTimer = null
  revealing.value = false
}

const pendingResultsText = computed(() =>
  state.value.status === 'searching'
    ? pendingText.value
    : 'Waiting: a search is running on another book',
)

// Changing the region searches there: not while a search runs, here or on another book.
// One search at a time keeps answers from racing each other.
const regionLocked = computed(() => props.searchLocked || state.value.status === 'searching')

// Search becomes Retry once a search ran, whatever its outcome, until the search box or region
// changes: from then on it is Search, even if changed back. Retry asks Audible again (a new
// request), where Search may be served by the dialog's answered searches
const retry = computed(
  () => hasSearched.value && !edited.value && state.value.status !== 'searching',
)

const canSearch = computed(
  () => !props.searchLocked && state.value.status !== 'searching' && !!match.value.query.trim(),
)

function idle(): SearchState {
  return {
    status: 'idle',
    kind: 'query',
    results: [],
    error: null,
    searched: null,
    requestMs: null,
  }
}

function setMatched(record: SearchResult) {
  match.value.picked = record
  match.value.asinError = null
  emit('matched', record)
}

// The results list on screen, and the height an empty one keeps when a failed or canceled search
// takes them away (cards that don't keep the room from the start): nothing moves until the next
// results replace it
const resultsListEl = ref<ComponentPublicInstance | null>(null)
const keptHeight = ref<number | null>(null)
// A card that doesn't keep the room from the start gets an empty list one result high with its
// first search, kept until results replace it: the spinner and the reveal show over it
const firstSearchRoom = ref(false)
const oneResultRoom = computed(() => !props.placeholders && firstSearchRoom.value)
// The result the room is measured with: a title, an author and an ASIN, and a narrator (the
// tallest a result gets)
const ROOM_SAMPLE: SearchResult = {
  asin: 'B000000000',
  title: 'Room',
  authors: [{ name: 'Room' }],
  narrators: [{ name: 'Room' }],
  language: 'english',
} as unknown as SearchResult
const emptyList = computed(
  () => props.placeholders || keptHeight.value !== null || firstSearchRoom.value,
)

function keepResultsHeight() {
  if (props.placeholders || !state.value.results.length) return
  keptHeight.value = (resultsListEl.value?.$el as HTMLElement | undefined)?.offsetHeight ?? 0
}

// The answer to a search, under the book: a query's results, or the records found for the ASIN
// (none is an error)
function showResults(searched: Searched, results: SearchResult[], requestMs: number | null) {
  const { kind, asin, region } = searched
  if (!asin) {
    state.value = { status: 'done', kind, results, error: null, searched, requestMs }
    startReveal()
    return
  }
  const found = results
    .filter((r) => r.asin?.toUpperCase() === asin)
    .map((r) => ({ ...r, region: r.region || region }))
  // The records found are offered to pick from, like a query's: nothing is picked for the user
  if (found.length) {
    state.value = { status: 'done', kind, results: found, error: null, searched, requestMs }
    startReveal()
  } else {
    // Found nothing (orange): a title search is what comes next
    match.value.asinError = noRecordError(asin, region)
    state.value = { status: 'done', kind, results: [], error: null, searched, requestMs }
  }
}

// Search what the search box holds, in the region, the way Add New does; the results open under
// the book, to pick from. An ASIN alone the region has no record for is an error. Values already
// answered while the dialog is open are served without a request, unless fresh (Retry)
async function search(fresh = false) {
  if (!canSearch.value) return
  const query = match.value.query.trim()
  const asin = queryAsin(query)
  const kind: SearchKind = asin ? 'asin' : 'query'
  if (!asin && /^ASIN:/i.test(query)) {
    // A field error, not a request one: nothing is sent. The previous results go, so the error
    // shows in their room
    match.value.picked = null
    endReveal()
    keepResultsHeight()
    if (!state.value.results.length) firstSearchRoom.value = true
    state.value = {
      ...state.value,
      status: 'invalid',
      kind: 'asin',
      results: [],
      error: INVALID_ASIN_MESSAGE,
    }
    return
  }
  const region = match.value.region
  const searched: Searched = { kind, asin: asin ?? '', query, region }
  const cacheKey = searchCacheKey(query, region)
  hasSearched.value = true
  edited.value = false
  // A new search starts over: the record picked in the previous results is no longer selected, and
  // New goes back to no record (nothing stays picked that the user can't see)
  match.value.picked = null
  match.value.asinError = null
  endReveal()
  if (!state.value.results.length) firstSearchRoom.value = true
  const answered = fresh ? undefined : searchCache?.get(cacheKey)
  if (answered) {
    showResults(searched, answered, null)
    return
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
  const startedAt = performance.now()
  try {
    const results = await searchLikeAddNew(query, region, current.signal)
    // A canceled search answers to nobody: the card already went back to its fields
    if (controller !== current) return
    searchCache?.set(cacheKey, results)
    showResults(searched, results, performance.now() - startedAt)
  } catch (e) {
    if (controller !== current) return
    const error = timedOut
      ? `The search took longer than ${SEARCH_TIMEOUT_MS / 1000} seconds and was stopped.`
      : `The search failed: ${(e as Error)?.message ?? 'unknown error'}`
    // An ASIN that could not be looked up stays unusable until it is (its error shows over the
    // results' room)
    if (asin) match.value.asinError = error
    keepResultsHeight()
    state.value = { status: 'failed', kind, results: [], error, searched, requestMs: null }
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
  endReveal()
}

// Cancel search: the search stops, its results stay, and the card says it was canceled (orange)
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
  if (!result.asin || props.searchLocked || state.value.status === 'searching' || revealing.value)
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
  // The record only: the search box and region keep the search as typed, so a wrong pick undone
  // leaves them as they were
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
// Search, Enter and a region change all start it; with nothing changed since the last search,
// that is Retry: a new request
function searchByPriority() {
  void search(retry.value)
}

// Another region: the search runs again there right away, the one change that searches by itself
function onRegionChanged() {
  forgetRecord()
  // A field changed: a search, not a Retry of the previous one
  edited.value = true
  searchByPriority()
}

// The search box's erase button: like typing, it only prepares the next search
function clearQuery() {
  match.value.query = ''
  edited.value = true
}

// Any change to the search box, even back to what was searched: Search, no longer Retry
function onEdited() {
  edited.value = true
}

watch(
  () => state.value.status === 'searching',
  (searching) => emit('searching', searching),
)

function revert() {
  Object.assign(match.value, match.value.initial)
  edited.value = true
  forgetRecord()
}

onMounted(() => {
  if (!props.searchOnMount) return
  queryEl.value?.focus()
  queryEl.value?.select()
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

/* The new record flows as text, so "checked" follows its last line instead of taking a line of
   its own */
.match-records dd.match-new-picked {
  display: block;
}

/* Line height 1: a "checked" box as tall as the text line, centred on it, would stick out of it
   and make the picked record taller than "No new record yet" */
.match-status {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  margin-left: 0.4rem;
  line-height: 1;
  vertical-align: middle;
  white-space: nowrap;
  color: var(--status-success);
}

/* ASIN, Title, Author, Region and Search always share the whole row */
.match-fields {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(5rem, 6rem) auto;
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

/* A narrow screen, or a dialog short of room: the fields two by two, Search on a line of its own,
   and two lines kept for Current and New */
@media (max-width: 720px) {
  .match-fields {
    grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  }

  .match-fields > .match-field:first-child,
  .match-search-btn {
    grid-column: 1 / -1;
    width: auto;
  }

  .match-records dd {
    min-height: 2.8em;
  }
}

.match-card-narrow .match-fields {
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
}

.match-card-narrow .match-fields > .match-field:first-child,
.match-card-narrow .match-search-btn {
  grid-column: 1 / -1;
  width: auto;
}

.match-card-narrow .match-records dd {
  min-height: 2.8em;
}

/* While a new search runs: the previous results greyed out, not clickable, under a spinning
   wheel laid over them (it takes no room). Once answered, the wheel's line says what was found,
   stays readable, then fades out while the results come out of grey, still not clickable */
.match-results {
  position: relative;
}

.match-results-pending .match-results-header,
.match-results-pending .match-search-results {
  opacity: 0.35;
  pointer-events: none;
  filter: grayscale(1);
}

.match-results-revealing .match-results-header,
.match-results-revealing .match-search-results {
  pointer-events: none;
  animation: match-results-unveil var(--match-reveal-fade) ease-out var(--match-reveal-hold) both;
}

/* Over the spinner's colour, which comes later */
.match-results-spinner.match-results-found {
  color: var(--status-success);
  animation: match-results-fade var(--match-reveal-fade) ease-in var(--match-reveal-hold) both;
}

@keyframes match-results-unveil {
  from {
    opacity: 0.35;
    filter: grayscale(1);
  }
  to {
    opacity: 1;
    filter: none;
  }
}

@keyframes match-results-fade {
  from {
    opacity: 1;
  }
  to {
    opacity: 0;
  }
}

/* No fade with reduced motion: the line says what was found for the same time, then goes */
@media (prefers-reduced-motion: reduce) {
  .match-results-revealing .match-results-header,
  .match-results-revealing .match-search-results,
  .match-results-spinner.match-results-found {
    animation: none;
  }
}

.match-results-spinner {
  position: absolute;
  inset: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 0.4rem;
  padding: 0 1rem;
  color: var(--status-progress);
  pointer-events: none;
}

.match-results-pending-text {
  font-size: 0.8rem;
  color: #ddd;
  text-align: center;
}

/* Kept room: the list at its full height, results or not */
.match-results-placeholders .match-search-results,
.match-results-placeholders .match-results-empty {
  height: 320px;
  box-sizing: border-box;
}

/* The list, its empty room and the unseen result in one place: as tall as the tallest */
.match-results-cell {
  display: grid;
}

.match-results-cell > * {
  grid-area: 1 / 1;
}

.match-results-sizer {
  visibility: hidden;
  pointer-events: none;
}

.match-results-empty {
  box-sizing: border-box;
  margin-top: 0.4rem;
  border: 1px dashed #333;
  border-radius: 6px;
}

/* Why there are no results, in the middle of their room, in the search's colour */
.match-results-empty {
  display: flex;
  align-items: center;
  justify-content: center;
}

.match-results-reason {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  margin: 0;
  padding: 0 1rem;
  font-size: 0.8rem;
  text-align: center;
}

.match-results-reason.match-search-warning {
  color: var(--status-warning);
}

.match-results-reason.match-search-error {
  color: var(--status-error);
}

.match-results-header {
  margin: 0.75rem 0 0;
  font-size: 0.8rem;
  color: #aaa;
}

/* How the search went, in its colour: the header's own grey comes first */
.match-results-header.match-search-warning {
  color: var(--status-warning);
}

.match-results-header.match-search-error {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  color: var(--status-error);
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
