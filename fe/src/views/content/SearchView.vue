<template>
  <div class="search-page">
    <div class="search-header">
      <h2>Search Media</h2>
      <p>Search for audiobooks and media across your configured APIs</p>
    </div>

    <!-- Quick raw-fetch debug control -->
    <div class="mb-md">
      <button
        @click="fetchRawDebug(searchQuery || 'Harry')"
        class="search-button bg-secondary"
      >
        Fetch Raw API Results (apiService)
      </button>
      <button
        @click="fetchRawDebugWindow(searchQuery || 'Harry')"
        class="search-button bg-dark ml-sm"
      >
        Fetch Raw API Results (window.fetch)
      </button>
    </div>

    <!-- Debug: show raw search results (temporary) -->
    <div
      v-if="!searchStore.isSearching && !searchStore.isCancelled"
      class="debug-results bg-white border p-md rounded mt-md"
    >
      <strong>Debug: raw searchStore.searchResults</strong>
      <pre class="debug-pre">{{
        JSON.stringify(searchStore.searchResults, null, 2)
      }}</pre>
      <div class="mt-sm">
        <strong>Raw debug fetch results (window.rawDebugResults):</strong>
        <div>Count: {{ rawDebugResults ? rawDebugResults.length : 0 }}</div>
        <pre class="debug-pre">{{
          JSON.stringify(rawDebugResults, null, 2)
        }}</pre>
      </div>
    </div>

    <div class="search-form">
      <div class="search-input-group">
        <input
          v-model="searchQuery"
          type="text"
          placeholder="Enter search query..."
          class="search-input form-input"
          @keyup.enter="performSearch"
        />
        <button
          @click="performSearch"
          :disabled="!searchQuery.trim() || searchStore.isSearching"
          class="search-button btn"
        >
          {{ searchStore.isSearching ? 'Searching...' : 'Search' }}
        </button>
        <button
          v-if="searchStore.isSearching"
          @click="cancelSearch"
          class="search-button cancel-button btn"
        >
          Cancel
        </button>
      </div>

      <div class="search-filters">
        <select v-model="selectedCategory" class="filter-select form-select">
          <option value="">All Categories</option>
          <option value="audiobook">Audiobooks</option>
          <option value="music">Music</option>
          <option value="podcast">Podcasts</option>
        </select>
      </div>
    </div>

    <div v-if="searchStore.isSearching" class="loading">
      <PhSpinner class="ph-spin" />
      <p>Searching...</p>
    </div>
    <div v-else-if="showCancelled" class="cancelled">
      <PhXCircle />
      <p>Search cancelled</p>
    </div>

    <div v-else-if="showResults" class="search-results">
      <h3>Search Results ({{ searchStore.searchResults.length }})</h3>
      <div class="results-grid">
        <div v-for="result in searchStore.searchResults" :key="result.id" class="result-card">
          <div class="result-info">
            <h4>
              <a
                v-if="getResultLink(result)"
                :href="getResultLink(result)"
                target="_blank"
                rel="noopener noreferrer"
              >
                {{ safeText(result.title) }}
              </a>
              <span v-else>{{ safeText(result.title) }}</span>
            </h4>
            <p class="result-artist">{{ safeText(result.artist) }}</p>
            <p class="result-album">{{ safeText(result.album) }}</p>

            <!-- Quality Score Badge -->
            <div v-if="getResultScore(result.id)" class="quality-score">
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
                    <PhStar />
                    Score: {{ getVisibleScoreValue(result.id) ?? '-' }}
                  </span>
                </template>
              </ScorePopover>
            </div>

            <!-- Audiobook metadata -->
            <div v-if="result.narrator || result.runtime || result.series" class="audiobook-meta">
              <p v-if="result.narrator" class="meta-narrator">
                Narrated by {{ safeText(result.narrator) }}
              </p>
              <div class="meta-details">
                <span v-if="result.runtime" class="meta-runtime">
                  ⏱ {{ formatRuntime(result.runtime) }}
                </span>
                <span v-if="result.series" class="meta-series">
                  Series: {{ safeText(result.series)
                  }}<span v-if="result.seriesNumber"> #{{ result.seriesNumber }}</span>
                </span>
              </div>
            </div>

            <div class="result-meta">
              <span class="result-size">{{ formatFileSize(result.size) }}</span>
              <span v-if="result.quality" class="result-quality">{{ result.quality }}</span>
              <span class="result-source">{{ result.source }}</span>
              <span v-if="result.language" class="result-language">
                <PhGlobe /> {{ capitalizeLanguage(result.language) }}
              </span>
            </div>
            <div class="result-stats">
              <span v-if="result.seeders !== undefined && result.seeders !== null" class="seeders"
                >↑ {{ result.seeders }}</span
              >
              <span
                v-if="result.leechers !== undefined && result.leechers !== null"
                class="leechers"
                >↓ {{ result.leechers }}</span
              >
              <span v-if="result.grabs !== undefined" class="grabs">✚ {{ result.grabs }}</span>
              <span v-if="result.files !== undefined" class="files">📁 {{ result.files }}</span>
            </div>
          </div>
          <div class="result-actions">
            <button
              @click="addToLibrary(result)"
              :class="['add-button','btn', { added: addedResults.has(result.id), 'btn-primary': !addedResults.has(result.id) }]"
              :disabled="isAddingToLibrary || addedResults.has(result.id)"
            >
              <template v-if="addedResults.has(result.id)">
                <PhCheck />
              </template>
              <template v-else>
                <PhPlus />
              </template>
              {{ addedResults.has(result.id) ? 'Added' : 'Add to Library' }}
            </button>
          </div>
        </div>
      </div>
    </div>

    <div v-else-if="showNoResults" class="no-results">
      <p>No results found for "{{ searchQuery }}"</p>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, watch, nextTick, onMounted } from 'vue'
import { PhSpinner, PhXCircle, PhStar, PhCheck, PhPlus, PhGlobe } from '@phosphor-icons/vue'
import { useSearchStore } from '@/stores/search'
import { useLibraryStore } from '@/stores/library'
import { apiService } from '@/services/api'
import { logger } from '@/utils/logger'
import { errorTracking } from '@/services/errorTracking'
import type { SearchResult, AudibleBookMetadata, QualityScore, QualityProfile } from '@/types'
import { useToast } from '@/services/toastService'
import { getScoreBreakdownTooltip } from '@/composables/useScore'
import ScorePopover from '@/components/ui/ScorePopover.vue'
import { safeText } from '@/utils/textUtils'

const searchStore = useSearchStore()
const libraryStore = useLibraryStore()
const toast = useToast()

const searchQuery = ref('')
const selectedCategory = ref('')
const isAddingToLibrary = ref(false)
const addedResults = ref(new Set<string>())
const qualityScores = ref<Map<string, QualityScore>>(new Map())
const defaultProfile = ref<QualityProfile | null>(null)
const rawDebugResults = ref<unknown[] | null>(null)

// Load default quality profile on mount
onMounted(async () => {
  try {
    defaultProfile.value = await apiService.getDefaultQualityProfile()
  } catch (error) {
    logger.warn('No default quality profile found:', error)
  }
  // Expose the search store and search results for quick debugging in DevTools
  try {
    const w = window as unknown as Record<string, unknown>
    w.searchStore = searchStore
    w.searchResults = searchStore.searchResults
    logger.debug('[SearchView] Exposed searchStore and searchResults on window for debugging')
  } catch {}
})

const fetchRawDebug = async (q: string) => {
  try {
    // Use canonical /search/quick endpoint for debug
    const results = await apiService.intelligentSearch(q || 'Harry')
    rawDebugResults.value = results as unknown[]
    try {
      ;(window as unknown as Record<string, unknown>).rawDebugResults = results
    } catch {}
    logger.debug('[SearchView] Raw debug results fetched (apiService)', results)
  } catch (e) {
    logger.error('[SearchView] Raw debug fetch failed (apiService)', e as Error)
    rawDebugResults.value = null
  }
}

const capitalizeLanguage = (language: string | undefined): string => {
  if (!language) return ''
  return language.charAt(0).toUpperCase() + language.slice(1).toLowerCase()
}

const fetchRawDebugWindow = async (q: string) => {
  try {
    // Attempt to ensure antiforgery token before manual window.fetch debug POSTs.
    try {
      await apiService.ensureAntiforgeryForCurrentAuth()
    } catch {}

    const body = JSON.stringify({ mode: 'Simple', query: q })
    const resp = await window.fetch('/api/search', {
      method: 'POST',
      credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      body,
    })
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`)
    const results = await resp.json()
    rawDebugResults.value = results as unknown[]
    try {
      ;(window as unknown as Record<string, unknown>).rawDebugResults = results
    } catch {}
    logger.debug('[SearchView] Raw debug results fetched (window.fetch)', results)
  } catch (e) {
    logger.error('[SearchView] Raw debug fetch failed (window.fetch)', e as Error)
    rawDebugResults.value = null
  }
}

const performSearch = async () => {
  if (!searchQuery.value.trim()) {
    return
  }

  // Clear added results and scores for new search
  addedResults.value.clear()
  qualityScores.value.clear()

  await searchStore.search(searchQuery.value.trim(), selectedCategory.value || undefined)

  // Ensure the store and results are available on window for debugging (dev only)
  try {
    const w = window as unknown as Record<string, unknown>
    w.searchStore = searchStore
    w.searchResults = searchStore.searchResults
    logger.debug('[SearchView] Bound searchStore/searchResults to window after search')
  } catch {}

  // Wait for next tick to ensure searchResults are updated
  await nextTick()

  // Score search results if we have a default profile
  if (defaultProfile.value?.id && searchStore.searchResults.length > 0) {
    try {
      const scores = await apiService.scoreSearchResults(
        defaultProfile.value.id,
        searchStore.searchResults,
      )
      // Map scores by search result ID
      scores.forEach((score) => {
        qualityScores.value.set(score.searchResult.id, score)
      })
      logger.debug('Quality scores loaded:', scores.length)
    } catch (error) {
      logger.warn('Failed to score search results:', error)
    }
  }

  // Check which results are already in library
  checkExistingInLibrary()

  // UX: show toast and scroll to results (or show info if none)
  try {
    if (searchStore.searchResults.length > 0) {
      toast.success('Search complete', `${searchStore.searchResults.length} results found`)
      await nextTick()
      try {
        const el = document.querySelector('.search-results') as HTMLElement | null
        if (el) {
          await nextTick()

          // Compute nav offset
          const topNav = document.querySelector('.top-nav') as HTMLElement | null
          const navOffset = topNav ? topNav.offsetHeight + 12 : 72

          // Find nearest scrollable ancestor; fallback to window
          function findScrollableAncestor(node: HTMLElement | null): HTMLElement | Window {
            let current = node?.parentElement || null
            while (current && current !== document.documentElement) {
              const style = getComputedStyle(current)
              const overflowY = style.overflowY
              if ((overflowY === 'auto' || overflowY === 'scroll') && current.scrollHeight > current.clientHeight) return current
              current = current.parentElement
            }
            return window
          }

          const ancestor = findScrollableAncestor(el)
          if (ancestor === window) {
            const top = window.scrollY + el.getBoundingClientRect().top - navOffset
            window.scrollTo({ top: Math.max(0, top), behavior: 'smooth' })
          } else {
            const anc = ancestor as HTMLElement
            const top = anc.scrollTop + el.getBoundingClientRect().top - anc.getBoundingClientRect().top - navOffset
            anc.scrollTo({ top: Math.max(0, top), behavior: 'smooth' })
          }
        }
      } catch {}
    } else {
      toast.info('No results', `No results found for "${searchQuery.value}"`)
      await nextTick()
      try {
        const elNo = document.querySelector('.no-results') as HTMLElement | null
        if (elNo) {
          await nextTick()

          const topNav = document.querySelector('.top-nav') as HTMLElement | null
          const navOffset = topNav ? topNav.offsetHeight + 12 : 72

          function findScrollableAncestor(node: HTMLElement | null): HTMLElement | Window {
            let current = node?.parentElement || null
            while (current && current !== document.documentElement) {
              const style = getComputedStyle(current)
              const overflowY = style.overflowY
              if ((overflowY === 'auto' || overflowY === 'scroll') && current.scrollHeight > current.clientHeight) return current
              current = current.parentElement
            }
            return window
          }

          const ancestor = findScrollableAncestor(elNo)
          if (ancestor === window) {
            const top = window.scrollY + elNo.getBoundingClientRect().top - navOffset
            window.scrollTo({ top: Math.max(0, top), behavior: 'smooth' })
          } else {
            const anc = ancestor as HTMLElement
            const top = anc.scrollTop + elNo.getBoundingClientRect().top - anc.getBoundingClientRect().top - navOffset
            anc.scrollTo({ top: Math.max(0, top), behavior: 'smooth' })
          }
        }
      } catch {}
    }
  } catch (e) {
    // Non-fatal: don't let toast/scroll errors break search flow
    logger.debug('[SearchView] toast/scroll after search failed', e)
  }
}

const cancelSearch = () => {
  searchStore.cancel()
}

// UI state helpers to ensure only one state is shown at a time
import { computed } from 'vue'
const showCancelled = computed(() => {
  return !!searchStore.isCancelled
})
const showResults = computed(() => {
  return !searchStore.isSearching && !searchStore.isCancelled && searchStore.hasResults
})
const showNoResults = computed(() => {
  return (
    !!searchQuery.value &&
    !searchStore.isSearching &&
    !searchStore.isCancelled &&
    !searchStore.hasResults
  )
})

const checkExistingInLibrary = () => {
  // Ensure library is loaded
  if (libraryStore.audiobooks.length === 0) {
    libraryStore.fetchLibrary().then(() => {
      markExistingResults()
    })
  } else {
    markExistingResults()
  }
}

const markExistingResults = () => {
  const libraryAsins = new Set(
    libraryStore.audiobooks.filter((book) => book.asin).map((book) => book.asin!),
  )

  searchStore.searchResults.forEach((result) => {
    if (result.asin && libraryAsins.has(result.asin)) {
      addedResults.value.add(result.id)
    }
  })
}

// Watch for search results changes to mark existing audiobooks
watch(
  () => searchStore.searchResults,
  (newVal) => {
    // Keep a developer-friendly reference for debugging
    try {
      const w = window as unknown as Record<string, unknown>
      w.searchResults = newVal
      w.searchStore = searchStore
    } catch {}

    if (searchStore.searchResults.length > 0) {
      checkExistingInLibrary()
    }
  },
  { deep: true },
)

const addToLibrary = async (result: SearchResult) => {
  if (!result.asin) {
    errorTracking.captureMessage('No ASIN available for result', 'warning', {
      component: 'SearchView',
      operation: 'addToLibrary',
      metadata: { result },
    })
    toast.warning('Cannot add', 'Cannot add to library: No ASIN available for this result')
    return
  }

  isAddingToLibrary.value = true
  try {
    // Fetch full metadata from the backend
    const metadata = await apiService.getAudibleMetadata<AudibleBookMetadata>(result.asin)

    // Add to library
    await apiService.addToLibrary(metadata, { searchResult: result })

    toast.success('Added to library', `"${metadata.title}" has been added to your library!`)

    // Mark this result as added
    addedResults.value.add(result.id)
  } catch (error: unknown) {
    errorTracking.captureException(error as Error, {
      component: 'SearchView',
      operation: 'addToLibrary',
      metadata: { resultId: result.id },
    })
    const errorMessage = error instanceof Error ? error.message : String(error)

    // Check if it's a conflict (already exists)
    if (errorMessage.includes('409') || errorMessage.includes('Conflict')) {
      toast.warning('Already exists', 'This audiobook is already in your library.')
    } else {
      toast.error('Add failed', 'Failed to add audiobook. Please try again.')
    }
  } finally {
    isAddingToLibrary.value = false
  }
}

const formatFileSize = (bytes: number): string => {
  const sizes = ['Bytes', 'KB', 'MB', 'GB']
  if (bytes === 0) return '0 Bytes'
  const i = Math.floor(Math.log(bytes) / Math.log(1024))
  return Math.round((bytes / Math.pow(1024, i)) * 100) / 100 + ' ' + sizes[i]
}

const formatRuntime = (seconds: number): string => {
  if (!seconds) return ''
  const totalMinutes = Math.floor(seconds / 60)
  const hours = Math.floor(totalMinutes / 60)
  const mins = totalMinutes % 60
  if (hours > 0 && mins > 0) {
    return `${hours}h ${mins}m`
  } else if (hours > 0) {
    return `${hours}h`
  } else {
    return `${mins}m`
  }
}

const getResultScore = (resultId: string): QualityScore | undefined => {
  return qualityScores.value.get(resultId)
}

// Return the best available external link for a search result
function getResultLink(result: SearchResult): string | undefined {
  if (!result) return undefined
  const r = result as unknown as Record<string, unknown>
  const src = !result ? '' : (result.downloadType || '').toLowerCase()

  if (src === 'usenet' || src === 'nzb') {
    // For Usenet results prefer the ID URL if present and looks like a URL (guid/info page), otherwise prefer details page before NZB download
    if (typeof r.id === 'string' && /^(https?:)?\/\//.test(r.id)) return r.id as string
    if (typeof r.resultUrl === 'string' && (r.resultUrl as string).trim().length > 0)
      return r.resultUrl as string
    if (result.productUrl) return result.productUrl
    if (typeof r.sourceLink === 'string' && (r.sourceLink as string).trim().length > 0)
      return r.sourceLink as string
    if (result.nzbUrl) return result.nzbUrl
    return undefined
  }

  if (typeof r.resultUrl === 'string' && (r.resultUrl as string).trim().length > 0)
    return r.resultUrl as string
  if (result.productUrl) return result.productUrl
  if (result.torrentUrl) return result.torrentUrl
  if (result.nzbUrl) return result.nzbUrl
  if (result.magnetLink) return result.magnetLink
  return undefined
}

const getScoreClass = (score: number): string => {
  if (score >= 80) return 'excellent'
  if (score >= 60) return 'good'
  if (score >= 40) return 'fair'
  return 'poor'
}

// Visible score value for the UI: prefer smartScore when available and normalize to 0-100
import { computeNormalizedSmart } from '@/composables/useScore'

function getVisibleScoreValue(resultId: string): number | undefined {
  const q = getResultScore(resultId)
  if (!q) return undefined

  // Prefer breakdown-based normalized total when available
  if (q.smartScoreBreakdown && Object.keys(q.smartScoreBreakdown).length > 0) {
    return computeNormalizedSmart(q.smartScoreBreakdown).total
  }

  // Fallback to smartScore numeric normalization
  if (typeof q.smartScore === 'number' && !isNaN(q.smartScore))
    return Math.round(Math.min(100, q.smartScore / 100))
  if (typeof q.totalScore === 'number') return q.totalScore
  return undefined
}

function getVisibleScoreClass(resultId: string): string {
  const val = getVisibleScoreValue(resultId) ?? 0
  return getScoreClass(val)
}

// getScoreBreakdownTooltip is provided by the useScore composable
</script>

<style scoped>
.search-page {
  max-width: 1200px;
  margin: 0 auto;
}

.search-header {
  text-align: center;
  margin-bottom: 2rem;
}

.search-header h2 {
  margin: 0 0 0.5rem 0;
  color: #2c3e50;
}

.search-form {
  background: white;
  padding: 2rem;
  border-radius: 6px;
  box-shadow: 0 2px 4px rgba(0,0,0,0.1);
  margin-bottom: 2rem;
}

.search-input-group {
  display: flex;
  gap: 1rem;
  margin-bottom: 1rem;
}

.search-input {
  flex: 1;
  padding: 0.75rem;
  border: 1px solid #ddd;
  border-radius: 6px;
  font-size: 1rem;
}

.search-button {
  padding: 0.75rem 2rem;
  background-color: #3498db;
  color: white;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  font-size: 1rem;
  transition: background-color 0.2s;
}

.search-button:hover:not(:disabled) {
  background-color: #2980b9;
}

.search-button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}
.search-button.advanced-button {
  background-color: #9b59b6;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.search-button.advanced-button:hover:not(:disabled) {
  background-color: #8e44ad;
}
.cancel-button {
  background-color: #e74c3c;
}

.cancel-button:hover:not(:disabled) {
  background-color: #c0392b;
}

.search-filters {
  display: flex;
  gap: 1rem;
}

.filter-select {
  padding: 0.5rem;
  border: 1px solid #ddd;
  border-radius: 6px;
}

.loading {
  text-align: center;
  padding: 2rem;
  color: #666;
}

.loading i {
  font-size: 2rem;
  color: #3498db;
  display: block;
  margin-bottom: 1rem;
}

.cancelled {
  text-align: center;
  padding: 2rem;
  color: #e74c3c;
}

.cancelled i {
  font-size: 2rem;
  display: block;
  margin-bottom: 1rem;
}

.search-results h3 {
  margin-bottom: 1rem;
  color: #2c3e50;
}

.results-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(400px, 1fr));
  gap: 1rem;
}

.result-card {
  background: white;
  border-radius: 6px;
  padding: 1.5rem;
  box-shadow: 0 2px 4px rgba(0,0,0,0.1);
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
}

.result-info {
  flex: 1;
}

.result-info h4 {
  margin: 0 0 0.5rem 0;
  color: #2c3e50;
  font-size: 1.1rem;
}

.result-artist {
  margin: 0 0 0.25rem 0;
  font-weight: 500;
  color: #555;
}

.result-album {
  margin: 0 0 0.5rem 0;
  color: #777;
  font-style: italic;
}

.audiobook-meta {
  margin: 0.75rem 0;
  padding: 0.5rem 0;
  border-top: 1px solid #f0f0f0;
  border-bottom: 1px solid #f0f0f0;
}

.meta-narrator {
  margin: 0 0 0.5rem 0;
  color: #555;
  font-size: 0.9rem;
}

.meta-details {
  display: flex;
  gap: 1rem;
  flex-wrap: wrap;
  font-size: 0.85rem;
}

.meta-runtime {
  color: #3498db;
  font-weight: 500;
}

.meta-series {
  color: #9b59b6;
  font-weight: 500;
}

.result-meta {
  display: flex;
  gap: 1rem;
  margin-bottom: 0.5rem;
  font-size: 0.9rem;
}

.result-meta span {
  background-color: #f8f9fa;
  padding: 0.25rem 0.5rem;
  border-radius: 6px;
  color: #666;
}

.quality-score {
  margin: 0.5rem 0;
}

.score-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  padding: 0.25rem 0.75rem;
  border-radius: 6px;
  font-size: 0.85rem;
  font-weight: 500;
}

.score-badge.rejected {
  background-color: rgba(231, 76, 60, 0.15);
  color: #e74c3c;
  border: 1px solid rgba(231, 76, 60, 0.3);
}

.score-badge.excellent {
  background-color: rgba(39, 174, 96, 0.15);
  color: #27ae60;
  border: 1px solid rgba(39, 174, 96, 0.3);
}

.score-badge.good {
  background-color: rgba(52, 152, 219, 0.15);
  color: #3498db;
  border: 1px solid rgba(52, 152, 219, 0.3);
}

.score-badge.fair {
  background-color: rgba(241, 196, 15, 0.15);
  color: #f39c12;
  border: 1px solid rgba(241, 196, 15, 0.3);
}

.score-badge.poor {
  background-color: rgba(149, 165, 166, 0.15);
  color: #7f8c8d;
  border: 1px solid rgba(149, 165, 166, 0.3);
}

.result-stats {
  display: flex;
  gap: 1rem;
  font-size: 0.9rem;
}

.seeders {
}

.leechers {
}

.grabs {
  margin-left: 8px;
  color: var(--muted);
}

.files {
  margin-left: 8px;
  color: var(--muted);
}
  color: #27ae60;
  font-weight: 500;
}

.leechers {
  color: #e74c3c;
  font-weight: 500;
}

.result-actions {
  margin-left: 1rem;
}

.add-button {
  padding: 0.5rem 1rem;
  background-color: #27ae60;
  color: white;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  font-size: 0.9rem;
  transition: background-color 0.2s;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.add-button:hover:not(:disabled) {
  background-color: #229954;
}

.add-button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.add-button.added {
  background-color: #27ae60;
  opacity: 1;
}

.add-button.added:disabled {
  opacity: 1;
  cursor: not-allowed;
}

.no-results {
  text-align: center;
  padding: 2rem;
  color: #666;
  background: white;
  border-radius: 6px;
  box-shadow: 0 2px 4px rgba(0,0,0,0.1);
}

/* Debug panel styles */
.debug-pre {
  max-height: 300px;
  overflow: auto;
  font-size: 12px;
}
</style>
