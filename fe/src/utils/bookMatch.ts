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
import type { InjectionKey } from 'vue'
import type { Audiobook, SearchResult } from '@/types'
import { ASIN_PATTERN } from '@/services/audiobookSearch'
import { regionLabel } from '@/utils/languageMapping'

// The Audible record a book points at, as edited in a book match card (Repair and Find Match)

// What an ASIN must look like, shown when Search is asked for one that doesn't
export const INVALID_ASIN_MESSAGE =
  'Not an ASIN: an ASIN is B followed by 9 letters or digits (B08G9PRS1K), or 10 digits (1980006520).'
// Catalogue offered after the book's and the configured ones
export const FALLBACK_REGION = 'us'

export interface BookMatchValues {
  asin: string
  region: string
  // What the search looks for: a title (or an ASIN) and an author, editable
  query: string
  author: string
}

export interface BookMatch extends BookMatchValues {
  // The book's new record, looked up in its region: picked in the search results, or the one
  // record found for its ASIN. Only a book with one can be repaired or matched.
  pickedSearchResult: SearchResult | null
  // Why the ASIN can't be used in its region, once checked there (no record, or the check failed):
  // the book can't be repaired or matched until it changes
  asinError: string | null
  // Values when the card opened, restored by Revert
  initialValues: BookMatchValues
  // The record the book points at today (empty when it has none)
  currentRecordKey: { asin: string; region: string }
}

export function createBookMatch(
  initialValues: BookMatchValues,
  currentRecordKey: { asin: string; region: string },
): BookMatch {
  return {
    ...initialValues,
    pickedSearchResult: null,
    asinError: null,
    initialValues: { ...initialValues },
    currentRecordKey,
  }
}

// In seconds, with up to 2 decimals: 1.23 s, 1.5 s, 2 s
export function formatDuration(durationMs: number): string {
  return `${Number((durationMs / 1000).toFixed(2))} s`
}

// Where a search's answer comes from, after its results, its "No results" or its "No record": a
// request and how long it took, or the searches answered while the dialog is open (then possibly
// stale: Retry asks again)
export function answerSource(requestMs: number | null): string {
  return requestMs === null ? 'Cached results' : `Request: ${formatDuration(requestMs)}`
}

export { regionLabel }

export function isAsinValid(bookMatch: BookMatch): boolean {
  return ASIN_PATTERN.test(bookMatch.asin.trim())
}

// A new record (the picked one) whose ASIN or region differs from the one the book points at today
export function hasAsinChanged(bookMatch: BookMatch): boolean {
  const { pickedSearchResult, currentRecordKey } = bookMatch
  if (!pickedSearchResult?.asin) return false
  return (
    pickedSearchResult.asin.toUpperCase() !== currentRecordKey.asin.toUpperCase() ||
    (pickedSearchResult.region || bookMatch.region) !== currentRecordKey.region
  )
}

// The state a book match card shows on its left bar: a new record looked up (matched), an ASIN
// its region rejects (error), or nothing to do yet (ignored)
export type MatchState = 'ignored' | 'matched' | 'error'

export function matchState(bookMatch: BookMatch): MatchState {
  // A record the user picked (a new search unpicks it first)
  if (bookMatch.pickedSearchResult) return 'matched'
  return bookMatch.asinError ? 'error' : 'ignored'
}

// A task run on a book once its record is chosen (Repair's repair), shown by its whole card:
// waiting its turn, running (blue, breathing), done (light green), warning (orange: done in part),
// failed (red), or skipped (grey: left out of the task)
export type BookTaskState = 'waiting' | 'running' | 'done' | 'warning' | 'failed' | 'skipped'

export interface BookTask {
  state: BookTaskState
  // One line saying where the task is or how it ended
  text: string
}

export function noRecordError(asin: string, region: string): string {
  return `No record for ${asin} in Audible ${regionLabel(region)}`
}

// A book's Audible record as the card shows it: the book today (Revert, Current) or the record it
// will point at (New)
export interface BookRecord {
  asin?: string | null
  title?: string | null
  subtitle?: string | null
  authors?: string[]
  region?: string | null
}

// What tells one Audible record from another: its ASIN and, when known, its region (an ASIN can be
// sold in several regions)
export interface RecordKey {
  asin: string
  region?: string | null
  // A record without an ASIN (a source other than Audible, e.g. OpenLibrary): its id there
  id?: string | null
}

// Whether a search result is that record: by its ASIN (and region, when both have one), or, for a
// record without an ASIN, by its id at its source
export function isResultOfRecord(
  searchResult: SearchResult,
  recordKey: RecordKey | null | undefined,
): boolean {
  if (!recordKey) return false
  if (!recordKey.asin)
    return !searchResult.asin && !!recordKey.id && searchResult.id === recordKey.id
  if (searchResult.asin?.toUpperCase() !== recordKey.asin.toUpperCase()) return false
  return !recordKey.region || !searchResult.region || searchResult.region === recordKey.region
}

// The library's books by ASIN: which records they hold. A record goes on one book
export type LibraryBooksByAsin = Map<string, Audiobook[]>

export function libraryBooksByAsin(libraryBooks: Audiobook[]): LibraryBooksByAsin {
  const booksByAsin: LibraryBooksByAsin = new Map()
  for (const book of libraryBooks) {
    if (!book.asin) continue
    const asinKey = book.asin.toUpperCase()
    booksByAsin.set(asinKey, [...(booksByAsin.get(asinKey) ?? []), book])
  }
  return booksByAsin
}

// The library book holding a search result's record, among the ones that count: the same ASIN in
// the same region, a region not known counting as the same one
export function libraryBookHolding(
  searchResult: SearchResult,
  booksByAsin: LibraryBooksByAsin,
  isCounted: (book: Audiobook) => boolean,
): Audiobook | null {
  const asin = searchResult.asin?.toUpperCase()
  if (!asin) return null
  return (
    booksByAsin
      .get(asin)
      ?.find(
        (book) =>
          isCounted(book) && isResultOfRecord(searchResult, { asin, region: book.asinRegion }),
      ) ?? null
  )
}

// A search result as a book record, in the region it was found in
export function recordFromResult(result: SearchResult, region: string): BookRecord {
  return {
    asin: result.asin,
    title: result.title,
    subtitle: result.subtitle,
    authors: (result.authors ?? [])
      .map((author) => author.name)
      .filter((name): name is string => !!name),
    region: result.region || region,
  }
}

// Title: Subtitle, the subtitle left out when there is none (some books carry their volume number
// only in the subtitle)
export function fullTitle(title?: string | null, subtitle?: string | null): string {
  return [title?.trim(), subtitle?.trim()].filter(Boolean).join(': ')
}

export type RecordPartKey = 'asin' | 'title' | 'authors' | 'region'

// The parts of a book record's one line, in order, without the ones it doesn't have
export function recordParts(record: BookRecord): { key: RecordPartKey; text: string }[] {
  const authors = (record.authors ?? [])
    .map((author) => author.trim())
    .filter(Boolean)
    .join(', ')
  const allParts: { key: RecordPartKey; text: string }[] = [
    { key: 'asin', text: record.asin?.trim() ?? '' },
    { key: 'title', text: fullTitle(record.title, record.subtitle) },
    { key: 'authors', text: authors },
    { key: 'region', text: record.region ? regionLabel(record.region) : '' },
  ]
  return allParts.filter((part) => part.text)
}

// One line for a book record, the same next to Revert, on Current and on New:
// ASIN - Title: Subtitle - Author(s) - Region, without the parts it doesn't have
export function bookSummary(record: BookRecord): string {
  return recordParts(record)
    .map((part) => part.text)
    .join(' - ')
}

export function isEdited(bookMatch: BookMatch): boolean {
  const { initialValues } = bookMatch
  return (
    bookMatch.asin !== initialValues.asin ||
    bookMatch.region !== initialValues.region ||
    bookMatch.query !== initialValues.query ||
    bookMatch.author !== initialValues.author ||
    !!bookMatch.pickedSearchResult
  )
}

// The searches answered while a dialog is open, shared by its book cards: a search with values
// already answered (back to a region, a field typed back, Revert, another book searched the same
// way) is served from here, without a request. Retry skips it; a failed search is never kept. The
// dialog provides it and empties it each time it opens.
export type SearchCache = Map<string, SearchResult[]>
export const SEARCH_CACHE: InjectionKey<SearchCache> = Symbol('bookMatchSearchCache')

export function searchCacheKey(title: string, author: string, region: string): string {
  return `${region}|${title}|${author}`
}
