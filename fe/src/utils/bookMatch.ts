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
import { searchRegionOptions } from '@/utils/languageMapping'

// The Audible record a book points at, as edited in a book match card (Repair and Find Match)

export const ASIN_PATTERN = /^[A-Z0-9]{10}$/i
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

export function isEdited(match: BookMatch): boolean {
  const { initial } = match
  return (
    match.asin !== initial.asin ||
    match.region !== initial.region ||
    match.query !== initial.query ||
    match.author !== initial.author
  )
}

// The search parameters for the title and author, or for an ASIN typed as the title
export function searchParams(match: BookMatch) {
  const query = match.query.trim()
  return ASIN_PATTERN.test(query)
    ? { asin: query.toUpperCase(), cap: 5 }
    : { title: query, author: match.author.trim() || undefined, cap: 5 }
}
