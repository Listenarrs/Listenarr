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
        <!-- Title (or ASIN), author and region, each named above its field, on one line; they
             wrap only when the modal is too narrow -->
        <div class="search-fields">
          <label class="search-field search-field-query">
            <span class="search-field-label">
              Title or ASIN
              <PhSpinner v-if="isSearching" class="ph-spin search-spinner" :size="12" />
            </span>
            <input
              ref="inputEl"
              v-model="searchQuery"
              class="form-input search-input"
              placeholder="Title or ASIN…"
              data-testid="find-match-query"
              @input="onInput"
              @keydown.escape="emit('close')"
              @keydown.enter="runSearch"
            />
          </label>
          <label class="search-field search-field-author">
            <span class="search-field-label">Author</span>
            <input
              v-model="authorQuery"
              class="form-input search-input"
              placeholder="Author (optional)…"
              data-testid="find-match-author"
              @input="onInput"
              @keydown.escape="emit('close')"
              @keydown.enter="runSearch"
            />
          </label>
          <label class="search-field search-field-region">
            <span class="search-field-label">Region</span>
            <select
              v-model="region"
              class="form-input region-select"
              data-testid="find-match-region"
              @change="runSearch"
            >
              <option v-for="o in regionOptions" :key="o.value" :value="o.value">
                {{ o.label }}
              </option>
            </select>
          </label>
        </div>

        <FindMatchResults
          v-if="searchResults.length > 0"
          :results="searchResults"
          @select="select"
        />

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
import FindMatchResults from './FindMatchResults.vue'
import { useLibraryImportStore, type LibraryImportItem } from '@/stores/libraryImport'
import { orderedSearchRegionOptions, searchRegionOptions } from '@/utils/languageMapping'
import {
  buildLibraryImportInitialAuthor,
  buildLibraryImportInitialQuery,
} from '@/utils/libraryImportSearch'
import type { SearchResult } from '@/types'

const props = defineProps<{ item: LibraryImportItem }>()
const emit = defineEmits<{
  close: []
  select: [result: SearchResult]
}>()

const inputEl = ref<HTMLInputElement | null>(null)
// Build the initial query: ASIN → filename stem (when more specific than folder) → folderName
// detectedTitle comes from the audio file's "album" tag which is often the series name — skip it
const searchQuery = ref(buildLibraryImportInitialQuery(props.item))
const authorQuery = ref(buildLibraryImportInitialAuthor(props.item))
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
  align-items: flex-end;
  gap: 0.5rem;
}

.search-field {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
  min-width: 0;
}

.search-field-query {
  flex: 2 1 12rem;
}

.search-field-author {
  flex: 1 1 9rem;
}

.search-field-region {
  flex: 0 0 auto;
}

.search-field-label {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  font-size: 0.8rem;
  color: #aaa;
}

.search-input {
  width: 100%;
  box-sizing: border-box;
}

.search-spinner {
  color: #888;
  flex-shrink: 0;
}

.no-results,
.hint-text {
  padding: 0.75rem 0;
  font-size: 0.85rem;
  color: #666;
  text-align: center;
}
</style>
