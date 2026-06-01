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
  <Modal :visible="visible" size="lg" @close="closeModal">
    <template #header>
      <ModalHeader :title="'Add to Library'" @close="closeModal" />
    </template>

    <template #default>
      <ModalBody>
        <div ref="modalRef" class="add-library-modal-content" tabindex="-1">
          <div class="book-layout">
            <!-- Book Image -->
            <div class="book-image">
              <div class="image-viewport">
                <img
                  v-if="imageSrc"
                  :src="imageSrc"
                  :alt="currentMetadata.title || 'Audiobook cover'"
                  loading="lazy"
                  @error="onImageError"
                  @load="onImageLoad"
                  :aria-hidden="!currentMetadata.title"
                />
                <div v-else class="placeholder-cover">
                  <PhImage />
                  <span>No Cover</span>
                </div>
              </div>
            </div>

            <!-- Book Details -->
            <div class="book-details">
              <div v-if="!showMetadataEditor" class="detail-section detail-header">
                <div>
                  <h3>{{ currentMetadata.title }}</h3>
                  <p v-if="currentMetadata.authors?.length" class="authors">
                    by {{ currentMetadata.authors.join(', ') }}
                  </p>
                  <p v-if="currentMetadata.narrators?.length" class="narrators">
                    Narrated by {{ currentMetadata.narrators.join(', ') }}
                  </p>
                </div>
                <button
                  v-if="editableMetadata"
                  type="button"
                  class="btn btn-secondary metadata-toggle-btn"
                  @click="toggleMetadataEditor"
                >
                  <PhPencilSimple />
                  Edit Metadata
                </button>
              </div>

              <template v-if="!showMetadataEditor">
                <div v-if="currentMetadata.description" class="detail-section">
                  <h4>Description</h4>
                  <div class="description">
                    {{ stripHtmlAndNormalize(currentMetadata.description) }}
                  </div>
                </div>

                <div class="detail-section" id="add-library-desc">
                  <h4>Publication Information</h4>
                  <div class="detail-grid">
                    <div v-if="currentMetadata.publisher" class="detail-item">
                      <span class="label">Publisher:</span>
                      <span class="value">{{ currentMetadata.publisher }}</span>
                    </div>
                    <div v-if="publishDate" class="detail-item">
                      <span class="label">Release Date:</span>
                      <span class="value">{{ formatDate(publishDate) }}</span>
                    </div>
                    <div v-else-if="publishYear" class="detail-item">
                      <span class="label">Release Date:</span>
                      <span class="value">{{ publishYear }}</span>
                    </div>
                    <div v-if="currentMetadata.language" class="detail-item">
                      <span class="label">Language:</span>
                      <span class="value">{{ capitalizeFirst(currentMetadata.language) }}</span>
                    </div>
                    <div v-if="currentMetadata.runtime" class="detail-item">
                      <span class="label">Listening Length:</span>
                      <span class="value">{{ formatRuntime(currentMetadata.runtime) }}</span>
                    </div>
                    <div v-if="currentMetadata.edition" class="detail-item">
                      <span class="label">Edition:</span>
                      <span class="value">{{ currentMetadata.edition }}</span>
                    </div>
                    <div v-if="currentMetadata.version" class="detail-item">
                      <span class="label">Version:</span>
                      <span class="value">{{ currentMetadata.version }}</span>
                    </div>
                  </div>
                </div>

                <div class="detail-section">
                  <h4>Identifiers</h4>
                  <div class="detail-grid">
                    <div v-if="normalizedSourceName" class="detail-item">
                      <span class="label">Metadata Source:</span>
                      <span class="value">
                        <a
                          v-if="audibleSourceUrl"
                          :href="audibleSourceUrl"
                          target="_blank"
                          rel="noopener noreferrer"
                        >
                          {{ normalizedSourceName }}
                        </a>
                        <span v-else>{{ normalizedSourceName }}</span>
                      </span>
                    </div>
                    <div v-if="currentMetadata.asin" class="detail-item">
                      <span class="label">ASIN:</span>
                      <span class="value">
                        <a :href="audibleProductUrl" target="_blank" rel="noopener noreferrer">
                          {{ currentMetadata.asin }}
                        </a>
                      </span>
                    </div>
                    <div v-if="currentMetadata.isbn" class="detail-item">
                      <span class="label">ISBN:</span>
                      <span class="value">{{ currentMetadata.isbn }}</span>
                    </div>
                    <div v-if="currentMetadata.openLibraryId && openLibraryUrl" class="detail-item">
                      <span class="label">OpenLibrary ID:</span>
                      <span class="value">
                        <a :href="openLibraryUrl" target="_blank" rel="noopener noreferrer">
                          {{ currentMetadata.openLibraryId }}
                        </a>
                      </span>
                    </div>
                  </div>
                </div>

                <div
                  v-if="displaySeriesMemberships.length || displayGenres.length"
                  class="detail-section"
                >
                  <h4>Series & Genre Information</h4>
                  <div class="detail-grid">
                    <div
                      v-if="displaySeriesMemberships.length"
                      class="detail-item detail-item--wide"
                    >
                      <span class="label">Series:</span>
                      <div class="value series-membership-list">
                        <span
                          v-for="(membership, index) in displaySeriesMemberships"
                          :key="`${membership.seriesName}-${membership.seriesNumber || index}`"
                          class="series-membership-pill"
                        >
                          {{ membership.seriesName
                          }}<span v-if="membership.seriesNumber">
                            #{{ membership.seriesNumber }}</span
                          >
                          <span v-if="membership.isPrimary" class="series-membership-primary"
                            >Primary</span
                          >
                        </span>
                      </div>
                    </div>
                    <div v-if="displayGenres.length" class="detail-item">
                      <span class="label">Genres:</span>
                      <span class="value">{{ displayGenres.join(', ') }}</span>
                    </div>
                  </div>
                </div>

                <div v-if="hasFlags" class="detail-section">
                  <h4>Content Flags</h4>
                  <div class="flags">
                    <span v-if="currentMetadata.explicit" class="flag explicit">Explicit</span>
                    <span v-if="currentMetadata.abridged" class="flag abridged">Abridged</span>
                  </div>
                </div>
              </template>

              <div
                v-if="editableMetadata && showMetadataEditor"
                class="detail-section metadata-editor"
              >
                <div class="metadata-editor-header">
                  <h4>Edit Metadata</h4>
                  <button
                    type="button"
                    class="btn btn-secondary metadata-toggle-btn"
                    @click="toggleMetadataEditor"
                  >
                    <PhEye />
                    View Details
                  </button>
                </div>
                <div class="metadata-edit-grid">
                  <div class="detail-item detail-item--wide">
                    <span class="label">Title</span>
                    <input v-model="editableMetadata.title" type="text" class="form-input" />
                  </div>
                  <div class="detail-item detail-item--wide">
                    <span class="label">Subtitle</span>
                    <input v-model="editableMetadata.subtitle" type="text" class="form-input" />
                  </div>
                  <div class="detail-item">
                    <span class="label">Edition</span>
                    <input
                      v-model="editableMetadata.edition"
                      type="text"
                      class="form-input"
                      placeholder="e.g. Revised Edition"
                    />
                  </div>
                  <div class="detail-item">
                    <span class="label">Version</span>
                    <input
                      v-model="editableMetadata.version"
                      type="text"
                      class="form-input"
                      placeholder="Source/version label"
                    />
                  </div>
                  <div class="detail-item detail-item--wide">
                    <span class="label">Authors</span>
                    <input
                      v-model="authorsInput"
                      type="text"
                      class="form-input"
                      placeholder="Comma-separated authors"
                    />
                  </div>
                  <div class="detail-item detail-item--wide">
                    <span class="label">Narrators</span>
                    <input
                      v-model="narratorsInput"
                      type="text"
                      class="form-input"
                      placeholder="Comma-separated narrators"
                    />
                  </div>
                  <div class="detail-item detail-item--full">
                    <span class="label">Description</span>
                    <textarea
                      v-model="editableMetadata.description"
                      rows="5"
                      class="form-input metadata-textarea"
                    />
                  </div>
                  <div class="detail-item">
                    <span class="label">Publisher</span>
                    <input v-model="editableMetadata.publisher" type="text" class="form-input" />
                  </div>
                  <div class="detail-item">
                    <span class="label">Language</span>
                    <input v-model="editableMetadata.language" type="text" class="form-input" />
                  </div>
                  <div class="detail-item">
                    <span class="label">Release Date</span>
                    <input
                      v-model="editableMetadata.publishedDate"
                      type="text"
                      class="form-input"
                      placeholder="YYYY-MM-DD"
                    />
                  </div>
                  <div class="detail-item">
                    <span class="label">Publish Year</span>
                    <input
                      v-model="editableMetadata.publishYear"
                      type="text"
                      class="form-input"
                      placeholder="YYYY"
                    />
                  </div>
                  <div class="detail-item">
                    <span class="label">Listening Length (minutes)</span>
                    <input
                      v-model.number="editableMetadata.runtime"
                      type="number"
                      min="0"
                      class="form-input"
                    />
                  </div>
                  <div class="detail-item">
                    <span class="label">Series</span>
                    <input v-model="editableMetadata.series" type="text" class="form-input" />
                  </div>
                  <div class="detail-item">
                    <span class="label">Series Number</span>
                    <input v-model="editableMetadata.seriesNumber" type="text" class="form-input" />
                  </div>
                  <div class="detail-item detail-item--wide">
                    <span class="label">Genres</span>
                    <input
                      v-model="genresInput"
                      type="text"
                      class="form-input"
                      placeholder="Comma-separated genres"
                    />
                  </div>
                  <div class="detail-item">
                    <span class="label">ASIN</span>
                    <input v-model="editableMetadata.asin" type="text" class="form-input" />
                  </div>
                  <div class="detail-item">
                    <span class="label">ISBN</span>
                    <input v-model="editableMetadata.isbn" type="text" class="form-input" />
                  </div>
                  <div class="detail-item">
                    <span class="label">OpenLibrary ID</span>
                    <input
                      v-model="editableMetadata.openLibraryId"
                      type="text"
                      class="form-input"
                    />
                  </div>
                  <div class="detail-item detail-item--wide">
                    <span class="label">Cover Image URL</span>
                    <input v-model="editableMetadata.imageUrl" type="text" class="form-input" />
                  </div>
                </div>
                <div class="checkbox-group metadata-flags">
                  <Checkbox v-model="editableMetadata.explicit">
                    <strong>Explicit</strong>
                    <small>Mark this release as explicit content</small>
                  </Checkbox>
                  <Checkbox v-model="editableMetadata.abridged">
                    <strong>Abridged</strong>
                    <small>Mark this release as abridged</small>
                  </Checkbox>
                </div>
              </div>
            </div>
          </div>

          <!-- Customization Options -->
          <div class="detail-section library-options">
            <h4>Library Options</h4>

            <FormRow>
              <div class="checkbox-group">
                <Checkbox v-model="options.monitored">
                  <strong>Monitor for new releases</strong>
                  <small>Automatically search for better quality versions of this audiobook</small>
                </Checkbox>
              </div>
            </FormRow>

            <FormRow>
              <div class="checkbox-group">
                <Checkbox v-model="options.autoSearch">
                  <strong>Search for downloads immediately</strong>
                  <small
                    >Start searching for available downloads right after adding to library</small
                  >
                </Checkbox>
              </div>
            </FormRow>

            <div class="option-group">
              <label class="form-label">Destination</label>
              <div class="form-control-card">
                <div class="destination-display">
                  <div class="destination-row">
                    <div class="root-select">
                      <RootFolderSelect
                        v-model:rootId="selectedRootId"
                        v-model:customPath="customRootPath"
                        hideLabel
                      />
                    </div>
                    <input
                      v-if="selectedRootId === 0"
                      type="text"
                      v-model="customRootPath"
                      class="form-input custom-path-input"
                      placeholder="e.g. C:\\Audiobooks or /mnt/audiobooks"
                    />
                    <input
                      v-else
                      type="text"
                      v-model="options.relativePath"
                      class="form-input relative-input"
                      placeholder="e.g. Author/Title"
                      @input="onRelativePathInput"
                    />
                  </div>
                  <small class="form-help" v-if="selectedRootId === 0">
                    Enter an absolute path where files will be stored
                  </small>
                  <small class="form-help" v-else>
                    Select a named root (or custom path) and edit the path relative to it on the
                    right.
                  </small>
                  <!-- Path length warning -->
                  <div v-if="destinationPathWarning" class="path-length-warning">
                    <PhWarning :size="16" />
                    <span>{{ destinationPathWarning }}</span>
                  </div>
                </div>
              </div>
            </div>

            <div class="option-group">
              <label class="form-label">Quality Profile</label>
              <select v-model="options.qualityProfileId" class="form-select">
                <option :value="null">Use Default Profile</option>
                <option v-for="profile in qualityProfiles" :key="profile.id" :value="profile.id">
                  {{ profile.name }}{{ profile.isDefault ? ' (Default)' : '' }}
                </option>
              </select>
              <small class="form-help">
                Choose which quality profile to use for automatic downloads. Leave as "Use Default
                Profile" to automatically use the default profile.
              </small>
            </div>
          </div>
        </div>
      </ModalBody>
    </template>

    <template #footer>
      <button class="btn btn-secondary" @click="closeModal">
        <PhX />
        Cancel
      </button>
      <button class="btn btn-primary" @click="addToLibrary" :disabled="isAdding || metadataLoading">
        <PhSpinner v-if="isAdding" class="ph-spin" />
        <PhPlus v-else />
        {{ isAdding ? 'Adding...' : 'Add to Library' }}
      </button>
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { ref, onMounted, watch, computed, onBeforeUnmount, nextTick } from 'vue'
import type {
  AudibleBookMetadata,
  QualityProfile,
  Audiobook,
  AudiobookSeriesMembership,
} from '@/types'
import { apiService } from '@/services/api'
import { useConfigurationStore } from '@/stores/configuration'
import { useToast } from '@/services/toastService'
import { logger } from '@/utils/logger'
import { Modal, ModalHeader, ModalBody } from '@/components/feedback'
import RootFolderSelect from '@/components/form/RootFolderSelect.vue'
import Checkbox from '@/components/form/Checkbox.vue'
import FormRow from '@/components/settings/FormRow.vue'
import { useRootFoldersStore } from '@/stores/rootFolders'
import { buildAudibleProductUrl } from '@/utils/marketDomains'
import {
  PhX,
  PhSpinner,
  PhPlus,
  PhImage,
  PhWarning,
  PhPencilSimple,
  PhEye,
} from '@phosphor-icons/vue'
import { toForward, normalizeForCompare } from '@/utils/path'
import { formatDate } from '@/utils/searchResultFormatting'
import { stripHtmlAndNormalize } from '@/utils/textUtils'
import { usePathLengthCheck } from '@/composables/usePathLengthCheck'

interface Props {
  visible: boolean
  book: AudibleBookMetadata
  resolvedImageUrl?: string
}

interface Emits {
  (e: 'close'): void
  (e: 'added', audiobook: Audiobook): void
}

const props = defineProps<Props>()
const emit = defineEmits<Emits>()

const configStore = useConfigurationStore()
const toast = useToast()

const isAdding = ref(false)
const qualityProfiles = ref<QualityProfile[]>([])

const options = ref({
  monitored: true,
  qualityProfileId: null as number | null,
  autoSearch: false,
  // editable relative path portion (relative to rootPath)
  relativePath: '' as string | null,
})

const editableMetadata = ref<AudibleBookMetadata | null>(null)
const relativePathManuallyEdited = ref(false)
const showMetadataEditor = ref(false)

function trimToUndefined(value: string | null | undefined): string | undefined {
  const trimmed = (value || '').trim()
  return trimmed.length ? trimmed : undefined
}

function normalizeList(values: string[] | null | undefined): string[] {
  return (values || []).map((value) => value.trim()).filter((value) => value.length > 0)
}

function splitList(value: string | null | undefined): string[] {
  return (value || '')
    .split(/[\r\n,]+/)
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0)
}

function joinList(values: string[] | null | undefined): string {
  return normalizeList(values).join(', ')
}

function firstIsbn(value: unknown): string | undefined {
  if (Array.isArray(value)) {
    const first = value.find((entry) => typeof entry === 'string' && entry.trim().length > 0)
    return typeof first === 'string' ? first.trim() : undefined
  }
  return typeof value === 'string' ? trimToUndefined(value) : undefined
}

function normalizeSeriesMemberships(
  memberships: AudiobookSeriesMembership[] | null | undefined,
  legacySeries?: string | null,
  legacySeriesNumber?: string | null,
): AudiobookSeriesMembership[] {
  const normalized = (memberships || [])
    .map((membership, index) => ({
      id: membership.id,
      seriesName: trimToUndefined(membership.seriesName) || '',
      seriesNumber: trimToUndefined(membership.seriesNumber),
      seriesAsin: trimToUndefined(membership.seriesAsin),
      isPrimary: Boolean(membership.isPrimary),
      sortOrder: typeof membership.sortOrder === 'number' ? membership.sortOrder : index,
    }))
    .filter((membership) => membership.seriesName.length > 0)
    .sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0))

  if (normalized.length === 0) {
    const fallbackSeries = trimToUndefined(legacySeries)
    if (fallbackSeries) {
      return [
        {
          seriesName: fallbackSeries,
          seriesNumber: trimToUndefined(legacySeriesNumber),
          isPrimary: true,
          sortOrder: 0,
        },
      ]
    }
  }

  if (!normalized.some((membership) => membership.isPrimary) && normalized[0]) {
    normalized[0].isPrimary = true
  }

  return normalized.map((membership, index) => ({
    ...membership,
    sortOrder: index,
  }))
}

function primarySeriesMembership(
  memberships: AudiobookSeriesMembership[] | null | undefined,
  legacySeries?: string | null,
  legacySeriesNumber?: string | null,
): AudiobookSeriesMembership | undefined {
  const normalized = normalizeSeriesMemberships(memberships, legacySeries, legacySeriesNumber)
  return normalized.find((membership) => membership.isPrimary) || normalized[0]
}

function cloneMetadata(source: AudibleBookMetadata): AudibleBookMetadata {
  const normalizedMemberships = normalizeSeriesMemberships(
    source.seriesMemberships,
    source.series,
    source.seriesNumber,
  )
  const primaryMembership = primarySeriesMembership(normalizedMemberships)

  return {
    ...source,
    title: trimToUndefined(source.title) || 'Unknown Title',
    subtitle: trimToUndefined(source.subtitle),
    authors: normalizeList(source.authors),
    narrators: normalizeList(source.narrators),
    publishedDate: trimToUndefined(source.publishedDate),
    publishYear: trimToUndefined(source.publishYear),
    series: trimToUndefined(source.series) || trimToUndefined(primaryMembership?.seriesName),
    seriesNumber:
      trimToUndefined(source.seriesNumber) || trimToUndefined(primaryMembership?.seriesNumber),
    seriesMemberships: normalizedMemberships,
    description: trimToUndefined(source.description),
    genres: normalizeList(source.genres),
    tags: normalizeList(source.tags),
    isbn: firstIsbn(source.isbn),
    asin: trimToUndefined(source.asin) || '',
    publisher: trimToUndefined(source.publisher),
    language: trimToUndefined(source.language),
    runtime: typeof source.runtime === 'number' ? source.runtime : undefined,
    edition: trimToUndefined(source.edition),
    version: trimToUndefined(source.version),
    imageUrl: trimToUndefined(source.imageUrl),
    explicit: Boolean(source.explicit),
    abridged: Boolean(source.abridged),
    source: trimToUndefined(source.source),
    sourceLink: trimToUndefined(source.sourceLink),
    region: trimToUndefined(source.region),
    openLibraryId: trimToUndefined(source.openLibraryId),
    metadataSource: trimToUndefined(source.metadataSource),
  }
}

function buildMetadataPayload(): AudibleBookMetadata {
  const source = editableMetadata.value || props.book
  const publishedDate = trimToUndefined(source?.publishedDate)
  const derivedYear = publishedDate?.match(/\d{4}/)?.[0]
  const explicitPublishYear = trimToUndefined(source?.publishYear)
  const normalizedMemberships = normalizeSeriesMemberships(
    source?.seriesMemberships,
    source?.series,
    source?.seriesNumber,
  )
  const primaryMembership = primarySeriesMembership(
    normalizedMemberships,
    source?.series,
    source?.seriesNumber,
  )

  return {
    ...cloneMetadata(source),
    title: trimToUndefined(source?.title) || 'Unknown Title',
    subtitle: trimToUndefined(source?.subtitle),
    authors: normalizeList(source?.authors),
    narrators: normalizeList(source?.narrators),
    publishedDate,
    publishYear: explicitPublishYear || derivedYear,
    series: trimToUndefined(source?.series) || trimToUndefined(primaryMembership?.seriesName),
    seriesNumber:
      trimToUndefined(source?.seriesNumber) || trimToUndefined(primaryMembership?.seriesNumber),
    seriesMemberships: normalizedMemberships,
    description: trimToUndefined(source?.description),
    genres: normalizeList(source?.genres),
    tags: normalizeList(source?.tags),
    isbn: firstIsbn(source?.isbn),
    asin: trimToUndefined(source?.asin) || '',
    publisher: trimToUndefined(source?.publisher),
    language: trimToUndefined(source?.language),
    runtime:
      typeof source?.runtime === 'number' && !Number.isNaN(source.runtime)
        ? source.runtime
        : undefined,
    edition: trimToUndefined(source?.edition),
    version: trimToUndefined(source?.version),
    imageUrl: trimToUndefined(source?.imageUrl),
    explicit: Boolean(source?.explicit),
    abridged: Boolean(source?.abridged),
    region: trimToUndefined(source?.region),
    openLibraryId: trimToUndefined(source?.openLibraryId),
  }
}

const currentMetadata = computed(() => editableMetadata.value || enriched.value || props.book)

const authorsInput = computed({
  get: () => joinList(editableMetadata.value?.authors),
  set: (value: string) => {
    if (!editableMetadata.value) return
    editableMetadata.value.authors = splitList(value)
  },
})

const narratorsInput = computed({
  get: () => joinList(editableMetadata.value?.narrators),
  set: (value: string) => {
    if (!editableMetadata.value) return
    editableMetadata.value.narrators = splitList(value)
  },
})

const genresInput = computed({
  get: () => joinList(editableMetadata.value?.genres),
  set: (value: string) => {
    if (!editableMetadata.value) return
    editableMetadata.value.genres = splitList(value)
  },
})

const publishDate = computed(() => currentMetadata.value?.publishedDate || undefined)
const publishYear = computed(() => {
  if (currentMetadata.value?.publishedDate) {
    const match = currentMetadata.value.publishedDate.match(/\d{4}/)
    return match ? match[0] : undefined
  }
  const legacy = currentMetadata.value?.publishYear
  return legacy || undefined
})

const normalizedSourceName = computed(() => {
  const source = (metadataSource.value || currentMetadata.value?.source || '').trim()
  if (!source) return ''
  if (source.toLowerCase().includes('audible')) return 'Audible'
  return source
})

const audibleSourceUrl = computed(() => {
  const source = (metadataSource.value || currentMetadata.value?.source || '').toLowerCase()
  const asin = currentMetadata.value?.asin
  if (!source.includes('audible') || !asin) return null
  return buildAudibleProductUrl(asin, currentMetadata.value?.region)
})

const audibleProductUrl = computed(() => {
  const asin = currentMetadata.value?.asin
  return asin ? buildAudibleProductUrl(asin, currentMetadata.value?.region) : '#'
})

const openLibraryUrl = computed(() => {
  const olid = currentMetadata.value?.openLibraryId
  if (!olid) return null

  if (/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(olid)) {
    return null
  }

  if (olid.startsWith('/works/') || olid.startsWith('/books/')) {
    return `https://openlibrary.org${olid}`
  }

  if (/^OL\w+[WM]$/i.test(olid)) {
    const type = olid.toUpperCase().endsWith('W') ? 'works' : 'books'
    return `https://openlibrary.org/${type}/${olid}`
  }

  return `https://openlibrary.org/books/${olid}`
})

const hasFlags = computed(() =>
  Boolean(currentMetadata.value?.explicit || currentMetadata.value?.abridged),
)

const displayGenres = computed(() => {
  if (currentMetadata.value?.genres && currentMetadata.value.genres.length)
    return currentMetadata.value.genres
  return []
})

const displaySeriesMemberships = computed(() =>
  normalizeSeriesMemberships(
    currentMetadata.value?.seriesMemberships,
    currentMetadata.value?.series,
    currentMetadata.value?.seriesNumber,
  ),
)

const rootStore = useRootFoldersStore()
const selectedRootId = ref<number | null>(null)
const customRootPath = ref<string | null>(null)

const rootPath = ref<string>('')
const previewFull = ref<string>('')
const previewRelative = ref<string>('')

// Path length check — reactively compute the full destination path
const estimatedFullPath = computed(() => {
  let root = ''
  if (selectedRootId.value === 0) {
    root = (customRootPath.value || '').trim()
  } else if (selectedRootId.value && selectedRootId.value > 0) {
    const found = rootStore.folders.find((f) => f.id === selectedRootId.value)
    root = found?.path || ''
  } else {
    const defaultRoot = rootStore.folders.find((f) => f.isDefault)
    root = defaultRoot?.path || configStore.applicationSettings?.outputPath || ''
  }
  if (selectedRootId.value === 0) return root
  const rel = (options.value.relativePath || '').trim()
  if (!root) return rel
  if (!rel) return root
  const sep = root.includes('\\') ? '\\' : '/'
  return root.endsWith(sep) ? root + rel : root + sep + rel
})
const { pathLengthWarning: destinationPathWarning } = usePathLengthCheck(estimatedFullPath)

// Hold an enriched metadata object (populate if metadata sources available)
const enriched = ref<AudibleBookMetadata | null>(null)
// Image and metadata UI state
const imageError = ref(false)
const imageLoading = ref(false)
const imageRetryCount = ref(0)
const metadataLoading = ref(false)
const metadataSource = ref<string | null>(null)

const imageSrc = computed(() => {
  // prefer resolvedImageUrl passed from parent
  const base = props.resolvedImageUrl || currentMetadata.value?.imageUrl || ''
  if (!base) return ''
  // If we retried, append cache-buster to force reload
  if (imageRetryCount.value > 0) {
    const sep = base.includes('?') ? '&' : '?'
    return `${base}${sep}r=${Date.now()}`
  }
  return base
})

// Local types for audible response to avoid `any`
interface AudiblePerson {
  name?: string
}
interface AudibleSeries {
  asin?: string
  name?: string
  position?: string | number
}
interface AudibleGenre {
  name?: string
}
interface Audible {
  asin?: string
  title?: string
  subtitle?: string
  authors?: AudiblePerson[]
  narrators?: AudiblePerson[]
  publisher?: string
  publishDate?: string
  releaseDate?: string
  description?: string
  imageUrl?: string
  lengthMinutes?: number
  language?: string
  region?: string
  genres?: AudibleGenre[]
  series?: AudibleSeries[]
  bookFormat?: string
  version?: string
  isbn?: string
}

interface AudibleMetadataResponse {
  metadata?: Partial<Audible>
  source?: string
}

// Helper to map audible response to AudibleBookMetadata
const mapAudibleToAudible = (
  audible: Partial<Audible> | undefined,
  source?: string,
): AudibleBookMetadata => {
  let publishYear: string | undefined
  let publishedDate: string | undefined
  const dateStr = audible?.publishDate || audible?.releaseDate
  if (dateStr && typeof dateStr === 'string') {
    publishedDate = dateStr
    const yearMatch = dateStr.match(/\d{4}/)
    publishYear = yearMatch ? yearMatch[0] : undefined
  }

  const authors = (audible?.authors || []).map((a) => a?.name).filter(Boolean) as string[]
  const narrators = (audible?.narrators || []).map((n) => n?.name).filter(Boolean) as string[]
  const genres = (audible?.genres || []).map((g) => g?.name).filter(Boolean) as string[]
  const seriesMemberships = normalizeSeriesMemberships(
    audible?.series?.map((series, index) => ({
      seriesName: series?.name || '',
      seriesNumber: series?.position !== undefined ? String(series.position) : undefined,
      seriesAsin: series?.asin,
      isPrimary: index === 0,
      sortOrder: index,
    })),
    props.book?.series,
    props.book?.seriesNumber,
  )
  const primaryMembership = primarySeriesMembership(
    seriesMemberships,
    props.book?.series,
    props.book?.seriesNumber,
  )

  return {
    asin: audible?.asin || props.book?.asin || '',
    title: audible?.title || props.book?.title || 'Unknown Title',
    subtitle: audible?.subtitle,
    authors: authors.length ? authors : props.book?.authors || [],
    narrators: narrators.length ? narrators : props.book?.narrators || [],
    publisher: audible?.publisher || props.book?.publisher,
    publishYear: publishYear || props.book?.publishYear,
    publishedDate: publishedDate || props.book?.publishedDate,
    description: audible?.description || props.book?.description,
    imageUrl: audible?.imageUrl || props.book?.imageUrl,
    runtime:
      typeof audible?.lengthMinutes === 'number' ? audible.lengthMinutes : props.book?.runtime,
    language: audible?.language || props.book?.language,
    edition: props.book?.edition,
    version: audible?.version || props.book?.version,
    genres: genres.length ? genres : props.book?.genres || [],
    region: audible?.region || props.book?.region,
    series: primaryMembership?.seriesName || props.book?.series,
    seriesNumber:
      primaryMembership?.seriesNumber ||
      (props.book?.seriesNumber && props.book.seriesNumber !== 'null'
        ? props.book.seriesNumber
        : undefined),
    seriesMemberships,
    abridged:
      typeof audible?.bookFormat === 'string'
        ? audible.bookFormat.toLowerCase().includes('abridged')
        : Boolean(props.book?.abridged),
    isbn: audible?.isbn || props.book?.isbn,
    source: source || props.book?.source,
  }
}

function resolvePreviewRoot(): string | undefined {
  if (selectedRootId.value === 0) return undefined
  if (selectedRootId.value && selectedRootId.value > 0) {
    const found = rootStore.folders.find((folder) => folder.id === selectedRootId.value)
    return found?.path || undefined
  }
  const defaultRoot = rootStore.folders.find((folder) => folder.isDefault)
  return defaultRoot?.path || configStore.applicationSettings?.outputPath || undefined
}

async function refreshPreviewFromMetadata(force = false) {
  if (!props.visible || selectedRootId.value === 0) return
  if (!force && relativePathManuallyEdited.value) return

  const metadataForPreview = buildMetadataPayload()
  const destinationRoot = resolvePreviewRoot()
  const response = await apiService.previewLibraryPath(metadataForPreview, destinationRoot)
  previewFull.value = response?.fullPath || ''
  previewRelative.value = response?.relativePath || ''
  options.value.relativePath = deriveRelative(
    previewRelative.value,
    previewFull.value,
    destinationRoot || '',
  )
}

// helper to load profiles/settings and seed preview
const seedPreview = async () => {
  await configStore.loadQualityProfiles()
  qualityProfiles.value = configStore.qualityProfiles

  // Load application settings to get default root
  await configStore.loadApplicationSettings()
  // Load named root folders if available
  await rootStore.load()
  if (rootStore.folders.length > 0) {
    const def = rootStore.folders.find((f) => f.isDefault) || rootStore.folders[0]
    selectedRootId.value = def?.id ?? null
    // override rootPath for preview
    rootPath.value = def?.path || configStore.applicationSettings?.outputPath || ''
  } else {
    // Fallback to legacy outputPath if no root folders
    rootPath.value = configStore.applicationSettings?.outputPath || ''
  }

  enriched.value = null
  metadataSource.value = null
  showMetadataEditor.value = false

  // Attempt to fetch enriched metadata for the ASIN (if present) so preview/add use metadata sources
  try {
    if (props.book?.asin) {
      metadataLoading.value = true
      try {
        const resp = await apiService.getAudibleMetadata<
          AudibleMetadataResponse | Partial<Audible>
        >(props.book.asin, props.book.region)
        const payload = (resp && typeof resp === 'object' ? resp : {}) as
          | AudibleMetadataResponse
          | Partial<Audible>
        const source =
          'source' in payload && typeof payload.source === 'string' ? payload.source : undefined
        const metadata =
          'metadata' in payload && payload.metadata && typeof payload.metadata === 'object'
            ? payload.metadata
            : (payload as Partial<Audible>)

        if (metadata && typeof metadata === 'object') {
          const enrichedMeta = mapAudibleToAudible(metadata, source)
          // Sanitize seriesNumber to filter out the string "null"
          if (enrichedMeta.seriesNumber === 'null') {
            enrichedMeta.seriesNumber = undefined
          }
          enriched.value = enrichedMeta
          metadataSource.value = source || null
        }
      } catch (metaErr) {
        // ignore metadata fetch errors - we'll fall back to provided book
        logger.debug('Metadata fetch failed in AddLibraryModal:', metaErr)
      } finally {
        metadataLoading.value = false
      }
    }

    editableMetadata.value = cloneMetadata((enriched.value || props.book) as AudibleBookMetadata)
    relativePathManuallyEdited.value = false
    await refreshPreviewFromMetadata(true)
  } catch (e) {
    console.error('Failed to preview path:', e)
  }
}

// Load when mounted
onMounted(() => {
  if (props.visible) {
    void seedPreview()
  }
})

// Watch for resolvedImageUrl changes to reset image error state
watch(
  () => props.resolvedImageUrl,
  () => {
    imageError.value = false
    imageRetryCount.value = 0
  },
)

function onImageError() {
  imageLoading.value = false
  imageError.value = true
}

function onImageLoad() {
  imageLoading.value = false
  imageError.value = false
}

// Helper to derive a relative path from server preview/paths
function deriveRelative(
  serverRelative: string | undefined | null,
  serverFull: string | undefined | null,
  root: string | undefined | null,
): string {
  const rootVal = root || ''
  // Prefer explicit server-provided relative
  if (serverRelative && String(serverRelative).trim().length > 0) return serverRelative

  // If no root configured, fall back to showing the full path
  if (!rootVal) return serverFull || ''
  if (!serverFull) return ''

  // Normalize separators to forward slash for comparison
  const normRoot = toForward(rootVal)
  const normFull = toForward(serverFull)

  // Ensure trailing slash on root for slicing
  const rootWithSlash = normRoot.endsWith('/') ? normRoot : normRoot + '/'

  if (normalizeForCompare(normFull) === normalizeForCompare(normRoot)) return ''
  if (normalizeForCompare(normFull).startsWith(normalizeForCompare(rootWithSlash))) {
    const rel = normFull.slice(rootWithSlash.length).replace(/^\/+/, '')
    // Preserve user's original separator preference from configured root
    const useBackslash = rootVal.includes('\\')
    return useBackslash ? rel.replace(/\//g, '\\') : rel
  }

  // Not under root: show full path so user can edit it
  return serverFull
}

// Re-seed preview if the passed book changes after mount (parent may update props)
watch(
  () => props.book,
  (newVal) => {
    if (!newVal) return
    if (props.visible) {
      void seedPreview()
    }
  },
)

watch(
  () => props.visible,
  (value) => {
    if (value) {
      void seedPreview()
    }
  },
)

watch(
  () =>
    JSON.stringify({
      title: editableMetadata.value?.title,
      subtitle: editableMetadata.value?.subtitle,
      edition: editableMetadata.value?.edition,
      authors: editableMetadata.value?.authors,
      narrators: editableMetadata.value?.narrators,
      publisher: editableMetadata.value?.publisher,
      language: editableMetadata.value?.language,
      asin: editableMetadata.value?.asin,
      series: editableMetadata.value?.series,
      seriesNumber: editableMetadata.value?.seriesNumber,
      publishYear: editableMetadata.value?.publishYear,
      publishedDate: editableMetadata.value?.publishedDate,
    }),
  () => {
    void refreshPreviewFromMetadata()
  },
)

watch(
  () => selectedRootId.value,
  () => {
    void refreshPreviewFromMetadata(true)
  },
)

function onRelativePathInput() {
  relativePathManuallyEdited.value = true
}

function toggleMetadataEditor() {
  showMetadataEditor.value = !showMetadataEditor.value
}

const modalRef = ref<HTMLElement | null>(null)

const closeModal = () => {
  emit('close')
}

// Focus management for accessibility: trap focus inside modal and restore on close
let previousActiveElement: HTMLElement | null = null

const getFocusable = (container: HTMLElement | null): HTMLElement[] => {
  if (!container) return []
  const selectors = [
    'a[href]',
    'button:not([disabled])',
    'textarea:not([disabled])',
    'input:not([disabled])',
    'select:not([disabled])',
    '[tabindex]:not([tabindex="-1"])',
  ].join(',')
  return Array.from(container.querySelectorAll<HTMLElement>(selectors))
}

function onKeyDown(e: KeyboardEvent) {
  if (!modalRef.value) return
  if (e.key === 'Escape') {
    e.stopPropagation()
    closeModal()
    return
  }

  if (e.key === 'Tab') {
    const focusable = getFocusable(modalRef.value)
    if (focusable.length === 0) {
      e.preventDefault()
      return
    }
    const first = focusable[0]
    const last = focusable[focusable.length - 1]
    const active = document.activeElement as HTMLElement | null
    if (e.shiftKey) {
      if (!active || active === first) {
        e.preventDefault()
        last?.focus()
      }
    } else {
      if (!active || active === last) {
        e.preventDefault()
        first?.focus()
      }
    }
  }
}

watch(
  () => props.visible,
  async (val) => {
    if (val) {
      previousActiveElement = document.activeElement as HTMLElement | null
      await nextTick()
      if (modalRef.value) {
        modalRef.value.focus()
      }
      document.addEventListener('keydown', onKeyDown, { capture: true })
    } else {
      showMetadataEditor.value = false
      document.removeEventListener('keydown', onKeyDown, { capture: true })
      if (previousActiveElement && typeof previousActiveElement.focus === 'function') {
        previousActiveElement.focus()
      }
    }
  },
)

onBeforeUnmount(() => {
  document.removeEventListener('keydown', onKeyDown, { capture: true })
})

const addToLibrary = async () => {
  if (!props.book) return

  isAdding.value = true
  // Combine rootPath + relativePath into full destination path
  let destination: string | undefined = undefined
  try {
    const rel = (options.value.relativePath || '').trim()
    // Resolve selected root (custom, named, or default)
    let root = null
    if (selectedRootId.value === 0) root = customRootPath.value || ''
    else if (selectedRootId.value && selectedRootId.value > 0) {
      const found = rootStore.folders.find((f) => f.id === selectedRootId.value)
      root = found?.path || ''
    } else {
      // Use default root folder, fallback to legacy outputPath for compatibility
      const defaultRoot = rootStore.folders.find((f) => f.isDefault)
      root = defaultRoot?.path || configStore.applicationSettings?.outputPath || ''
    }

    if (selectedRootId.value === 0) {
      // Custom path: use exactly what the user entered (no pattern/relative path)
      const cleaned = (root || '').trim()
      destination = cleaned.length ? cleaned : undefined
    } else if (root && rel) {
      const sep = root.includes('\\') ? '\\' : '/'
      const cleanedRel = rel.replace(/\\|\//g, sep)
      destination = root.endsWith(sep) ? root + cleanedRel : root + sep + cleanedRel
    } else if (root && !rel) {
      destination = root
    }

    const metadataToSend = buildMetadataPayload()
    const result = await apiService.addToLibrary(metadataToSend, {
      monitored: options.value.monitored,
      qualityProfileId: options.value.qualityProfileId || undefined,
      autoSearch: options.value.autoSearch,
      destinationPath: destination || undefined,
    })
    toast.success('Added', `"${metadataToSend.title}" has been added to your library!`)
    emit('added', result.audiobook)
    closeModal()
  } catch (err: unknown) {
    console.error('Failed to add audiobook:', err)
    const errorMessage =
      err instanceof Error ? err.message : 'Failed to add audiobook. Please try again.'
    toast.error('Add failed', errorMessage)
  } finally {
    isAdding.value = false
  }
}

const formatRuntime = (minutes: number): string => {
  if (!minutes) return 'Unknown'
  // Guard against legacy data stored in seconds
  const normalized = minutes >= 20000 ? Math.round(minutes / 60) : minutes
  const hours = Math.floor(normalized / 60)
  const mins = normalized % 60
  return `${hours}h ${mins}m`
}

const capitalizeFirst = (str: string): string => {
  if (!str) return ''
  return str.charAt(0).toUpperCase() + str.slice(1).toLowerCase()
}
</script>

<style scoped>
/* Keep only layout and content-related styles; modal wrapper styles come from shared modal stylesheet */
.add-library-modal-content {
  display: flex;
  flex-direction: column;
  gap: 2rem;
  outline: none;
}

.image-viewport {
  width: 100%;
  aspect-ratio: 1/1;
  position: relative;
  border-radius: 6px;
  overflow: hidden;
  background: #333;
  display: flex;
  align-items: center;
  justify-content: center;
  box-shadow: 0 4px 8px rgba(0, 0, 0, 0.3);
}
.image-viewport img {
  width: 100%;
  height: 100%;
  object-fit: cover;
  display: block;
}
.placeholder-cover {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  color: var(--text-muted);
}
.image-loading-overlay {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgba(0, 0, 0, 0.25);
  color: white;
}
.image-error-overlay {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgba(0, 0, 0, 0.6);
  color: white;
}
.image-error-overlay .error-inner {
  text-align: center;
}
.image-error-overlay .error-inner .btn.small {
  margin-top: 0.5rem;
}

.meta-source-row {
  margin-bottom: 0.5rem;
}

.book-layout {
  display: grid;
  grid-template-columns: 200px 1fr;
  gap: 2rem;
  align-items: start;
}

.book-image {
  position: sticky;
  top: 0;
}

.book-details {
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
}

.detail-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 1rem;
}

.detail-section h3 {
  margin: 0 0 0.5rem 0;
  color: white;
  font-size: 1.75rem;
  line-height: 1.2;
}

.detail-section h4 {
  margin: 0 0 1rem 0;
  color: white;
  font-size: 1.1rem;
  font-weight: 500;
  border-bottom: 1px solid #333;
  padding-bottom: 0.5rem;
}

.authors {
  color: var(--brand-500);
  font-size: 1.1rem;
  font-weight: 500;
  margin: 0 0 0.25rem 0;
}

.metadata-toggle-btn {
  flex-shrink: 0;
  white-space: nowrap;
}

.narrators {
  color: #ccc;
  font-style: italic;
  margin: 0;
}

.description {
  color: #ccc;
  line-height: 1.6;
  margin: 0;
  white-space: pre-wrap;
}

.detail-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 1rem;
}

.detail-item {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.detail-item--wide {
  grid-column: span 2;
}

.detail-item--full {
  grid-column: 1 / -1;
}

.detail-item .label {
  color: #999;
  font-size: 0.9rem;
  font-weight: 500;
}

.detail-item .value {
  color: white;
  font-weight: 400;
}

.series-membership-list {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
}

.series-membership-pill {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  padding: 0.3rem 0.55rem;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.06);
  border: 1px solid rgba(255, 255, 255, 0.1);
}

.series-membership-primary {
  font-size: 0.72rem;
  color: #9ec4ff;
}

.flags {
  display: flex;
  gap: 0.5rem;
  flex-wrap: wrap;
}

.flag {
  padding: 0.25rem 0.75rem;
  border-radius: 6px;
  font-size: 0.8rem;
  font-weight: 500;
}

.flag.explicit {
  background-color: rgba(231, 76, 60, 0.2);
  color: #e74c3c;
  border: 1px solid #e74c3c;
}

.flag.abridged {
  background-color: rgba(243, 156, 18, 0.2);
  color: #f39c12;
  border: 1px solid #f39c12;
}

.library-options {
  margin-top: 0;
}

.metadata-editor {
  padding: 1rem;
  border: 1px solid #333;
  border-radius: 10px;
  background: rgba(255, 255, 255, 0.02);
}

.metadata-editor-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 1rem;
  margin-bottom: 1rem;
}

.metadata-editor-header h4 {
  margin-bottom: 0;
}

.metadata-edit-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 1rem;
}

.metadata-textarea {
  min-height: 7rem;
  resize: vertical;
}

.metadata-flags {
  margin-top: 1rem;
}

.form-label {
  display: block;
  color: white;
  font-weight: 500;
  margin-bottom: 0.5rem;
}

.form-select {
  width: 100%;
  padding: 0.75rem;
  border: 1px solid #555;
  border-radius: 6px;
  background-color: #333;
  color: white;
  font-size: 1rem;
}

.form-select:focus {
  outline: none;
  border-color: var(--brand-focus);
  box-shadow: 0 0 0 2px rgba(var(--brand-rgb), 0.2);
}

.form-help {
  display: block;
  color: #ccc;
  font-size: 0.85rem;
  margin-top: 0.5rem;
}

.option-group {
  margin: 2rem 0;
}

.modal-content .form-group {
  margin-bottom: 0.25rem;
}
/* Destination display styles */
.destination-display {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  padding: 0.5rem 0;
}

/* root-label is used instead of readonly-path */

.form-input {
  width: 100%;
  padding: 0.6rem 0.75rem;
  border-radius: 6px;
  border: 1px solid #3a3a3a;
  background-color: #2a2a2a;
  color: #fff;
  font-size: 0.95rem;
}

.form-input:focus {
  outline: none;
  border-color: var(--brand-focus);
  box-shadow: 0 0 0 3px rgba(var(--brand-rgb), 0.06);
}

/* Row layout for destination: root left, input right */
.destination-row {
  display: flex;
  gap: 0.75rem;
  align-items: center;
}

.root-label {
  padding: 0.45rem 0 0.45rem 0.6rem;
  color: #ccc;
  font-family:
    ui-monospace, SFMono-Regular, Menlo, Monaco, 'Roboto Mono', 'Segoe UI Mono', monospace;
  font-size: 0.9rem;
  width: fit-content;
  white-space: nowrap;
}

.relative-input {
  flex: 1 1 auto;
}

/* Buttons are centralized in `src/assets/buttons.css` and `src/assets/modals.css`. Use `.btn` / `.btn-primary` here. */

/* Button color variants centralized in `src/assets/modals.css` */

/* Responsive design */
@media (max-width: 768px) {
  .book-layout {
    grid-template-columns: 1fr;
    gap: 1.5rem;
  }

  .book-image {
    position: static;
    max-width: 200px;
    margin: 0 auto;
  }

  .detail-grid {
    grid-template-columns: 1fr;
  }

  .detail-header {
    flex-direction: column;
    align-items: stretch;
  }

  .metadata-editor-header {
    flex-direction: column;
    align-items: stretch;
  }

  .metadata-toggle-btn {
    width: 100%;
    justify-content: center;
  }

  .metadata-edit-grid {
    grid-template-columns: 1fr;
  }

  .detail-item--wide,
  .detail-item--full {
    grid-column: auto;
  }

  .modal-footer {
    flex-direction: column-reverse;
  }

  .btn {
    justify-content: center;
  }
}

.path-length-warning {
  display: flex;
  align-items: flex-start;
  gap: 0.5rem;
  margin-top: 0.5rem;
  padding: 0.625rem 0.75rem;
  background-color: rgba(255, 152, 0, 0.08);
  border: 1px solid rgba(255, 152, 0, 0.35);
  border-radius: 6px;
  color: #ffb74d;
  font-size: 0.8rem;
  line-height: 1.5;
}

.path-length-warning svg {
  flex-shrink: 0;
  margin-top: 0.125rem;
}
</style>
