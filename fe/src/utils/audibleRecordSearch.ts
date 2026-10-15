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
import { apiService } from '@/services/api'
import type { SearchResult } from '@/types'

// Finding a book's Audible record in one region, shared by Find Match (Library Import) and Repair:
// search by title and author, or type an ASIN, which is looked up in the selected region.
// The functions mutate the state they are given: pass the reactive object.

// 10 letters or digits, with at least one digit (Audible ASINs start with B0, or are ISBN-10s):
// a 10-letter title such as "Alchemised" is a title, not an ASIN
export const ASIN_PATTERN = /^(?=.*\d)[A-Z0-9]{10}$/i

export interface RecordSearchState {
  asin: string
  region: string
  query: string
  author: string
  // The ASIN known to exist in that region (picked from the results or found by the lookup)
  chosen: { asin: string; region: string } | null
  // The ASIN of the previous match was cleared because the title or author changed
  asinCleared: boolean
  lookingUp: boolean
  asinNotFound: boolean
  lookupId: number
  searching: boolean
  searched: boolean
  results: SearchResult[]
}

export function createRecordSearchState(init: {
  asin?: string
  region: string
  query?: string
  author?: string
}): RecordSearchState {
  return {
    asin: init.asin ?? '',
    region: init.region,
    query: init.query ?? '',
    author: init.author ?? '',
    chosen: null,
    asinCleared: false,
    lookingUp: false,
    asinNotFound: false,
    lookupId: 0,
    searching: false,
    searched: false,
    results: [],
  }
}

// An Audible result is usable only with a title (an ASIN the catalogue doesn't sell in that market
// comes back as an empty stub); each one is tagged with the region it was found in
function usableInRegion(found: SearchResult[] | undefined, region: string): SearchResult[] {
  return (found ?? [])
    .filter((r) => r.asin && r.title)
    .map((r) => ({ ...r, region: r.region || region }))
}

export function firstAuthor(result: SearchResult): string {
  const first = (result.authors as Array<{ name?: string } | string> | undefined)?.[0]
  return (typeof first === 'string' ? first : first?.name) ?? ''
}

export function resultTitle(result: SearchResult): string {
  return [result.title, result.subtitle].filter(Boolean).join(': ')
}

export function resultMeta(result: SearchResult): string {
  const names = (list?: Array<{ name?: string } | string>) =>
    (list ?? []).map((x) => (typeof x === 'string' ? x : x?.name)).filter(Boolean)
  const authors = names(result.authors as Array<{ name?: string } | string>)
  const narrators = names(result.narrators as Array<{ name?: string } | string>)
  const seriesRaw = result.series as unknown
  const series = Array.isArray(seriesRaw)
    ? (seriesRaw as Array<{ name?: string }>)[0]?.name
    : (seriesRaw as string | undefined)
  return [
    authors.join(', '),
    narrators.length ? `read by ${narrators.join(', ')}` : '',
    series,
    result.language,
    result.asin,
  ]
    .filter(Boolean)
    .join(' · ')
}

export function asinMalformed(state: RecordSearchState): boolean {
  const asin = state.asin.trim()
  return asin.length > 0 && !ASIN_PATTERN.test(asin)
}

// The ASIN in the field is known to exist in the selected region
export function isChosen(state: RecordSearchState): boolean {
  const asin = state.asin.trim().toUpperCase()
  return Boolean(state.chosen && state.chosen.asin === asin && state.chosen.region === state.region)
}

function cancelLookup(state: RecordSearchState) {
  ++state.lookupId
  state.lookingUp = false
  state.asinNotFound = false
}

// Search the title and author in the selected region; a title that is itself an ASIN is looked up
// as one
export async function searchRecords(state: RecordSearchState) {
  const term = state.query.trim()
  if (!term) return
  const region = state.region
  const author = state.author.trim()
  state.searching = true
  try {
    const found = await apiService.advancedSearch(
      ASIN_PATTERN.test(term)
        ? { asin: term.toUpperCase(), region, cap: 5 }
        : { title: term, ...(author ? { author } : {}), region, cap: 8 },
    )
    state.results = usableInRegion(found, region)
  } catch {
    state.results = []
  } finally {
    state.searching = false
    state.searched = true
  }
}

// Look the ASIN up in the selected region. When the catalogue has it, the title and author show
// which book it is and the ASIN counts as chosen; otherwise the state says so. Returns the record.
export async function lookupAsin(state: RecordSearchState): Promise<SearchResult | null> {
  const asin = state.asin.trim().toUpperCase()
  const region = state.region
  const id = ++state.lookupId
  state.chosen = null
  state.asinNotFound = false
  state.asinCleared = false
  state.lookingUp = true
  let found: SearchResult[] = []
  try {
    found = usableInRegion(await apiService.advancedSearch({ asin, region, cap: 5 }), region)
  } catch {
    found = []
  }
  if (id !== state.lookupId) return null
  state.lookingUp = false
  state.searched = false
  const record = found.find((r) => (r.asin ?? '').toUpperCase() === asin)
  if (!record) {
    state.asinNotFound = true
    state.results = []
    return null
  }
  state.query = record.title ?? state.query
  state.author = firstAuthor(record) || state.author
  state.results = [record]
  state.chosen = { asin, region }
  return record
}

export function pickRecord(state: RecordSearchState, result: SearchResult) {
  cancelLookup(state)
  state.asin = (result.asin ?? '').toUpperCase()
  if (result.region) state.region = result.region
  state.chosen = { asin: state.asin, region: state.region }
  state.asinCleared = false
}

// A new title or author means another book: an ASIN that wasn't chosen no longer applies
export function onTitleOrAuthorEdited(state: RecordSearchState) {
  if (state.chosen || state.lookingUp) return
  state.asinNotFound = false
  if (state.asin) {
    state.asin = ''
    state.asinCleared = true
  }
}

// ASINs differ from one Audible market to another: an ASIN is looked up again in the new region,
// otherwise the title is searched there. Results of the previous region no longer apply.
export async function onRegionChanged(state: RecordSearchState) {
  state.results = []
  state.searched = false
  if (ASIN_PATTERN.test(state.asin.trim())) {
    await lookupAsin(state)
    return
  }
  cancelLookup(state)
  state.chosen = null
  if (state.query.trim()) await searchRecords(state)
}

export async function onAsinEdited(state: RecordSearchState) {
  state.asinCleared = false
  if (ASIN_PATTERN.test(state.asin.trim())) {
    await lookupAsin(state)
    return
  }
  cancelLookup(state)
  state.chosen = null
}

export function resetRecordSearch(
  state: RecordSearchState,
  values: { asin: string; region: string; query: string; author: string },
) {
  cancelLookup(state)
  Object.assign(state, values, {
    chosen: null,
    asinCleared: false,
    results: [],
    searched: false,
  })
}
