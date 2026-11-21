import type { Page } from '@playwright/test'
import {
  expect,
  libraryImportSearchResultFixture,
  libraryImportUnmatchedFixture,
  rootFolderFixture,
  settingsFixture,
  test,
  type ApiStubs,
} from '../support/test.js'

const rootFolderId = 1

async function openLibraryImport(
  page: Page,
  api: ApiStubs,
  savedItems: ReturnType<typeof libraryImportUnmatchedFixture>[] = [],
) {
  await api.stubLibraryImportSavedResults(rootFolderId, savedItems)
  await page.goto('/library-import')
  await api.wait('getRootFolders')
  await api.wait('getSettings')
  await api.wait('savedLibraryImportResults')
}

const startMatching = (page: Page) => page.locator('.footer-center button', { hasText: 'Start Matching' })

test.describe('Library Import browser behavior (API-stubbed)', () => {
  test.beforeEach(async ({ api }) => {
    await api.stubRootFolders([
      rootFolderFixture({
        id: rootFolderId,
        name: 'Incoming',
        path: '/incoming',
        isDefault: true,
        canScanFilesystem: true,
      }),
    ])
    await api.stubLibrary([])
  })

  test('scans unmatched rows, searches each in turn, and continues after a failed search', async ({
    page,
    api,
  }) => {
    const firstItem = libraryImportUnmatchedFixture({
      fullPath: '/incoming/Author One/First Book/First Book.m4b',
      relativePath: 'Author One/First Book',
      bookFolder: '/incoming/Author One/First Book',
      title: 'First Book',
      author: 'Author One',
    })
    const secondItem = libraryImportUnmatchedFixture({
      fullPath: '/incoming/Author Two/Second Book/Second Book.m4b',
      relativePath: 'Author Two/Second Book',
      bookFolder: '/incoming/Author Two/Second Book',
      title: 'Second Book',
      author: 'Author Two',
    })
    await openLibraryImport(page, api)
    await api.stubLibraryImportScan(rootFolderId, [firstItem, secondItem])

    await page.locator('button', { hasText: 'Scan' }).first().click()
    await api.wait('startLibraryImportScan')
    await api.wait('getLibraryImportScanResults')
    await expect(page.locator('.import-row')).toHaveCount(2)
    await expect(page.locator('.import-row', { hasText: 'First Book' })).toBeVisible()
    await expect(page.locator('.import-row', { hasText: 'Second Book' })).toBeVisible()

    const selectedMatch = libraryImportSearchResultFixture({
      asin: 'B000000002',
      id: 'B000000002',
      title: 'Second Book Audible Edition',
      authors: [{ name: 'Author Two' }],
    })
    const otherAuthorResult = libraryImportSearchResultFixture({
      asin: 'B000000003',
      id: 'B000000003',
      title: 'Wrong Author Edition',
      authors: [{ name: 'Another Author' }],
    })
    let searchCount = 0
    await api.stubSearch(() => {
      searchCount += 1
      if (searchCount === 1) {
        return { statusCode: 500, body: { message: 'Search unavailable' } }
      }
      return { statusCode: 200, body: [otherAuthorResult, selectedMatch] }
    }, 'libraryImportSearch')

    await startMatching(page).click()
    expect((await api.wait('libraryImportSearch')).request.body).toEqual({
      mode: 'Advanced',
      title: 'First Book',
      author: 'Author One',
      region: 'us',
      cap: 5,
    })
    expect((await api.wait('libraryImportSearch')).request.body).toEqual({
      mode: 'Advanced',
      title: 'Second Book',
      author: 'Author Two',
      region: 'us',
      cap: 5,
    })

    await expect(page.locator('.import-row').nth(0)).toContainText('No match found')
    await expect(page.locator('.import-row', { hasText: 'Second Book' })).toContainText(
      'Second Book Audible Edition',
    )
    await expect(page.locator('.import-row').nth(1).locator('input[type="checkbox"]')).toBeChecked()
    await expect(page.locator('.import-row').nth(0).locator('input[type="checkbox"]')).toBeDisabled()
  })

  test('restores selected matches after reloading the page', async ({ page, api }) => {
    const item = libraryImportUnmatchedFixture({
      fullPath: '/incoming/Author/Reload Book/Reload Book.m4b',
      relativePath: 'Author/Reload Book',
      bookFolder: '/incoming/Author/Reload Book',
      title: 'Reload Book',
      author: 'Author',
    })
    const match = libraryImportSearchResultFixture({
      asin: 'B000000010',
      id: 'B000000010',
      title: 'Reload Book Match',
      authors: [{ name: 'Author' }],
    })
    await api.stubSearch(() => ({ statusCode: 200, body: [match] }), 'libraryImportSearch')
    await openLibraryImport(page, api, [item])

    await startMatching(page).click()
    await api.wait('libraryImportSearch')
    await expect(page.locator('.import-row', { hasText: 'Reload Book Match' })).toBeVisible()
    await expect(page.locator('.import-row input[type="checkbox"]')).toBeChecked()

    await page.reload()
    await api.wait('getRootFolders')
    await api.wait('getSettings')
    await api.wait('savedLibraryImportResults')
    await expect(page.locator('.import-row', { hasText: 'Reload Book Match' })).toBeVisible()
    await expect(page.locator('.import-row input[type="checkbox"]')).toBeChecked()
    await expect(startMatching(page)).toHaveCount(0)
  })

  test('stops queued searches until Start Matching is explicitly restarted', async ({ page, api }) => {
    const items = ['First', 'Second', 'Third'].map((title, index) =>
      libraryImportUnmatchedFixture({
        fullPath: `/incoming/Author ${index + 1}/${title} Book/${title} Book.m4b`,
        relativePath: `Author ${index + 1}/${title} Book`,
        bookFolder: `/incoming/Author ${index + 1}/${title} Book`,
        title: `${title} Book`,
        author: `Author ${index + 1}`,
      }),
    )
    const match = libraryImportSearchResultFixture({
      asin: 'B000000020',
      id: 'B000000020',
      title: 'Matched Book',
    })
    let searchCount = 0
    await api.stubSearch(() => {
      searchCount += 1
      return {
        statusCode: 200,
        body: [match],
        ...(searchCount === 1 ? { delay: 800 } : {}),
      }
    }, 'libraryImportSearch')
    await openLibraryImport(page, api, items)

    await startMatching(page).click()
    await page.locator('.footer-center').locator('button', { hasText: 'Cancel' }).click()
    await api.wait('libraryImportSearch')
    await expect(page.locator('.import-row .match-status.unsearched')).toHaveCount(2)
    await expect.poll(() => searchCount).toBe(1)

    await startMatching(page).click()
    await api.wait('libraryImportSearch')
    await api.wait('libraryImportSearch')
    await expect.poll(() => searchCount).toBe(3)
    await expect(page.locator('.import-row .match-title')).toHaveCount(3)
    await expect(page.locator('.import-row .match-title', { hasText: 'Matched Book' })).not.toHaveCount(0)
  })

  test('uses scanned tags and the configured region for Find Match, then applies the picked result', async ({
    page,
    api,
  }) => {
    const item = libraryImportUnmatchedFixture({
      fullPath: '/incoming/Scanned Author/Scanned Folder/Scanned Title.m4b',
      relativePath: 'Scanned Author/Scanned Folder',
      bookFolder: '/incoming/Scanned Author/Scanned Folder',
      title: 'Scanned Title',
      author: 'Scanned Author',
      asin: 'B000000101',
    })
    const match = libraryImportSearchResultFixture({
      asin: 'B000000101',
      id: 'B000000101',
      title: 'Selected Audible Title',
      authors: [{ name: 'Selected Author' }],
    })
    await api.stubMetadata(
      {
        statusCode: 200,
        body: {
          metadata: {
            asin: match.asin,
            title: match.title,
            authors: match.authors,
          },
        },
      },
      'findMatchAsinMetadata',
    )
    await openLibraryImport(page, api, [item])

    await page.locator('.import-row').locator('button[title="Search for a match"]').click()
    await expect(page.locator('[data-testid="match-title"]')).toHaveValue('Scanned Title')
    await expect(page.locator('[data-testid="match-author"]')).toHaveValue('Scanned Author')
    await expect(page.locator('[data-testid="match-asin"]')).toHaveValue('B000000101')
    await expect(page.locator('[data-testid="match-region"]')).toHaveValue('us')
    expect((await api.wait('findMatchAsinMetadata')).request.url).toContain('/metadata/B000000101?region=us')
    await page.locator('[data-testid="find-match-result"]', { hasText: 'Selected Audible Title' }).click()
    const confirm = page.locator('[data-testid="find-match-confirm"]')
    await expect(confirm).toBeEnabled()
    await confirm.click()

    await expect(page.locator('.import-row', { hasText: 'Selected Audible Title' })).toBeVisible()
    await expect(page.locator('.import-row input[type="checkbox"]')).toBeChecked()
  })

  test('keeps scan tags ready when the scanned ASIN has no record in the configured region', async ({
    page,
    api,
  }) => {
    const item = libraryImportUnmatchedFixture({
      fullPath: '/incoming/Scanned Author/Scanned Folder/Scanned Title.m4b',
      relativePath: 'Scanned Author/Scanned Folder',
      bookFolder: '/incoming/Scanned Author/Scanned Folder',
      title: 'Scanned Title',
      author: 'Scanned Author',
      asin: 'B000000101',
    })
    await api.stubSettings(settingsFixture({ defaultSearchRegion: 'fr' }))
    await api.stubMetadata({ statusCode: 404, body: { message: 'No record for ASIN' } }, 'missingFrenchAsin')
    await openLibraryImport(page, api, [item])

    await page.locator('.import-row').locator('button[title="Search for a match"]').click()
    await expect(page.locator('[data-testid="match-title"]')).toHaveValue('Scanned Title')
    await expect(page.locator('[data-testid="match-author"]')).toHaveValue('Scanned Author')
    await expect(page.locator('[data-testid="match-asin"]')).toHaveValue('B000000101')
    await expect(page.locator('[data-testid="match-region"]')).toHaveValue('fr')
    expect((await api.wait('missingFrenchAsin')).request.url).toContain('?region=fr')
    await expect(page.getByText('No record for B000000101 in Audible France').first()).toBeVisible()
  })
})
