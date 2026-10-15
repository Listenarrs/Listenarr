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
  buildLibraryImportInitialAuthor,
  buildLibraryImportInitialQuery,
  buildLibraryImportSearchParams,
  buildLibraryImportTitle,
  titleFitsSearch,
} from '@/utils/libraryImportSearch'

describe('library import search helpers', () => {
  it('searches a single-file book by its file name, with the author from the tags', () => {
    const item = {
      fullPath: 'C:\\incoming\\Zelazny\\Jack of Shadows.m4b',
      folderName: 'Zelazny',
      sourceFiles: ['C:\\incoming\\Zelazny\\Jack of Shadows.m4b'],
      fileCount: 1,
      detectedTitle: 'Amber (tag)',
      detectedAuthor: 'Roger Zelazny',
    }

    expect(buildLibraryImportTitle(item)).toBe('Jack of Shadows')
    expect(buildLibraryImportInitialQuery(item)).toBe('Jack of Shadows')
    expect(buildLibraryImportInitialAuthor(item)).toBe('Roger Zelazny')
    expect(buildLibraryImportSearchParams(item, 5)).toEqual({
      title: 'Jack of Shadows',
      author: 'Roger Zelazny',
      cap: 5,
    })
  })

  it('searches a multi-file book by its folder name', () => {
    const item = {
      fullPath: '/incoming/Primal Hunter 4/01.mp3',
      folderName: 'Primal Hunter 4',
      sourceFiles: ['/incoming/Primal Hunter 4/01.mp3', '/incoming/Primal Hunter 4/02.mp3'],
      fileCount: 2,
      detectedTitle: 'The Primal Hunter',
    }

    expect(buildLibraryImportInitialQuery(item)).toBe('Primal Hunter 4')
    expect(buildLibraryImportSearchParams(item, 5)).toEqual({ title: 'Primal Hunter 4', cap: 5 })
  })

  it('uses the file count when the file list is not known', () => {
    expect(
      buildLibraryImportTitle({ fullPath: '/in/Book/01.mp3', folderName: 'Book', fileCount: 12 }),
    ).toBe('Book')
    expect(buildLibraryImportTitle({ fullPath: '/in/Book/Book One.m4b', folderName: 'Book' })).toBe(
      'Book One',
    )
  })

  it('turns a trailing number in brackets into a volume number', () => {
    const item = {
      fullPath: 'C:\\incoming\\The Land (3).m4b',
      folderName: 'The Land',
    }

    expect(buildLibraryImportInitialQuery(item)).toBe('The Land 3')
    expect(buildLibraryImportSearchParams(item, 5)).toEqual({
      title: 'The Land 3',
      cap: 5,
    })
  })

  it('still prioritizes asin over title and author', () => {
    const item = {
      fullPath: 'C:\\incoming\\Book.m4b',
      folderName: 'Book',
      detectedTitle: 'Ignored Title',
      detectedAuthor: 'Ignored Author',
      detectedAsin: 'B0DQR9D4YG',
    }

    expect(buildLibraryImportInitialQuery(item)).toBe('B0DQR9D4YG')
    expect(buildLibraryImportInitialAuthor(item)).toBe('')
    expect(buildLibraryImportSearchParams(item, 5)).toEqual({
      asin: 'B0DQR9D4YG',
      cap: 5,
    })
  })

  it('accepts a result only when its title fits the searched name', () => {
    // Audible's keyword search returns this for "not a match"
    expect(titleFitsSearch('not a match', { title: 'The Perfect Match' })).toBe(false)
    // the name carries more than the title: track number, subtitle, series
    expect(
      titleFitsSearch('04 - Primal Hunter 4 - A LitRPG Adventure (The Primal Hunter)', {
        title: 'Primal Hunter 4',
      }),
    ).toBe(true)
    // the volume is in the subtitle or the series
    expect(
      titleFitsSearch('Primal Hunter 4', {
        title: 'Primal Hunter',
        subtitle: 'Primal Hunter, Tome 4',
      }),
    ).toBe(true)
    expect(
      titleFitsSearch('Les cites des Anciens 1', {
        title: 'Dragons et serpents',
        series: [{ name: 'Les Cités des Anciens', position: '1' }],
      }),
    ).toBe(true)
    expect(
      titleFitsSearch('Dragons et serpents (Les Cités des Anciens 1)', {
        title: 'Dragons et serpents',
      }),
    ).toBe(true)
    expect(titleFitsSearch('Primal Hunter 4', { title: 'Primal Hunter 7' })).toBe(false)
  })
})
