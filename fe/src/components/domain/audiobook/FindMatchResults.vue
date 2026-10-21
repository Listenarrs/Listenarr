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
<!-- The results of a Find Match search: cover, title, author, series, ASIN and narrators; a click
     picks one. Used by Find Match (Library Import) and Repair. -->
<template>
  <div class="results-list">
    <div
      v-for="result in results"
      :key="result.asin ?? result.title"
      class="result-item"
      data-testid="find-match-result"
      @click="emit('select', result)"
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
          <span v-if="seriesName(result)"> · {{ seriesName(result) }}</span>
          <span v-if="result.asin" class="result-asin"> · {{ result.asin }}</span>
        </span>
        <span v-if="narrators(result)" class="result-narrator">
          Narrated by {{ narrators(result) }}
        </span>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { useProtectedImages } from '@/composables/useProtectedImages'
import { getPlaceholderUrl } from '@/utils/placeholder'
import { extractNarrators, type NormalizedResult } from '@/utils/searchResultHelpers'
import type { SearchResult } from '@/types'

defineProps<{ results: SearchResult[] }>()
const emit = defineEmits<{ select: [result: SearchResult] }>()

const { getProtectedImageSrc } = useProtectedImages()
const placeholderUrl = getPlaceholderUrl()

function narrators(result: SearchResult): string {
  return extractNarrators(result as unknown as NormalizedResult)
}

// The search returns the series as a list of { name } or as a name
function seriesName(result: SearchResult): string | undefined {
  const series = result.series as unknown
  if (Array.isArray(series)) return (series[0] as { name?: string } | undefined)?.name
  return typeof series === 'string' ? series : undefined
}
</script>

<style scoped>
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
</style>
