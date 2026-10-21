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
import { useSearch } from '@/composables/useSearch'
import {
  getPrimaryPreferredSearchLanguageForRegion,
  searchRegionOptions,
} from '@/utils/languageMapping'

// The Audible record a book points at, as edited in a book match card (Repair and Find Match)

// An ASIN as Add New's search reads one: B and 9 letters or digits, or 10 digits (an ISBN-10, the
// last one possibly X)
export const ASIN_PATTERN = /^(B[0-9A-Z]{9}|\d{9}(?:X|\d))$/i

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
  picked: SearchResult | null
  // Why the ASIN can't be used in its region, once checked there (no record, or the check failed):
  // the book can't be repaired or matched until it changes
  asinError: string | null
  // Values when the card opened, restored by Revert
  initial: BookMatchValues
  // The record the book points at today (empty when it has none)
  current: { asin: string; region: string }
}

export function createBookMatch(
  initial: BookMatchValues,
  current: { asin: string; region: string },
): BookMatch {
  return { ...initial, picked: null, asinError: null, initial: { ...initial }, current }
}

export function regionLabel(region: string): string {
  return searchRegionOptions.find((o) => o.value === region)?.label ?? region
}

export function asinValid(match: BookMatch): boolean {
  return ASIN_PATTERN.test(match.asin.trim())
}

// Another ASIN or region than the record the book points at today
export function asinChanged(match: BookMatch): boolean {
  return (
    match.asin.trim().toUpperCase() !== match.current.asin.toUpperCase() ||
    match.region !== match.current.region
  )
}

// The state a book match card shows on its left bar: a new record looked up (matched), an ASIN
// its region rejects (error), or nothing to do yet (ignored)
export type MatchState = 'ignored' | 'matched' | 'error'

export function matchState(match: BookMatch): MatchState {
  if (match.asinError) return 'error'
  return match.picked ? 'matched' : 'ignored'
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

// A search result as a book record, in the region it was found in
export function recordFromResult(result: SearchResult, region: string): BookRecord {
  return {
    asin: result.asin,
    title: result.title,
    subtitle: result.subtitle,
    authors: (result.authors ?? []).map((a) => a.name).filter((name): name is string => !!name),
    region: result.region || region,
  }
}

// Title: Subtitle, the subtitle left out when there is none (some books carry their volume number
// only in the subtitle)
export function fullTitle(title?: string | null, subtitle?: string | null): string {
  return [title?.trim(), subtitle?.trim()].filter(Boolean).join(': ')
}

// One line for a book record, the same next to Revert, on Current and on New:
// ASIN - Title: Subtitle - Author(s) - Region, without the parts it doesn't have
export function bookSummary(record: BookRecord): string {
  const title = fullTitle(record.title, record.subtitle)
  const authors = (record.authors ?? [])
    .map((a) => a.trim())
    .filter(Boolean)
    .join(', ')
  return [record.asin?.trim(), title, authors, record.region ? regionLabel(record.region) : '']
    .filter(Boolean)
    .join(' - ')
}

export function isEdited(match: BookMatch): boolean {
  const { initial } = match
  return (
    match.asin !== initial.asin ||
    match.region !== initial.region ||
    match.query !== initial.query ||
    match.author !== initial.author
  )
}

// What Add New's search box would hold for this book: the title and author as TITLE:/AUTHOR:
// tokens, or the title alone when it is an ASIN (Add New then looks the ASIN up)
export function addNewQuery(match: BookMatch): string {
  const title = match.query.trim()
  const author = match.author.trim()
  if (ASIN_PATTERN.test(title) || !author) return title
  return `TITLE:${title} AUTHOR:${author}`
}

// Add New's own search (useSearch), so a book is searched the way it is added: titles through the
// search endpoint filtered on the region's language, an ASIN through the metadata sources in their
// priority order. Results are tagged with the region they were found in; aborting the signal
// rejects at once. An ASIN the region has no record for answers no result.
export async function searchLikeAddNew(
  query: string,
  region: string,
  signal: AbortSignal,
): Promise<SearchResult[]> {
  const search = useSearch()
  search.searchQuery.value = query
  search.searchLanguage.value = region
  search.preferredSearchLanguage.value = getPrimaryPreferredSearchLanguageForRegion(region)
  const aborted = new Promise<never>((_, reject) =>
    signal.addEventListener('abort', () => {
      search.cancelSearch()
      reject(new DOMException('The search was stopped', 'AbortError'))
    }),
  )
  try {
    const results = (await Promise.race([search.performSearch(), aborted])) ?? []
    return (results as SearchResult[])
      .filter((r) => !(r.asin && !r.title))
      .map((r) => ({ ...r, region: r.region || region }))
  } catch (e) {
    if ((e as { status?: number })?.status === 404) return []
    throw e
  }
}
