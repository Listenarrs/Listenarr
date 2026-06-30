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
import { describe, expect, it } from 'vitest'
import {
  buildLibraryFacets,
  matchLibraryBooks,
  matchLibraryFacets,
  type LibrarySearchInput,
} from '@/utils/librarySearch'

const library: LibrarySearchInput[] = [
  {
    id: 1,
    title: 'Project Hail Mary',
    authors: ['Andy Weir'],
    narrators: ['Ray Porter'],
    series: undefined,
    imageUrl: 'phm.jpg',
  },
  {
    id: 2,
    title: 'The Martian',
    authors: ['Andy Weir'],
    narrators: ['R. C. Bray'],
    series: undefined,
    imageUrl: 'martian.jpg',
  },
  {
    id: 3,
    title: 'Skyward',
    authors: ['Brandon Sanderson'],
    // Same narrator with different casing/punctuation must merge with book 4.
    narrators: ['Suzy Jackson'],
    series: 'Skyward',
    imageUrl: 'skyward.jpg',
  },
  {
    id: 4,
    title: 'Starsight',
    authors: ['Brandon Sanderson'],
    narrators: ['suzy  jackson', 'Full Cast'],
    series: 'Skyward',
    imageUrl: 'starsight.jpg',
  },
  {
    id: 5,
    title: 'The Sandman',
    authors: ['Neil Gaiman'],
    // Dramatized — every credit but the production token is a real person.
    narrators: ['James McAvoy', 'A Full Cast'],
    series: undefined,
    imageUrl: 'sandman.jpg',
  },
]

describe('buildLibraryFacets', () => {
  it('fans a book out across each narrator and merges normalized duplicates', () => {
    const { narrators } = buildLibraryFacets(library)
    const suzy = narrators.find((n) => n.normKey === 'suzy jackson')
    expect(suzy).toBeDefined()
    // Books 3 and 4 both credit Suzy Jackson despite casing/spacing differences.
    expect(suzy?.count).toBe(2)
    expect(suzy?.name).toBe('Suzy Jackson') // first-seen display casing
  })

  it('excludes non-person narrator credits ("Full Cast")', () => {
    const { narrators } = buildLibraryFacets(library)
    const keys = narrators.map((n) => n.normKey)
    expect(keys).not.toContain('full cast')
    expect(keys).not.toContain('a full cast')
    // The real co-narrator on a dramatized book is still surfaced.
    expect(keys).toContain('james mcavoy')
  })

  it('counts authors and series book-centrically', () => {
    const { authors, series } = buildLibraryFacets(library)
    expect(authors.find((a) => a.normKey === 'andy weir')?.count).toBe(2)
    expect(authors.find((a) => a.normKey === 'brandon sanderson')?.count).toBe(2)
    expect(series.find((s) => s.normKey === 'skyward')?.count).toBe(2)
  })

  it('counts a name once per book even when listed twice (matches .some() filter)', () => {
    const dupe: LibrarySearchInput[] = [
      { id: 9, title: 'Dup', authors: ['Jane Doe', 'jane  doe'], narrators: ['Ray Porter', 'Ray Porter'] },
    ]
    const { authors, narrators } = buildLibraryFacets(dupe)
    expect(authors.find((a) => a.normKey === 'jane doe')?.count).toBe(1)
    expect(narrators.find((n) => n.normKey === 'ray porter')?.count).toBe(1)
  })
})

describe('matchLibraryBooks', () => {
  it('matches on title or author, case-insensitively', () => {
    expect(matchLibraryBooks(library, 'martian').map((b) => b.id)).toEqual([2])
    expect(matchLibraryBooks(library, 'weir').map((b) => b.id).sort()).toEqual([1, 2])
  })

  it('honors the result limit', () => {
    expect(matchLibraryBooks(library, 'the', 1)).toHaveLength(1)
  })

  it('parses a release year from publishYear or publishedDate', () => {
    const books: LibrarySearchInput[] = [
      { id: 1, title: 'Y From Field', authors: ['A'], publishYear: '2011' },
      { id: 2, title: 'Y From Date', authors: ['A'], publishedDate: '2008-05-01' },
      { id: 3, title: 'Y Missing', authors: ['A'] },
    ]
    const byId = Object.fromEntries(matchLibraryBooks(books, 'y ').map((b) => [b.id, b.year]))
    expect(byId[1]).toBe(2011)
    expect(byId[2]).toBe(2008)
    expect(byId[3]).toBeUndefined()
  })
})

describe('matchLibraryFacets', () => {
  it('matches normalized substrings ranked by book count then name', () => {
    const { authors } = buildLibraryFacets(library)
    const hits = matchLibraryFacets(authors, 'an')
    // All three contain "an"; Weir(2) and Sanderson(2) outrank Gaiman(1), and the
    // two-book tie breaks alphabetically — so order is deterministic.
    expect(hits.map((h) => h.name)).toEqual(['Andy Weir', 'Brandon Sanderson', 'Neil Gaiman'])
  })

  it('returns nothing for a blank/punctuation-only query', () => {
    const { authors } = buildLibraryFacets(library)
    expect(matchLibraryFacets(authors, '   ')).toEqual([])
    expect(matchLibraryFacets(authors, '!!!')).toEqual([])
  })
})
