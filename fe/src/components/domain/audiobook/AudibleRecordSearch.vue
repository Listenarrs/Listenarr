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
<!-- Find a book's Audible record in one region: ASIN, Region, Title, Author and Search, then the
     results to pick from. Used by Find Match (Library Import) and Repair. -->
<template>
  <div class="record-search">
    <div class="record-fields">
      <label class="record-field">
        <span>ASIN</span>
        <input
          :value="state.asin"
          type="text"
          class="form-input"
          data-testid="record-asin"
          @input="onAsinInput"
          @keydown.escape="emit('escape')"
        />
      </label>
      <label class="record-field">
        <span>Region</span>
        <select
          :value="state.region"
          class="form-input"
          data-testid="record-region"
          @change="onRegionInput"
        >
          <option v-for="o in regionOptions" :key="o.value" :value="o.value">
            {{ o.label }}
          </option>
        </select>
      </label>
      <label class="record-field record-field-title">
        <span>Title</span>
        <input
          ref="titleEl"
          :value="state.query"
          type="text"
          class="form-input"
          data-testid="record-title"
          @input="onTextInput('query', $event)"
          @keydown.enter.prevent="void searchRecords(state)"
          @keydown.escape="emit('escape')"
        />
      </label>
      <label class="record-field record-field-author">
        <span>Author</span>
        <input
          :value="state.author"
          type="text"
          class="form-input"
          data-testid="record-author"
          @input="onTextInput('author', $event)"
          @keydown.enter.prevent="void searchRecords(state)"
          @keydown.escape="emit('escape')"
        />
      </label>
      <button
        type="button"
        class="btn btn-secondary record-search-btn"
        data-testid="record-search"
        :disabled="state.searching || !state.query.trim()"
        @click="void searchRecords(state)"
      >
        <PhSpinner v-if="state.searching" class="ph-spin" :size="14" />
        <PhMagnifyingGlass v-else :size="14" />
        Search
      </button>
    </div>

    <p v-if="state.lookingUp" class="record-hint" data-testid="record-asin-lookup">
      Looking up {{ state.asin.trim().toUpperCase() }} in {{ regionLabel(state.region) }}…
    </p>
    <p
      v-else-if="state.asinNotFound"
      class="record-hint record-hint-error"
      data-testid="record-asin-not-found"
    >
      No record for {{ state.asin.trim().toUpperCase() }} in {{ regionLabel(state.region) }}: check
      the ASIN or choose another region.
    </p>
    <p
      v-else-if="asinMalformed(state)"
      class="record-hint record-hint-error"
      data-testid="record-asin-invalid"
    >
      An ASIN has 10 letters or digits, including at least one digit.
    </p>
    <p v-else-if="state.asinCleared" class="record-hint" data-testid="record-asin-cleared">
      ASIN cleared: it belonged to the previous match. Search and pick the right book, or type its
      ASIN.
    </p>
    <slot name="hint" />

    <p
      v-if="state.searched && !state.searching && !state.results.length"
      class="record-empty"
      data-testid="record-no-results"
    >
      No results for "{{ state.query.trim() }}"{{
        state.author.trim() ? ` by "${state.author.trim()}"` : ''
      }}
      in {{ regionLabel(state.region) }}.
    </p>
    <ul v-if="state.results.length" class="record-results">
      <li v-for="(result, index) in state.results" :key="result.asin ?? `${index}-${result.title}`">
        <button
          type="button"
          class="record-result"
          :class="{ picked: pickedAsin && result.asin?.toUpperCase() === pickedAsin }"
          data-testid="record-result"
          @click="choose(result)"
        >
          <img
            v-if="result.imageUrl"
            :src="getProtectedImageSrc(result.imageUrl, placeholderUrl)"
            class="record-result-thumb"
            alt=""
          />
          <span class="record-result-text">
            <span class="record-result-title">{{ resultTitle(result) }}</span>
            <span class="record-result-meta">{{ resultMeta(result) }}</span>
          </span>
        </button>
      </li>
    </ul>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { PhMagnifyingGlass, PhSpinner } from '@phosphor-icons/vue'
import { useProtectedImages } from '@/composables/useProtectedImages'
import {
  asinMalformed,
  isChosen,
  onAsinEdited,
  onRegionChanged,
  onTitleOrAuthorEdited,
  pickRecord,
  resultMeta,
  resultTitle,
  searchRecords,
  type RecordSearchState,
} from '@/utils/audibleRecordSearch'
import { searchRegionOptions } from '@/utils/languageMapping'
import { getPlaceholderUrl } from '@/utils/placeholder'
import type { SearchResult } from '@/types'

const props = withDefaults(
  defineProps<{
    // The reactive search state, owned by the parent (one per book in Repair)
    state: RecordSearchState
    regionOptions: ReadonlyArray<{ value: string; label: string }>
    autofocus?: boolean
  }>(),
  { autofocus: false },
)

const emit = defineEmits<{
  // A result was clicked
  pick: [result: SearchResult]
  // The ASIN was found by a lookup (typed, or region changed)
  found: []
  escape: []
}>()

const { getProtectedImageSrc } = useProtectedImages()
const placeholderUrl = getPlaceholderUrl()
const titleEl = ref<HTMLInputElement | null>(null)

const pickedAsin = computed(() =>
  isChosen(props.state) ? props.state.asin.trim().toUpperCase() : '',
)

function regionLabel(region: string): string {
  return searchRegionOptions.find((o) => o.value === region)?.label ?? region
}

const valueOf = (event: Event) => (event.target as HTMLInputElement | HTMLSelectElement).value

async function afterLookup(promise: Promise<void>) {
  await promise
  if (isChosen(props.state)) emit('found')
}

function onAsinInput(event: Event) {
  const state = props.state
  state.asin = valueOf(event)
  void afterLookup(onAsinEdited(state))
}

function onRegionInput(event: Event) {
  const state = props.state
  state.region = valueOf(event)
  void afterLookup(onRegionChanged(state))
}

function onTextInput(field: 'query' | 'author', event: Event) {
  const state = props.state
  state[field] = valueOf(event)
  onTitleOrAuthorEdited(state)
}

function choose(result: SearchResult) {
  pickRecord(props.state, result)
  emit('pick', result)
}

onMounted(() => {
  if (!props.autofocus) return
  titleEl.value?.focus()
  titleEl.value?.select()
})
</script>

<style scoped>
.record-fields {
  display: grid;
  grid-template-columns:
    minmax(8rem, 1fr) minmax(8rem, 1fr) minmax(10rem, 2fr) minmax(8rem, 1.5fr)
    auto;
  gap: 0.5rem;
  align-items: end;
}

.record-field {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  font-size: 0.8rem;
}

.record-search-btn {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
}

.record-hint {
  margin: 0.5rem 0 0;
  font-size: 0.8rem;
  color: #ffb74d;
}

.record-hint-error {
  color: #ef9a9a;
}

.record-empty {
  margin: 0.5rem 0 0;
  font-size: 0.8rem;
  opacity: 0.75;
}

.record-results {
  list-style: none;
  margin: 0.5rem 0 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  max-height: 320px;
  overflow-y: auto;
}

.record-result {
  width: 100%;
  display: flex;
  align-items: center;
  gap: 0.6rem;
  padding: 0.4rem 0.6rem;
  border-radius: 6px;
  border: 1px solid rgba(255, 255, 255, 0.08);
  background: transparent;
  color: inherit;
  text-align: left;
  cursor: pointer;
}

.record-result:hover {
  background-color: rgba(255, 255, 255, 0.04);
}

.record-result.picked {
  border-color: rgba(76, 175, 80, 0.6);
  background-color: rgba(76, 175, 80, 0.08);
}

.record-result-thumb {
  width: 36px;
  height: 36px;
  object-fit: cover;
  border-radius: 3px;
  flex-shrink: 0;
}

.record-result-text {
  display: flex;
  flex-direction: column;
  gap: 0.1rem;
  min-width: 0;
}

.record-result-meta {
  font-size: 0.75rem;
  opacity: 0.75;
}

@media (max-width: 720px) {
  .record-fields {
    grid-template-columns: 1fr;
  }
}
</style>
