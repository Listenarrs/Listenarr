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

// A search for the title and author, or the check of the ASIN in its region
export type SearchKind = 'title' | 'asin'

// invalid: Search was asked for something it can't search (an ASIN that isn't one), a field error;
// canceled: the user stopped the search. results: the last answer, kept until a new one replaces it
export interface SearchState {
  status: 'idle' | 'searching' | 'done' | 'failed' | 'invalid' | 'canceled'
  kind: SearchKind
  results: SearchResult[]
  error: string | null
  // What the results shown were searched with, for their header
  searched: Searched | null
  // How long their request took, or null when the dialog had them already (answered before)
  requestMs: number | null
}

// The parameters of one search, taken when it starts: typing afterwards doesn't change them
export interface Searched {
  kind: SearchKind
  asin: string
  title: string
  author: string
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
    kind: 'title',
    results: [],
    error: null,
    searched: null,
    requestMs: null,
  }
}

// What a search looked for: the ASIN (which has priority when filled), or the title and author
export function describeSearch(searched: Searched): string {
  if (searched.kind === 'asin') return `ASIN "${searched.asin}"`
  return `"${searched.title}"${searched.author ? ` by "${searched.author}"` : ''}`
}

// What the running search does, on the bar's tooltip and for screen readers
export function pendingSearchText(running: Searched | null): string | null {
  return running ? `Searching ${describeSearch(running)} in ${regionLabel(running.region)}…` : null
}

// Blue while searching; red when the request failed; orange for a field error or a search that
// found nothing; green once matched (grey when left out); grey otherwise.
// Matched (green) is only a picked record, the one state Repair counts; a search that found records
// with nothing picked yet stays ignored (grey)
export function cardBarState(
  { status, results }: SearchState,
  matched: boolean,
  excluded: boolean,
): CardBarState {
  if (status === 'searching') return 'searching'
  if (status === 'failed') return 'error'
  if (status === 'invalid' || status === 'canceled' || (status === 'done' && results.length === 0))
    return 'warning'
  return matched && !excluded ? 'matched' : 'ignored'
}

// The results' header line, always there: what was searched, where, and where the answer comes
// from (orange when it found nothing, red when it failed). Why there are no results is said in the
// middle of their room. A failed, canceled or refused search shows no results: the empty list
// again, as before any search
export function searchHeader({ searched, results, status, requestMs }: SearchState): SearchHeader {
  if (status === 'invalid')
    return { text: 'No search request done', tone: 'warning', outcome: 'stopped' }
  if (!searched) return { text: 'No search request done', outcome: 'none' }
  const what = `${describeSearch(searched)} in ${regionLabel(searched.region)}`
  if (status === 'canceled')
    return { text: `No results for ${what}`, tone: 'warning', outcome: 'stopped' }
  if (status === 'failed')
    return { text: `No results for ${what}`, tone: 'error', outcome: 'stopped' }
  if (results.length)
    return { text: `Results for ${what} - ${answerSource(requestMs)}`, outcome: 'results' }
  return {
    text: `No results for ${what} - ${answerSource(requestMs)}`,
    tone: 'warning',
    outcome: 'empty',
  }
}

// Why a search left no results, in the middle of their room: a failed request (red), or nothing
// found, the search canceled, an ASIN that isn't one (orange). Nothing before any search
export function noResultsReason({
  searched,
  results,
  status,
  error,
}: SearchState): { text: string; tone: SearchTone } | null {
  if (status === 'invalid') return { text: error ?? '', tone: 'warning' }
  if (status === 'canceled') return { text: 'Search canceled', tone: 'warning' }
  if (status === 'failed') return { text: error ?? '', tone: 'error' }
  if (status !== 'done' || results.length || !searched) return null
  return {
    text:
      searched.kind === 'asin'
        ? noRecordError(searched.asin, searched.region)
        : 'Nothing found: no book for this title and author',
    tone: 'warning',
  }
}

// The records an ASIN search found for that ASIN, tagged with the region they were found in
export function recordsForAsin(results: SearchResult[], searched: Searched): SearchResult[] {
  return results
    .filter((r) => r.asin?.toUpperCase() === searched.asin)
    .map((r) => ({ ...r, region: r.region || searched.region }))
}

export function searchErrorText(error: unknown, timedOut: boolean): string {
  return timedOut
    ? `The search took longer than ${SEARCH_TIMEOUT_MS / 1000} seconds and was stopped.`
    : `The search failed: ${(error as Error)?.message ?? 'unknown error'}`
}
