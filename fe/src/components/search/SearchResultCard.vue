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
  <div class="title-result-card">
    <!-- Cover Image -->
    <div class="result-poster">
      <img
        v-if="coverUrl"
        :src="coverUrl"
        :alt="book.title"
        loading="lazy"
        decoding="async"
        @error="$emit('image-error')"
      />
      <template v-else>
        <img
          :src="placeholderUrl"
          alt="Cover unavailable"
          loading="lazy"
          class="placeholder-cover-image"
          decoding="async"
        />
      </template>
    </div>

    <!-- Result Info -->
    <div class="result-info">
      <!-- Title and Subtitle -->
      <slot name="title">
        <h3>{{ safeText(book.title) }}</h3>
        <p v-if="book.searchResult?.subtitle" class="result-subtitle">
          {{ safeText(book.searchResult.subtitle) }}
        </p>
      </slot>

      <!-- Author -->
      <slot name="author">
        <p class="result-author">by {{ formatAuthors(book) }}</p>
      </slot>

      <!-- Narrator -->
      <slot name="narrator">
        <p v-if="book.searchResult?.narrator" class="result-narrator">
          Narrated by {{ book.searchResult.narrator }}
        </p>
      </slot>

      <!-- Stats (Runtime, Language) -->
      <slot name="stats">
        <div class="result-stats">
          <span v-if="book.searchResult?.runtime" class="stat-item">
            <PhClock />
            {{ formatRuntime(book.searchResult?.lengthMinutes ?? book.searchResult?.runtime ?? 0) }}
          </span>
          <span v-if="book.searchResult?.language" class="stat-item">
            <PhGlobe />
            {{ capitalizeLanguage(book.searchResult.language) }}
          </span>
        </div>
      </slot>

      <!-- Series -->
      <slot name="series">
        <div
          v-if="
            (typeof book.searchResult?.series === 'string' &&
              book.searchResult.series.trim().length > 0) ||
            (Array.isArray(book.seriesList) &&
              book.seriesList.some((s) => typeof s === 'string' && s.trim().length > 0))
          "
          class="result-series"
        >
          <!-- Debug output: log series and seriesList values -->
          <pre
            style="
              font-size: 10px;
              color: #888;
              background: #f9f9f9;
              padding: 2px 4px;
              margin-bottom: 2px;
            "
          >
            series: {{ JSON.stringify(book.searchResult?.series) }} | seriesList: {{
              JSON.stringify(book.seriesList)
            }}
          </pre>
          <span
            class="series-badge"
            :title="
              book.searchResult?.seriesList?.length
                ? book.searchResult.seriesList.join(', ')
                : `${book.searchResult?.series}${book.searchResult?.seriesNumber ? ` #${book.searchResult.seriesNumber}` : ''}`
            "
          >
            <PhBook />
            {{ safeText(book.searchResult?.series ?? (book.seriesList && book.seriesList[0]))
            }}<span v-if="book.searchResult?.seriesNumber">
              #{{ book.searchResult.seriesNumber }}</span
            >
          </span>
        </div>
      </slot>

      <!-- Metadata Badges -->
      <slot name="metadata">
        <div class="metadata-badges">
          <span v-if="book.publisher?.length" class="metadata-badge">
            <PhBuilding />
            {{ safeText(book.publisher[0]) }}
          </span>
          <span v-if="book.searchResult?.publishedDate" class="metadata-badge">
            <PhCalendar />
            {{ formatDate(book.searchResult.publishedDate) }}
          </span>
          <span v-else-if="book.first_publish_year" class="metadata-badge">
            <PhCalendar />
            {{ book.first_publish_year }}
          </span>
          <span v-if="asin" class="metadata-badge">
            <PhBarcode />
            {{ asin }}
          </span>
          <span v-if="openLibraryId && !asin" class="metadata-badge">
            <PhBarcode />
            {{ openLibraryId }}
          </span>
        </div>
      </slot>

      <!-- Metadata Source Links -->
      <slot name="meta-links">
        <div class="result-meta">
          <a
            v-if="sharedMetadataSourceUrl"
            :href="sharedMetadataSourceUrl"
            target="_blank"
            rel="noopener noreferrer"
            class="metadata-source-link source-link combined-source-link"
            :data-source="metadataSource"
          >
            <PhGlobe />
            <PhCloud />
            {{ metadataSourceLabel }}
          </a>
          <template v-else>
            <a
              v-if="metadataSource && metadataSourceUrl"
              :href="metadataSourceUrl"
              target="_blank"
              rel="noopener noreferrer"
              class="metadata-source-link"
              :data-source="metadataSource"
            >
              <PhGlobe />
              {{ metadataSourceLabel }}
            </a>
            <span
              v-else-if="metadataSource"
              class="metadata-source-badge"
              :data-source="metadataSource"
            >
              <PhGlobe />
              {{ metadataSourceLabel }}
            </span>

            <a
              v-if="sourceUrl"
              :href="sourceUrl"
              target="_blank"
              rel="noopener noreferrer"
              class="source-link"
            >
              <PhCloud />
              {{ sourceLabel }}
            </a>
            <span v-else-if="book.searchResult?.source" class="source-badge">
              <PhCloud />
              Source: {{ book.searchResult.source }}
            </span>
          </template>
        </div>
      </slot>
    </div>

    <!-- Action Buttons -->
    <div class="result-actions">
      <slot name="actions">
        <button
          :class="['btn', isAdded ? 'btn-success' : 'btn-primary']"
          @click="$emit('add')"
          :disabled="isAdded"
        >
          <component :is="isAdded ? PhCheck : PhPlus" />
          {{ isAdded ? 'Added' : 'Add to Library' }}
        </button>
      </slot>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import {
  PhClock,
  PhGlobe,
  PhBook,
  PhBuilding,
  PhCalendar,
  PhBarcode,
  PhCloud,
  PhPlus,
  PhCheck,
} from '@phosphor-icons/vue'
import type { SearchResult } from '@/types'
import type { OpenLibraryBook } from '@/services/openlibrary'
import { safeText } from '@/utils/textUtils'
import { formatRuntime, formatDate, capitalizeLanguage } from '@/utils/searchResultFormatting'
import { getPlaceholderUrl } from '@/utils/placeholder'

export interface SearchResultCardProps {
  /** The book/search result to display */
  book: OpenLibraryBook & {
    searchResult?: SearchResult
    imageUrl?: string
    metadataSource?: string
  }
  /** Cover image URL (if available) */
  coverUrl?: string
  /** Whether the book is already in the library */
  isAdded?: boolean
  /** Optional metadata source URL */
  metadataSourceUrl?: string
  /** Optional product/source URL */
  sourceUrl?: string
}

const props = withDefaults(defineProps<SearchResultCardProps>(), {
  isAdded: false,
  coverUrl: undefined,
  metadataSourceUrl: undefined,
  sourceUrl: undefined,
})

defineEmits<{
  add: []
  'image-error': []
}>()

const placeholderUrl = getPlaceholderUrl()

/**
 * Get the primary ID (ASIN preferred, fallback to OpenLibrary ID)
 */
const asin = computed(() => {
  const book = props.book as unknown as Record<string, unknown>
  return (
    (book['asin'] as string | undefined) ||
    props.book.searchResult?.asin ||
    (props.book.key && !props.book.key.startsWith('OL') ? props.book.key : undefined)
  )
})

/**
 * Get OpenLibrary ID if available and no ASIN
 */
const openLibraryId = computed(() => {
  if (asin.value) return undefined
  return props.book.searchResult?.id || props.book.key || undefined
})

const nestedSearchResult = computed(() => {
  const nested = (props.book.searchResult as unknown as Record<string, unknown> | undefined)?.[
    'searchResult'
  ]
  return nested && typeof nested === 'object' ? (nested as Record<string, unknown>) : undefined
})

/**
 * Format author names from various sources
 */
const formatAuthors = (book: typeof props.book): string => {
  // If author_name is explicitly provided and is an array, respect it (including empty array -> Unknown)
  if (book.author_name !== undefined) {
    if (Array.isArray(book.author_name)) {
      if (book.author_name.length) return book.author_name.slice(0, 2).join(', ')
      return 'Unknown Author'
    }
    if (typeof book.author_name === 'string' && book.author_name.trim())
      return book.author_name.trim()
  }

  // Fallback to searchResult authors when author_name was not explicitly provided
  if (props.book.searchResult?.authors && props.book.searchResult.authors.length) {
    return props.book.searchResult.authors
      .map((a: unknown) => {
        const obj = a as Record<string, unknown>
        return (obj?.name as string) || String(a)
      })
      .slice(0, 2)
      .join(', ')
  }
  return 'Unknown Author'
}

/**
 * Check if URL is from Audible domain
 */
const isAudibleHost = (url?: string): boolean => {
  if (!url) return false
  try {
    const host = new URL(url, window.location.origin).hostname.toLowerCase()
    return host.startsWith('audible.') || host.startsWith('www.audible.')
  } catch {
    return false
  }
}

const areSameUrl = (left?: string, right?: string): boolean => {
  if (!left || !right) return false

  try {
    return (
      new URL(left, window.location.origin).href === new URL(right, window.location.origin).href
    )
  } catch {
    return left === right
  }
}

/**
 * Generate metadata source label
 */
const metadataSourceLabel = computed((): string => {
  if (!metadataSource.value) return ''
  const source = metadataSource.value.toLowerCase()
  if (source.includes('audible')) return 'Audible'
  return `Metadata: ${metadataSource.value}`
})

const metadataSource = computed((): string | undefined => {
  const source =
    props.book.metadataSource ??
    props.book.searchResult?.metadataSource ??
    (nestedSearchResult.value?.['metadataSource'] as string | undefined)

  return typeof source === 'string' && source.trim() ? source : undefined
})

const sharedMetadataSourceUrl = computed((): string | undefined => {
  if (!metadataSource.value) return undefined
  return areSameUrl(props.metadataSourceUrl, props.sourceUrl) ? props.metadataSourceUrl : undefined
})

/**
 * Generate source label
 */
const sourceLabel = computed((): string => {
  if (!props.sourceUrl) return ''
  if (isAudibleHost(props.sourceUrl)) return 'Audible'
  return (
    props.book.searchResult?.source ||
    (nestedSearchResult.value?.['source'] as string | undefined) ||
    metadataSource.value ||
    'OpenLibrary'
  )
})
</script>

<style scoped>
.title-result-card {
  display: grid;
  grid-template-columns: 120px 1fr 200px;
  grid-gap: 20px;
  padding: 16px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  background-color: var(--color-surface);
  transition: all 0.2s ease;
}

.title-result-card:hover {
  border-color: var(--color-accent);
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.1);
}

.result-poster {
  display: flex;
  justify-content: center;
  align-items: flex-start;
  min-width: 120px;
}

.result-poster img {
  width: 120px;
  height: auto;
  max-height: 180px;
  border-radius: 4px;
  object-fit: cover;
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.15);
}

.placeholder-cover-image {
  background-color: var(--color-placeholder-bg, #f0f0f0);
  color: var(--color-placeholder-text, #999);
}

.result-info {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
}

.result-info h3 {
  margin: 0;
  font-size: 16px;
  font-weight: 600;
  line-height: 1.4;
  overflow: hidden;
  text-overflow: ellipsis;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  line-clamp: 2;
  -webkit-box-orient: vertical;
}

.result-subtitle {
  margin: 0;
  font-size: 13px;
  color: var(--color-text-secondary);
  overflow: hidden;
  text-overflow: ellipsis;
  display: -webkit-box;
  -webkit-line-clamp: 1;
  line-clamp: 1;
  -webkit-box-orient: vertical;
}

.result-author {
  margin: 0;
  font-size: 13px;
  color: var(--color-text-secondary);
}

.result-narrator {
  margin: 0;
  font-size: 12px;
  color: var(--color-text-tertiary);
  font-style: italic;
}

.result-stats {
  display: flex;
  gap: 12px;
  font-size: 12px;
  color: var(--color-text-secondary);
  flex-wrap: wrap;
}

.stat-item {
  display: flex;
  align-items: center;
  gap: 4px;
}

.stat-item svg {
  width: 14px;
  height: 14px;
}

.result-series {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
  margin-top: 4px;
}

.series-badge {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3px 8px;
  background-color: var(--color-series-bg, rgba(33, 150, 243, 0.1));
  color: var(--color-series-text, #2196f3);
  border-radius: 12px;
  font-size: 11px;
  font-weight: 500;
  white-space: nowrap;
}

.series-badge svg {
  width: 12px;
  height: 12px;
}

.metadata-badges {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  margin-top: 4px;
}

.metadata-badge {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 6px;
  background-color: var(--color-metadata-bg, #f5f5f5);
  color: var(--color-metadata-text, #666);
  border-radius: 4px;
  font-size: 11px;
  white-space: nowrap;
}

.metadata-badge svg {
  width: 12px;
  height: 12px;
}

.result-meta {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  margin-top: 4px;
  font-size: 12px;
}

.metadata-source-link,
.source-link {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: var(--color-link, #2196f3);
  text-decoration: none;
  transition: color 0.2s ease;
}

.metadata-source-link:hover,
.source-link:hover {
  color: var(--color-link-hover, #1976d2);
  text-decoration: underline;
}

.metadata-source-link svg,
.source-link svg {
  width: 12px;
  height: 12px;
}

.metadata-source-badge,
.source-badge {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: var(--color-badge-text, #999);
}

.metadata-source-badge svg,
.source-badge svg {
  width: 12px;
  height: 12px;
}

.result-actions {
  display: flex;
  flex-direction: column;
  gap: 8px;
  justify-content: flex-start;
}

.btn {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 8px 12px;
  border: 1px solid transparent;
  border-radius: 4px;
  font-size: 13px;
  font-weight: 500;
  cursor: pointer;
  transition: all 0.2s ease;
  white-space: nowrap;
  min-height: 32px;
}

.btn:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.btn-primary {
  background-color: var(--color-primary, #2196f3);
  color: white;
  border-color: var(--color-primary, #2196f3);
}

.btn-primary:hover:not(:disabled) {
  background-color: var(--color-primary-hover, #1976d2);
  border-color: var(--color-primary-hover, #1976d2);
}

.btn-success {
  background-color: var(--color-success, #4caf50);
  color: white;
  border-color: var(--color-success, #4caf50);
}

.btn-secondary {
  background-color: var(--color-secondary-bg, #f5f5f5);
  color: var(--color-secondary-text, #333);
  border-color: var(--color-border, #ddd);
}

.btn-secondary:hover:not(:disabled) {
  background-color: var(--color-secondary-bg-hover, #efefef);
  border-color: var(--color-border-hover, #ccc);
}

.btn svg {
  width: 16px;
  height: 16px;
}

/* Responsive */
@media (max-width: 768px) {
  .title-result-card {
    grid-template-columns: 80px 1fr;
    gap: 12px;
  }

  .result-poster {
    grid-row: 1 / 3;
  }

  .result-poster img {
    width: 80px;
    max-height: 120px;
  }

  .result-actions {
    grid-column: 1 / 3;
    flex-direction: row;
    gap: 6px;
    margin-top: 8px;
  }

  .btn {
    flex: 1;
  }

  .result-info h3 {
    font-size: 14px;
  }
}

@media (max-width: 480px) {
  .title-result-card {
    grid-template-columns: 1fr;
  }

  .result-poster {
    grid-row: auto;
    grid-column: 1;
    max-width: 100%;
  }

  .result-poster img {
    width: 100%;
    max-width: 120px;
    max-height: 180px;
  }

  .result-actions {
    grid-column: 1;
  }

  .metadata-badges,
  .result-series {
    gap: 4px;
  }
}
</style>
