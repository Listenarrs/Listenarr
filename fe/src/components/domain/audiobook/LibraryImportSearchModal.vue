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
            </div>
          </div>
        </div>

        <div v-else-if="hasSearched && !isSearching" class="no-results">
          No results for "{{ searchQuery }}"{{ authorQuery ? ` by "${authorQuery}"` : '' }}
        </div>

        <div v-else-if="!hasSearched && !isSearching" class="hint-text">
          Type a title or paste an ASIN to search
        </div>
      </div>
    </ModalBody>
  </Modal>
</template>

<script setup lang="ts">
import { ref, onMounted, nextTick } from 'vue'
import { PhSpinner } from '@phosphor-icons/vue'
import { Modal, ModalHeader, ModalBody } from '@/components/feedback'
import { apiService } from '@/services/api'
import { useProtectedImages } from '@/composables/useProtectedImages'
import type { LibraryImportItem } from '@/stores/libraryImport'
import {
  buildLibraryImportInitialAuthor,
  buildLibraryImportInitialQuery,
} from '@/utils/libraryImportSearch'
import { getPlaceholderUrl } from '@/utils/placeholder'
import type { SearchResult } from '@/types'
import { extractNarrators } from '@/utils/searchResultHelpers'

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
const searchResults = ref<SearchResult[]>([])
const isSearching = ref(false)
const hasSearched = ref(false)

let debounceTimer: ReturnType<typeof setTimeout> | null = null

onMounted(async () => {
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
  isSearching.value = true
  hasSearched.value = false
  try {
    const isAsin = /^[A-Z0-9]{10}$/i.test(q)
    const params = isAsin
      ? { asin: q, cap: 5 }
      : { title: q, author: authorQuery.value.trim() || undefined, cap: 5 }
    searchResults.value = await apiService.advancedSearch(params)
    hasSearched.value = true
  } finally {
    isSearching.value = false
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
  flex-direction: column;
  gap: 0.4rem;
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

.no-results,
.hint-text {
  padding: 0.75rem 0;
  font-size: 0.85rem;
  color: #666;
  text-align: center;
}
</style>
