import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import type { SearchResult } from '@/types'
import { apiService } from '@/services/api'
import { errorTracking } from '@/services/errorTracking'

export const useSearchStore = defineStore('search', () => {
  const searchResults = ref<SearchResult[]>([])
  const isSearching = ref(false)
  const isCancelled = ref(false)
  const searchQuery = ref('')
  const selectedCategory = ref<string>('')
  const selectedApiIds = ref<string[]>([])
  let abortController: AbortController | null = null

  const hasResults = computed(() => searchResults.value.length > 0)

  // Expose store refs for debugging in browser DevTools
  try {
    ;(window as unknown as Record<string, unknown>).pinia_search = {
      searchResults,
      isSearching,
      isCancelled,
      searchQuery,
      selectedCategory,
      selectedApiIds,
      hasResults,
      // debug functions omitted to avoid forward reference issues
    }
  } catch {}

  const search = async (query: string, category?: string, apiIds?: string[]) => {
    isSearching.value = true
    isCancelled.value = false
    searchQuery.value = query
    selectedCategory.value = category || ''
    selectedApiIds.value = apiIds || []

    abortController = new AbortController()

    try {
      // Ensure antiforgery token exists for the current auth before making unsafe request.
      // Non-fatal: if this fails, we'll continue and the ApiService.request logic will
      // attempt its own CSRF retry as a fallback.
      try {
        await apiService.ensureAntiforgeryForCurrentAuth()
      } catch {}

      // Use canonical intelligentSearch endpoint for quick search
      const response: SearchResult[] = await apiService.intelligentSearch(
        query,
        category,
        abortController.signal,
      )
      const results = response
      searchResults.value = results
    } catch (error) {
      if (error instanceof Error && error.name === 'AbortError') {
        isCancelled.value = true
        searchResults.value = []
      } else {
        errorTracking.captureException(error as Error, {
          component: 'SearchStore',
          operation: 'search',
          metadata: { query, category },
        })
        searchResults.value = []
      }
    } finally {
      isSearching.value = false
      abortController = null
    }
  }

  const cancel = () => {
    if (abortController) {
      abortController.abort()
      isCancelled.value = true
      isSearching.value = false
      searchResults.value = [] // Clear results when cancelled
    }
  }

  const clearResults = () => {
    searchResults.value = []
    searchQuery.value = ''
    selectedCategory.value = ''
    selectedApiIds.value = []
    isCancelled.value = false
  }

  return {
    searchResults,
    isSearching,
    isCancelled,
    searchQuery,
    selectedCategory,
    selectedApiIds,
    hasResults,
    search,
    cancel,
    clearResults,
  }
})
