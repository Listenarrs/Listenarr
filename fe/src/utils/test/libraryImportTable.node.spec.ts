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
import { sortLibraryImportItems } from '@/utils/libraryImportTable'

describe('sortLibraryImportItems', () => {
  const items = [
    {
      id: '3',
      folderName: 'Gamma',
      fullPath: '/books/Gamma/book.m4b',
      format: 'M4B',
      fileCount: 1,
      selectedMatch: null,
    },
    {
      id: '1',
      folderName: 'Alpha',
      detectedTitle: 'Zeta Book',
      fullPath: '/books/Alpha/book-2.mp3',
      format: 'MP3',
      fileCount: 2,
      selectedMatch: {
        title: 'Zeta Match',
        authors: [{ name: 'Author Z' }],
      },
    },
    {
      id: '2',
      folderName: 'Beta',
      fullPath: '/books/Beta/book-1.m4b',
      format: 'M4B',
      fileCount: 1,
      selectedMatch: {
        title: 'Alpha Match',
        authors: [{ name: 'Author A' }],
      },
    },
  ]

  it('sorts by folder ascending', () => {
    const sorted = sortLibraryImportItems(items, 'folder', 'asc')
    expect(sorted.map((item) => item.id)).toEqual(['2', '3', '1'])
  })

  it('sorts by format descending and uses file count as a tiebreaker', () => {
    const sorted = sortLibraryImportItems(items, 'format', 'desc')
    expect(sorted.map((item) => item.id)).toEqual(['1', '2', '3'])
  })

  it('sorts unmatched items after matched items when sorting by match', () => {
    const sorted = sortLibraryImportItems(items, 'match', 'asc')
    expect(sorted.map((item) => item.id)).toEqual(['2', '1', '3'])
  })
})
