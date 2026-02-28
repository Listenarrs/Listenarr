/**
 * Composable for managing advanced search form state and persistence
 * Handles form validation, localStorage persistence with debouncing,
 * and search parameter management for the AddNewView component
 */

import { ref, computed, watch, onMounted } from 'vue'

/**
 * Advanced search form parameters
 */
export interface AdvancedSearchParams {
  title?: string
  author?: string
  isbn?: string
  series?: string
  asin?: string
  language?: string
}

/**
 * Persisted state structure for localStorage
 */
interface PersistedAdvancedState {
  // persisted state may contain stringified booleans from older versions
  showAdvanced?: boolean | string
  params?: AdvancedSearchParams
}

const STORAGE_KEY = 'listenarr.addnew.advanced'
const SAVE_DEBOUNCE_MS = 200

/**
 * Composable for managing advanced search form state
 * @returns Advanced search state, validation, and control methods
 */
export const useAdvancedSearch = () => {
  // Form visibility state
  const showAdvancedSearch = ref(false)

  // Form parameters
  const advancedSearchParams = ref<AdvancedSearchParams>({
    title: '',
    author: '',
    isbn: '',
    series: '',
    asin: '',
    language: '',
  })

  // Debounce timer for localStorage saves
  const saveTimer = ref<number | null>(null)

  /**
   * Validate if at least one search parameter is filled
   * Used for enabling/disabling search button
   */
  const isValidAdvancedSearch = computed(() => {
    const p = advancedSearchParams.value
    return Boolean(
      (p.title && p.title.trim()) ||
        (p.author && p.author.trim()) ||
        (p.series && p.series.trim()) ||
        (p.isbn && p.isbn.trim()) ||
        (p.asin && p.asin.trim()),
    )
  })

  /**
   * Save advanced search state to localStorage with debouncing
   * Prevents excessive writes while user is typing
   * @internal
   */
  const saveAdvancedState = () => {
    try {
      // Debug lines removed for test cleanliness
      if (saveTimer.value) window.clearTimeout(saveTimer.value)
    } catch {
      // ignore cleanup errors
    }

    saveTimer.value = window.setTimeout(() => {
      try {
        const payload: PersistedAdvancedState = {
          showAdvanced: showAdvancedSearch.value,
          params: advancedSearchParams.value,
        }
        window.localStorage.setItem(STORAGE_KEY, JSON.stringify(payload))
      } catch {
        // swallow localStorage errors (quota exceeded, private mode, etc.)
      }

      try {
        saveTimer.value = null
      } catch {
        // ignore cleanup errors
      }
    }, SAVE_DEBOUNCE_MS)
  }

  /**
   * Load persisted advanced search state from localStorage
   */
  const loadAdvancedState = () => {
    try {
      const raw = window.localStorage.getItem(STORAGE_KEY)
      if (raw) {
        const parsed = JSON.parse(raw) as PersistedAdvancedState
        if (typeof parsed === 'object' && parsed !== null) {
          // Accept truthy boolean values and stringified booleans for compatibility
          if (parsed.showAdvanced === true || parsed.showAdvanced === 'true') {
            showAdvancedSearch.value = true
          }
          if (parsed.params && typeof parsed.params === 'object') {
            advancedSearchParams.value = Object.assign({}, advancedSearchParams.value, parsed.params)
          }
        }
      }
    } catch {
      // ignore localStorage errors
    }
  }

  // Load persisted state immediately so tests and callers see restored values
  loadAdvancedState()

  /**
   * Toggle advanced search visibility
   */
  const toggleAdvancedSearch = () => {
    showAdvancedSearch.value = !showAdvancedSearch.value
    // Persistence is handled by the watcher (synchronous flush) — no direct save
  }

  /**
   * Reset form to empty state
   */
  const resetAdvancedSearch = () => {
    advancedSearchParams.value = {
      title: '',
      author: '',
      isbn: '',
      series: '',
      asin: '',
      language: '',
    }
    // Persist handled by watcher
  }

  /**
   * Update a single search parameter
   */
  const updateSearchParam = (key: keyof AdvancedSearchParams, value: string) => {
    advancedSearchParams.value[key] = value
    // Persistence handled by deep watcher (flush: 'sync')
  }

  /**
   * Get all search parameters as query string
   * Used for API calls
   */
  const getSearchQuery = () => {
    return advancedSearchParams.value
  }

  /**
   * Clear saved state from localStorage
   * Useful for debugging or user preference reset
   */
  const clearPersistedState = () => {
    try {
      // Cancel any pending save
      if (saveTimer.value) {
        try {
          window.clearTimeout(saveTimer.value)
        } catch {}
        saveTimer.value = null
      }
      window.localStorage.removeItem(STORAGE_KEY)
    } catch {
      // ignore errors
    }
  }

  /**
   * Initialize: load persisted state on mount
   */
  onMounted(() => {
    loadAdvancedState()
  })

  /**
   * Watch for changes to persist to localStorage
   */
  // Ensure watchers run synchronously so tests (and consumers) observe
  // the scheduled debounce timer immediately after a mutation.
  watch(
    () => showAdvancedSearch.value,
    () => saveAdvancedState(),
    { flush: 'sync' },
  )

  watch(advancedSearchParams, () => saveAdvancedState(), { deep: true, flush: 'sync' })

  /**
   * Cleanup: clear debounce timer
   */
  const cleanup = () => {
    try {
      if (saveTimer.value) {
        window.clearTimeout(saveTimer.value)
        saveTimer.value = null
      }
    } catch {
      // ignore cleanup errors
    }
  }

  return {
    // State
    showAdvancedSearch,
    advancedSearchParams,

    // Computed
    isValidAdvancedSearch,

    // Methods
    toggleAdvancedSearch,
    resetAdvancedSearch,
    updateSearchParam,
    getSearchQuery,
    clearPersistedState,
    cleanup,
  }
}
