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
import type { SearchResult } from '@/types'
import { answerSource, noRecordError, regionLabel } from '@/utils/bookMatch'

// The search under a book in a book match card (Repair Books and Find Match): its state and what
// the card says about it

// A search slower than this is stopped and reported, with Retry
export const SEARCH_TIMEOUT_MS = 30_000

// What the search box holds: an ASIN alone (bare or ASIN:), looked up in the region, or any other
// query (a title, TITLE:/AUTHOR:/… tokens), searched like Add New
export type SearchKind = 'query' | 'asin'

// invalid: Search was asked for something it can't search (an ASIN that isn't one), a field error;
// canceled: the user stopped the search. searchResults: the last answer, kept until a new one replaces it
export interface SearchState {
  status: 'idle' | 'searching' | 'done' | 'failed' | 'invalid' | 'canceled'
  kind: SearchKind
  searchResults: SearchResult[]
  error: string | null
  // What the results shown were searched with, for their header
  searchParameters: SearchParameters | null
  // How long their request took, or null when the dialog had them already (answered before)
  requestMs: number | null
}

// The parameters of one search, taken when it starts: typing afterwards doesn't change them
export interface SearchParameters {
  kind: SearchKind
  // The ASIN looked up, empty for any other query
  asin: string
  // The search box as typed
  query: string
  region: string
}

// Orange: nothing found, canceled or refused; red: the request failed
export type SearchTone = 'warning' | 'error'

// How the results' header line reads: no search yet, results, nothing found, or a search stopped
// short (failed, canceled, refused)
export type SearchHeaderOutcome = 'none' | 'results' | 'empty' | 'stopped'

export interface SearchHeader {
  text: string
  tone?: SearchTone
  outcome: SearchHeaderOutcome
}

// The bar along the bottom of the card
export type CardBarState = 'searching' | 'error' | 'warning' | 'matched' | 'ignored'

export function idleSearch(): SearchState {
  return {
    status: 'idle',
    kind: 'query',
    searchResults: [],
    error: null,
    searchParameters: null,
    requestMs: null,
  }
}

// What a search looked for: the ASIN, or the search box as typed
export function describeSearch(searchParameters: SearchParameters): string {
  return searchParameters.kind === 'asin'
    ? `ASIN "${searchParameters.asin}"`
    : `"${searchParameters.query}"`
}

// What the running search does, on the bar's tooltip and for screen readers
export function pendingSearchText(runningSearch: SearchParameters | null): string | null {
  return runningSearch
    ? `Searching ${describeSearch(runningSearch)} in ${regionLabel(runningSearch.region)}…`
    : null
}

// Blue while searching; red when the request failed; orange for a field error or a search that
// found nothing; green once matched (grey when left out); grey otherwise.
// Matched (green) is only a picked record, the one state Repair counts; a search that found records
// with nothing picked yet stays ignored (grey)
export function cardBarState(
  { status, searchResults }: SearchState,
  isMatched: boolean,
  isExcluded: boolean,
): CardBarState {
  if (status === 'searching') return 'searching'
  if (status === 'failed') return 'error'
  if (
    status === 'invalid' ||
    status === 'canceled' ||
    (status === 'done' && searchResults.length === 0)
  )
    return 'warning'
  return isMatched && !isExcluded ? 'matched' : 'ignored'
}

// The results' header line, always there: what was searched, where, and where the answer comes
// from (orange when it found nothing, red when it failed). Why there are no results is said in the
// middle of their room. A failed, canceled or refused search shows no results: the empty list
// again, as before any search
export function searchHeader({
  searchParameters,
  searchResults,
  status,
  requestMs,
}: SearchState): SearchHeader {
  if (status === 'invalid')
    return { text: 'No search request done', tone: 'warning', outcome: 'stopped' }
  if (!searchParameters) return { text: 'No search request done', outcome: 'none' }
  const searchedText = `${describeSearch(searchParameters)} in ${regionLabel(searchParameters.region)}`
  if (status === 'canceled')
    return { text: `No results for ${searchedText}`, tone: 'warning', outcome: 'stopped' }
  if (status === 'failed')
    return { text: `No results for ${searchedText}`, tone: 'error', outcome: 'stopped' }
  if (searchResults.length)
    return { text: `Results for ${searchedText} - ${answerSource(requestMs)}`, outcome: 'results' }
  return {
    text: `No results for ${searchedText} - ${answerSource(requestMs)}`,
    tone: 'warning',
    outcome: 'empty',
  }
}

// Why a search left no results, in the middle of their room: a failed request (red), or nothing
// found, the search canceled, an ASIN that isn't one (orange). Nothing before any search
export function noResultsReason({
  searchParameters,
  searchResults,
  status,
  error,
}: SearchState): { text: string; tone: SearchTone } | null {
  if (status === 'invalid') return { text: error ?? '', tone: 'warning' }
  if (status === 'canceled') return { text: 'Search canceled', tone: 'warning' }
  if (status === 'failed') return { text: error ?? '', tone: 'error' }
  if (status !== 'done' || searchResults.length || !searchParameters) return null
  return {
    text:
      searchParameters.kind === 'asin'
        ? noRecordError(searchParameters.asin, searchParameters.region)
        : 'Nothing found for this search',
    tone: 'warning',
  }
}

// The records an ASIN search found for that ASIN, tagged with the region they were found in
export function recordsForAsin(
  searchResults: SearchResult[],
  searchParameters: SearchParameters,
): SearchResult[] {
  return searchResults
    .filter((searchResult) => searchResult.asin?.toUpperCase() === searchParameters.asin)
    .map((searchResult) => ({
      ...searchResult,
      region: searchResult.region || searchParameters.region,
    }))
}

export function searchErrorText(error: unknown, hasTimedOut: boolean): string {
  return hasTimedOut
    ? `The search took longer than ${SEARCH_TIMEOUT_MS / 1000} seconds and was stopped.`
    : `The search failed: ${(error as Error)?.message ?? 'unknown error'}`
}
