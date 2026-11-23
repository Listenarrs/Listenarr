import { expect, libraryImportUnmatchedFixture, rootFolderFixture, test } from '../support/test.js'

test.describe('Library Import footer choices (API-stubbed)', () => {
  test.beforeEach(async ({ api }) => {
    await api.stubRootFolders([
      rootFolderFixture({
        id: 1,
        name: 'Incoming',
        path: '/incoming',
        isDefault: true,
        canScanFilesystem: true,
      }),
      rootFolderFixture({ id: 2, name: 'Library', path: '/library', canScanFilesystem: true }),
    ])
    await api.stubLibrary([])
    await api.stubLibraryImportSavedResults(1, [libraryImportUnmatchedFixture()])
  })

  test('keeps On import and its destination folder after going to another page and back', async ({
    page,
    api,
  }) => {
    await page.goto('/library-import')
    await api.wait('savedLibraryImportResults')
    const action = page
      .locator('.import-footer select')
      .filter({ has: page.locator('option[value="move"]') })
    const destination = page.locator('.import-footer .destination-select')
    await expect(action).toHaveValue('none')

    await action.selectOption('move')
    await destination.selectOption('2')

    await page.locator('a.nav-item', { hasText: 'Audiobooks' }).click()
    await expect(page).toHaveURL(/\/audiobooks/)
    await expect(action).toHaveCount(0)
    await page.locator('a.nav-item', { hasText: 'Library Import' }).click()
    await expect(page).toHaveURL(/\/library-import/)

    await expect(action).toHaveValue('move')
    await expect(destination).toHaveValue('2')
  })
})
