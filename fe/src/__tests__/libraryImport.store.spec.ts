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
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import type { SearchResult } from '@/types'

const startManualImport = vi.fn()
const addToLibrary = vi.fn()
const updateAudiobook = vi.fn()
const advancedSearch = vi.fn()
const scanUnmatchedFiles = vi.fn()
const getUnmatchedResults = vi.fn()
const getSavedUnmatchedFiles = vi.fn()
let unmatchedScanHandler:
  | ((payload: { jobId: string; error?: string }) => void | Promise<void>)
  | null = null

vi.mock('@/services/api', () => ({
  apiService: {
    addToLibrary,
    updateAudiobook,
    startManualImport,
    advancedSearch,
    getAudibleMetadata: vi.fn(),
    scanUnmatchedFiles,
    getUnmatchedResults,
    getSavedUnmatchedFiles,
  },
}))

vi.mock('@/services/signalr', () => ({
  signalRService: {
    onUnmatchedScanComplete: vi.fn((handler) => {
      unmatchedScanHandler = handler
      return () => {
        unmatchedScanHandler = null
      }
    }),
  },
}))

vi.mock('@/utils/logger', () => ({
  logger: {
    debug: vi.fn(),
  },
}))

describe('library import store', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    unmatchedScanHandler = null
    setActivePinia(createPinia())
    addToLibrary.mockResolvedValue({ audiobook: { id: 42 } })
    updateAudiobook.mockResolvedValue({})
    startManualImport.mockResolvedValue({ importedCount: 3, totalCount: 3, results: [] })
    advancedSearch.mockResolvedValue([])
    getSavedUnmatchedFiles.mockResolvedValue({ items: [], lastScannedAt: null })
  })

  it('submits every grouped source file during import', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    store.items = {
      'C:\\incoming\\Part 1.mp3': {
        id: 'C:\\incoming\\Part 1.mp3',
        fullPath: 'C:\\incoming\\Part 1.mp3',
        sourceFiles: [
          'C:\\incoming\\Part 1.mp3',
          'C:\\incoming\\Part 2.mp3',
          'C:\\incoming\\Part 10.mp3',
        ],
        folderPath: 'C:\\incoming',
        relativePath: 'Ordered Book',
        folderName: 'Ordered Book',
        format: 'MP3',
        fileCount: 3,
        selectedMatch: {
          title: 'Ordered Book',
          authors: [],
        } as unknown as SearchResult,
        hasSearched: true,
        isSearching: false,
        selected: true,
      },
    }

    store.action = 'move'
    startManualImport.mockResolvedValueOnce({
      importedCount: 3,
      totalCount: 3,
      results: [
        {
          success: true,
          sourcePath: 'C:\\incoming\\Part 1.mp3',
          destinationPath: 'D:\\library\\Ordered Book\\Part 1.mp3',
          warning: 'The source file was retained because durable identity is unavailable.',
        },
      ],
    })

    const result = await store.importSelected('D:\\library')

    expect(addToLibrary).toHaveBeenCalledTimes(1)
    expect(startManualImport).toHaveBeenCalledTimes(1)
    expect(startManualImport).toHaveBeenCalledWith({
      path: 'C:\\incoming',
      mode: 'interactive',
      action: 'move',
      includeCompanionFiles: true,
      cleanupEmptySourceFolders: true,
      items: [
        { fullPath: 'C:\\incoming\\Part 1.mp3', matchedAudiobookId: 42 },
        { fullPath: 'C:\\incoming\\Part 2.mp3', matchedAudiobookId: 42 },
        { fullPath: 'C:\\incoming\\Part 10.mp3', matchedAudiobookId: 42 },
      ],
    })
    expect(result.warnings).toEqual([
      'The source file was retained because durable identity is unavailable.',
    ])
  })

  it('registers files in place using the discovered book folder and backend success result', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    store.items = {
      '/audiobooks/Author/Book/Book.m4b': {
        id: '/audiobooks/Author/Book/Book.m4b',
        fullPath: '/audiobooks/Author/Book/Book.m4b',
        sourceFiles: ['/audiobooks/Author/Book/Book.m4b'],
        folderPath: '/audiobooks/Author/Book',
        relativePath: 'Author/Book',
        folderName: 'Book',
        format: 'M4B',
        fileCount: 1,
        selectedMatch: {
          title: 'Book',
          authors: [{ name: 'Author' }],
        } as unknown as SearchResult,
        hasSearched: true,
        isSearching: false,
        selected: true,
      },
    }
    store.action = 'none'
    startManualImport.mockResolvedValueOnce({
      importedCount: 1,
      totalCount: 1,
      results: [
        {
          success: true,
          sourcePath: '/audiobooks/Author/Book/Book.m4b',
          destinationPath: '/audiobooks/Author/Book/Book.m4b',
        },
      ],
    })

    const result = await store.importSelected('')

    expect(addToLibrary).toHaveBeenCalledWith(
      expect.any(Object),
      expect.objectContaining({
        destinationPath: '/audiobooks/Author/Book',
      }),
    )
    expect(startManualImport).toHaveBeenCalledWith({
      path: '/audiobooks/Author/Book',
      mode: 'interactive',
      action: 'none',
      includeCompanionFiles: false,
      cleanupEmptySourceFolders: false,
      items: [
        {
          fullPath: '/audiobooks/Author/Book/Book.m4b',
          matchedAudiobookId: 42,
        },
      ],
    })
    expect(result).toEqual({ imported: 1, errors: [], warnings: [] })
    expect(store.itemList).toHaveLength(0)
  })

  it('keeps the library import item when the backend skips in-place registration', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    store.items = {
      '/audiobooks/Author/Book/Book.m4b': {
        id: '/audiobooks/Author/Book/Book.m4b',
        fullPath: '/audiobooks/Author/Book/Book.m4b',
        sourceFiles: ['/audiobooks/Author/Book/Book.m4b'],
        folderPath: '/audiobooks/Author/Book',
        relativePath: 'Author/Book',
        folderName: 'Book',
        format: 'M4B',
        fileCount: 1,
        selectedMatch: {
          title: 'Book',
          authors: [{ name: 'Author' }],
        } as unknown as SearchResult,
        hasSearched: true,
        isSearching: false,
        selected: true,
      },
    }
    store.action = 'none'
    startManualImport.mockResolvedValueOnce({
      importedCount: 0,
      totalCount: 1,
      results: [
        {
          success: false,
          skipped: true,
          skipReason: 'The existing file could not be registered safely in place.',
        },
      ],
    })

    const result = await store.importSelected('')

    expect(result.imported).toBe(0)
    expect(result.errors).toEqual([
      'Book: The existing file could not be registered safely in place.',
    ])
    expect(store.itemList).toHaveLength(1)
  })

  it('keeps a multi-file book selected when only part of the backend registration succeeds', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    store.items = {
      '/audiobooks/Author/Book/Part 1.m4b': {
        id: '/audiobooks/Author/Book/Part 1.m4b',
        fullPath: '/audiobooks/Author/Book/Part 1.m4b',
        sourceFiles: ['/audiobooks/Author/Book/Part 1.m4b', '/audiobooks/Author/Book/Part 2.m4b'],
        folderPath: '/audiobooks/Author/Book',
        relativePath: 'Author/Book',
        folderName: 'Book',
        format: 'M4B',
        fileCount: 2,
        selectedMatch: {
          title: 'Book',
          authors: [{ name: 'Author' }],
        } as unknown as SearchResult,
        hasSearched: true,
        isSearching: false,
        selected: true,
      },
    }
    store.action = 'none'
    startManualImport.mockResolvedValueOnce({
      importedCount: 1,
      totalCount: 2,
      results: [
        { success: true, sourcePath: '/audiobooks/Author/Book/Part 1.m4b' },
        {
          success: false,
          sourcePath: '/audiobooks/Author/Book/Part 2.m4b',
          error: 'The existing file could not be registered safely in place.',
        },
      ],
    })

    const result = await store.importSelected('')

    expect(result.imported).toBe(0)
    expect(result.errors).toHaveLength(1)
    expect(store.itemList).toHaveLength(1)
    expect(store.itemList[0]?.selected).toBe(true)
  })

  it('does not rewrite an existing audiobook BasePath for in-place registration', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    store.items = {
      '/audiobooks/Author/Book/Book.m4b': {
        id: '/audiobooks/Author/Book/Book.m4b',
        fullPath: '/audiobooks/Author/Book/Book.m4b',
        sourceFiles: ['/audiobooks/Author/Book/Book.m4b'],
        folderPath: '/audiobooks/Author/Book',
        relativePath: 'Author/Book',
        folderName: 'Book',
        format: 'M4B',
        fileCount: 1,
        selectedMatch: {
          title: 'Book',
          authors: [{ name: 'Author' }],
        } as unknown as SearchResult,
        hasSearched: true,
        isSearching: false,
        selected: true,
      },
    }
    store.action = 'none'
    addToLibrary.mockRejectedValueOnce({
      status: 409,
      body: { audiobook: { id: 77 } },
    })
    startManualImport.mockResolvedValueOnce({
      importedCount: 1,
      totalCount: 1,
      results: [{ success: true }],
    })

    const result = await store.importSelected('')

    expect(updateAudiobook).not.toHaveBeenCalled()
    expect(startManualImport).toHaveBeenCalledWith(
      expect.objectContaining({
        action: 'none',
        items: [
          {
            fullPath: '/audiobooks/Author/Book/Book.m4b',
            matchedAudiobookId: 77,
          },
        ],
      }),
    )
    expect(result.imported).toBe(1)
  })

  it('ignores foreign scan completions until its own job id is assigned', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    scanUnmatchedFiles.mockImplementation(async () => {
      await unmatchedScanHandler?.({ jobId: 'foreign-job' })
      return { jobId: 'own-job' }
    })
    getUnmatchedResults.mockImplementation(async (jobId: string) => {
      expect(jobId).toBe('own-job')
      return {
        status: 'Completed',
        error: null,
        items: [
          {
            fullPath: 'C:\\incoming\\Book A.mp3',
            sourceFiles: ['C:\\incoming\\Book A.mp3'],
            bookFolder: 'C:\\incoming',
            relativePath: 'Book A',
            title: 'Book A',
            author: 'Author A',
            series: null,
            asin: null,
            format: 'MP3',
            fileCount: 1,
          },
        ],
      }
    })

    await store.triggerScan(7)

    expect(getUnmatchedResults).not.toHaveBeenCalledWith('foreign-job')
    expect(getUnmatchedResults).toHaveBeenCalledWith('own-job')
    expect(Object.keys(store.items)).toEqual(['C:\\incoming\\Book A.mp3'])
    expect(store.scanStatus).toBe('done')
  })

  it('prefers detected title and author for automatic matching before folder fallback', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    advancedSearch.mockResolvedValue([
      {
        title: 'Jack of Shadows',
        authors: [{ name: 'Roger Zelazny' }],
      },
    ])

    store.items = {
      'C:\\incoming\\Chapter 01.mp3': {
        id: 'C:\\incoming\\Chapter 01.mp3',
        fullPath: 'C:\\incoming\\Chapter 01.mp3',
        sourceFiles: ['C:\\incoming\\Chapter 01.mp3'],
        folderPath: 'C:\\incoming',
        relativePath: 'test-import',
        folderName: 'test-import',
        detectedTitle: 'Jack of Shadows',
        detectedAuthor: 'Roger Zelazny',
        format: 'MP3',
        fileCount: 1,
        selectedMatch: null,
        hasSearched: false,
        isSearching: false,
        selected: false,
      },
    }

    store.startProcessing()
    await new Promise((resolve) => setTimeout(resolve, 0))

    expect(advancedSearch).toHaveBeenCalledWith({
      title: 'Jack of Shadows',
      author: 'Roger Zelazny',
      cap: 5,
    })
    expect(store.items['C:\\incoming\\Chapter 01.mp3']?.selectedMatch?.title).toBe(
      'Jack of Shadows',
    )
  })

  it('carries every series membership of a multi-series match into the add request', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    // A book can legitimately belong to more than one series. Audnexus returns these as
    // seriesPrimary/seriesSecondary and the backend builds one membership per entry, so the
    // import path has to send both rather than keeping only the first.
    store.items = {
      '/incoming/Two Series/Book.m4b': {
        id: '/incoming/Two Series/Book.m4b',
        fullPath: '/incoming/Two Series/Book.m4b',
        sourceFiles: ['/incoming/Two Series/Book.m4b'],
        folderPath: '/incoming/Two Series',
        relativePath: 'Two Series',
        folderName: 'Two Series',
        format: 'M4B',
        fileCount: 1,
        selectedMatch: {
          title: 'Two Series Book',
          authors: [{ name: 'Author' }],
          series: [
            { asin: 'B01E633FQM', name: 'First Series', position: '0' },
            { asin: 'B01F5TL5K4', name: 'Second Series', position: '7' },
          ],
        } as unknown as SearchResult,
        hasSearched: true,
        isSearching: false,
        selected: true,
      },
    }
    store.action = 'none'

    await store.importSelected('')

    const metadata = addToLibrary.mock.calls[0][0]
    expect(metadata.seriesMemberships).toEqual([
      {
        seriesName: 'First Series',
        seriesNumber: '0',
        seriesAsin: 'B01E633FQM',
        isPrimary: true,
        sortOrder: 0,
      },
      {
        seriesName: 'Second Series',
        seriesNumber: '7',
        seriesAsin: 'B01F5TL5K4',
        isPrimary: false,
        sortOrder: 1,
      },
    ])
    // The primary is still mirrored onto the legacy scalars.
    expect(metadata.series).toBe('First Series')
    expect(metadata.seriesNumber).toBe('0')
  })

  it('drops a series asin that is really the series name', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()

    // When the search endpoint cannot re-fetch the book by ASIN it synthesizes a single
    // series entry whose `asin` is a copy of the series name. That must not be persisted
    // as a series ASIN.
    store.items = {
      '/incoming/Fallback/Book.m4b': {
        id: '/incoming/Fallback/Book.m4b',
        fullPath: '/incoming/Fallback/Book.m4b',
        sourceFiles: ['/incoming/Fallback/Book.m4b'],
        folderPath: '/incoming/Fallback',
        relativePath: 'Fallback',
        folderName: 'Fallback',
        format: 'M4B',
        fileCount: 1,
        selectedMatch: {
          title: 'Fallback Book',
          authors: [{ name: 'Author' }],
          series: [{ asin: 'Some Series', name: 'Some Series', position: '2' }],
        } as unknown as SearchResult,
        hasSearched: true,
        isSearching: false,
        selected: true,
      },
    }
    store.action = 'none'

    await store.importSelected('')

    const metadata = addToLibrary.mock.calls[0][0]
    expect(metadata.seriesMemberships).toEqual([
      {
        seriesName: 'Some Series',
        seriesNumber: '2',
        seriesAsin: undefined,
        isPrimary: true,
        sortOrder: 0,
      },
    ])
  })
})
