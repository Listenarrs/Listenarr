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
<!-- The search under the book: its header line says how it went (failed in red; nothing found,
     invalid or canceled in orange), over its results, which stay until a new search on this book
     answers. The last results stay while a new search runs, so nothing moves; they are greyed out
     under a spinning wheel and can't be picked until it answers. The same while another book is
     searched: one search at a time. Once answered, the wheel's line says what was found, then
     fades out to the results. -->
<template>
  <div
    class="match-results"
    :class="{
      'match-results-placeholders': shouldKeepResultsRoom,
      'match-results-pending': isPending,
      'match-results-revealing': isRevealing && !isPending,
    }"
    :style="{
      '--match-reveal-hold': `${resultsReveal.holdMs}ms`,
      '--match-reveal-fade': `${resultsReveal.fadeMs}ms`,
    }"
    :aria-busy="isPending || isRevealing"
    data-testid="match-results"
  >
    <!-- Always there: "No search request done" until a search, then what was found or not,
         and where the answer comes from -->
    <p
      class="match-results-header"
      :class="isPending || !header.tone ? undefined : `match-search-${header.tone}`"
      :data-testid="HEADER_TEST_ID_BY_OUTCOME[header.outcome]"
    >
      <PhWarningCircle v-if="header.tone === 'error'" :size="14" />
      {{ header.text }}
    </p>
    <!-- The list and its empty room share one place; from the first search on (several books),
         an unseen result with every line a result can have keeps that place at least one result
         high, so a short answer doesn't move what is under the book -->
    <div class="match-results-cell">
      <FindMatchResults
        v-if="searchResults.length"
        ref="listRef"
        class="match-search-results"
        :search-results="searchResults"
        :selected-record-key="selectedRecordKey"
        :current-record-key="currentRecordKey"
        @select="(searchResult) => emit('select', searchResult)"
      />
      <!-- No results: the empty list, at its full height when the card keeps its room, else at
           the height of the results a failed or canceled search took away, or of one result from
           the first search on (the spinner and the reveal need something to cover). Why there are
           no results, in the middle of that room: once the spinner is gone -->
      <div
        v-else-if="hasEmptyList"
        class="match-results-empty"
        :style="
          shouldKeepResultsRoom || keptHeight === null ? undefined : { height: `${keptHeight}px` }
        "
        data-testid="match-results-placeholder"
      >
        <p
          v-if="noResultsReason && !isPending"
          class="match-results-reason"
          :class="`match-search-${noResultsReason.tone}`"
          data-testid="match-results-reason"
        >
          <PhWarningCircle :size="16" />
          {{ noResultsReason.text }}
        </p>
      </div>
      <FindMatchResults
        v-if="hasOneResultRoom"
        class="match-search-results match-results-sizer"
        :search-results="[ROOM_SAMPLE]"
        aria-hidden="true"
        data-testid="match-results-sizer"
      />
    </div>
    <span v-if="isPending" class="match-results-spinner" data-testid="match-results-pending">
      <PhSpinner class="ph-spin" :size="28" />
      <span class="match-results-pending-text" data-testid="match-results-pending-text">
        {{ isSearching ? pendingText : 'Waiting: a search is running on another book' }}
      </span>
    </span>
    <span
      v-else-if="isRevealing"
      class="match-results-spinner match-results-found"
      data-testid="match-results-found"
    >
      <PhCheckCircle :size="28" weight="fill" />
      <span class="match-results-pending-text">{{ foundText }}</span>
    </span>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, type ComponentPublicInstance } from 'vue'
import { PhCheckCircle, PhSpinner, PhWarningCircle } from '@phosphor-icons/vue'
import FindMatchResults from './FindMatchResults.vue'
import { resultsReveal } from '@/utils/resultsReveal'
import type { SearchHeader, SearchHeaderOutcome, SearchTone } from '@/utils/bookSearch'
import type { RecordKey } from '@/utils/bookMatch'
import type { SearchResult } from '@/types'

const props = defineProps<{
  searchResults: SearchResult[]
  header: SearchHeader
  noResultsReason: { text: string; tone: SearchTone } | null
  // This book's search runs, and what it does
  isSearching: boolean
  pendingText: string | null
  // Another book is being searched: one search at a time
  isSearchLocked: boolean
  isRevealing: boolean
  // The list's room kept from the start, at its full height whatever it holds
  shouldKeepResultsRoom: boolean
  // The record picked (green) and the one the book points at today (tagged)
  selectedRecordKey: RecordKey | null
  currentRecordKey: RecordKey | null
}>()

const emit = defineEmits<{ select: [searchResult: SearchResult] }>()

const HEADER_TEST_ID_BY_OUTCOME: Record<SearchHeaderOutcome, string> = {
  none: 'match-results-header',
  results: 'match-results-header',
  empty: 'match-search-empty',
  stopped: 'match-search-error',
}

// The result the room is measured with: a title, an author and an ASIN, and a narrator (the
// tallest a result gets)
const ROOM_SAMPLE: SearchResult = {
  asin: 'B000000000',
  title: 'Room',
  authors: [{ name: 'Room' }],
  narrators: [{ name: 'Room' }],
  language: 'english',
} as unknown as SearchResult

// The results list on screen, and the height an empty one keeps when a failed or canceled search
// takes them away (cards that don't keep the room from the start): nothing moves until the next
// results replace it
const listRef = ref<ComponentPublicInstance | null>(null)
const keptHeight = ref<number | null>(null)
// A card that doesn't keep the room from the start gets an empty list one result high with its
// first search, kept until results replace it: the spinner and the reveal show over it
const hasFirstSearchRoom = ref(false)
const hasOneResultRoom = computed(() => !props.shouldKeepResultsRoom && hasFirstSearchRoom.value)
const hasEmptyList = computed(
  () => props.shouldKeepResultsRoom || keptHeight.value !== null || hasFirstSearchRoom.value,
)

// The results shown are waiting for a search: this book's (they are the previous search's, stale)
// or another book's. Not to be picked until it answers
const isPending = computed(
  () =>
    (props.searchResults.length > 0 || hasEmptyList.value) &&
    (props.isSearching || props.isSearchLocked),
)

const foundText = computed(() => {
  const resultCount = props.searchResults.length
  return `Found ${resultCount} ${resultCount === 1 ? 'result' : 'results'}`
})

// The results on screen are about to go: their height is kept by the empty list
function keepHeight() {
  if (props.shouldKeepResultsRoom) return
  keptHeight.value = (listRef.value?.$el as HTMLElement | undefined)?.offsetHeight ?? 0
}

// A search starts with no results on screen: the empty list one result high from now on
function openRoom() {
  hasFirstSearchRoom.value = true
}

defineExpose({ keepHeight, openRoom })
</script>

<style scoped>
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

/* Why there are no results, in the middle of their room, in the search's colour */
.match-results-empty {
  display: flex;
  align-items: center;
  justify-content: center;
  box-sizing: border-box;
  margin-top: 0.4rem;
  border: 1px dashed #333;
  border-radius: 6px;
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

/* How the search went, in its colour: the header's own grey comes first */
.match-results-header {
  margin: 0.75rem 0 0;
  font-size: 0.8rem;
  color: #aaa;
}

.match-search-warning {
  color: var(--status-warning);
}

.match-results-header.match-search-error {
  display: flex;
  align-items: center;
  gap: 0.35rem;
}

.match-search-error {
  color: var(--status-error);
}

.match-search-results {
  margin-top: 0.4rem;
}
</style>
