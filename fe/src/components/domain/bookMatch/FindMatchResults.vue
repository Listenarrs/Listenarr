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
     picks one, which turns green. The record the book points at today wears a "Current" tag (its
     ASIN in another region, a grey "Other region" one), and
     each record its language in the bottom right corner: green when it is the preferred search
     language (Settings, Search), grey otherwise or when Audible gives none. Editions of one book in
     a catalogue (Audible France also sells the German and US ones) then tell apart at a glance.
     Used by Find Match (Library Import) and Repair. -->
<template>
  <div class="results-list">
    <div
      v-for="searchResult in searchResults"
      :key="searchResult.asin ?? searchResult.title"
      class="result-item"
      :class="{
        selected: isSameRecord(searchResult, selectedRecordKey),
        'has-current': isSameRecord(searchResult, currentRecordKey),
      }"
      :aria-selected="isSameRecord(searchResult, selectedRecordKey)"
      data-testid="find-match-result"
      @click="emit('select', searchResult)"
    >
      <img
        v-if="searchResult.imageUrl"
        :src="getProtectedImageSrc(searchResult.imageUrl, placeholderUrl)"
        class="result-thumb"
        alt=""
      />
      <div class="result-info">
        <span class="result-title">{{ fullTitle(searchResult.title, searchResult.subtitle) }}</span>
        <span class="result-meta">
          {{ searchResult.authors?.[0]?.name }}
          <span v-if="seriesName(searchResult)"> · {{ seriesName(searchResult) }}</span>
          <span v-if="searchResult.asin" class="result-asin"> · {{ searchResult.asin }}</span>
        </span>
        <span v-if="narratorNames(searchResult)" class="result-narrator">
          Narrated by {{ narratorNames(searchResult) }}
        </span>
      </div>
      <span
        v-if="isSameRecord(searchResult, currentRecordKey)"
        class="result-current"
        data-testid="find-match-current"
      >
        Current
      </span>
      <span
        v-else-if="isOtherRegion(searchResult)"
        class="result-other-region"
        :title="otherRegionTitle(searchResult)"
        data-testid="find-match-other-region"
      >
        Other region
      </span>
      <span
        class="result-language"
        :class="{ preferred: isPreferredLanguage(searchResult) }"
        data-testid="find-match-language"
      >
        {{ languageLabel(searchResult) }}
      </span>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useProtectedImages } from '@/composables/useProtectedImages'
import { useConfigurationStore } from '@/stores/configuration'
import {
  normalizePreferredSearchLanguage,
  normalizeSearchResultLanguage,
  preferredSearchLanguageOptions,
} from '@/utils/languageMapping'
import { getPlaceholderUrl } from '@/utils/placeholder'
import { extractNarrators, type NormalizedResult } from '@/utils/searchResultHelpers'
import { fullTitle, regionLabel, type RecordKey } from '@/utils/bookMatch'
import type { SearchResult } from '@/types'

// selectedRecordKey: the record picked (green); currentRecordKey: the one the book points at
// today (tagged)
const props = defineProps<{
  searchResults: SearchResult[]
  selectedRecordKey?: RecordKey | null
  currentRecordKey?: RecordKey | null
}>()
const emit = defineEmits<{ select: [searchResult: SearchResult] }>()

const { getProtectedImageSrc } = useProtectedImages()
const placeholderUrl = getPlaceholderUrl()
const configStore = useConfigurationStore()

// The preferred search language of the settings; "all" prefers none
const preferredLanguage = computed(() =>
  normalizePreferredSearchLanguage(configStore.applicationSettings?.defaultSearchLanguage),
)

function isPreferredLanguage(searchResult: SearchResult): boolean {
  const language = normalizeSearchResultLanguage(searchResult.language)
  return !!language && preferredLanguage.value !== 'all' && language === preferredLanguage.value
}

// "French", "German"…; a language the list doesn't know keeps Audible's word, capitalised
function languageLabel(searchResult: SearchResult): string {
  const language = normalizeSearchResultLanguage(searchResult.language)
  const rawLanguage = searchResult.language?.trim()
  if (!language && !rawLanguage) return 'Language unknown'
  const languageOption = preferredSearchLanguageOptions.find(
    (candidateOption) => candidateOption.value === language,
  )
  if (languageOption) return languageOption.label
  return rawLanguage!.charAt(0).toUpperCase() + rawLanguage!.slice(1)
}

function isSameRecord(
  searchResult: SearchResult,
  recordKey: RecordKey | null | undefined,
): boolean {
  if (!recordKey?.asin || searchResult.asin?.toUpperCase() !== recordKey.asin.toUpperCase())
    return false
  return !recordKey.region || !searchResult.region || searchResult.region === recordKey.region
}

// The book's ASIN, found in another catalogue than the one it points at: a different record
function isOtherRegion(searchResult: SearchResult): boolean {
  const currentRecordKey = props.currentRecordKey
  if (!currentRecordKey?.asin || !currentRecordKey.region || !searchResult.region) return false
  return (
    searchResult.asin?.toUpperCase() === currentRecordKey.asin.toUpperCase() &&
    searchResult.region.toLowerCase() !== currentRecordKey.region.toLowerCase()
  )
}

function otherRegionTitle(searchResult: SearchResult): string {
  return (
    `Same ASIN as the book's current record, but in ${regionLabel(searchResult.region!)}, ` +
    `not ${regionLabel(props.currentRecordKey!.region!)}`
  )
}

function narratorNames(searchResult: SearchResult): string {
  return extractNarrators(searchResult as unknown as NormalizedResult)
}

// The search returns the series as a list of { name } or as a name
function seriesName(searchResult: SearchResult): string | undefined {
  const series = searchResult.series as unknown
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
  position: relative;
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

.result-item.selected {
  background: color-mix(in srgb, var(--status-success) 12%, transparent);
}

.result-thumb {
  width: 36px;
  height: 36px;
  object-fit: cover;
  border-radius: 3px;
  flex-shrink: 0;
}

/* Room for the tags on the right, so a long title stops before them */
.result-info {
  min-width: 0;
  flex: 1;
  padding-right: 6.5rem;
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
.result-current,
.result-other-region,
.result-language {
  position: absolute;
  right: 0.6rem;
  padding: 0.1rem 0.45rem;
  border-radius: 999px;
  font-size: 0.7rem;
  white-space: nowrap;
}

.result-other-region {
  top: 0.4rem;
  border: 1px solid #555;
  color: #aaa;
}

.result-language {
  bottom: 0.4rem;
  border: 1px solid #555;
  color: #aaa;
}

.result-language.preferred {
  border-color: var(--status-success);
  color: var(--status-success);
  background: color-mix(in srgb, var(--status-success) 12%, transparent);
}

.result-current {
  top: 0.4rem;
  border: 1px solid var(--status-success);
  color: var(--status-success);
  background: color-mix(in srgb, var(--status-success) 12%, transparent);
}
</style>
