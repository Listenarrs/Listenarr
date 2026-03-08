<template>
  <div class="wanted-view">
    <div class="page-header">
      <h1>
        <PhHeart />
        Wanted
      </h1>
      <div class="wanted-actions">
        <button
          class="btn btn-primary"
          @click="searchMissing"
          :disabled="categorizedWanted.missing.length === 0"
        >
          <PhRobot />
          Automatic Search All
        </button>
        <button class="btn btn-secondary" @click="openManualImport">
          <PhFolderPlus />
          Manual Import
        </button>
      </div>
    </div>

    <div class="wanted-filters">
      <!-- Mobile dropdown -->
      <div class="filter-tabs-mobile">
        <CustomSelect v-model="selectedTab" :options="mobileTabOptions" class="tab-dropdown" />
      </div>

      <!-- Desktop tabs -->
      <div class="filter-tabs-desktop">
        <div class="filter-tabs">
          <button
            v-for="tab in filterTabs"
            :key="tab.value"
            :class="['tab', { active: selectedTab === tab.value }]"
            @click="selectedTab = tab.value"
          >
            {{ tab.label }}
            <span v-if="tab.count > 0" class="tab-badge">{{ tab.count }}</span>
          </button>
        </div>
      </div>
    </div>

    <!-- Loading State -->
    <LoadingState v-if="loading" message="Loading wanted audiobooks..." />

    <!-- Wanted List -->
    <div
      v-else-if="filteredWanted.length > 0"
      ref="scrollContainer"
      class="wanted-list-container"
      @scroll="updateVisibleRange"
    >
      <div class="wanted-list-spacer" :style="{ height: `${totalHeight}px` }">
        <div class="wanted-list" :style="{ transform: `translateY(${topPadding}px)` }">
          <div
            v-for="item in visibleWanted"
            :key="item.id"
            class="wanted-item"
          >
            <div class="wanted-poster">
              <img
                :src="getProtectedImageSrc(item.imageUrl, `wanted-${item.id}`, getPlaceholderUrl())"
                :alt="item.title"
                loading="lazy"
                decoding="async"
                @error="handleImageError"
              />
            </div>
            <div class="wanted-info">
              <h3>
                <span v-if="hasActiveDownload(item)" class="download-indicator" title="Downloading">
                  <PhDownloadSimple :size="20" weight="fill" />
                </span>
                {{ safeText(item.title) }}
              </h3>
              <h4 v-if="item.authors?.length">
                by {{ item.authors.map((author) => safeText(author)).join(', ') }}
              </h4>
              <div class="wanted-meta">
                <span v-if="item.series"
                  >{{ safeText(item.series)
                  }}<span v-if="item.seriesNumber"> #{{ item.seriesNumber }}</span></span
                >
                <span v-if="item.publishYear">Released: {{ formatDate(item.publishYear) }}</span>
                <span v-if="item.runtime">{{ formatRuntime(item.runtime) }}</span>
              </div>
              <div class="wanted-quality">
                <template v-if="getQualityProfileForAudiobook(item)">
                  Wanted Quality:
                  <span class="profile-name">{{
                    getQualityProfileForAudiobook(item)?.name ?? 'Unknown'
                  }}</span>
                </template>
                <template v-else-if="item.quality"> Wanted Quality: {{ item.quality }} </template>
                <template v-else> Wanted Quality: unknown </template>
              </div>
              <div v-if="searchResults[item.id]" class="search-status">
                <template v-if="searching[item.id]">
                  <PhSpinner class="ph-spin" />
                </template>
                {{ searchResults[item.id] }}
              </div>
            </div>
            <div class="wanted-status">
              <span :class="['status-badge', getStatusClass(item)]">
                {{ getStatusText(item) }}
              </span>
            </div>
            <div class="wanted-actions-cell">
              <button
                class="icon-button"
                @click="searchAudiobook(item)"
                :disabled="searching[item.id]"
                title="Automatic Search"
              >
                <PhRobot />
              </button>
              <button class="icon-button" @click="openManualSearch(item)" title="Manual Search">
                <PhMagnifyingGlass />
              </button>
              <button
                class="icon-button danger"
                @click="markAsSkipped(item)"
                :disabled="searching[item.id]"
                title="Unmonitor Audiobook"
              >
                <PhX />
              </button>
            </div>
          </div>
        </div>
        <!-- Close wanted-list -->
      </div>
      <!-- Close wanted-list-spacer -->
    </div>
    <!-- Close wanted-list-container -->

    <!-- Empty State -->
    <EmptyState
      v-else
      :title="getEmptyStateTitle()"
      :message="getEmptyStateMessage()"
    >
      <template #icon>
        <PhCheckCircle :size="48" />
      </template>
    </EmptyState>

    <!-- Manual Search Modal -->
    <ManualSearchModal
      :is-open="showManualSearchModal"
      :audiobook="selectedAudiobook"
      @close="closeManualSearch"
      @downloaded="handleDownloaded"
    />

    <!-- Manual Import Modal -->
    <ManualImportModal
      :is-open="showManualImportModal"
      @close="closeManualImport"
      @imported="handleImported"
    />
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, nextTick } from 'vue'
import { useLibraryStore } from '@/stores/library'
import { useConfigurationStore } from '@/stores/configuration'
import { apiService } from '@/services/api'
import { errorTracking } from '@/services/errorTracking'
import { handleImageError } from '@/utils/imageFallback'
import ManualSearchModal from '@/components/domain/search/ManualSearchModal.vue'
import ManualImportModal from '@/components/feedback/ManualImportModal.vue'
import CustomSelect from '@/components/form/CustomSelect.vue'
import { EmptyState, LoadingState } from '@/components/base'
import type { Audiobook, SearchResult, Download } from '@/types'
import { safeText } from '@/utils/textUtils'
import {
  PhHeart,
  PhRobot,
  PhFolderPlus,
  PhSpinner,
  PhMagnifyingGlass,
  PhX,
  PhCheckCircle,
  PhBooks,
  PhQuestion,
  PhXCircle,
  PhSkipForward,
  PhDownloadSimple,
} from '@phosphor-icons/vue'
import { logger } from '@/utils/logger'
import { useDownloadsStore } from '@/stores/downloads'
import { useProtectedImages } from '@/composables/useProtectedImages'

const downloadsStore = useDownloadsStore()
const { getProtectedImageSrc } = useProtectedImages()

const libraryStore = useLibraryStore()

const configurationStore = useConfigurationStore()

// Virtual scrolling setup
const scrollContainer = ref<HTMLElement | null>(null)
const ROW_HEIGHT = 165 // Height of wanted item: 120px poster + 40px padding (20px*2) + 5px gap/border
const BUFFER_ROWS = 3

const visibleRange = ref({ start: 0, end: 20 })

// Update visible range for virtual scrolling (defined early to avoid TDZ when called from onMounted)
const updateVisibleRange = () => {
  if (!scrollContainer.value) return

  const scrollTop = scrollContainer.value.scrollTop
  const viewportHeight = scrollContainer.value.clientHeight

  const firstVisibleIndex = Math.floor(scrollTop / ROW_HEIGHT)
  const visibleItemCount = Math.ceil(viewportHeight / ROW_HEIGHT)

  const startIndex = Math.max(0, firstVisibleIndex - BUFFER_ROWS)
  // filteredWanted may not be initialized yet; guard with optional chaining
  const endIndex = Math.min(
    firstVisibleIndex + visibleItemCount + BUFFER_ROWS,
    filteredWanted?.value?.length || 0,
  )

  visibleRange.value = { start: startIndex, end: endIndex }
}

const getQualityProfileForAudiobook = (audiobook: Audiobook) => {
  if (!audiobook || !audiobook.qualityProfileId) {
    return null
  }
  const profile = configurationStore.qualityProfiles.find(
    (profile) => profile.id === audiobook.qualityProfileId,
  )
  return profile || null
}

const selectedTab = ref('all')
import { getPlaceholderUrl } from '@/utils/placeholder'
const loading = ref(false)
const searching = ref<Record<number, boolean>>({})
const searchResults = ref<Record<number, string>>({})
const showManualSearchModal = ref(false)
const selectedAudiobook = ref<Audiobook | null>(null)
const showManualImportModal = ref(false)

const mobileTabOptions = computed(() => [
  { value: 'all', label: 'All', icon: PhBooks },
  { value: 'missing', label: 'Missing', icon: PhQuestion },
  { value: 'searching', label: 'Searching', icon: PhMagnifyingGlass },
  { value: 'failed', label: 'Failed', icon: PhXCircle },
  { value: 'skipped', label: 'Skipped', icon: PhSkipForward },
])

onMounted(async () => {
  loading.value = true
  await libraryStore.fetchLibrary()
  await configurationStore.loadQualityProfiles()
  loading.value = false

  // Initialize virtual scrolling
  await nextTick()
  updateVisibleRange()

})

// Filter audiobooks that are monitored and missing files
// Prefer the server-provided `wanted` flag when present. If the server
// does not include the flag (migration / older records), fall back to a
// local computation: monitored && no files (or no primary filePath).
const wantedAudiobooks = computed(() => {
  return libraryStore.audiobooks.filter((audiobook) => {
    const serverWanted = (audiobook as unknown as Record<string, unknown>)['wanted']

    // If server explicitly provided true/false, honor it
    if (serverWanted === true) return true
    if (serverWanted === false) return false

    // Fallback: treat as wanted when monitored and there are no files
    const hasFiles = Array.isArray(audiobook.files) ? audiobook.files.length > 0 : false
    const hasPrimaryFile = !!(audiobook.filePath && audiobook.filePath.toString().trim() !== '')

    return !!audiobook.monitored && !hasFiles && !hasPrimaryFile
  })
})

// Categorize wanted audiobooks by their current search state
const categorizedWanted = computed(() => {
  const all = wantedAudiobooks.value
  const searchingItems = all.filter((a) => searching.value[a.id])
  const failedItems = all.filter(
    (a) =>
      searchResults.value[a.id] &&
      searchResults.value[a.id] !== 'Searching...' &&
      !searching.value[a.id],
  )
  const missingItems = all.filter((a) => !searching.value[a.id] && !searchResults.value[a.id])
  const skippedItems: Audiobook[] = [] // For future use, currently empty

  return {
    all,
    searching: searchingItems,
    failed: failedItems,
    missing: missingItems,
    skipped: skippedItems,
  }
})

// Count by status
const filterTabs = computed(() => [
  { label: 'All', value: 'all', count: categorizedWanted.value.all.length },
  { label: 'Missing', value: 'missing', count: categorizedWanted.value.missing.length },
  { label: 'Searching', value: 'searching', count: categorizedWanted.value.searching.length },
  { label: 'Failed', value: 'failed', count: categorizedWanted.value.failed.length },
  { label: 'Skipped', value: 'skipped', count: categorizedWanted.value.skipped.length },
])

const filteredWanted = computed(() => {
  switch (selectedTab.value) {
    case 'all':
      return categorizedWanted.value.all
    case 'missing':
      return categorizedWanted.value.missing
    case 'searching':
      return categorizedWanted.value.searching
    case 'failed':
      return categorizedWanted.value.failed
    case 'skipped':
      return categorizedWanted.value.skipped
    default:
      return categorizedWanted.value.all
  }
})

const visibleWanted = computed(() => {
  return filteredWanted.value.slice(visibleRange.value.start, visibleRange.value.end)
})

const totalHeight = computed(() => {
  return filteredWanted.value.length * ROW_HEIGHT
})

const topPadding = computed(() => {
  return visibleRange.value.start * ROW_HEIGHT
})

// Map audiobook IDs to active downloads (exclude terminal/completed states)
const activeDownloadsByAudiobook = computed(() => {
  const map = new Map<number, Download>()
  const terminalStates = ['Completed', 'Failed', 'Ready', 'Moved', 'ImportBlocked']
  
  downloadsStore.downloads.forEach((download) => {
    if (download.audiobookId && !terminalStates.includes(download.status)) {
      map.set(download.audiobookId, download)
    }
  })
  return map
})

function hasActiveDownload(item: Audiobook): boolean {
  return activeDownloadsByAudiobook.value.has(item.id)
}

function getActiveDownload(item: Audiobook): Download | undefined {
  return activeDownloadsByAudiobook.value.get(item.id)
}

function getStatusClass(item: Audiobook): string {
  if (hasActiveDownload(item)) {
    return 'downloading'
  }
  if (searching.value[item.id]) {
    return 'searching'
  }
  if (searchResults.value[item.id] && searchResults.value[item.id] !== 'Searching...') {
    return 'failed'
  }
  return 'missing'
}

function getStatusText(item: Audiobook): string {
  const download = getActiveDownload(item)
  if (download) {
    if (download.status === 'Downloading') {
      return `Downloading (${download.progress.toFixed(0)}%)`
    }
    return download.status
  }
  if (searching.value[item.id]) {
    return 'Searching'
  }
  if (searchResults.value[item.id] && searchResults.value[item.id] !== 'Searching...') {
    return 'Failed'
  }
  return 'Missing'
}

function getEmptyStateTitle(): string {
  switch (selectedTab.value) {
    case 'all':
      return 'No Wanted Audiobooks'
    case 'missing':
      return 'No Missing Audiobooks'
    case 'searching':
      return 'No Searching Audiobooks'
    case 'failed':
      return 'No Failed Audiobooks'
    case 'skipped':
      return 'No Skipped Audiobooks'
    default:
      return 'No Audiobooks'
  }
}

function getEmptyStateMessage(): string {
  switch (selectedTab.value) {
    case 'all':
      return 'All your monitored audiobooks have files!'
    case 'missing':
      return 'All wanted audiobooks are currently being searched or have been searched.'
    case 'searching':
      return 'No audiobooks are currently being searched.'
    case 'failed':
      return 'No audiobooks have failed searches.'
    case 'skipped':
      return 'No audiobooks have been skipped.'
    default:
      return 'No audiobooks in this category.'
  }
}

const formatDate = (date: string | undefined): string => {
  if (!date) return 'Unknown'
  try {
    return new Date(date).toLocaleDateString('en-US', {
      month: 'short',
      day: 'numeric',
      year: 'numeric',
    })
  } catch {
    return date
  }
}

const normalizeRuntimeMinutes = (runtime: number): number => {
  // Wanted data can contain legacy second-based runtime values from older metadata flows.
  // Treat very large values as seconds and normalize to minutes for display.
  return runtime >= 20000 ? Math.round(runtime / 60) : Math.round(runtime)
}

const formatRuntime = (runtime: number | undefined): string => {
  if (!runtime || runtime <= 0) return 'Unknown'
  const totalMinutes = normalizeRuntimeMinutes(runtime)
  const hours = Math.floor(totalMinutes / 60)
  const minutes = totalMinutes % 60
  return `${hours}h ${minutes}m`
}

const searchMissing = async () => {
  logger.debug('Automatic search for all missing audiobooks')

  // Search all missing audiobooks sequentially
  for (const audiobook of categorizedWanted.value.missing) {
    await searchAudiobook(audiobook)
    // Add a small delay between searches to avoid overwhelming indexers
    await new Promise((resolve) => setTimeout(resolve, 1000))
  }
}

function openManualSearch(item: Audiobook) {
  selectedAudiobook.value = item
  showManualSearchModal.value = true
}

function openManualImport() {
  showManualImportModal.value = true
}

function closeManualImport() {
  showManualImportModal.value = false
}

async function handleImported(result: { imported: number }) {
  logger.debug('Manual import completed, imported:', result.imported)
  // Refresh library
  await libraryStore.fetchLibrary()
  closeManualImport()
}

function closeManualSearch() {
  showManualSearchModal.value = false
  selectedAudiobook.value = null
}

function handleDownloaded(result: SearchResult) {
  logger.debug('Downloaded:', result)
  // Refresh downloads and library after successful manual download so Activity/Downloads show the new item
  setTimeout(async () => {
    try {
      await downloadsStore.loadDownloads()
    } catch (e) {
      logger.warn('Failed to refresh downloads after manual download:', e)
    }
    await libraryStore.fetchLibrary()
    closeManualSearch()
  }, 2000)
}

const searchAudiobook = async (item: Audiobook) => {
  logger.debug('Searching audiobook:', item.title)

  searching.value[item.id] = true
  searchResults.value[item.id] = 'Searching...'

  try {
    const result = await apiService.searchAndDownload(item.id)

    if (result.success) {
      searchResults.value[item.id] = `Found on ${result.indexerUsed}, downloading...`

      // Refresh downloads and library to update status (ensure DDL downloads show up immediately)
      setTimeout(async () => {
        try {
          await downloadsStore.loadDownloads()
        } catch (e) {
          logger.warn('Failed to refresh downloads after search:', e)
        }
        await libraryStore.fetchLibrary()
        delete searching.value[item.id]
        delete searchResults.value[item.id]
      }, 2000)
    } else {
      searchResults.value[item.id] = result.message || 'No matches found'
      setTimeout(() => {
        delete searching.value[item.id]
        delete searchResults.value[item.id]
      }, 5000)
    }
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'WantedView',
      operation: 'searchWanted',
      metadata: { itemId: item.id },
    })
    searchResults.value[item.id] = 'Search failed'
    setTimeout(() => {
      delete searching.value[item.id]
      delete searchResults.value[item.id]
    }, 5000)
  }
}

const markAsSkipped = async (item: Audiobook) => {
  logger.debug('Mark as skipped:', item.title)

  try {
    await apiService.updateAudiobook(item.id, { monitored: false })
    await libraryStore.fetchLibrary()
  } catch (err) {
    logger.error('Failed to unmonitor audiobook:', err)
  }
}

// Handle dropdown tab change
// const onTabChange = (event: Event) => {
//   const target = event.target as HTMLSelectElement
//   const newTab = target.value as 'all' | 'missing' | 'searching' | 'failed' | 'skipped'
//   selectedTab.value = newTab
// }
</script>

<style scoped>
.wanted-view {
  padding: 1em;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 2rem;
}

/* Virtual scrolling container */
.wanted-list-container {
  height: calc(100vh - 225px);
  overflow-y: auto;
  position: relative;
  padding: 1rem 0.5em 1rem 0;
  scrollbar-gutter: stable; /* Reserve space for scrollbar to prevent layout shifts */
  width: calc(100% + 0.5em); /* Leaves space for scrollbar without cutting off content */
}

.wanted-list-spacer {
  position: relative;
  width: 100%;
}

.wanted-list {
  position: absolute;
  top: 0;
  left: 0;
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.page-header h1 {
  margin: 0;
  color: white;
  font-size: 2rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 500;
}

.page-header h1 svg {
  color: #fa5252;
  width: 32px;
  height: 32px;
}

.wanted-actions {
  display: flex;
  gap: 0.75rem;
}

/* Button visuals are centralized in `src/assets/buttons.css`.
   Component-local duplication removed to ensure a single source of truth.
   Use `.btn`, `.btn-primary`, `.icon-button`, etc. */
.filter-tabs {
  display: flex;
  gap: 0.5rem;
  border-bottom: 2px solid rgba(255, 255, 255, 0.1);
  overflow-x: auto;
  scrollbar-width: none;
}

.filter-tabs::-webkit-scrollbar {
  display: none;
}

.tab {
  background: none;
  border: none;
  color: #adb5bd;
  cursor: pointer;
  padding: 0.875rem 1.5rem;
  border-radius: 6px;
  transition: all 0.2s ease;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 500;
  white-space: nowrap;
  position: relative;
  font-size: 0.9rem;
}

.tab::after {
  content: '';
  position: absolute;
  bottom: -2px;
  left: 0;
  right: 0;
  height: 2px;
  background: transparent;
  transition: background 0.2s ease;
}

.tab:hover {
  background-color: rgba(255, 255, 255, 0.05);
  color: white;
}

.tab.active {
  background-color: rgba(77, 171, 247, 0.15);
  color: #4dabf7;
  font-weight: 500;
}

.tab.active::after {
  background: #4dabf7;
}

.tab-badge {
  background-color: rgba(255, 255, 255, 0.15);
  border-radius: 6px;
  padding: 0.15rem 0.5rem;
  font-size: 0.75rem;
  font-weight: 500;
  min-width: 20px;
  text-align: center;
}

.tab.active .tab-badge {
  background-color: rgba(77, 171, 247, 0.25);
  color: #74c0fc;
}

.loading-state {
  text-align: center;
  padding: 4rem 2rem;
  color: #adb5bd;
  background-color: #2a2a2a;
  border-radius: 6px;
  border: 1px solid rgba(255, 255, 255, 0.05);
}

.loading-state svg {
  font-size: 3rem;
  color: #4dabf7;
  margin-bottom: 1rem;
  width: 48px;
  height: 48px;
}

.loading-state p {
  font-size: 1.1rem;
  font-weight: 500;
}

.ph-spin {
  animation: spin 1s linear infinite;
}

/* @keyframes spin is centralized in src/assets/animations.css */

.wanted-list {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.wanted-item {
  display: flex;
  align-items: center;
  padding: 1.25rem;
  background-color: #2a2a2a;
  border-radius: 6px;
  border-left: 4px solid #fa5252;
  transition: all 0.2s ease;
  border: 1px solid rgba(255, 255, 255, 0.05);
}

.wanted-item:hover {
  background-color: #2f2f2f;
  border-color: rgba(250, 82, 82, 0.3);
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.3);
  border-left-color: #ff6b6b;
}

.wanted-poster {
  width: 120px;
  height: 120px;
  margin-right: 1.25rem;
  background-color: rgba(255, 255, 255, 0.05);
  border-radius: 6px;
  overflow: hidden;
  flex-shrink: 0;
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.3);
  transition: transform 0.2s ease;
}

.wanted-item:hover .wanted-poster {
  transform: scale(1.02);
}

.wanted-poster img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.wanted-info {
  flex: 1;
  min-width: 0;
}

.wanted-info h3 {
  margin: 0 0 0.35rem 0;
  color: white;
  font-size: 1.1rem;
  font-weight: 500;
  line-height: 1.3;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.download-indicator {
  color: #51cf66;
  display: inline-flex;
  align-items: center;
  animation: bounce 2s ease-in-out infinite;
}

@keyframes bounce {
  0%, 100% {
    transform: translateY(0);
  }
  50% {
    transform: translateY(-3px);
  }
}

.wanted-info h4 {
  margin: 0 0 0.5rem 0;
  color: #4dabf7;
  font-size: 0.9rem;
  font-weight: 500;
}

.wanted-meta {
  display: flex;
  gap: 0.75rem;
  margin-bottom: 0.5rem;
  color: #868e96;
  font-size: 0.85rem;
  flex-wrap: wrap;
}

.wanted-meta span {
  background-color: rgba(255, 255, 255, 0.05);
  padding: 0.25rem 0.6rem;
  border-radius: 6px;
}

.wanted-meta span span {
  background-color: unset;
  padding: 0;
  border-radius: unset;
}

.wanted-quality {
  color: #ffd43b;
  font-size: 0.85rem;
  font-weight: 500;
  background-color: rgba(255, 212, 59, 0.1);
  padding: 0.25rem 0.6rem;
  border-radius: 6px;
  display: inline-block;
}

.profile-name {
  color: #fcc419;
  font-weight: 500;
}

.search-status {
  margin-top: 0.5rem;
  color: #4dabf7;
  font-size: 0.85rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  background-color: rgba(77, 171, 247, 0.1);
  padding: 0.35rem 0.7rem;
  border-radius: 6px;
  display: inline-flex;
}

.search-status svg {
  width: 16px;
  height: 16px;
}

.wanted-status {
  margin: 0 1rem;
  flex-shrink: 0;
}

.status-badge {
  padding: 0.35rem 0.85rem;
  border-radius: 6px;
  font-size: 0.75rem;
  font-weight: 500;
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

.status-badge.missing {
  background-color: rgba(250, 82, 82, 0.15);
  color: #fa5252;
  border: 1px solid rgba(250, 82, 82, 0.3);
}

.status-badge.searching {
  background-color: rgba(77, 171, 247, 0.15);
  color: #4dabf7;
  border: 1px solid rgba(77, 171, 247, 0.3);
}

.status-badge.downloading {
  background-color: rgba(81, 207, 102, 0.15);
  color: #51cf66;
  border: 1px solid rgba(81, 207, 102, 0.3);
  animation: pulse 2s ease-in-out infinite;
}

@keyframes pulse {
  0%, 100% {
    opacity: 1;
  }
  50% {
    opacity: 0.6;
  }
}

.status-badge.failed {
  background-color: rgba(134, 142, 150, 0.15);
  color: #868e96;
  border: 1px solid rgba(134, 142, 150, 0.3);
}

.status-badge.skipped {
  background-color: rgba(134, 142, 150, 0.15);
  color: #868e96;
  border: 1px solid rgba(134, 142, 150, 0.3);
}

.wanted-actions-cell {
  display: flex;
  gap: 0.5rem;
  flex-shrink: 0;
}

.empty-state {
  text-align: center;
  padding: 4rem 2rem;
  color: #adb5bd;
  background-color: #2a2a2a;
  border-radius: 6px;
  border: 1px solid rgba(255, 255, 255, 0.05);
}

.empty-icon {
  font-size: 4rem;
  margin-bottom: 1.5rem;
  color: #868e96;
}

.empty-icon svg {
  width: 80px;
  height: 80px;
}

.empty-state h2 {
  color: white;
  margin-bottom: 0.75rem;
  font-weight: 500;
  font-size: 1.5rem;
}

.empty-state p {
  color: #868e96;
  font-size: 1rem;
  line-height: 1.5;
  max-width: 400px;
  margin: 0 auto;
}

@media (max-width: 768px) {
  .wanted-item {
    flex-direction: column;
    align-items: flex-start;
  }

  .wanted-poster {
    margin-bottom: 1rem;
    margin-right: 0;
  }

  .wanted-status,
  .wanted-actions-cell {
    margin: 1rem 0 0 0;
    align-self: stretch;
  }

  .wanted-actions-cell {
    justify-content: flex-end;
    gap: 0.75rem;
  }

  .wanted-actions-cell .icon-button {
    padding: 0.75rem;
    min-width: 44px;
    min-height: 44px;
  }

  .wanted-actions-cell .icon-button svg {
    width: 24px;
    height: 24px;
  }

  /* Stack page header actions vertically on mobile */
  .wanted-actions {
    flex-direction: column;
    gap: 0.5rem;
    width: 100%;
  }

  .wanted-actions .btn {
    width: 80%;
    justify-content: center;
    margin: 0 0 0 auto;
    font-size: 0.85rem;
  }

  /* Mobile filter tabs */
  .filter-tabs-mobile {
    display: block;
  }

  .filter-tabs-desktop {
    display: none;
  }

  .tab-dropdown {
    width: 100%;
    color: #fff;
    font-size: 0.95rem;
    cursor: pointer;
    transition: all 0.2s ease;
  }

  .tab-dropdown:focus {
    outline: none;
    border-color: #4dabf7;
    box-shadow: 0 0 0 3px rgba(77, 171, 247, 0.1);
  }

  .tab-dropdown option {
    background-color: #2a2a2a;
    color: #fff;
  }
}

/* Desktop filter tabs */
@media (min-width: 769px) {
  .filter-tabs-mobile {
    display: none;
  }

  .filter-tabs-desktop {
    display: block;
  }
}
</style>
