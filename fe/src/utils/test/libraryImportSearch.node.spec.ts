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
  buildLibraryImportFallbackTitle,
  buildLibraryImportInitialAuthor,
  buildLibraryImportInitialQuery,
  buildLibraryImportSearchParams,
} from '@/utils/libraryImportSearch'

describe('library import search helpers', () => {
  it('prefers detected title and author before folder fallback', () => {
    const item = {
      fullPath: 'C:\\incoming\\Chapter 01.mp3',
      folderName: 'test-import',
      detectedTitle: 'Jack of Shadows',
      detectedAuthor: 'Roger Zelazny',
    }

    expect(buildLibraryImportInitialQuery(item)).toBe('Jack of Shadows')
    expect(buildLibraryImportInitialAuthor(item)).toBe('Roger Zelazny')
    expect(buildLibraryImportSearchParams(item, 5)).toEqual({
      title: 'Jack of Shadows',
      author: 'Roger Zelazny',
      cap: 5,
    })
  })

  it('falls back to the filename or folder when no detected title exists', () => {
    const item = {
      fullPath: 'C:\\incoming\\The Land (3).m4b',
      folderName: 'The Land',
    }

    expect(buildLibraryImportFallbackTitle(item)).toBe('The Land 3')
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
})
