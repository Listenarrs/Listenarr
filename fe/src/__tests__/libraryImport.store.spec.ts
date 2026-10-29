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
const getAudibleMetadata = vi.fn()
const getApplicationSettings = vi.fn()
const searchEditions = vi.fn()
let unmatchedScanHandler:
  | ((payload: { jobId: string; error?: string }) => void | Promise<void>)
  | null = null

vi.mock('@/services/api', () => ({
  apiService: {
    addToLibrary,
    updateAudiobook,
    startManualImport,
    advancedSearch,
    getAudibleMetadata,
    getApplicationSettings,
    scanUnmatchedFiles,
    getUnmatchedResults,
    getSavedUnmatchedFiles,
    searchEditions,
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
    getApplicationSettings.mockResolvedValue({ defaultSearchRegion: 'fr' })
    getAudibleMetadata.mockResolvedValue({})
    searchEditions.mockResolvedValue([])
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
          warning: 'The source file was retained because source cleanup could not be proven safe.',
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
      'The source file was retained because source cleanup could not be proven safe.',
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

  describe('a newer load or scan replaces the previous one', () => {
    const unmatchedFile = (fullPath: string) => ({
      fullPath,
      sourceFiles: [fullPath],
      bookFolder: '/srv',
      relativePath: fullPath,
      title: fullPath,
      author: null,
      series: null,
      asin: null,
      format: 'MP3',
      fileCount: 1,
    })
    const completed = (jobId: string, fullPath: string) => ({
      jobId,
      status: 'Completed',
      error: null,
      warnings: [],
      items: [unmatchedFile(fullPath)],
    })
    function deferred<T>() {
      let resolve!: (value: T) => void
      const promise = new Promise<T>((r) => (resolve = r))
      return { promise, resolve }
    }

    it("ignores a replaced scan's late result", async () => {
      const { useLibraryImportStore } = await import('@/stores/libraryImport')
      const store = useLibraryImportStore()
      const firstCheck = deferred<ReturnType<typeof completed>>()
      scanUnmatchedFiles.mockImplementation(async (rootFolderId: number) => ({
        jobId: `job-${rootFolderId}`,
      }))
      getUnmatchedResults.mockImplementation((jobId: string) =>
        jobId === 'job-1' ? firstCheck.promise : Promise.resolve(completed(jobId, '/srv/2/B.mp3')),
      )

      const firstScan = store.triggerScan(1)
      await vi.waitFor(() => expect(getUnmatchedResults).toHaveBeenCalledWith('job-1'))
      await store.triggerScan(2)
      firstCheck.resolve(completed('job-1', '/srv/1/A.mp3'))
      await firstScan

      expect(store.rootFolderId).toBe(2)
      expect(Object.keys(store.items)).toEqual(['/srv/2/B.mp3'])
      expect(store.scanStatus).toBe('done')
      expect(JSON.parse(localStorage.getItem('listenarr-import-matches-2') ?? '{}')).toHaveProperty(
        ['/srv/2/B.mp3'],
      )
    })

    it("stops listening to the replaced scan's completion event", async () => {
      const { useLibraryImportStore } = await import('@/stores/libraryImport')
      const store = useLibraryImportStore()
      scanUnmatchedFiles.mockResolvedValue({ jobId: 'job-1' })
      getUnmatchedResults.mockResolvedValue({
        ...completed('job-1', '/srv/1/A.mp3'),
        status: 'Queued',
      })
      await store.triggerScan(1)
      const firstScanHandler = unmatchedScanHandler

      getSavedUnmatchedFiles.mockResolvedValue({ items: [unmatchedFile('/srv/2/B.mp3')] })
      await store.initFromRootFolder(2)

      expect(firstScanHandler).not.toBeNull()
      expect(unmatchedScanHandler).toBeNull()
      getUnmatchedResults.mockResolvedValue(completed('job-1', '/srv/1/A.mp3'))
      await firstScanHandler?.({ jobId: 'job-1' })
      expect(Object.keys(store.items)).toEqual(['/srv/2/B.mp3'])
    })

    it("ignores a replaced load's late saved results", async () => {
      const { useLibraryImportStore } = await import('@/stores/libraryImport')
      const store = useLibraryImportStore()
      const firstLoad = deferred<{ items: ReturnType<typeof unmatchedFile>[] }>()
      getSavedUnmatchedFiles.mockImplementation((rootFolderId: number) =>
        rootFolderId === 1
          ? firstLoad.promise
          : Promise.resolve({ items: [unmatchedFile('/srv/2/B.mp3')] }),
      )

      const load = store.initFromRootFolder(1)
      await store.initFromRootFolder(2)
      firstLoad.resolve({ items: [unmatchedFile('/srv/1/A.mp3')] })
      await load

      expect(store.rootFolderId).toBe(2)
      expect(Object.keys(store.items)).toEqual(['/srv/2/B.mp3'])
    })

    it('keeps the matches already made in a folder it scans without loading it first', async () => {
      const { useLibraryImportStore } = await import('@/stores/libraryImport')
      const store = useLibraryImportStore()
      const match = { title: 'Book B', asin: 'B000000002' }
      localStorage.setItem(
        'listenarr-import-matches-2',
        JSON.stringify({
          '/srv/2/B.mp3': { selectedMatch: match, hasSearched: true, selected: true },
        }),
      )
      scanUnmatchedFiles.mockResolvedValue({ jobId: 'job-2' })
      getUnmatchedResults.mockResolvedValue(completed('job-2', '/srv/2/B.mp3'))

      await store.triggerScan(2)

      expect(store.items['/srv/2/B.mp3']).toMatchObject({
        selectedMatch: match,
        hasSearched: true,
        selected: true,
      })
    })

    it('keeps the saved matches when a scan of a folder that was not loaded is replaced by another scan of it', async () => {
      const { useLibraryImportStore } = await import('@/stores/libraryImport')
      const store = useLibraryImportStore()
      const match = { title: 'Book A', asin: 'B000000001' }
      localStorage.setItem(
        'listenarr-import-matches-1',
        JSON.stringify({
          '/srv/1/A.mp3': { selectedMatch: match, hasSearched: true, selected: true },
        }),
      )
      getSavedUnmatchedFiles.mockResolvedValue({ items: [unmatchedFile('/srv/2/B.mp3')] })
      await store.initFromRootFolder(2)
      const firstCheck = deferred<ReturnType<typeof completed>>()
      let jobCount = 0
      scanUnmatchedFiles.mockImplementation(async () => ({ jobId: `job-${++jobCount}` }))
      getUnmatchedResults.mockImplementation((jobId: string) =>
        jobId === 'job-1' ? firstCheck.promise : Promise.resolve(completed(jobId, '/srv/1/A.mp3')),
      )

      const firstScan = store.triggerScan(1)
      await vi.waitFor(() => expect(getUnmatchedResults).toHaveBeenCalledWith('job-1'))
      await store.triggerScan(1)
      firstCheck.resolve(completed('job-1', '/srv/1/A.mp3'))
      await firstScan

      expect(store.items['/srv/1/A.mp3']).toMatchObject({ selectedMatch: match, selected: true })
      expect(JSON.parse(localStorage.getItem('listenarr-import-matches-1') ?? '{}')).toMatchObject({
        '/srv/1/A.mp3': { selectedMatch: match, selected: true },
      })
    })

    it('stops Start Matching when another folder is scanned, and drops the late answer of a row it no longer shows', async () => {
      const { useLibraryImportStore } = await import('@/stores/libraryImport')
      const store = useLibraryImportStore()
      localStorage.clear()
      getSavedUnmatchedFiles.mockResolvedValue({
        items: [unmatchedFile('/srv/5/B1.mp3'), unmatchedFile('/srv/5/B2.mp3')],
      })
      await store.initFromRootFolder(5)
      const firstSearch = deferred<SearchResult[]>()
      advancedSearch.mockReturnValueOnce(firstSearch.promise)
      scanUnmatchedFiles.mockResolvedValue({ jobId: 'job-4' })
      getUnmatchedResults.mockResolvedValue(completed('job-4', '/srv/4/A.mp3'))

      store.startProcessing()
      await vi.waitFor(() => expect(advancedSearch).toHaveBeenCalledTimes(1))
      await store.triggerScan(4)
      firstSearch.resolve([{ title: 'Book B1' } as SearchResult])
      await new Promise((resolve) => setTimeout(resolve, 0))

      expect(Object.keys(store.items)).toEqual(['/srv/4/A.mp3'])
      expect(store.isProcessing).toBe(false)
      expect(advancedSearch).toHaveBeenCalledTimes(1)
      expect(
        Object.keys(JSON.parse(localStorage.getItem('listenarr-import-matches-4') ?? '{}')),
      ).toEqual(['/srv/4/A.mp3'])
    })

    it('keeps Start Matching going when the folder it matches is scanned again', async () => {
      const { useLibraryImportStore } = await import('@/stores/libraryImport')
      const store = useLibraryImportStore()
      localStorage.clear()
      getSavedUnmatchedFiles.mockResolvedValue({
        items: [unmatchedFile('/srv/5/B1.mp3'), unmatchedFile('/srv/5/B2.mp3')],
      })
      await store.initFromRootFolder(5)
      const firstSearch = deferred<SearchResult[]>()
      advancedSearch.mockReturnValueOnce(firstSearch.promise)
      scanUnmatchedFiles.mockResolvedValue({ jobId: 'job-5' })
      getUnmatchedResults.mockResolvedValue({
        ...completed('job-5', '/srv/5/B1.mp3'),
        items: [unmatchedFile('/srv/5/B1.mp3'), unmatchedFile('/srv/5/B2.mp3')],
      })

      store.startProcessing()
      await vi.waitFor(() => expect(advancedSearch).toHaveBeenCalledTimes(1))
      await store.triggerScan(5)
      firstSearch.resolve([{ title: 'Book B1' } as SearchResult])

      await vi.waitFor(() => expect(store.isProcessing).toBe(false))
      expect(advancedSearch).toHaveBeenCalledTimes(2)
      expect(store.items['/srv/5/B1.mp3']?.selectedMatch?.title).toBe('Book B1')
    })

    it('does not let a stopped Start Matching run end the run started after it', async () => {
      const { useLibraryImportStore } = await import('@/stores/libraryImport')
      const store = useLibraryImportStore()
      localStorage.clear()
      getSavedUnmatchedFiles.mockResolvedValue({ items: [unmatchedFile('/srv/5/B1.mp3')] })
      await store.initFromRootFolder(5)
      const stoppedSearch = deferred<SearchResult[]>()
      const nextSearch = deferred<SearchResult[]>()
      advancedSearch
        .mockReturnValueOnce(stoppedSearch.promise)
        .mockReturnValueOnce(nextSearch.promise)

      store.startProcessing()
      await vi.waitFor(() => expect(advancedSearch).toHaveBeenCalledTimes(1))
      store.stopProcessing()
      store.startProcessing()
      await vi.waitFor(() => expect(advancedSearch).toHaveBeenCalledTimes(2))
      stoppedSearch.resolve([])
      await new Promise((resolve) => setTimeout(resolve, 0))
      expect(store.isProcessing).toBe(true)

      nextSearch.resolve([{ title: 'Book B1' } as SearchResult])
      await vi.waitFor(() => expect(store.isProcessing).toBe(false))
    })

    it("does not show another folder's rows while scanning a new one", async () => {
      const { useLibraryImportStore } = await import('@/stores/libraryImport')
      const store = useLibraryImportStore()
      getSavedUnmatchedFiles.mockResolvedValue({ items: [unmatchedFile('/srv/1/A.mp3')] })
      await store.initFromRootFolder(1)
      scanUnmatchedFiles.mockRejectedValue(new Error('offline'))

      await store.triggerScan(2)

      expect(store.scanStatus).toBe('error')
      expect(store.itemList).toEqual([])
    })
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
        jobId,
        status: 'Completed',
        error: null,
        warnings: ['One path could not be read and was skipped.'],
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
    expect(store.scanWarnings).toEqual(['One path could not be read and was skipped.'])
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
    await vi.waitFor(() => expect(store.isProcessing).toBe(false))

    expect(advancedSearch).toHaveBeenCalledWith({
      title: 'Jack of Shadows',
      author: 'Roger Zelazny',
      cap: 5,
      region: 'fr',
    })
    expect(store.items['C:\\incoming\\Chapter 01.mp3']?.selectedMatch?.title).toBe(
      'Jack of Shadows',
    )
  })

  function unsearchedItem(id: string, detectedAsin?: string) {
    return {
      id,
      fullPath: id,
      sourceFiles: [id],
      folderPath: '/incoming/AlterWorld',
      relativePath: 'AlterWorld',
      folderName: 'AlterWorld',
      detectedTitle: 'AlterWorld',
      detectedAsin,
      format: 'MP3',
      fileCount: 1,
      selectedMatch: null,
      hasSearched: false,
      isSearching: false,
      selected: false,
    }
  }

  it('matches in the configured region and keeps that region on the match', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()
    advancedSearch.mockResolvedValue([{ title: 'AlterWorld', asin: 'B0H9MD3W8Q', authors: [] }])
    store.items = {
      '/incoming/AlterWorld/01.mp3': unsearchedItem('/incoming/AlterWorld/01.mp3', 'B0H9MD3W8Q'),
    }

    store.startProcessing()
    await vi.waitFor(() => expect(store.isProcessing).toBe(false))

    expect(advancedSearch).toHaveBeenCalledTimes(1)
    expect(advancedSearch).toHaveBeenCalledWith({ asin: 'B0H9MD3W8Q', cap: 5, region: 'fr' })
    expect(store.items['/incoming/AlterWorld/01.mp3']?.selectedMatch?.region).toBe('fr')
  })

  it('searches the configured region only and never switches catalogues on its own', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()
    advancedSearch.mockResolvedValue([])
    store.items = { '/incoming/AlterWorld/01.mp3': unsearchedItem('/incoming/AlterWorld/01.mp3') }

    store.startProcessing()
    await vi.waitFor(() => expect(store.isProcessing).toBe(false))

    expect(advancedSearch).toHaveBeenCalledTimes(1)
    expect(advancedSearch).toHaveBeenCalledWith(expect.objectContaining({ region: 'fr' }))
    expect(store.items['/incoming/AlterWorld/01.mp3']?.selectedMatch).toBeNull()
  })

  it('treats empty "Unknown Title" stubs as no result', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()
    advancedSearch.mockResolvedValue([{ asin: 'B0BK9W82PQ', authors: [] }])
    store.items = {
      '/incoming/PH4/01.mp3': unsearchedItem('/incoming/PH4/01.mp3', 'B0BK9W82PQ'),
    }

    store.startProcessing()
    await vi.waitFor(() => expect(store.isProcessing).toBe(false))

    expect(store.items['/incoming/PH4/01.mp3']?.selectedMatch).toBeNull()
  })

  it('searches the region the user picked and tags the results with it', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()
    advancedSearch.mockResolvedValue([
      { title: 'Primal Hunter 4', asin: 'B0BK9W82PQ', authors: [] },
      { asin: 'B0ZZZZZZZZ' },
    ])

    const results = await store.searchInRegion({ title: 'The Primal Hunter 4', cap: 5 }, 'us')

    expect(advancedSearch).toHaveBeenCalledWith({
      title: 'The Primal Hunter 4',
      cap: 5,
      region: 'us',
    })
    expect(results).toEqual([expect.objectContaining({ asin: 'B0BK9W82PQ', region: 'us' })])
  })

  it('keeps the configured region for a saved match that has none', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()
    getAudibleMetadata.mockResolvedValue({ authors: [{ name: 'D. Rus' }], narrators: [] })
    store.items = {
      '/incoming/AlterWorld/01.mp3': {
        ...unsearchedItem('/incoming/AlterWorld/01.mp3'),
        // saved before regions were recorded
        selectedMatch: {
          title: 'AlterWorld',
          asin: 'B0H9MD3W8Q',
          authors: [],
        } as unknown as SearchResult,
        hasSearched: true,
        selected: true,
      },
    }
    store.action = 'none'

    await store.importSelected('/media/audiobooks')

    expect(getAudibleMetadata).toHaveBeenCalledWith('B0H9MD3W8Q', 'fr')
    expect(addToLibrary).toHaveBeenCalledWith(
      expect.objectContaining({ region: 'fr' }),
      expect.anything(),
    )
  })

  function savedMatchWithoutRegion(store: { items: unknown; action: unknown }) {
    store.items = {
      '/incoming/Princess/01.mp3': {
        ...unsearchedItem('/incoming/Princess/01.mp3'),
        // saved before regions were recorded
        selectedMatch: {
          title: 'A Princess of Mars',
          asin: 'B008DFUGCQ',
          authors: [],
        } as unknown as SearchResult,
        hasSearched: true,
        selected: true,
      },
    }
    store.action = 'none'
  }

  it.each([
    [
      'a 404',
      () =>
        getAudibleMetadata.mockRejectedValue(
          Object.assign(new Error('Not Found'), { status: 404 }),
        ),
    ],
    [
      'an empty record',
      () => getAudibleMetadata.mockResolvedValue({ asin: 'B008DFUGCQ', title: '' }),
    ],
  ])(
    'leaves a saved match unresolved, with the reason, when its region answers %s',
    async (_case, arrange) => {
      const { useLibraryImportStore } = await import('@/stores/libraryImport')
      const store = useLibraryImportStore()
      arrange()
      savedMatchWithoutRegion(store)

      const { imported, errors } = await store.importSelected('/media/audiobooks')

      expect(getAudibleMetadata).toHaveBeenCalledWith('B008DFUGCQ', 'fr')
      expect(addToLibrary).not.toHaveBeenCalled()
      expect(startManualImport).not.toHaveBeenCalled()
      expect(imported).toBe(0)
      expect(errors).toHaveLength(1)
      const row = store.items['/incoming/Princess/01.mp3']
      expect(row?.selectedMatch).toBeNull()
      expect(row?.selected).toBe(false)
      expect(row?.matchError).toContain('B008DFUGCQ')
      expect(row?.matchError).toContain('Search for a match')
    },
  )

  it('keeps the match but does not import when the metadata lookup fails for another reason', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()
    getAudibleMetadata.mockRejectedValue(
      Object.assign(new Error('Too Many Requests'), { status: 429 }),
    )
    savedMatchWithoutRegion(store)

    const { imported, errors } = await store.importSelected('/media/audiobooks')

    expect(addToLibrary).not.toHaveBeenCalled()
    expect(imported).toBe(0)
    expect(errors[0]).toContain('Too Many Requests')
    const row = store.items['/incoming/Princess/01.mp3']
    expect(row?.selectedMatch?.asin).toBe('B008DFUGCQ')
    expect(row?.matchError).toBeUndefined()
  })

  it('keeps a match picked while the failing lookup of the previous one was pending', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()
    let answerFrench: (error: Error) => void = () => {}
    getAudibleMetadata.mockImplementation(
      () => new Promise((_resolve, reject) => (answerFrench = reject)),
    )
    savedMatchWithoutRegion(store)

    const importing = store.importSelected('/media/audiobooks')
    await vi.waitFor(() => expect(getAudibleMetadata).toHaveBeenCalledWith('B008DFUGCQ', 'fr'))
    // The user picks the US record before the French lookup answers
    store.selectMatch('/incoming/Princess/01.mp3', {
      title: 'A Princess of Mars',
      asin: 'B008DFUGCQ',
      region: 'us',
    } as unknown as SearchResult)
    answerFrench(Object.assign(new Error('Not Found'), { status: 404 }))
    await importing

    const row = store.items['/incoming/Princess/01.mp3']
    expect(row?.selectedMatch?.region).toBe('us')
    expect(row?.matchError).toBeUndefined()
  })

  it('clears the reason once a new match is picked', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()
    getAudibleMetadata.mockRejectedValue(Object.assign(new Error('Not Found'), { status: 404 }))
    savedMatchWithoutRegion(store)
    await store.importSelected('/media/audiobooks')

    store.selectMatch('/incoming/Princess/01.mp3', {
      title: 'A Princess of Mars',
      asin: 'B008DFUGCQ',
      region: 'us',
    } as unknown as SearchResult)

    const row = store.items['/incoming/Princess/01.mp3']
    expect(row?.matchError).toBeUndefined()
    expect(row?.selectedMatch?.region).toBe('us')
  })

  it('fetches full metadata from the region the match came from', async () => {
    const { useLibraryImportStore } = await import('@/stores/libraryImport')
    const store = useLibraryImportStore()
    getAudibleMetadata.mockResolvedValue({ authors: [{ name: 'D. Rus' }], narrators: [] })
    store.items = {
      '/incoming/AlterWorld/01.mp3': {
        ...unsearchedItem('/incoming/AlterWorld/01.mp3'),
        selectedMatch: {
          title: 'AlterWorld',
          asin: 'B0H9MD3W8Q',
          authors: [],
          region: 'fr',
        } as unknown as SearchResult,
        hasSearched: true,
        selected: true,
      },
    }
    store.action = 'none'

    await store.importSelected('/media/audiobooks')

    expect(getAudibleMetadata).toHaveBeenCalledWith('B0H9MD3W8Q', 'fr')
    expect(addToLibrary).toHaveBeenCalledWith(
      expect.objectContaining({ authors: ['D. Rus'], region: 'fr' }),
      expect.anything(),
    )
  })

  describe('editions beyond Audible search', () => {
    const planer = {
      source: 'Audiosilo',
      title: 'Guards! Guards!',
      asin: 'B004FU8XPA',
      region: 'uk',
      authors: [{ name: 'Terry Pratchett' }],
      narrators: [{ name: 'Nigel Planer' }],
      lengthMinutes: 608,
    } as unknown as SearchResult

    it('matches the edition whose runtime equals the file, including one only the community catalogue lists', async () => {
      const { useLibraryImportStore } = await import('@/stores/libraryImport')
      const store = useLibraryImportStore()
      store.items = {
        '/incoming/Guards/01.mp3': {
          ...unsearchedItem('/incoming/Guards/01.mp3'),
          detectedTitle: 'Guards! Guards! (Unabridged)',
          detectedAuthor: 'Terry Pratchett',
          durationSeconds: 608 * 60,
        },
      }
      advancedSearch.mockResolvedValue([
        { title: 'Guards! Guards!', asin: 'B09M8W9JY5', lengthMinutes: 809, authors: [{ name: 'Terry Pratchett' }] },
        { title: 'Guards! Guards!', asin: 'B004EXIHDQ', lengthMinutes: 194, authors: [{ name: 'Terry Pratchett' }] },
      ] as unknown as SearchResult[])
      searchEditions.mockResolvedValue([planer])

      store.startProcessing()
      await vi.waitFor(() => expect(store.isProcessing).toBe(false))

      expect(searchEditions).toHaveBeenCalledWith('Guards! Guards!', 'Terry Pratchett', 'fr')
      expect(store.items['/incoming/Guards/01.mp3']?.selectedMatch?.asin).toBe('B004FU8XPA')
    })

    it('imports a community-catalogue edition from its own record when Audible no longer serves it', async () => {
      const { useLibraryImportStore } = await import('@/stores/libraryImport')
      const store = useLibraryImportStore()
      getAudibleMetadata.mockRejectedValue(Object.assign(new Error('Not Found'), { status: 404 }))
      startManualImport.mockResolvedValueOnce({
        importedCount: 1,
        totalCount: 1,
        results: [{ success: true, sourcePath: '/incoming/Guards/01.mp3' }],
      })
      store.items = {
        '/incoming/Guards/01.mp3': {
          ...unsearchedItem('/incoming/Guards/01.mp3'),
          selectedMatch: planer,
          hasSearched: true,
          selected: true,
        },
      }
      store.action = 'none'

      const result = await store.importSelected('/media/audiobooks')

      expect(result.imported).toBe(1)
      expect(addToLibrary).toHaveBeenCalledWith(
        expect.objectContaining({ asin: 'B004FU8XPA', narrators: ['Nigel Planer'], runtime: 608 * 60 }),
        expect.anything(),
      )
    })

    it('still refuses an Audible match whose record is gone', async () => {
      const { useLibraryImportStore } = await import('@/stores/libraryImport')
      const store = useLibraryImportStore()
      getAudibleMetadata.mockRejectedValue(Object.assign(new Error('Not Found'), { status: 404 }))
      store.items = {
        '/incoming/Guards/01.mp3': {
          ...unsearchedItem('/incoming/Guards/01.mp3'),
          selectedMatch: { ...planer, source: 'Audible' } as SearchResult,
          hasSearched: true,
          selected: true,
        },
      }
      store.action = 'none'

      const result = await store.importSelected('/media/audiobooks')

      expect(result.imported).toBe(0)
      expect(addToLibrary).not.toHaveBeenCalled()
    })
  })
})
