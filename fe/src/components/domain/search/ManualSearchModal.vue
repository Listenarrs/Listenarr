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
  <Modal :visible="isOpen" size="lg" @close="close">
    <template #header>
      <ModalHeader
        :title="`Manual Search - ${audiobook?.title || ''}`"
        :icon="PhMagnifyingGlass"
        @close="close"
      />
    </template>

    <template #default>
      <ModalBody>
        <!-- Search Status -->
        <div v-if="searching" class="search-status">
          <PhSpinner class="ph-spin" />
          <span>Searching indexers... ({{ searchedIndexers }}/{{ totalIndexers }})</span>
        </div>

        <!-- Results Table -->
        <div v-if="displayResults.length > 0 || !searching" class="results-container">
          <div class="results-header">
            <!-- Search Bar -->
            <div class="search-bar">
              <div class="search-input-wrapper">
                <PhMagnifyingGlass class="search-icon" />
                <input
                  v-model="searchQuery"
                  type="text"
                  class="search-input form-input"
                  placeholder="Search for audiobooks..."
                  @keyup.enter="search"
                  :disabled="searching"
                />
                <button
                  class="btn btn-primary"
                  @click="search"
                  :disabled="searching || !searchQuery.trim()"
                >
                  <span v-if="!searching"><PhMagnifyingGlass /></span>
                  <span v-else><PhSpinner class="ph-spin" /></span>
                  Search
                </button>
                <button v-if="!searching" class="btn btn-secondary btn-sm" @click="search">
                  <PhArrowClockwise />
                  Refresh
                </button>
              </div>
            </div>

            <div class="results-controls">
              <div class="results-count">
                {{ displayResults.length }} result{{ displayResults.length !== 1 ? 's' : '' }} found
              </div>
            </div>
          </div>

          <div v-if="displayResults.length === 0 && !searching" class="no-results">
            <PhMagnifyingGlass />
            <p>No results found</p>
            <p class="hint">Try adjusting your indexer settings or search criteria</p>
          </div>

          <div v-else class="results-table-wrapper">
            <table class="results-table">
              <thead>
                <tr>
                  <th class="col-source sortable" @click="setSort('Source')">
                    <span class="header-content">
                      Source
                      <component :is="getSortIcon('Source')" class="sort-icon" />
                    </span>
                  </th>
                  <th class="col-age sortable" @click="setSort('PublishedDate')">
                    <span class="header-content">
                      Age
                      <component :is="getSortIcon('PublishedDate')" class="sort-icon" />
                    </span>
                  </th>
                  <th class="col-title sortable" @click="setSort('Title')">
                    <span class="header-content">
                      Title
                      <component :is="getSortIcon('Title')" class="sort-icon" />
                    </span>
                  </th>
                  <th class="col-indexer sortable" @click="setSort('Source')">
                    <span class="header-content">
                      Indexer
                      <component :is="getSortIcon('Source')" class="sort-icon" />
                    </span>
                  </th>
                  <th class="col-size sortable" @click="setSort('Size')">
                    <span class="header-content">
                      Size
                      <component :is="getSortIcon('Size')" class="sort-icon" />
                    </span>
                  </th>
                  <th v-if="anyHasPeers" class="col-seeders sortable" @click="setSort('Seeders')">
                    <span class="header-content">
                      Seeders
                      <component :is="getSortIcon('Seeders')" class="sort-icon" />
                    </span>
                  </th>
                  <th v-if="anyHasPeers" class="col-leechers sortable" @click="setSort('Leechers')">
                    <span class="header-content">
                      Leechers
                      <component :is="getSortIcon('Leechers')" class="sort-icon" />
                    </span>
                  </th>
                  <th class="col-grabs sortable" @click="setSort('Grabs')">
                    <span class="header-content">
                      Grabs
                      <component :is="getSortIcon('Grabs')" class="sort-icon" />
                    </span>
                  </th>
                  <th
                    v-if="anyHasLanguage"
                    class="col-language sortable"
                    @click="setSort('Language')"
                  >
                    <span class="header-content">
                      Languages
                      <component :is="getSortIcon('Language')" class="sort-icon" />
                    </span>
                  </th>
                  <th v-if="anyHasQuality" class="col-quality sortable" @click="setSort('Quality')">
                    <span class="header-content">
                      Quality
                      <component :is="getSortIcon('Quality')" class="sort-icon" />
                    </span>
                  </th>
                  <th class="col-score sortable" @click="setSort('Score')">
                    <span class="header-content">
                      Score
                      <component :is="getSortIcon('Score')" class="sort-icon" />
                    </span>
                  </th>
                  <th class="col-actions"></th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="result in displayResults" :key="result.id" class="result-row">
                  <td class="col-source">
                    <span :class="['source-badge', getSourceType(result).toLowerCase()]">
                      {{ getSourceType(result).toUpperCase() }}
                    </span>
                  </td>
                  <td class="col-age">{{ formatAge(result.publishedDate) }}</td>
                  <td class="col-title">
                    <div class="title-cell">
                      <a
                        v-if="result.id"
                        :href="result.id"
                        class="title-text"
                        target="_blank"
                        rel="noopener noreferrer"
                      >
                        {{ safeText(result.title) }}
                      </a>
                      <span v-else class="title-text">{{ safeText(result.title) }}</span>
                    </div>
                  </td>
                  <td class="col-indexer">
                    <span class="indexer-name">{{ result.source }}</span>
                  </td>
                  <td class="col-size">{{ formatSize(result.size) }}</td>
                  <td v-if="anyHasPeers" class="col-seeders">
                    <span
                      v-if="result.seeders !== undefined && result.seeders !== null"
                      class="seeders"
                      :class="{
                        good: (result.seeders ?? 0) > 10,
                        medium: (result.seeders ?? 0) > 0 && (result.seeders ?? 0) <= 10,
                      }"
                    >
                      <PhArrowUp /> {{ result.seeders }}
                    </span>
                  </td>
                  <td v-if="anyHasPeers" class="col-leechers">
                    <span
                      v-if="result.leechers !== undefined && result.leechers !== null"
                      class="leechers"
                    >
                      <PhArrowDown /> {{ result.leechers }}
                    </span>
                  </td>
                  <td class="col-grabs">
                    <span v-if="result.grabs !== undefined" class="grabs-badge"
                      ><strong>{{ result.grabs }}</strong></span
                    >
                    <span v-else class="grabs-badge unknown">-</span>
                  </td>
                  <td v-if="anyHasLanguage" class="col-language">
                    <span v-if="normalizeLanguage(result.language)" class="language-badge">
                      {{ normalizeLanguage(result.language) }}
                    </span>
                  </td>
                  <td v-if="anyHasQuality" class="col-quality">
                    <span v-if="result.quality" class="quality-badge">
                      {{ result.quality }}
                      <small v-if="shouldShowFormatFallback(result)" class="format-fallback">
                        · {{ result.format }}</small
                      >
                    </span>
                    <span v-else-if="result.format" class="quality-badge format-only">
                      {{ result.format }}
                    </span>
                  </td>
                  <td class="col-score">
                    <div v-if="getResultScore(result.id)" class="score-cell">
                      <ScorePopover :content="getScoreBreakdownTooltip(getResultScore(result.id))">
                        <template #default>
                          <span
                            v-if="getResultScore(result.id)?.isRejected"
                            class="score-badge rejected"
                            :title="getResultScore(result.id)?.rejectionReasons.join(', ')"
                          >
                            <PhXCircle />
                            Rejected
                          </span>
                          <span v-else :class="['score-badge', getVisibleScoreClass(result.id)]">
                            {{ getVisibleScoreValue(result.id) ?? '-' }}
                          </span>
                        </template>
                      </ScorePopover>
                    </div>
                    <span v-else class="score-badge loading">-</span>
                  </td>
                  <td class="col-actions">
                    <button
                      class="btn-icon btn-download"
                      @click="downloadResult(result)"
                      :disabled="downloading[result.id] || !result.downloadReference"
                      :title="
                        !result.downloadReference
                          ? 'Run the search again to refresh this download'
                          : downloading[result.id]
                            ? 'Sending to download client...'
                            : 'Download'
                      "
                    >
                      <span v-if="!downloading[result.id]"><PhDownloadSimple /></span>
                      <span v-else><PhSpinner class="ph-spin" /></span>
                    </button>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
      </ModalBody>
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { Modal, ModalHeader, ModalBody } from '@/components/feedback'
import {
  PhMagnifyingGlass,
  PhSpinner,
  PhArrowClockwise,
  PhArrowUp,
  PhArrowDown,
  PhXCircle,
  PhDownloadSimple,
  PhArrowsDownUp,
} from '@phosphor-icons/vue'
import { useToast } from '@/services/toastService'
import { apiService } from '@/services/api'
import { logger } from '@/utils/logger'
import type {
  Audiobook,
  SearchResult,
  QualityScore,
  QualityProfile,
  SearchSortBy,
  SearchSortDirection,
} from '@/types'
import { getScoreBreakdownTooltip, computeNormalizedSmart } from '@/composables/useScore'
import ScorePopover from '@/components/ui/ScorePopover.vue'
import { safeText } from '@/utils/textUtils'

interface Props {
  isOpen: boolean
  audiobook: Audiobook | null
}

const props = defineProps<Props>()
const emit = defineEmits<{
  close: []
  downloaded: [result: SearchResult]
}>()

const results = ref<SearchResult[]>([])
const searching = ref(false)
const downloading = ref<Record<string, boolean>>({})
const searchedIndexers = ref(0)
const totalIndexers = ref(0)
const qualityScores = ref<Map<string, QualityScore>>(new Map())
const qualityProfile = ref<QualityProfile | null>(null)
const sortBy = ref<SearchSortBy | 'Score'>('Score')
const sortDirection = ref<SearchSortDirection>('Descending')
const searchQuery = ref('')

watch(
  () => props.isOpen,
  (isOpen) => {
    if (isOpen && props.audiobook) {
      // Initialize search query with default query and auto-search
      searchQuery.value = buildSearchQuery()
      search()
    }
  },
)

const displayResults = computed(() => {
  // When sorting by Score, return a sorted copy derived from `results` so
  // the view always reflects the desired order even if `results` is later
  // replaced by the search logic.
  if (sortBy.value !== 'Score') return results.value

  const asc = sortDirection.value === 'Ascending'
  const copy = results.value.slice()
  copy.sort((a, b) => {
    const qa = qualityScores.value.get(a.id)
    const qb = qualityScores.value.get(b.id)

    const rejectedA = Boolean(qa?.isRejected)
    const rejectedB = Boolean(qb?.isRejected)
    if (rejectedA !== rejectedB) return rejectedA ? 1 : -1

    // Use the visible score (what the UI shows) for sorting so order matches display
    const scoreA = getVisibleScoreValue(a.id)
    const scoreB = getVisibleScoreValue(b.id)

    const hasA = typeof scoreA === 'number'
    const hasB = typeof scoreB === 'number'
    if (hasA !== hasB) return hasA ? -1 : 1
    if (!hasA && !hasB) return 0

    if (scoreA === scoreB) return 0
    // scoreA and scoreB are guaranteed to be numbers here (checked above), coerce to number for TS
    const sA = scoreA as number
    const sB = scoreB as number
    return asc ? sA - sB : sB - sA
  })
  return copy
})

const anyHasPeers = computed(() =>
  displayResults.value.some((r) => r.seeders !== undefined && r.seeders !== null),
)
const anyHasLanguage = computed(() =>
  displayResults.value.some((r) => !!normalizeLanguage(r.language)),
)

// Normalize language values from DTOs/indexers: treat explicit 'unknown' strings as absent
const normalizeLanguage = (value?: string | null): string | undefined => {
  if (!value) return undefined
  const v = value.toString().trim()
  if (v.length === 0) return undefined
  if (v.toLowerCase() === 'unknown') return undefined
  return v
}
const anyHasQuality = computed(() => displayResults.value.some((r) => !!r.quality || !!r.format))

function shouldShowFormatFallback(result: SearchResult): boolean {
  if (!result) return false
  const fmt = (result.format || '').toString().toLowerCase().trim()
  const qual = (result.quality || '').toString().toLowerCase().trim()
  if (!fmt) return false
  if (!qual) return true
  // Only show fallback when format token isn't already included in quality
  return !qual.includes(fmt)
}

function setSort(column: SearchSortBy | 'Score') {
  if (sortBy.value === column) {
    // Toggle direction if same column
    sortDirection.value = sortDirection.value === 'Ascending' ? 'Descending' : 'Ascending'
  } else {
    // New column, default to descending
    sortBy.value = column as SearchSortBy
    sortDirection.value = 'Descending'
  }

  // For Score sorting, sort frontend results, otherwise re-search with backend sorting
  if (column === 'Score') {
    // Frontend sorting for Score column
    sortFrontendResults()
  } else {
    // Backend sorting for other columns
    search()
  }
}

function getSortIcon(column: SearchSortBy | 'Score') {
  // Return a component reference for the current sort icon state.
  if (sortBy.value !== column) {
    return PhArrowsDownUp
  }
  return sortDirection.value === 'Ascending' ? PhArrowUp : PhArrowDown
}

function sortFrontendResults() {
  const ascending = sortDirection.value === 'Ascending'

  results.value.sort((a, b) => {
    const qa = getResultScore(a.id)
    const qb = getResultScore(b.id)

    const rejectedA = Boolean(qa?.isRejected)
    const rejectedB = Boolean(qb?.isRejected)

    // Put rejected items at the end always
    if (rejectedA && !rejectedB) return 1
    if (!rejectedA && rejectedB) return -1

    // Now handle scored vs unscored: scored items should appear before unscored
    const hasA = typeof qa?.totalScore === 'number'
    const hasB = typeof qb?.totalScore === 'number'
    if (hasA && !hasB) return -1
    if (!hasA && hasB) return 1
    if (!hasA && !hasB) return 0

    // Both have numeric scores — compare numerically
    const scoreA = qa!.totalScore
    const scoreB = qb!.totalScore

    if (scoreA === scoreB) return 0
    return ascending ? scoreA - scoreB : scoreB - scoreA
  })
}

async function search() {
  if (!props.audiobook) return

  searching.value = true
  results.value = []
  searchedIndexers.value = 0
  totalIndexers.value = 0

  try {
    // Get count of enabled indexers first
    const enabledIndexers = await apiService.getEnabledIndexers()
    totalIndexers.value = enabledIndexers.length

    // Build search query from title and author (fallback if no manual query)
    const query = searchQuery.value.trim() || buildSearchQuery()

    // Search each indexer individually to show progress
    const allResults: SearchResult[] = []
    const searchPromises = enabledIndexers.map(async (indexer) => {
      try {
        // Map MyAnonamouse indexer options (if present on the indexer) to searchByApi opts so backend can apply them

        let opts: Parameters<typeof apiService.searchByApi>[3] = undefined
        if (indexer.implementation === 'MyAnonamouse') {
          try {
            const settings = indexer.additionalSettings
              ? JSON.parse(indexer.additionalSettings)
              : {}
            const mam = settings.mam_options ?? settings
            opts = {
              mamFilter: mam?.filter || undefined,
              mamSearchInDescription:
                mam?.searchInDescription !== undefined ? mam?.searchInDescription : undefined,
              mamSearchInSeries:
                mam?.searchInSeries !== undefined ? mam?.searchInSeries : undefined,
              mamSearchInFilenames:
                mam?.searchInFilenames !== undefined ? mam?.searchInFilenames : undefined,
              mamLanguage: mam?.language || undefined,
              mamFreeleechWedge: mam?.freeleechWedge || undefined,
              mamEnrichResults: mam?.enrichResults !== undefined ? mam?.enrichResults : undefined,
              mamEnrichTopResults:
                mam?.enrichTopResults !== undefined ? mam?.enrichTopResults : undefined,
            }
          } catch (e) {
            logger.warn('Failed to parse MyAnonamouse options from indexer.additionalSettings', e)
          }
        }

        const indexerResultsRaw: unknown[] = await apiService.searchByApi(
          indexer.id.toString(),
          query,
          undefined,
          opts,
        )

        // Normalize Prowlarr-like IndexerResultDto into local SearchResult shape for the UI
        let normalized: SearchResult[] = []
        if (
          Array.isArray(indexerResultsRaw) &&
          indexerResultsRaw.length > 0 &&
          (indexerResultsRaw[0] as Record<string, unknown>).guid !== undefined
        ) {
          normalized = (indexerResultsRaw as Record<string, unknown>[]).map((dto) => ({
            id: String(dto.guid ?? dto.infoUrl ?? dto.fileName ?? Math.random()),
            title: String(dto.title ?? ''),
            size: typeof dto.size === 'string' ? Number(dto.size) || 0 : Number(dto.size ?? 0),
            seeders:
              typeof dto.seeders === 'string'
                ? Number(dto.seeders) || 0
                : typeof dto.seeders === 'number'
                  ? dto.seeders
                  : undefined,
            leechers:
              typeof dto.leechers === 'string'
                ? Number(dto.leechers) || 0
                : typeof dto.leechers === 'number'
                  ? dto.leechers
                  : undefined,
            grabs:
              typeof dto.grabs === 'string'
                ? Number(dto.grabs) || 0
                : typeof dto.grabs === 'number'
                  ? dto.grabs
                  : 0,
            files:
              typeof dto.files === 'string'
                ? Number(dto.files) || 0
                : typeof dto.files === 'number'
                  ? dto.files
                  : 0,
            magnetLink: '',
            torrentUrl: String(dto.downloadUrl ?? ''),
            nzbUrl: '',
            downloadType: String(dto.protocol ?? ''),
            downloadReference: String(dto.downloadReference ?? ''),
            quality: undefined,
            indexerId: String(dto.indexerId ?? indexer.id),
            indexerImplementation: String(dto.indexer ?? indexer.name),
            resultUrl: String(dto.infoUrl ?? dto.guid ?? ''),
            description: undefined,
            publisher: undefined,
            subtitle: undefined,
            publishYear: undefined,
            language: normalizeLanguage(
              String(dto.language ?? dto.lang_code ?? dto.languageCode ?? ''),
            ),
            runtime: undefined,
            narrator: undefined,
            imageUrl: undefined,
            asin: undefined,
            series: undefined,
            seriesNumber: undefined,
            productUrl: undefined,
            isEnriched: false,
            metadataSource: undefined,
            subtitles: undefined,
            artist: '',
            album: '',
            category: '',
            source: String(dto.indexer ?? indexer.name),
            sourceLink: String(dto.infoUrl ?? dto.guid ?? ''),
            publishedDate: String(
              dto.PublishDate ?? dto.publishDate ?? dto.added ?? dto.publish_date ?? '',
            ),
            // Use filetype when available (MP3/M4B/etc), fallback to protocol (torrent/nzb)
            format: String(dto.filetype ?? dto.protocol ?? ''),
            score: 0,
          }))
        } else {
          // Already in SearchResult shape
          normalized = indexerResultsRaw as SearchResult[]
        }

        // Normalize any 'unknown' language tokens to undefined so Usenet/DDL results don't show 'Unknown' in UI
        normalized.forEach((r) => {
          r.language = normalizeLanguage(r.language as string | undefined)
        })
        allResults.push(...normalized)
        searchedIndexers.value++
      } catch (error) {
        logger.warn(`Failed to search indexer ${indexer.name}:`, error)
        searchedIndexers.value++ // Still count as completed even if failed
      }
    })

    // Wait for all searches to complete
    await Promise.all(searchPromises)

    // Apply backend sorting if needed (for non-Score columns)
    if (sortBy.value !== 'Score') {
      const backendSortBy = sortBy.value as SearchSortBy
      // Sort results based on the current sort criteria
      allResults.sort((a, b) => {
        switch (backendSortBy) {
          case 'Seeders':
            return sortDirection.value === 'Ascending'
              ? (a.seeders ?? 0) - (b.seeders ?? 0)
              : (b.seeders ?? 0) - (a.seeders ?? 0)
          case 'Leechers':
            return sortDirection.value === 'Ascending'
              ? (a.leechers ?? 0) - (b.leechers ?? 0)
              : (b.leechers ?? 0) - (a.leechers ?? 0)
          case 'Grabs':
            return sortDirection.value === 'Ascending'
              ? (a.grabs ?? 0) - (b.grabs ?? 0)
              : (b.grabs ?? 0) - (a.grabs ?? 0)
          case 'Size':
            return sortDirection.value === 'Ascending' ? a.size - b.size : b.size - a.size
          case 'PublishedDate':
            return sortDirection.value === 'Ascending'
              ? new Date(a.publishedDate).getTime() - new Date(b.publishedDate).getTime()
              : new Date(b.publishedDate).getTime() - new Date(a.publishedDate).getTime()
          case 'Title':
            return sortDirection.value === 'Ascending'
              ? a.title.localeCompare(b.title)
              : b.title.localeCompare(a.title)
          case 'Source':
            return sortDirection.value === 'Ascending'
              ? a.source.localeCompare(b.source)
              : b.source.localeCompare(a.source)
          case 'Language':
            // Normalize undefined/unknown languages to empty string for comparison
            return sortDirection.value === 'Ascending'
              ? (a.language ?? '').localeCompare(b.language ?? '')
              : (b.language ?? '').localeCompare(a.language ?? '')
          case 'Quality':
            return sortDirection.value === 'Ascending'
              ? (a.quality ?? '').localeCompare(b.quality ?? '')
              : (b.quality ?? '').localeCompare(a.quality ?? '')
          default:
            return 0
        }
      })
    }

    // Deduplicate results by id (multiple indexers can return the same release)
    const seen = new Map<string, SearchResult>()
    for (const r of allResults) {
      if (!seen.has(r.id)) seen.set(r.id, r)
    }
    results.value = Array.from(seen.values())

    // Load quality profile and score results (always needed for Score column or display)
    await loadQualityProfileAndScore()

    // If sorting by Score, apply frontend sorting
    if (sortBy.value === 'Score') {
      sortFrontendResults()
    }
  } catch (err) {
    console.error('Manual search failed:', err)
  } finally {
    searching.value = false
  }
}

async function loadQualityProfileAndScore() {
  try {
    // Get the audiobook's quality profile or default
    if (props.audiobook?.qualityProfileId) {
      qualityProfile.value = await apiService.getQualityProfileById(
        props.audiobook.qualityProfileId,
      )
    } else {
      qualityProfile.value = await apiService.getDefaultQualityProfile()
    }

    // Score the search results
    if (qualityProfile.value?.id && results.value.length > 0) {
      const scores = await apiService.scoreSearchResults(qualityProfile.value.id, results.value)

      // Map scores by search result ID
      qualityScores.value.clear()
      scores.forEach((score) => {
        qualityScores.value.set(score.searchResult.id, score)
      })
    }
  } catch (error) {
    logger.warn('Failed to load quality profile or score results:', error)
  }
}

function buildSearchQuery(): string {
  if (!props.audiobook) return ''

  const parts: string[] = []

  if (props.audiobook.title) {
    parts.push(props.audiobook.title)
  }

  if (props.audiobook.authors && props.audiobook.authors.length > 0 && props.audiobook.authors[0]) {
    parts.push(props.audiobook.authors[0])
  }

  return parts.join(' ')
}

async function downloadResult(result: SearchResult) {
  downloading.value[result.id] = true
  const toast = useToast()

  try {
    // Check if this is a DDL
    const isDDL = getSourceType(result) === 'ddl'
    const audiobookId = props.audiobook?.id

    if (isDDL) {
      // For DDL, start download in background and add to activity
      await apiService.sendToDownloadClient(result, undefined, audiobookId)

      // Add to activity/downloads view (will be tracked there)
      // Show success message
      emit('downloaded', result)

      // Show feedback briefly
      setTimeout(() => {
        delete downloading.value[result.id]
      }, 1000)
    } else {
      // For torrents/NZB, send to download client (also pass audiobookId for future processing)
      await apiService.sendToDownloadClient(result, undefined, audiobookId)
      emit('downloaded', result)

      // Show success feedback briefly, then remove
      setTimeout(() => {
        delete downloading.value[result.id]
      }, 2000)
    }
  } catch (err) {
    console.error('Download failed:', err)
    const errorMessage = err instanceof Error ? err.message : 'Unknown error'

    // Show error in alert with more context
    let userMessage = `Download failed: ${errorMessage}`
    if (errorMessage.includes('Output path not configured')) {
      userMessage =
        'Download path not configured. Please go to Settings and configure the Output Path before downloading.'
    }

    // Show error as a non-blocking toast instead of a modal alert
    toast.error('Download failed', userMessage)
    delete downloading.value[result.id]
  }
}

function close() {
  emit('close')
}

function getSourceType(result: SearchResult): string {
  // Check downloadType first if it's set
  if (result.downloadType) {
    return result.downloadType.toLowerCase()
  }

  // Fallback to legacy detection logic
  // Check for torrent indicators
  if (result.magnetLink || result.torrentUrl) {
    return 'torrent'
  }
  // Check for NZB indicator
  if (result.nzbUrl) {
    return 'nzb'
  }
  // Check source name
  if (result.source?.toLowerCase().includes('torrent')) {
    return 'torrent'
  }
  // Default to NZB for usenet
  return 'nzb'
}

function formatAge(date: Date | string): string {
  const now = new Date()
  const published = new Date(date)
  const diffMs = now.getTime() - published.getTime()
  const diffDays = Math.floor(diffMs / (1000 * 60 * 60 * 24))

  if (diffDays === 0) return 'Today'
  if (diffDays === 1) return '1 day'
  if (diffDays < 30) return `${diffDays} days`
  if (diffDays < 365) {
    const months = Math.floor(diffDays / 30)
    return `${months} month${months !== 1 ? 's' : ''}`
  }
  const years = Math.floor(diffDays / 365)
  return `${years} year${years !== 1 ? 's' : ''}`
}

function formatSize(bytes: number): string {
  if (!bytes || bytes === 0) return '-'

  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let size = bytes
  let unitIndex = 0

  while (size >= 1024 && unitIndex < units.length - 1) {
    size /= 1024
    unitIndex++
  }

  return `${size.toFixed(1)} ${units[unitIndex]}`
}

function getResultScore(resultId: string): QualityScore | undefined {
  // Support both runtime shapes (ref<Map>) and test runner proxied Map (unwrapped)
  const qsRef = qualityScores as unknown as
    | { value?: Map<string, QualityScore> }
    | Map<string, QualityScore>
  if ('value' in qsRef && qsRef.value && typeof qsRef.value.get === 'function') {
    return qsRef.value.get(resultId)
  }
  if (typeof (qsRef as Map<string, QualityScore>).get === 'function') {
    return (qsRef as Map<string, QualityScore>).get(resultId)
  }
  return undefined
}

// Visible score value for the UI: prefer smartScore when available
function getVisibleScoreValue(resultId: string): number | undefined {
  const q = getResultScore(resultId)
  if (!q) return undefined

  // Prefer breakdown-based normalized total when available
  if (q.smartScoreBreakdown && Object.keys(q.smartScoreBreakdown).length > 0) {
    return computeNormalizedSmart(q.smartScoreBreakdown).total
  }

  // Fallback to smartScore numeric normalization
  if (typeof q.smartScore === 'number' && !isNaN(q.smartScore)) {
    // smartScore may be provided as fraction (0..1) or percentage (0..100). Normalize to 0..100.
    let ss = q.smartScore
    if (ss <= 1) ss = ss * 100
    return Math.round(Math.min(100, ss))
  }

  if (typeof q.totalScore === 'number') return q.totalScore
  return undefined
}

function getVisibleScoreClass(resultId: string): string {
  const val = getVisibleScoreValue(resultId) ?? 0
  return getScoreClass(val)
}

function getScoreClass(score: number): string {
  const s = score
  if (s >= 80) return 'excellent'
  if (s >= 60) return 'good'
  if (s >= 40) return 'fair'
  return 'poor'
}

// useScore composable provides getScoreBreakdownTooltip
</script>

<style scoped>
.modal-overlay {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  background-color: rgba(0, 0, 0, 0.75);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  padding: 2rem;
}

.modal-container {
  background-color: #1e1e1e;
  border-radius: 6px;
  width: 100%;
  max-height: 90vh;
  display: flex;
  flex-direction: column;
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.4);
}

.modal-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 1.5rem 2rem;
  border-bottom: 1px solid #3a3a3a;
}

.modal-header h2 {
  margin: 0;
  color: white;
  font-size: 1.5rem;
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.btn-close {
  background: none;
  border: none;
  color: #ccc;
  cursor: pointer;
  padding: 0.5rem;
  border-radius: 6px;
  transition: all 0.2s;
  font-size: 1.5rem;
  display: flex;
  align-items: center;
  justify-content: center;
}

.btn-close:hover {
  background-color: #3a3a3a;
  color: white;
}

.modal-body {
  padding: 1.5rem 2rem;
  overflow-y: auto;
  flex: 1;
}

.search-status {
  text-align: center;
  padding: 3rem 2rem;
  color: var(--brand-500);
  font-size: 1.1rem;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 1rem;
}

.search-status i {
  font-size: 3rem;
}

.results-container {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.results-header {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.results-controls {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.results-count {
  color: #ccc;
  font-size: 0.9rem;
}

.search-bar {
  width: 100%;
}

.search-input-wrapper {
  position: relative;
  display: flex;
  align-items: stretch; /* ensure input and button match height */
  gap: 0.5rem;
  max-width: 100%;
}

.search-icon {
  position: absolute;
  left: 0.75rem;
  top: 50%;
  transform: translateY(-50%);
  color: #8a8a8a;
  font-size: 1rem;
  z-index: 2;
  pointer-events: none; /* make icon non-interactive so clicks go to the input */
}

.search-input {
  flex: 1;
  padding: 0.5rem 1rem 0.5rem 2.5rem;
  background-color: #2a2a2a;
  border: 1px solid #3a3a3a;
  border-radius: 6px;
  color: white;
  font-size: 1rem;
  transition:
    border-color 0.2s,
    box-shadow 0.2s;
  height: 40px;
  box-sizing: border-box;
}

.search-input:focus {
  outline: none;
  border-color: var(--brand-focus);
  box-shadow: 0 0 0 2px rgba(var(--brand-rgb), 0.2);
}

.search-input:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

/* Ensure buttons in search wrapper match input height */
.search-input-wrapper .btn {
  height: 40px;
  padding: 0 1rem;
}

.no-results {
  text-align: center;
  padding: 4rem 2rem;
  color: #999;
}

.no-results i {
  font-size: 4rem;
  margin-bottom: 1rem;
  color: #555;
}

.no-results p {
  margin: 0.5rem 0;
  color: #ccc;
}

.no-results .hint {
  font-size: 0.9rem;
  color: #999;
}

.results-table-wrapper {
  overflow-x: auto;
  border: 1px solid #3a3a3a;
  border-radius: 6px;
  height: calc(100vh - 360px);
}

.results-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.875rem;
}

.results-table thead {
  background-color: #2a2a2a;
  position: sticky;
  top: 0;
  z-index: 1;
}

.results-table th {
  padding: 0.75rem;
  text-align: left;
  color: #ccc;
  font-weight: 500;
  text-transform: uppercase;
  font-size: 0.75rem;
  letter-spacing: 0.5px;
  border-bottom: 2px solid #3a3a3a;
}

.results-table th.col-actions {
  position: sticky;
  right: 0;
  z-index: 3;
  background-color: #2a2a2a;
  box-shadow:
    -1px 0 0 rgba(255, 255, 255, 0.06),
    -10px 0 20px rgba(0, 0, 0, 0.22);
}

.sortable {
  cursor: pointer;
  user-select: none;
  transition: background-color 0.2s;
}

.sortable:hover {
  background-color: #3a3a3a;
}

.header-content {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 100%;
}

.sort-icon {
  font-size: 0.8rem;
  margin-left: 0.5rem;
  opacity: 0.6;
  transition: opacity 0.2s;
}

.sort-icon-inactive {
  opacity: 0.3;
}

.sort-icon-active {
  opacity: 1;
  color: var(--brand-500);
}

.results-table tbody tr {
  border-bottom: 1px solid #2a2a2a;
  transition:
    background-color 0.2s,
    box-shadow 0.2s;
}

.results-table tbody tr:hover {
  background-color: rgba(33, 150, 243, 0.08);
  box-shadow: inset 2px 0 0 var(--brand-500);
}

.results-table tbody tr:hover .col-actions {
  background-color: rgba(27, 39, 52, 0.96);
}

/* Remove row background change on hover — underline title text instead */
.title-text {
  color: white;
  font-weight: 500;
  text-decoration: none;
  transition: color 0.2s;
}

.result-row:hover .title-text {
  color: var(--brand-300);
  text-decoration: underline;
  text-underline-offset: 2px;
}

.results-table td {
  padding: 0.75rem;
  color: #ddd;
  vertical-align: middle;
}

.results-table td.col-actions {
  position: sticky;
  right: 0;
  z-index: 2;
  background-color: #1e1e1e;
  box-shadow:
    -1px 0 0 rgba(255, 255, 255, 0.06),
    -10px 0 20px rgba(0, 0, 0, 0.22);
}

.col-source {
  width: 60px;
}

.col-age {
  width: 100px;
}

.col-title {
  min-width: 300px;
}

.col-indexer {
  width: 150px;
}

.col-size {
  width: 100px;
}

.col-seeders {
  width: 100px;
}

.col-leechers {
  width: 100px;
}

.col-language {
  width: 100px;
}

.col-grabs {
  width: 80px;
}

.grabs {
  margin-left: 8px;
  color: var(--muted);
}
.grabs.unknown {
  color: #7f8c8d;
}

.grabs-badge {
  display: inline-block;
  padding: 0.25rem 0.5rem;
  background-color: rgba(52, 152, 219, 0.15);
  border-radius: 4px;
  font-size: 0.75rem;
  font-weight: 600;
  color: #3498db;
  border: 1px solid rgba(52, 152, 219, 0.3);
}

.grabs-badge.unknown {
  background-color: #3a3a3a;
  color: #666;
  border: none;
}

.col-quality {
  width: 120px;
}

.col-actions {
  width: 60px;
  text-align: center;
}

.source-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  color: #adb5bd;
  font-size: 0.75rem;
  font-weight: 500;
  text-transform: uppercase;
  letter-spacing: 0.3px;
  background-color: rgba(255, 255, 255, 0.08);
  padding: 0.35rem 0.6rem;
  border-radius: 4px;
  white-space: nowrap;
  border: 1px solid rgba(255, 255, 255, 0.1);
  transition:
    background-color 0.2s ease,
    color 0.2s ease,
    border-color 0.2s ease;
}

.source-badge.torrent {
  background-color: rgba(52, 152, 219, 0.15);
  color: #3498db;
  border-color: rgba(52, 152, 219, 0.3);
}

.source-badge.nzb {
  background-color: rgba(155, 89, 182, 0.15);
  color: #9b59b6;
  border-color: rgba(155, 89, 182, 0.3);
}

.source-badge.ddl {
  background-color: rgba(26, 188, 156, 0.15);
  color: #1abc9c;
  border-color: rgba(26, 188, 156, 0.3);
}

.title-cell {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.indexer-name {
  color: var(--brand-400);
  font-size: 0.8rem;
}

.seeders,
.leechers {
  display: flex;
  align-items: center;
  gap: 0.25rem;
  font-size: 0.85rem;
}

.seeders {
  color: #999;
}

.seeders.good {
  color: #2ecc71;
  font-weight: 500;
}

.seeders.medium {
  color: #f39c12;
}

.leechers {
  color: #999;
}

.language-badge,
.quality-badge {
  display: inline-block;
  padding: 0.25rem 0.5rem;
  background-color: #3a3a3a;
  border-radius: 4px;
  font-size: 0.75rem;
  font-weight: 500;
  color: #ccc;
}

.quality-badge.format-only {
  color: #999;
  font-style: italic;
}

.score-cell {
  display: flex;
  align-items: center;
  justify-content: center;
}

.score-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  padding: 0.35rem 0.75rem;
  border-radius: 4px;
  font-size: 0.85rem;
  font-weight: 600;
  white-space: nowrap;
}

.score-badge.loading {
  background-color: transparent;
  color: #666;
  border: none;
  padding: 0;
}

.score-badge.rejected {
  background-color: rgba(231, 76, 60, 0.2);
  color: #ff6b6b;
  border: 1px solid rgba(231, 76, 60, 0.4);
}

.score-badge.excellent {
  background-color: rgba(39, 174, 96, 0.2);
  color: #51cf66;
  border: 1px solid rgba(39, 174, 96, 0.4);
}

.score-badge.good {
  background-color: rgba(52, 152, 219, 0.2);
  color: #74c0fc;
  border: 1px solid rgba(52, 152, 219, 0.4);
}

.score-badge.fair {
  background-color: rgba(241, 196, 15, 0.2);
  color: #ffd43b;
  border: 1px solid rgba(241, 196, 15, 0.4);
}

.score-badge.poor {
  background-color: rgba(149, 165, 166, 0.2);
  color: #adb5bd;
  border: 1px solid rgba(149, 165, 166, 0.4);
}

.btn-icon {
  background: none;
  border: none;
  color: #666;
  cursor: pointer;
  padding: 0.5rem;
  border-radius: 6px;
  transition: all 0.2s;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 1.2rem;
}

.result-row:hover .btn-icon {
  color: #ccc;
  background-color: rgba(33, 150, 243, 0.2);
}

.btn-icon:hover:not(:disabled) {
  background-color: var(--brand-500);
  color: white;
}

.btn-icon:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.btn-download:hover:not(:disabled) {
  background-color: var(--brand-500);
  color: white;
}

@media (max-width: 1200px) {
  .modal-container {
    max-width: 95%;
  }

  .results-table {
    font-size: 0.8rem;
  }

  .col-title {
    min-width: 200px;
  }
}

/* Mobile search overlay styles */
@media (max-width: 768px) {
  .modal-overlay {
    background-color: rgba(0, 0, 0, 0.9);
    padding: 0;
    align-items: flex-start;
    padding-top: 2rem;
  }

  .modal-container {
    position: absolute;
    top: 50%;
    left: 50%;
    transform: translate(-50%, -50%);
    width: 95%;
    max-width: 500px;
    max-height: 80vh;
    border-radius: 6px;
  }

  .modal-header {
    padding: 1rem 1.5rem;
  }

  .modal-header h2 {
    font-size: 1.25rem;
  }

  .modal-body {
    padding: 1rem 1.5rem;
  }

  /* Mobile search bar - inline with results header */
  .search-bar {
    width: 100%;
  }

  .search-input-wrapper {
    gap: 0.25rem;
    flex-wrap: wrap;
  }

  .search-input {
    padding: 0.5rem 1rem 0.5rem 2.25rem;
    font-size: 0.95rem;
    height: 44px;
    flex: 1 1 100%;
  }

  .search-icon {
    left: 0.75rem;
    font-size: 1.1rem;
    top: 25%;
  }

  /* Mobile button sizing - buttons on same line */
  .search-input-wrapper .btn {
    padding: 0.4rem 0.6rem;
    height: 44px;
    font-size: 0.85rem;
    flex: 1;
  }

  .search-input-wrapper .btn-primary {
    flex: 1.2;
  }

  /* Adjust results header spacing on mobile */
  .results-header {
    padding-top: 0;
  }

  .results-controls {
    flex-direction: column;
    gap: 0.75rem;
    align-items: stretch;
  }

  .results-count {
    text-align: center;
    font-size: 0.85rem;
  }

  .btn-sm {
    align-self: center;
    min-width: 120px;
  }

  /* Table responsiveness on mobile */
  .results-table-wrapper {
    margin: 0 -1rem;
    border-radius: 6px;
    border-left: none;
    border-right: none;
  }

  .results-table {
    font-size: 0.75rem;
  }

  .results-table th,
  .results-table td {
    padding: 0.5rem 0.25rem;
  }

  .col-title {
    min-width: 150px;
  }

  .col-indexer {
    width: 100px;
  }

  .col-size {
    width: 80px;
  }

  .col-peers {
    width: 100px;
  }

  .col-language,
  .col-quality {
    width: 90px;
  }

  .col-actions {
    width: 50px;
  }

  /* Hide less important columns on very small screens */
  @media (max-width: 480px) {
    .col-age,
    .col-language {
      display: none;
    }

    .col-title {
      min-width: 120px;
    }
  }
}
</style>
