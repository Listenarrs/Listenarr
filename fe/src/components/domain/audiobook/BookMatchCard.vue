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
     book is searched, the whole card waits: nothing in it can be edited, searched or picked. A task
     run on the book (Repair's repair) ends the search: its results go, the card is locked, and the
     whole card shows the task's progress, with a line saying where it is. -->
<template>
  <div
    class="form-control-card match-card"
    :class="[
      `match-card-${barState}`,
      { 'match-card-locked': searchLocked, 'match-card-narrow': narrow },
      task ? `match-card-task match-card-task-${task.state}` : '',
    ]"
    :data-task-state="task?.state"
    :data-match-state="barState"
    :title="searchLocked ? 'Another book is being searched' : undefined"
  >
    <fieldset class="match-lock" :disabled="searchLocked || !!task" data-testid="match-lock">
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
              @input="onEdited"
              @keydown.enter.prevent="searchByPriority"
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
              @keydown.enter.prevent="searchByPriority"
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
              @keydown.enter.prevent="searchByPriority"
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
          :disabled="!canSearchFor(searchKind)"
          :title="
            searchLocked
              ? 'Another book is being searched'
              : 'The search Add New and Library Import use, with the same rules and priorities'
          "
          @click="searchByPriority"
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

      <p v-if="task" class="match-task" data-testid="match-task">{{ task.text }}</p>

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
          :class="{ 'match-search-warning': resultsHeader.empty && !resultsPending }"
          :data-testid="resultsHeader.empty ? 'match-search-empty' : 'match-results-header'"
        >
          {{ resultsHeader.text }}
        </p>
        <FindMatchResults
          v-if="state.results.length"
          class="match-search-results"
          :results="state.results"
          :selected="pickedRecord"
          :current="match.current"
          @select="pick"
        />
        <div
          v-else-if="placeholders"
          class="match-results-empty"
          data-testid="match-results-placeholder"
        />
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
import { computed, inject, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import {
  PhArrowCounterClockwise,
  PhCheckCircle,
  PhBackspace,
  PhMagnifyingGlass,
  PhSpinner,
  PhWarningCircle,
  PhX,
  PhXCircle,
} from '@phosphor-icons/vue'
import FindMatchResults from './FindMatchResults.vue'
import { resultsReveal, revealMs } from '@/utils/resultsReveal'
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
  answerSource,
  searchCacheKey,
  searchLikeAddNew,
  SEARCH_CACHE,
  type BookMatch,
  type BookRecord,
  type BookTask,
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
    // The dialog is short of room (Repair measures its footer): the fields take the narrow
    // screen's layout before the screen itself is narrow
    narrow?: boolean
    // The results list's room kept from the start, at its full height whatever it holds. For a
    // card alone in its dialog (Find Match, Repair of one book), so nothing moves when a search
    // answers; with several books, the results open under each one when found
    placeholders?: boolean
    // A task run on the book once its record is chosen (Repair's repair): the search is over
    task?: BookTask | null
  }>(),
  {
    searchLocked: false,
    excluded: false,
    searchOnMount: false,
    narrow: false,
    task: null,
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
  // How long their request took, or null when the dialog had them already (answered before)
  requestMs: number | null
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
// The kind of the last search started (answered, failed or canceled), and whether a field was
// edited since: Retry until one is
const lastSearchKind = ref<SearchKind | null>(null)
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

// A filled ASIN has priority: a search looks it up on its own, leaving the title and author out.
// With the ASIN field empty, it looks for the title and author
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

// The results shown are waiting for a search: this book's (they are the previous search's, stale)
// or another book's (one search at a time). Not to be picked until it answers
const resultsPending = computed(
  () =>
    (state.value.results.length > 0 || props.placeholders) &&
    (state.value.status === 'searching' || props.searchLocked),
)

// The results' header line: what was searched, where, and where the answer comes from. Empty
// (orange) when it found nothing, or failed or was canceled with nothing to show
const resultsHeader = computed(() => {
  const { searched, results, status, requestMs } = state.value
  if (!searched) return { text: 'No search request done', empty: false }
  const what = `${describeSearch(searched)} in ${regionLabel(searched.region)}`
  if (results.length)
    return { text: `Results for ${what} - ${answerSource(requestMs)}`, empty: false }
  const outcome =
    status === 'failed'
      ? 'Search failed'
      : status === 'canceled'
        ? 'Search canceled'
        : answerSource(requestMs)
  return { text: `No results for ${what} - ${outcome}`, empty: true }
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

// Search becomes Retry once a search ran, whatever its outcome, until a field is edited: from then
// on it is Search, even if the field is changed back. Retry asks Audible again (a new request),
// where Search may be served by the dialog's answered searches
const retryKind = computed<SearchKind | null>(() => {
  if (edited.value || state.value.status === 'searching') return null
  return lastSearchKind.value
})

function canSearchFor(kind: SearchKind): boolean {
  if (props.searchLocked || state.value.status === 'searching') return false
  // An ASIN that isn't one can still be searched: Search then says what an ASIN is
  return kind === 'asin' ? !!match.value.asin.trim() : !!match.value.query.trim()
}

function idle(): SearchState {
  return {
    status: 'idle',
    kind: 'title',
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

// The answer to a search, under the book: a title search's results, or the records found for the
// ASIN (none is an error)
function showResults(
  kind: SearchKind,
  searched: Searched,
  results: SearchResult[],
  requestMs: number | null,
) {
  if (kind === 'title') {
    state.value = { status: 'done', kind, results, error: null, searched, requestMs }
    startReveal()
    return
  }
  const found = results
    .filter((r) => r.asin?.toUpperCase() === searched.asin)
    .map((r) => ({ ...r, region: r.region || searched.region }))
  // The records found are offered to pick from, like a title search's: nothing is picked for
  // the user
  if (found.length) {
    state.value = { status: 'done', kind, results: found, error: null, searched, requestMs }
    startReveal()
  } else {
    // Found nothing (orange): searching the title there is what comes next
    match.value.asinError = `${noRecordError(searched.asin, searched.region)} - ${answerSource(requestMs)}`
    state.value = { status: 'done', kind, results: [], error: null, searched, requestMs }
  }
}

// Search the title and author in the region, or look the ASIN up there; the results open under
// the book, to pick from. An ASIN its region has no record for is an error. Values already
// answered while the dialog is open are served without a request, unless fresh (Retry)
async function search(kind: SearchKind = 'title', fresh = false) {
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
  const query = kind === 'asin' ? asin : addNewQuery(match.value)
  const cacheKey = searchCacheKey(query, region)
  lastSearchKind.value = kind
  edited.value = false
  // A new search starts over: the record picked in the previous results is no longer selected, and
  // New goes back to no record (nothing stays picked that the user can't see)
  match.value.picked = null
  match.value.asinError = null
  endReveal()
  const answered = fresh ? undefined : searchCache?.get(cacheKey)
  if (answered) {
    showResults(kind, searched, answered, null)
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
    showResults(kind, searched, results, performance.now() - startedAt)
  } catch (e) {
    if (controller !== current) return
    const error = timedOut
      ? `The search took longer than ${SEARCH_TIMEOUT_MS / 1000} seconds and was stopped.`
      : `The search failed: ${(e as Error)?.message ?? 'unknown error'}`
    // An ASIN that could not be looked up stays unusable until it is (its error shows on the New
    // line)
    if (kind === 'asin') match.value.asinError = error
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
  stopSearch()
  state.value = { ...state.value, status: 'canceled', error: 'Search canceled.' }
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

// The erase buttons of the ASIN, Title and Author fields: like typing, emptying a field only
// prepares the next search (an empty ASIN lets Search look for the title and author)
function clearField(field: 'asin' | 'query' | 'author') {
  match.value[field] = ''
  edited.value = true
}

// Any change to a field, even back to what was searched: Search, no longer Retry
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
  titleEl.value?.focus()
  titleEl.value?.select()
  void search()
})

// A task starting ends the search: the running one stops, its results and messages go
watch(
  () => !!props.task,
  (hasTask) => {
    if (!hasTask) return
    stopSearch()
    state.value = idle()
  },
)

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

/* A task on the book: the whole card shows it, its bar in the same colour */
.match-task {
  margin: 0.75rem 0 0;
  font-size: 0.85rem;
  color: #ccc;
}

.form-control-card.match-card-task-running {
  animation: match-card-task-breathe 1.6s ease-in-out infinite alternate;
}

.form-control-card.match-card-task-done {
  background-color: color-mix(in srgb, var(--status-success) 14%, #232323);
  border-color: color-mix(in srgb, var(--status-success) 50%, #333);
}

.form-control-card.match-card-task-warning {
  background-color: color-mix(in srgb, var(--status-warning) 14%, #232323);
  border-color: color-mix(in srgb, var(--status-warning) 50%, #333);
}

.form-control-card.match-card-task-failed {
  background-color: color-mix(in srgb, var(--status-error) 14%, #232323);
  border-color: color-mix(in srgb, var(--status-error) 50%, #333);
}

.match-card-task-skipped {
  opacity: 0.6;
}

.match-card-task-running .match-bar {
  background: var(--status-progress);
  animation: match-bar-breathe 1.6s ease-in-out infinite alternate;
}

.match-card-task-done .match-bar {
  background: var(--status-success);
}

.match-card-task-warning .match-bar {
  background: var(--status-warning);
}

.match-card-task-failed .match-bar {
  background: var(--status-error);
}

.match-card-task-waiting .match-bar,
.match-card-task-skipped .match-bar {
  background: var(--status-idle);
}

@keyframes match-card-task-breathe {
  from {
    background-color: color-mix(in srgb, var(--status-progress) 4%, #232323);
    border-color: #333;
  }
  to {
    background-color: color-mix(in srgb, var(--status-progress) 18%, #232323);
    border-color: color-mix(in srgb, var(--status-progress) 60%, #333);
  }
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
  .match-card-searching .match-bar,
  .match-card-task-running .match-bar {
    animation: none;
  }

  .form-control-card.match-card-task-running {
    animation: none;
    background-color: color-mix(in srgb, var(--status-progress) 14%, #232323);
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

/* A narrow screen, or a dialog short of room: the fields two by two, Search on a line of its own,
   and two lines kept for Current and New */
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

.match-card-narrow .match-fields {
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
}

.match-card-narrow .match-search-btn {
  grid-column: 1 / -1;
  width: auto;
}

.match-card-narrow .match-records dd {
  min-height: 2.8em;
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
.match-results-empty {
  height: 320px;
  box-sizing: border-box;
}

.match-results-empty {
  margin-top: 0.4rem;
  border: 1px dashed #333;
  border-radius: 6px;
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
