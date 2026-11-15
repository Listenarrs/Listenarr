import { antiforgeryHeaders, fileExists } from '../support/stack.js'
import { expect, test } from '../support/write-test.js'

type RootFolder = { id: number; name: string; path: string }
type UnmatchedItem = { fullPath: string; sourceFiles?: string[]; bookFolder: string; title?: string; asin?: string }
type ScanResults = { status: 'Queued' | 'Processing' | 'Completed' | 'Failed'; error?: string; items: UnmatchedItem[] }
type ImportResult = { success: boolean; error?: string; skipReason?: string }

// The audiobook scripts/backend-integration/generate-audio-fixtures.sh generates, and the record
// the backend integration host's Audible fixtures serve for its ASIN.
const asin = 'B008DFUGCQ'
const title = 'A Princess of Mars'
const author = 'Edgar Rice Burroughs'
const sourcePath = '/backend-integration/import/Edgar Rice Burroughs/A Princess of Mars/A Princess of Mars.m4b'

test.describe('Backend integration generated audio import', () => {
  test('scans, matches, and moves a generated tagged audiobook into the library', async ({ request }) => {
    const rootFolders = (await (await request.get('/api/v1/rootfolders')).json()) as RootFolder[]
    const importFolder = rootFolders.find((folder) => folder.name === 'Backend Integration Import')
    const libraryFolder = rootFolders.find((folder) => folder.name === 'Backend Integration Library')
    if (!importFolder || !libraryFolder) throw new Error('The backend integration filesystem root folders are missing.')
    const headers = await antiforgeryHeaders(request)

    // Scan the import folder, as the Library Import page does.
    const scanResponse = await request.post(`/api/v1/rootfolders/${importFolder.id}/scan-unmatched`, { headers })
    expect(scanResponse.ok()).toBe(true)
    const { jobId } = (await scanResponse.json()) as { jobId: string }
    let scan: ScanResults | undefined
    await expect
      .poll(
        async () => {
          scan = (await (await request.get(`/api/v1/rootfolders/unmatched-results/${jobId}`)).json()) as ScanResults
          if (scan.status === 'Failed') throw new Error(`The scan failed: ${scan.error}`)
          return scan.status
        },
        { timeout: 30_000 },
      )
      .toBe('Completed')
    expect(scan!.items).toHaveLength(1)
    const item = scan!.items[0]!
    expect(item).toMatchObject({ asin, fullPath: sourcePath })

    // Look its ASIN up: the record is the match.
    const metadataResponse = await request.get(`/api/v1/metadata/${asin}`, { params: { region: 'us' } })
    expect(metadataResponse.status()).toBe(200)
    const { metadata } = (await metadataResponse.json()) as {
      metadata: { asin: string; title: string; authors: { name: string }[] }
    }
    expect(metadata.title).toBe(title)
    expect(metadata.authors[0]?.name).toBe(author)

    // Add the matched book to the library, then move its file there.
    const addResponse = await request.post('/api/v1/library/add', {
      data: {
        metadata: { asin, title, authors: [author], region: 'us', source: 'Audible' },
        monitored: true,
        autoSearch: false,
        destinationPath: libraryFolder.path,
      },
      headers,
    })
    expect(addResponse.ok(), await addResponse.text()).toBe(true)
    const { audiobook } = (await addResponse.json()) as { audiobook: { id: number } }

    const sourceFiles = item.sourceFiles ?? [item.fullPath]
    const importResponse = await request.post('/api/v1/library/manual-import', {
      data: {
        path: item.bookFolder,
        mode: 'interactive',
        action: 'move',
        includeCompanionFiles: true,
        cleanupEmptySourceFolders: true,
        items: sourceFiles.map((fullPath) => ({ fullPath, matchedAudiobookId: audiobook.id })),
      },
      headers,
    })
    expect(importResponse.status(), await importResponse.text()).toBe(200)
    const importResult = (await importResponse.json()) as { importedCount: number; results: ImportResult[] }
    expect(importResult.results.filter((result) => !result.success)).toEqual([])
    expect(importResult.importedCount).toBe(sourceFiles.length)

    // The library record points at the moved file, and the source is gone.
    const books = (await (await request.get('/api/v1/library')).json()) as { id: number; asin?: string }[]
    expect(books).toHaveLength(5)
    expect(books.find((book) => book.asin === asin)?.id).toBe(audiobook.id)
    const savedBook = (await (await request.get(`/api/v1/library/${audiobook.id}`)).json()) as {
      basePath?: string
      filePath?: string
      files?: { path?: string }[]
    }
    const storedPath = savedBook.files?.[0]?.path ?? savedBook.filePath ?? ''
    const destinationPath = storedPath.startsWith('/') ? storedPath : `${savedBook.basePath}/${storedPath}`
    expect(destinationPath).toContain('/backend-integration/library/')
    expect(await fileExists(destinationPath)).toBe(true)
    expect(await fileExists(sourcePath)).toBe(false)
  })
})
