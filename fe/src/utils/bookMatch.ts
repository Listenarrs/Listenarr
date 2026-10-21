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
  // The record picked in the search results (its ASIN and region are known to exist); null when
  // the ASIN is typed
  picked: SearchResult | null
  // Values when the card opened, restored by Revert
  initial: BookMatchValues
  // The record the book points at today (empty when it has none)
  current: { asin: string; region: string }
}

export function createBookMatch(
  initial: BookMatchValues,
  current: { asin: string; region: string },
): BookMatch {
  return { ...initial, picked: null, initial: { ...initial }, current }
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

// Keeping the current ASIN and region is fine; a new one must look like an ASIN
export function asinInvalid(match: BookMatch): boolean {
  return asinChanged(match) && !asinValid(match)
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

// The record of the match: the picked one, or the typed ASIN looked up in its region with the
// same search. A record Audible doesn't have there is refused.
export async function resolveRecord(
  match: BookMatch,
  search: (params: { asin: string; cap: number }, region: string) => Promise<SearchResult[]>,
): Promise<SearchResult> {
  if (match.picked) return match.picked
  const asin = match.asin.trim().toUpperCase()
  const found = (await search({ asin, cap: 5 }, match.region)).find(
    (r) => r.asin?.toUpperCase() === asin,
  )
  if (!found)
    throw new Error(
      `Audible ${regionLabel(match.region)} has no record for ${asin}. Search the book and pick it in the right region.`,
    )
  return { ...found, region: found.region || match.region }
}
