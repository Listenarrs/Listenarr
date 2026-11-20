import {
  audiobookFixture,
  expect,
  libraryImportUnmatchedFixture,
  rootFolderFixture,
  settingsFixture,
  test,
} from '../support/test.js'

const rootFolderId = 1
const audiobookId = 301
const asin = 'B000000301'

test.describe('Library Import apply flow (API-stubbed)', () => {
  test.beforeEach(async ({ api }) => {
    await api.stubRootFolders([
      rootFolderFixture({
        id: rootFolderId,
        name: 'Library',
        path: '/library',
        isDefault: true,
        canScanFilesystem: true,
      }),
    ])
    await api.stubSettings(settingsFixture({ outputPath: '/library', defaultSearchRegion: 'us' }))
    await api.stubLibrary([])

    const item = libraryImportUnmatchedFixture({
      fullPath: '/incoming/Edgar Rice Burroughs/Barsoom/A Princess of Mars.m4b',
      sourceFiles: ['/incoming/Edgar Rice Burroughs/Barsoom/A Princess of Mars.m4b'],
      relativePath: 'Edgar Rice Burroughs/Barsoom',
      bookFolder: '/incoming/Edgar Rice Burroughs/Barsoom',
      title: 'A Princess of Mars',
      author: 'Edgar Rice Burroughs',
      asin,
    })
    await api.stubLibraryImportSavedResults(rootFolderId, [item])
    await api.stubMetadata(
      {
        statusCode: 200,
        body: {
          source: 'audible',
          metadata: {
            asin,
            title: 'A Princess of Mars',
            authors: [{ name: 'Edgar Rice Burroughs' }],
            series: [{ name: 'Barsoom', sequence: 1 }],
          },
        },
      },
      'princessMetadata',
    )
    await api.stubAddLibrary(
      audiobookFixture({
        id: audiobookId,
        title: 'A Princess of Mars',
        authors: ['Edgar Rice Burroughs'],
        asin,
      }),
    )
    await api.stubManualImport({
      importedCount: 1,
      totalCount: 1,
      results: [{ success: true }],
    })
  })

  test('adds the matched book and moves its source files into the selected library folder', async ({
    page,
    api,
  }) => {
    await page.goto('/library-import')
    await api.wait('getRootFolders')
    await api.wait('getSettings')
    await api.wait('savedLibraryImportResults')

    await page.locator('.import-row').locator('button[title="Search for a match"]').click()
    await api.wait('princessMetadata')
    await page.locator('[data-testid="find-match-result"]', { hasText: 'A Princess of Mars' }).first().click()
    const confirm = page.locator('[data-testid="find-match-confirm"]')
    await expect(confirm).toBeEnabled()
    await confirm.click()
    await expect(page.locator('.import-row input[type="checkbox"]')).toBeChecked()

    await page.locator('.import-footer select').nth(1).selectOption('move')
    await page.locator('.footer-right button', { hasText: 'Import 1 Book' }).first().click()

    await api.wait('princessMetadata')
    const added = await api.wait('addAudiobookToLibrary')
    expect(added.request.body).toMatchObject({
      monitored: true,
      autoSearch: false,
      destinationPath: '/library',
    })
    expect(added.request.body.searchResult).toMatchObject({ asin, title: 'A Princess of Mars', region: 'us' })
    const imported = await api.wait('importManualFiles')
    expect(imported.request.body).toEqual({
      path: '/incoming/Edgar Rice Burroughs/Barsoom',
      mode: 'interactive',
      action: 'move',
      includeCompanionFiles: true,
      cleanupEmptySourceFolders: true,
      items: [
        {
          fullPath: '/incoming/Edgar Rice Burroughs/Barsoom/A Princess of Mars.m4b',
          matchedAudiobookId: audiobookId,
        },
      ],
    })
    await expect(page.getByText('Import complete').first()).toBeVisible()
  })
})
