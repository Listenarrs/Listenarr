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
import { defineStore } from 'pinia'
import { computed, ref, watch } from 'vue'
import type { AudibleBookMetadata, AudibleSearchResult, SearchResult } from '@/types'
import type { ISBNBook } from '@/services/isbn'
import type { OpenLibraryBook } from '@/services/openlibrary'
import { logger } from '@/utils/logger'

// Add New's search, owned here rather than by the page: a search keeps running when the page is
// left, and the page finds it again (running or done) when it comes back. Each search gets a
// searchRequestId; only the latest one may apply its results and save its context, so a late answer
// from a replaced search (or from a page that was left) can't overwrite the newer one.
//
// Two things are kept apart, never mixed:
// - the current inputs (draft): what the user typed or chose in the form, kept as is for the tab
//   session; only what the user changed is kept, so an untouched region still follows Settings;
// - the parameters of the results on screen (context): what the search that found them was made
//   with, shown above them and loaded back into the form only when the user asks.
//
// Both are kept in sessionStorage: they survive going to a book and back, not closing the tab.

// A title result of Add New, whatever source it came from
export type TitleSearchResult = Omit<OpenLibraryBook, 'isbn'> & {
  searchResult?: Partial<SearchResult> // Store the enriched SearchResult from intelligent search
  imageUrl?: string // For results that have direct image URLs
  metadataSource?: string // Store which metadata source was used
  isbn?: string | string[] // Normalized ISBN (string or array for OpenLibrary compatibility)
  region?: string // Audible/Amazon market region from the search result
}

// The advanced form's fields a search was made with
export type AdvancedSearchFields = {
  title?: string
  author?: string
  series?: string
  isbn?: string
  asin?: string
}
// What a search was made with: the search box's query or the advanced form's fields, the region
// and the language
export type SearchContext = {
  query?: string
  advanced?: AdvancedSearchFields
  region?: string
  language?: string
}
// The form as the user left it (only what they changed)
export type SearchDraft = { query?: string; region?: string; language?: string }
export type SearchType = 'asin' | 'title' | 'isbn' | null

const RESULTS_KEY = 'listenarr.addNewResults'
const SEARCH_TYPE_KEY = 'listenarr.addNewSearchType'
const TITLE_RESULTS_COUNT_KEY = 'listenarr.addNewTitleResultsCount'
const ASIN_FILTERING_KEY = 'listenarr.addNewAsinFiltering'
const RESOLVED_ASINS_KEY = 'listenarr.addNewResolvedAsins'
const SEARCH_CONTEXT_KEY = 'listenarr.addNewSearchContext'
const DRAFT_KEY = 'listenarr.addNewSearchDraft'
// Earlier versions kept these in localStorage, where they outlived the session
const LEGACY_LOCAL_KEYS = [
  RESULTS_KEY,
  SEARCH_TYPE_KEY,
  TITLE_RESULTS_COUNT_KEY,
  ASIN_FILTERING_KEY,
  RESOLVED_ASINS_KEY,
  'listenarr.addNewAddedAsins',
  'listenarr.addNewAddedOLIDs',
]

function save(key: string, value: string | null) {
  try {
    if (value === null) sessionStorage.removeItem(key)
    else sessionStorage.setItem(key, value)
  } catch {}
}

export const useAddNewSearchStore = defineStore('addNewSearch', () => {
  const audibleResult = ref<AudibleBookMetadata | null>(null)
  const titleResults = ref<TitleSearchResult[]>([])
  const isbnResult = ref<ISBNBook | null>(null)
  const allAudibleResults = ref<AudibleSearchResult[]>([])
  const totalTitleResultsCount = ref(0)
  const resolvedAsins = ref<Record<string, string>>({})
  const asinFilteringApplied = ref(false)
  const searchType = ref<SearchType>(null)
  // The parameters of the results shown (restored, or of the last search that succeeded)
  const context = ref<SearchContext | null>(null)
  // The form as the user left it
  const draft = ref<SearchDraft>({})
  // The search in progress, if any
  const running = ref(false)
  const runningContext = ref<SearchContext | null>(null)
  let latestSearchRequestId = 0

  const hasResults = computed(
    () =>
      Boolean(audibleResult.value) || titleResults.value.length > 0 || Boolean(isbnResult.value),
  )

  function clearResults() {
    audibleResult.value = null
    titleResults.value = []
    isbnResult.value = null
    allAudibleResults.value = []
    totalTitleResultsCount.value = 0
    resolvedAsins.value = {}
    asinFilteringApplied.value = false
  }

  // A new search empties the previous one, on screen and in the session, and supersedes any search
  // still running. Returns its searchRequestId.
  function begin(searchContext: SearchContext): number {
    latestSearchRequestId += 1
    clearResults()
    context.value = null
    save(SEARCH_CONTEXT_KEY, null)
    running.value = true
    runningContext.value = searchContext
    return latestSearchRequestId
  }

  function isCurrent(searchRequestId: number): boolean {
    return searchRequestId === latestSearchRequestId
  }

  // The search's results are applied: its context is saved with them
  function complete(searchRequestId: number) {
    if (!isCurrent(searchRequestId) || !running.value) return
    context.value = runningContext.value
    save(SEARCH_CONTEXT_KEY, JSON.stringify(context.value))
    running.value = false
    runningContext.value = null
  }

  // The search ended without results to keep (failed, or stopped before any): nothing is saved
  function settle(searchRequestId: number) {
    if (!isCurrent(searchRequestId) || !running.value) return
    running.value = false
    runningContext.value = null
  }

  // Cancel: the running search's answer, whenever it comes, is ignored
  function supersede() {
    latestSearchRequestId += 1
    running.value = false
    runningContext.value = null
  }

  // Restored once per tab session, when the store is first used
  try {
    for (const key of LEGACY_LOCAL_KEYS) localStorage.removeItem(key)
  } catch {}
  try {
    const stored = sessionStorage.getItem(RESULTS_KEY)
    if (stored) {
      const parsed = JSON.parse(stored)
      if (parsed.audibleResult) audibleResult.value = parsed.audibleResult
      if (parsed.titleResults) titleResults.value = parsed.titleResults
      if (parsed.isbnResult) isbnResult.value = parsed.isbnResult
    }
    const storedType = sessionStorage.getItem(SEARCH_TYPE_KEY)
    if (storedType) searchType.value = storedType as SearchType
    const storedCount = sessionStorage.getItem(TITLE_RESULTS_COUNT_KEY)
    if (storedCount) totalTitleResultsCount.value = parseInt(storedCount, 10)
    const storedFiltering = sessionStorage.getItem(ASIN_FILTERING_KEY)
    if (storedFiltering) asinFilteringApplied.value = storedFiltering === 'true'
    const storedResolved = sessionStorage.getItem(RESOLVED_ASINS_KEY)
    if (storedResolved) resolvedAsins.value = JSON.parse(storedResolved)
    // Only a search that left results on screen is restored with its context
    const storedContext = hasResults.value ? sessionStorage.getItem(SEARCH_CONTEXT_KEY) : null
    if (storedContext) context.value = JSON.parse(storedContext) as SearchContext
    const storedDraft = sessionStorage.getItem(DRAFT_KEY)
    if (storedDraft) draft.value = JSON.parse(storedDraft) as SearchDraft
  } catch (error) {
    logger.warn('Failed to restore persisted state:', error)
  }

  // The store lives as long as the app: these keep saving while the page is away
  watch([audibleResult, titleResults, isbnResult], () =>
    save(
      RESULTS_KEY,
      JSON.stringify({
        audibleResult: audibleResult.value,
        titleResults: titleResults.value,
        isbnResult: isbnResult.value,
      }),
    ),
  )
  watch(searchType, (v) => save(SEARCH_TYPE_KEY, v || ''))
  watch(totalTitleResultsCount, (v) => save(TITLE_RESULTS_COUNT_KEY, v.toString()))
  watch(asinFilteringApplied, (v) => save(ASIN_FILTERING_KEY, v.toString()))
  watch(resolvedAsins, (v) => save(RESOLVED_ASINS_KEY, JSON.stringify(v)), { deep: true })
  watch(draft, (v) => save(DRAFT_KEY, JSON.stringify(v)), { deep: true })

  return {
    audibleResult,
    titleResults,
    isbnResult,
    allAudibleResults,
    totalTitleResultsCount,
    resolvedAsins,
    asinFilteringApplied,
    searchType,
    context,
    draft,
    running,
    runningContext,
    hasResults,
    begin,
    isCurrent,
    complete,
    settle,
    supersede,
  }
})
