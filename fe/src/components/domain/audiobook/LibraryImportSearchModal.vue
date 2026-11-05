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
<template>
  <Modal :visible="true" size="md" @close="emit('close')">
    <template #header>
      <ModalHeader :title="`Find Match — ${item.folderName}`" @close="emit('close')" />
    </template>
    <ModalBody>
      <div class="search-wrap">
        <div class="search-fields">
          <div class="search-input-row">
            <input
              ref="inputEl"
              v-model="searchQuery"
              class="form-input search-input"
              placeholder="Title or ASIN…"
              @input="onInput"
              @keydown.escape="emit('close')"
              @keydown.enter="runSearch"
            />
          </div>
          <div class="search-input-row">
            <label class="region-label" for="find-match-region">Region</label>
            <select
              id="find-match-region"
              v-model="region"
              class="form-input region-select"
              data-testid="find-match-region"
              @change="runSearch"
            >
              <option v-for="o in regionOptions" :key="o.value" :value="o.value">
                {{ o.label }}
              </option>
            </select>
          </div>
          <div class="search-input-row">
            <input
              v-model="authorQuery"
              class="form-input search-input"
              placeholder="Author (optional)…"
              @input="onInput"
              @keydown.escape="emit('close')"
              @keydown.enter="runSearch"
            />
            <PhSpinner v-if="isSearching" class="ph-spin search-spinner" :size="16" />
          </div>
        </div>

        <div v-if="searchResults.length > 0" class="results-list">
          <div
            v-for="result in searchResults"
            :key="result.asin ?? result.title"
            class="result-item"
            @click="select(result)"
          >
            <img
              v-if="result.imageUrl"
              :src="getProtectedImageSrc(result.imageUrl, placeholderUrl)"
              class="result-thumb"
              alt=""
            />
            <div class="result-info">
              <span class="result-title">{{ result.title }}</span>
              <span class="result-meta">
                {{ result.authors?.[0]?.name }}
                <span v-if="result.series">
                  ·
                  {{
                    Array.isArray(result.series) ? (result.series as any)[0]?.name : result.series
                  }}</span
                >
                <span v-if="result.asin" class="result-asin"> · {{ result.asin }}</span>
              </span>
              <span v-if="extractNarrators(result)" class="result-narrator">
                Narrated by {{ extractNarrators(result) }}
              </span>
              <span
                v-if="result.lengthMinutes || result.source === 'Audiosilo'"
                class="result-edition"
                data-testid="find-match-edition"
              >
                <span v-if="result.lengthMinutes">{{ formatRuntime(result.lengthMinutes) }}</span>
                <span
                  v-if="runtimeDelta(result) !== undefined"
                  class="result-runtime-delta"
                  :class="{ 'is-match': runtimeMatches(result, item.durationSeconds) }"
                >
                  ({{ describeDelta(runtimeDelta(result)!) }})
                </span>
                <span
                  v-if="result.source === 'Audiosilo'"
                  class="result-source"
                  title="Edition from audiosilo-meta, a community catalogue that keeps recordings Audible's search no longer lists"
                  >Audiosilo</span
                >
              </span>
            </div>
          </div>
        </div>

        <div v-else-if="hasSearched && !isSearching" class="no-results">
          No results for "{{ searchQuery }}"{{ authorQuery ? ` by "${authorQuery}"` : '' }} in
          {{ regionLabel(region) }}
        </div>

        <div v-else-if="!hasSearched && !isSearching" class="hint-text">
          Type a title or paste an ASIN to search
        </div>
      </div>
    </ModalBody>
  </Modal>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onBeforeUnmount, nextTick } from 'vue'
import { PhSpinner } from '@phosphor-icons/vue'
import { Modal, ModalHeader, ModalBody } from '@/components/feedback'
import { useProtectedImages } from '@/composables/useProtectedImages'
import { useLibraryImportStore, type LibraryImportItem } from '@/stores/libraryImport'
import { orderedSearchRegionOptions, searchRegionOptions } from '@/utils/languageMapping'
import {
  buildLibraryImportInitialAuthor,
  buildLibraryImportInitialQuery,
} from '@/utils/libraryImportSearch'
import { getPlaceholderUrl } from '@/utils/placeholder'
import type { SearchResult } from '@/types'
import { extractNarrators } from '@/utils/searchResultHelpers'
import {
  formatRuntime,
  runtimeDeltaMinutes,
  runtimeMatches,
} from '@/utils/libraryImportEditions'

const props = defineProps<{ item: LibraryImportItem }>()
const emit = defineEmits<{
  close: []
  select: [result: SearchResult]
}>()

const { getProtectedImageSrc } = useProtectedImages()
const inputEl = ref<HTMLInputElement | null>(null)
const placeholderUrl = getPlaceholderUrl()
// Build the initial query: ASIN → filename stem (when more specific than folder) → folderName
// detectedTitle comes from the audio file's "album" tag which is often the series name — skip it
function initialQuery(): string {
  return buildLibraryImportInitialQuery(props.item)
}
const searchQuery = ref(initialQuery())
const authorQuery = ref(buildLibraryImportInitialAuthor(props.item))

// Runtime against the file's own length tells editions (narrators, abridged/unabridged) apart
function runtimeDelta(result: SearchResult): number | undefined {
  return runtimeDeltaMinutes(result, props.item.durationSeconds)
}

function describeDelta(delta: number): string {
  if (delta === 0) return 'same length as your file'
  return `${delta > 0 ? '+' : ''}${delta} min vs your file`
}
const searchResults = ref<SearchResult[]>([])
const isSearching = ref(false)
const hasSearched = ref(false)
const store = useLibraryImportStore()
// The configured region by default; the user can pick another one, which the selected match keeps
const configuredRegion = ref('')
const region = ref(props.item.selectedMatch?.region ?? '')
const regionOptions = computed(() =>
  orderedSearchRegionOptions([props.item.selectedMatch?.region, configuredRegion.value, 'us']),
)

function regionLabel(value: string): string {
  return searchRegionOptions.find((o) => o.value === value)?.label ?? value
}

let debounceTimer: ReturnType<typeof setTimeout> | null = null
// The search in flight: a newer one (typing, Enter, region change) aborts it, and only its own
// response may update the list, so a slow answer for another query or region never shows up
let searchController: AbortController | null = null

onBeforeUnmount(() => {
  if (debounceTimer) clearTimeout(debounceTimer)
  searchController?.abort()
})

onMounted(async () => {
  configuredRegion.value = await store.configuredRegion()
  if (!region.value) region.value = configuredRegion.value
  await nextTick()
  inputEl.value?.focus()
  inputEl.value?.select()
  if (searchQuery.value.trim()) runSearch()
})

function onInput() {
  if (debounceTimer) clearTimeout(debounceTimer)
  debounceTimer = setTimeout(() => runSearch(), 400)
}

async function runSearch() {
  const q = searchQuery.value.trim()
  if (!q) return
  if (debounceTimer) {
    clearTimeout(debounceTimer)
    debounceTimer = null
  }
  searchController?.abort()
  const controller = new AbortController()
  searchController = controller
  isSearching.value = true
  hasSearched.value = false
  // Results of the previous query or region can't be picked while this one runs
  searchResults.value = []
  // Importing waits for it (each search counts once, however it ends)
  store.findMatchSearchStarted()
  try {
    const isAsin = /^[A-Z0-9]{10}$/i.test(q)
    const params = isAsin
      ? { asin: q, cap: 5 }
      : { title: q, author: authorQuery.value.trim() || undefined, cap: 5 }
    const results = await store.searchInRegion(params, region.value || undefined, controller.signal)
    if (controller !== searchController) return
    searchResults.value = results
    hasSearched.value = true
  } catch (error) {
    // A cancelled search (a newer one, or the modal closing) fails on purpose: nothing to report.
    // The API wraps the AbortError into a generic error, so the signal tells it apart.
    if (controller === searchController && !controller.signal.aborted) throw error
  } finally {
    store.findMatchSearchEnded()
    if (controller === searchController) {
      searchController = null
      isSearching.value = false
    }
  }
}

function select(result: SearchResult) {
  emit('select', result)
  emit('close')
}
</script>

<style scoped>
.search-wrap {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.search-fields {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.5rem;
}

/* Title, region and author on one line; they wrap only when the modal is too narrow */
.search-fields > .search-input-row:first-child {
  flex: 1 1 9rem;
}

.search-fields > .search-input-row:nth-child(2) {
  flex: 0 0 auto;
}

.search-fields > .search-input-row:last-child {
  flex: 1 1 9rem;
}

.region-label {
  font-size: 0.8rem;
  color: #888;
}

.region-select {
  flex: 0 0 auto;
  width: auto;
}

.search-input-row {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.search-input {
  flex: 1;
}

.search-spinner {
  color: #888;
  flex-shrink: 0;
}

.results-list {
  max-height: 320px;
  overflow-y: auto;
  border: 1px solid #333;
  border-radius: 6px;
}

.result-item {
  display: flex;
  align-items: center;
  gap: 0.6rem;
  padding: 0.5rem 0.75rem;
  cursor: pointer;
  transition: background 0.15s;
  border-bottom: 1px solid #2a2a2a;
}

.result-item:last-child {
  border-bottom: none;
}

.result-item:hover {
  background: #2a2a2a;
}

.result-thumb {
  width: 36px;
  height: 36px;
  object-fit: cover;
  border-radius: 3px;
  flex-shrink: 0;
}

.result-info {
  min-width: 0;
  flex: 1;
}

.result-title {
  display: block;
  font-size: 0.875rem;
  color: #e0e0e0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.result-meta {
  display: block;
  font-size: 0.75rem;
  color: #888;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.result-asin {
  font-family: monospace;
  font-size: 0.7rem;
}

.result-narrator {
  display: block;
  font-size: 0.75rem;
  color: #888;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.result-edition {
  display: block;
  font-size: 0.75rem;
  color: #888;
}

.result-runtime-delta.is-match {
  color: #4caf50;
}

.result-source {
  margin-left: 0.4rem;
  padding: 0 0.35rem;
  border: 1px solid #666;
  border-radius: 3px;
  font-size: 0.65rem;
}

.no-results,
.hint-text {
  padding: 0.75rem 0;
  font-size: 0.85rem;
  color: #666;
  text-align: center;
}
</style>
