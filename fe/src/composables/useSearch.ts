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
import { ref, computed } from 'vue'
import { apiService } from '@/services/api'
import { logger } from '@/utils/logger'
import { getPreferredSearchLanguageFilter } from '@/utils/languageMapping'
import { ASIN_PATTERN, findRecords, type RecordQuery } from '@/utils/audibleRecordSearch'

export function useSearch() {
  // Reactive state
  const searchQuery = ref('')
  const searchLanguage = ref('us')
  const preferredSearchLanguage = ref('english')
  const searchType = ref<'asin' | 'title' | 'isbn' | null>(null)
  const isSearching = ref(false)
  const searchError = ref('')
  const searchStatus = ref('')

  // Abort controller for cancelling searches
  const searchAbortController = ref<AbortController | null>(null)

  // Computed properties
  const searchPlaceholder = computed(() => {
    if (searchType.value === 'asin') {
      return 'Enter ASIN (e.g., B08G9PRS1K)'
    } else if (searchType.value === 'title') {
      return 'Title, author, or ASIN (e.g., The Hobbit)'
    } else if (searchType.value === 'isbn') {
      return 'Enter ISBN (e.g., 9780547928227 or 0547928220)'
    }
    return 'Search by ASIN, ISBN, or title'
  })

  const detectSearchType = (query: string): 'asin' | 'title' | 'isbn' => {
    const trimmed = query.trim().toUpperCase()

    // Check for explicit prefixes first (ASIN:, ISBN:, AUTHOR:, TITLE:)
    if (trimmed.startsWith('ASIN:')) {
      return 'asin'
    }
    if (trimmed.startsWith('ISBN:')) {
      return 'isbn'
    }
    if (trimmed.startsWith('AUTHOR:') || trimmed.startsWith('TITLE:')) {
      return 'title'
    }

    // ISBN detection (more specific)
    // ISBN-13: 13 digits
    const isbn13 = /^\d{13}$/
    if (isbn13.test(trimmed)) {
      return 'isbn'
    }

    // ASIN / ISBN-10 pattern (ASINs often start with 'B' followed by 9 alphanumerics).
    // Use a strict regex to avoid misclassifying short title-like strings as ASINs.
    // Pattern covers: 'B' + 9 alnum (typical ASIN) OR 10-digit ISBN-10 (ending with digit or 'X').
    if (ASIN_PATTERN.test(trimmed)) {
      return 'asin'
    }

    return 'title'
  }

  const handleSearchInput = () => {
    searchError.value = ''
    const query = searchQuery.value.trim()

    if (isSearching.value) {
      try {
        cancelSearch()
      } catch (e) {
        logger.debug('Error cancelling previous search on input', e)
      }
    }

    if (query) {
      searchType.value = detectSearchType(query)
    } else {
      searchType.value = null
    }
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
    const languageFilter = getPreferredSearchLanguageFilter(preferredSearchLanguage.value)

    let results
    if (detectedType === 'asin') {
      results = await searchByAsin(query)
    } else if (detectedType === 'isbn') {
      // If query starts with 'isbn:' (case-insensitive), use advanced search
      const isbnMatch = query.match(/^isbn:(.+)/i)
      if (isbnMatch && isbnMatch[1]) {
        const params: RecordQuery = {
          isbn: isbnMatch[1].trim(),
          pagination: { page: 1, limit: 50 },
        }
        if (languageFilter) params.language = languageFilter
        try {
          isSearching.value = true
          searchError.value = ''
          searchStatus.value = 'Searching for audiobooks and fetching metadata...'
          results = await findRecords(params, searchLanguage.value)
        } catch (err) {
          searchError.value = err instanceof Error ? err.message : 'Failed to search for audiobooks'
          throw err
        } finally {
          isSearching.value = false
          setTimeout(() => {
            searchStatus.value = ''
          }, 1000)
        }
      } else {
        results = await searchByISBN(query)
      }
    } else {
      // For title-like queries, prefer the backend's advanced/title search which
      // returns enriched SearchResult objects (Audible where available).

      // Improved: parse advanced tokens (TITLE:, AUTHOR:, ISBN:, ASIN:, SERIES:) with multi-word support

      const params: RecordQuery = {}
      // Add SERIES: to the regex
      const regex = /(TITLE:|AUTHOR:|ISBN:|ASIN:|SERIES:)/gi
      const matches = []
      let match: RegExpExecArray | null
      while ((match = regex.exec(query)) !== null) {
        if (match && match[1]) {
          matches.push({
            key: match[1].toUpperCase().replace(':', ''),
            index: match.index ?? 0,
            len: match[1].length,
          })
        }
      }
      let foundAny = false
      for (let i = 0; i < matches.length; i++) {
        const mi = matches[i]
        if (!mi) continue
        const { key, index, len } = mi
        const start = index + len
        const nextMatch = matches[i + 1]
        const end = nextMatch ? nextMatch.index : query.length
        const value = query.substring(start, end).trim()
        if (value) {
          foundAny = true
          if (key === 'TITLE') params.title = value
          else if (key === 'AUTHOR') params.author = value
          else if (key === 'ISBN') params.isbn = value
          else if (key === 'ASIN') params.asin = value
          else if (key === 'SERIES') params.series = value
        }
      }
      if (!foundAny) params.title = query

      // Include language and a reasonable default pagination (the region is passed with the query)
      if (languageFilter) params.language = languageFilter
      params.pagination = { page: 1, limit: 50 }

      // Call the advanced search endpoint to get richer title results
      try {
        isSearching.value = true
        searchError.value = ''
        searchStatus.value = 'Searching for audiobooks and fetching metadata...'
        // Use canonical /search endpoint (POST) for advanced search
        results = await findRecords(params, searchLanguage.value)
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

    // Strip ASIN: prefix if present
    const cleanAsin = asin.replace(/^ASIN:/i, '').trim()

    // Validate ASIN using the same strict pattern as detection.
    // Matches: 'B' + 9 alphanumerics OR 10-digit numeric ASIN (ISBN-10 format)
    if (!ASIN_PATTERN.test(cleanAsin)) {
      searchError.value =
        'Invalid ASIN format. Expected an Amazon ASIN like B08G9PRS1K or 1980006520'
      return
    }

    isSearching.value = true
    searchError.value = ''
    searchStatus.value = `Searching for ASIN ${cleanAsin}...`

    try {
      // Cancel any previous search
      try {
        searchAbortController.value?.abort()
      } catch {}
      searchAbortController.value = null

      // The shared Audible search: the ASIN looked up in the selected region, empty stubs dropped
      return await findRecords({ asin: cleanAsin }, searchLanguage.value)
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
