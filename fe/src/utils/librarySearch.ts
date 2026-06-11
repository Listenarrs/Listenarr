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

import { normalizeCollectionText, isNonPersonNarrator } from './textUtils'

/**
 * Unified library search — matches a single query against the books the user
 * already owns across four dimensions (books, series, authors, narrators) and
 * returns each grouped. Shared by the header search overlay and the full results
 * page so both rank and bucket results identically.
 *
 * Collection facets (series/authors/narrators) are keyed with the same
 * normalizer the collection pages use, so a result's book count matches the page
 * it links to. Non-person narrator credits ("Full Cast") are excluded — they are
 * not browsable people.
 */

// The subset of an Audiobook this module reads. Kept structural so it does not
// couple to the full Audiobook type.
export interface LibrarySearchInput {
  id?: number
  title?: string
  authors?: string[]
  narrators?: string[]
  series?: string
  imageUrl?: string
  publishYear?: string | number
  publishedDate?: string
}

export type LibrarySearchKind = 'book' | 'series' | 'author' | 'narrator'

export interface LibraryBookMatch {
  id: number
  title: string
  author: string
  imageUrl: string
  year?: number
}

export interface LibraryFacetMatch {
  name: string
  normKey: string
  count: number
  imageUrl?: string
}

export interface LibraryFacets {
  series: LibraryFacetMatch[]
  authors: LibraryFacetMatch[]
  narrators: LibraryFacetMatch[]
}

/**
 * Build the distinct series / author / narrator index for a library, each with a
 * book count and a sample cover. Pure and dependency-free so a caller can compute
 * it once (e.g. a cached computed) and reuse it across many queries.
 */
export function buildLibraryFacets(books: readonly LibrarySearchInput[]): LibraryFacets {
  const series = new Map<string, LibraryFacetMatch>()
  const authors = new Map<string, LibraryFacetMatch>()
  const narrators = new Map<string, LibraryFacetMatch>()

  // `seen` dedupes within a single book so a book that lists the same name twice
  // still counts once — matching the collection page's `.some()` filter and the
  // grouped-grid fan-out, so counts stay identical across all three surfaces.
  const add = (
    map: Map<string, LibraryFacetMatch>,
    raw: string | undefined,
    cover: string,
    seen: Set<string>,
  ) => {
    const display = (raw ?? '').trim()
    if (!display) return
    const normKey = normalizeCollectionText(display)
    if (!normKey || seen.has(normKey)) return
    seen.add(normKey)
    let facet = map.get(normKey)
    if (!facet) {
      facet = { name: display, normKey, count: 0, imageUrl: cover || undefined }
      map.set(normKey, facet)
    }
    facet.count++
    if (!facet.imageUrl && cover) facet.imageUrl = cover
  }

  for (const book of books) {
    const cover = book.imageUrl || ''
    if (book.series) add(series, book.series, cover, new Set())
    const authorsSeen = new Set<string>()
    for (const author of book.authors ?? []) add(authors, author, cover, authorsSeen)
    const narratorsSeen = new Set<string>()
    for (const narrator of book.narrators ?? []) {
      if (!isNonPersonNarrator(narrator)) add(narrators, narrator, cover, narratorsSeen)
    }
  }

  return {
    series: [...series.values()],
    authors: [...authors.values()],
    narrators: [...narrators.values()],
  }
}

/** Best-effort 4-digit release year from publishYear or publishedDate. */
function parsePublishYear(book: LibrarySearchInput): number | undefined {
  if (book.publishYear != null && book.publishYear !== '') {
    const y = Number.parseInt(String(book.publishYear), 10)
    if (Number.isFinite(y)) return y
  }
  const match = (book.publishedDate || '').match(/\d{4}/)
  return match ? Number.parseInt(match[0], 10) : undefined
}

/** Books whose title or any author contains the query (case-insensitive). */
export function matchLibraryBooks(
  books: readonly LibrarySearchInput[],
  rawQuery: string,
  limit?: number,
): LibraryBookMatch[] {
  const lower = rawQuery.trim().toLowerCase()
  if (!lower) return []
  const matches: LibraryBookMatch[] = []
  for (const book of books) {
    const titleHit = (book.title || '').toLowerCase().includes(lower)
    const authorHit = (Array.isArray(book.authors) ? book.authors.join(' ').toLowerCase() : '').includes(
      lower,
    )
    if (!titleHit && !authorHit) continue
    matches.push({
      id: book.id ?? 0,
      title: book.title || 'Unknown',
      author: Array.isArray(book.authors) ? book.authors[0] || '' : '',
      imageUrl: book.imageUrl || '',
      year: parsePublishYear(book),
    })
    if (limit != null && matches.length >= limit) break
  }
  return matches
}

/**
 * Filter a prepared facet list by a normalized-substring match, ranked
 * most-books-first then alphabetically.
 */
export function matchLibraryFacets(
  facets: readonly LibraryFacetMatch[],
  rawQuery: string,
  limit?: number,
): LibraryFacetMatch[] {
  const nq = normalizeCollectionText(rawQuery)
  if (!nq) return []
  const matched = facets
    .filter((facet) => facet.normKey.includes(nq))
    .sort((a, b) => b.count - a.count || a.name.localeCompare(b.name))
  return limit != null ? matched.slice(0, limit) : matched
}
