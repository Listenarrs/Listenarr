/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
import { ref, computed, type Ref } from 'vue'
import { apiService } from '@/services/api'
import {
  detectSearchType,
  queryAsin,
  searchAsin,
  searchIsbn,
  searchTitle,
} from '@/services/audiobookSearch'
import { logger } from '@/utils/logger'
import { getPreferredSearchLanguageFilter } from '@/utils/languageMapping'

type SearchType = 'asin' | 'title' | 'isbn' | null

// options.searchType: shared state to keep the detected search type in (Add New keeps it in its
// search store, with the results it belongs to)
export function useSearch(options: { searchType?: Ref<SearchType> } = {}) {
  // Reactive state
  const searchQuery = ref('')
  const searchLanguage = ref('us')
  const preferredSearchLanguage = ref('english')
  const searchType = options.searchType ?? ref<SearchType>(null)
  const isSearching = ref(false)
  const searchError = ref('')
  const searchStatus = ref('')

  // Abort controller for cancelling searches
  const searchAbortController = ref<AbortController | null>(null)

  // Computed properties
  // The type the text in the field would be searched as, for the hint under it
  const inputType = computed<SearchType>(() =>
    searchQuery.value.trim() ? detectSearchType(searchQuery.value) : null,
  )

  const searchPlaceholder = computed(() => {
    const type = inputType.value ?? searchType.value
    if (type === 'asin') {
      return 'Enter ASIN (e.g., B08G9PRS1K)'
    } else if (type === 'title') {
      return 'Title, author, or ASIN (e.g., The Hobbit)'
    } else if (type === 'isbn') {
      return 'Enter ISBN (e.g., 9780547928227 or 0547928220)'
    }
    return 'Search by ASIN, ISBN, or title'
  })

  // Typing prepares the next search; it neither stops the running one (only Cancel does) nor changes
  // the type of the results on screen (searchType), which belongs to the search that found them
  const handleSearchInput = () => {
    searchError.value = ''
  }

  const performSearch = async () => {
    if (isSearching.value) {
      logger.debug('performSearch ignored because a search is already running')
      return null
    }

    const query = searchQuery.value.trim()
    logger.debug('performSearch called with query:', query)

    if (!query) {
      searchError.value = 'Please enter a search term'
      return null
    }

    const detectedType = detectSearchType(query)
    logger.debug('Detected search type:', detectedType)
    searchType.value = detectedType

    let results
    if (detectedType === 'asin') {
      results = await searchByAsin(query)
    } else {
      // ISBNs and titles (or TITLE:/AUTHOR:/… tokens): the shared search, in the region and the
      // preferred language
      try {
        isSearching.value = true
        searchError.value = ''
        searchStatus.value = 'Searching for audiobooks and fetching metadata...'
        const options = { region: searchLanguage.value, language: preferredSearchLanguage.value }
        results =
          detectedType === 'isbn'
            ? await searchIsbn(query, options)
            : await searchTitle(query, options)
      } catch (err) {
        searchError.value = err instanceof Error ? err.message : 'Failed to search for audiobooks'
        throw err
      } finally {
        isSearching.value = false
        setTimeout(() => {
          searchStatus.value = ''
        }, 1000)
      }
    }

    return results
  }

  const searchByAsin = async (asin: string) => {
    logger.debug('searchByAsin called with:', asin)

    const cleanAsin = queryAsin(asin)
    if (!cleanAsin) {
      searchError.value =
        'Invalid ASIN format. Expected an Amazon ASIN like B08G9PRS1K or 1980006520'
      return
    }

    isSearching.value = true
    searchError.value = ''
    searchStatus.value = `Searching for ASIN ${cleanAsin}...`

    try {
      // Cancel any previous search and create controller for this request
      try {
        searchAbortController.value?.abort()
      } catch {}
      searchAbortController.value = new AbortController()

      // The metadata endpoint, through the shared search
      return await searchAsin(cleanAsin, searchLanguage.value)
    } catch (error) {
      logger.error('ASIN search failed:', error)
      searchError.value = error instanceof Error ? error.message : 'Failed to search for audiobook'
      throw error
    } finally {
      isSearching.value = false
      // Keep a brief 'done' status then clear
      setTimeout(() => {
        searchStatus.value = ''
      }, 1200)
    }
  }

  const searchByTitle = async (query: string) => {
    logger.debug('searchByTitle called with:', query)

    isSearching.value = true
    searchError.value = ''
    searchStatus.value = 'Searching for audiobooks and fetching metadata...'

    try {
      // Cancel any previous search
      try {
        searchAbortController.value?.abort()
      } catch {}
      searchAbortController.value = new AbortController()

      // Use canonical /search/title endpoint
      const requestOptions: RequestInit & { region?: string; language?: string } = {
        signal: searchAbortController.value.signal,
        region: searchLanguage.value,
      }
      const languageFilter = getPreferredSearchLanguageFilter(preferredSearchLanguage.value)
      if (languageFilter) requestOptions.language = languageFilter

      const results = await apiService.searchByTitle(query, requestOptions)

      logger.debug('Title search returned:', results)
      logger.debug('Number of results:', results?.length)

      searchStatus.value = 'Processing search results...'

      return results
    } catch (error) {
      if (error && (error as Error).name === 'AbortError') {
        logger.debug('Title search aborted by user')
        searchError.value = 'Search cancelled'
      } else {
        logger.error('Title search failed:', error)
        searchError.value =
          error instanceof Error ? error.message : 'Failed to search for audiobooks'
      }
      throw error
    } finally {
      isSearching.value = false
      // Clear status shortly after completion
      setTimeout(() => {
        searchStatus.value = ''
      }, 1000)
    }
  }

  const searchByISBN = async (isbn: string) => {
    // This will be implemented when extracting the ISBN logic
    logger.debug('searchByISBN called with:', isbn)
    // For now, delegate to title search with ISBN prefix
    return await searchByTitle(`ISBN:${isbn}`)
  }

  const cancelSearch = () => {
    if (searchAbortController.value) {
      try {
        searchAbortController.value.abort()
        logger.debug('User requested search cancellation')
        searchStatus.value = 'Search cancelled'
      } catch (e) {
        logger.debug('Failed to abort search controller', e)
      }
    }
    isSearching.value = false
    // Clear controller reference
    try {
      searchAbortController.value = null
    } catch {}
  }

  return {
    // State
    searchQuery,
    searchLanguage,
    preferredSearchLanguage,
    searchType,
    isSearching,
    searchError,
    searchStatus,

    // Computed
    searchPlaceholder,
    inputType,

    // Methods
    detectSearchType,
    handleSearchInput,
    performSearch,
    searchByAsin,
    searchByTitle,
    searchByISBN,
    cancelSearch,
  }
}
