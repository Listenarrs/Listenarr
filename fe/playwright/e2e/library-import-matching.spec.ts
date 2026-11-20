import type { Page } from '@playwright/test'
import {
  expect,
  importRow,
  openLibraryImport,
  scan,
  startMatchingButton,
  test,
} from './support/test.js'

const matchTitle = (page: Page, book: string) => importRow(page, book).locator('.match-title')
const rowCheckbox = (page: Page, book: string) =>
  importRow(page, book).locator('input[type="checkbox"]')
const findMatch = (page: Page, book: string) =>
  importRow(page, book).locator('button[title="Search for a match"]').click()
const findMatchResult = (page: Page, title: string) =>
  page.getByTestId('find-match-result').filter({ hasText: title })

/** Holds the app's search requests until `release()`, so a test can act while Start Matching runs. */
async function holdSearches(page: Page) {
  let release!: () => void
  const released = new Promise<void>((resolveRelease) => {
    release = resolveRelease
  })
  await page.route('**/api/v1/search', async (route) => {
    await released
    await route.continue()
  })
  return release
}

test.describe('Library Import matching', () => {
  test('Start Matching finds each book by ASIN or by title and author, and flags the ones it cannot', async ({
    page,
  }) => {
    await openLibraryImport(page)
    await scan(page)
    await startMatchingButton(page).click()
    await expect(startMatchingButton(page)).toHaveCount(0, { timeout: 60_000 })
    await expect(page.locator('.processing-label')).toHaveCount(0, { timeout: 60_000 })

    // By the tagged ASIN.
    await expect(matchTitle(page, 'A Princess of Mars')).toHaveText('A Princess of Mars')
    await expect(matchTitle(page, 'A Princess of Mars')).toHaveAttribute(
      'title',
      'ASIN: B008DFUGCQ',
    )
    await expect(matchTitle(page, 'Twenty Thousand Leagues Under the Sea')).toHaveText(
      'Twenty Thousand Leagues Under the Sea',
    )
    // By title and author: the result by the book's author wins over the first result.
    await expect(matchTitle(page, 'Tanar of Pellucidar')).toHaveText('Tanar of Pellucidar')
    await expect(importRow(page, 'Tanar of Pellucidar').locator('.match-author')).toHaveText(
      'Edgar Rice Burroughs',
    )
    await expect(importRow(page, 'Tanar of Pellucidar').locator('.match-author')).not.toHaveClass(
      /author-mismatch/,
    )
    await expect(matchTitle(page, 'Allan Quatermain')).toHaveText('Allan Quatermain')
    // No tags, and a folder name Audible doesn't know.
    await expect(importRow(page, 'Pellucidar').locator('.match-status.no-match')).toHaveText(
      /No match found/,
    )
    await expect(rowCheckbox(page, 'Pellucidar')).toBeDisabled()

    // Matched books are selected; the header box selects or clears all of them.
    for (const book of [
      'A Princess of Mars',
      'Tanar of Pellucidar',
      'Allan Quatermain',
      'Twenty Thousand Leagues Under the Sea',
    ]) {
      await expect(rowCheckbox(page, book)).toBeChecked()
    }
    await expect(page.locator('.selected-label')).toHaveText('4 selected')
    const selectAll = page.locator('thead input[type="checkbox"]')
    await expect(selectAll).toBeChecked()
    await selectAll.uncheck()
    await expect(page.locator('.selected-label')).toHaveCount(0)
    await expect(page.locator('.footer-right button')).toBeDisabled()
    await rowCheckbox(page, 'Tanar of Pellucidar').check()
    await expect(selectAll).toHaveJSProperty('indeterminate', true)
    await selectAll.check()
    await expect(page.locator('.selected-label')).toHaveText('4 selected')
    await expect(page.locator('.footer-right button')).toHaveText('Import 4 Books')

    // Clearing a match unselects its book.
    await importRow(page, 'Allan Quatermain').locator('button[title="Clear match"]').click()
    await expect(
      importRow(page, 'Allan Quatermain').locator('.match-status.no-match'),
    ).toBeVisible()
    await expect(page.locator('.footer-right button')).toHaveText('Import 3 Books')

    // The matches and selections stay while the app shows another page, and after a reload; nothing is
    // left to match.
    await page.locator('a[href="/wanted"]').first().click()
    await expect(page).toHaveURL(/\/wanted$/)
    await page.locator('a[href="/library-import"]').first().click()
    await expect(matchTitle(page, 'Tanar of Pellucidar')).toHaveText('Tanar of Pellucidar')
    await expect(page.locator('.footer-right button')).toHaveText('Import 3 Books')
    await page.reload()
    await expect(matchTitle(page, 'Tanar of Pellucidar')).toHaveText('Tanar of Pellucidar')
    await expect(
      importRow(page, 'Allan Quatermain').locator('.match-status.no-match'),
    ).toBeVisible()
    await expect(page.locator('.footer-right button')).toHaveText('Import 3 Books')
    await expect(startMatchingButton(page)).toHaveCount(0)
  })

  test('Cancel stops Start Matching after the search in flight, and Start Matching resumes it', async ({
    page,
  }) => {
    await openLibraryImport(page)
    await scan(page)
    const release = await holdSearches(page)

    await startMatchingButton(page).click()
    await expect(page.locator('.processing-label')).toHaveText(/Processing 0 \/ 5\.\.\./)
    await expect(page.locator('.import-row .match-status.searching')).toHaveCount(1)
    await expect(page.locator('.footer-right button')).toBeDisabled()
    await page.locator('.footer-center button', { hasText: 'Cancel' }).click()
    await expect(page.locator('.import-row .match-status.searching')).toHaveCount(0)
    release()
    await expect(startMatchingButton(page)).toBeVisible()
    await expect(page.locator('.import-row .match-status.unsearched')).toHaveCount(4)

    await page.unroute('**/api/v1/search')
    await startMatchingButton(page).click()
    await expect(page.locator('.import-row .match-status.unsearched')).toHaveCount(0, {
      timeout: 60_000,
    })
    await expect(page.locator('.import-row .match-title')).toHaveCount(4)
  })

  test('a search that fails leaves its book unmatched and Start Matching goes on', async ({
    page,
  }) => {
    await openLibraryImport(page)
    await scan(page)
    // The search for Allan Quatermain never reaches the app.
    await page.route('**/api/v1/search', async (route) => {
      if (route.request().postData()?.includes('Allan Quatermain'))
        await route.abort('connectionreset')
      else await route.continue()
    })
    await startMatchingButton(page).click()
    await expect(page.locator('.processing-label')).toHaveCount(0, { timeout: 60_000 })
    await expect(importRow(page, 'Allan Quatermain').locator('.match-status.no-match')).toHaveText(
      /No match found/,
    )
    await expect(page.locator('.import-row .match-title')).toHaveCount(3)
  })

  test('back on the page while Start Matching runs, the page shows the folder being matched', async ({
    page,
  }) => {
    await openLibraryImport(page)
    await page.locator('.folder-select').selectOption({ label: 'E2E Second' })
    await scan(page)
    const release = await holdSearches(page)
    await startMatchingButton(page).click()
    await expect(page.locator('.processing-label')).toBeVisible()

    await page.locator('a[href="/wanted"]').first().click()
    await expect(page).toHaveURL(/\/wanted$/)
    await page.locator('a[href="/library-import"]').first().click()
    await expect(page.locator('.folder-select option:checked')).toHaveText('E2E Second')
    await expect(page.locator('.processing-label')).toBeVisible()
    release()
    await expect(matchTitle(page, 'Tarzan of the Apes')).toHaveText('Tarzan of the Apes')
  })

  test('Find Match searches what the scan found, and the picked record becomes the match', async ({
    page,
  }) => {
    await openLibraryImport(page)
    await scan(page)

    // A book without tags: its folder name, which finds nothing; a search by title and author does.
    await findMatch(page, 'Pellucidar')
    const dialog = page.getByTestId('find-match-card')
    await expect(page.getByTestId('match-title')).toHaveValue('Pellucidar')
    await expect(page.getByTestId('match-asin')).toHaveValue('')
    await expect(page.getByTestId('match-region')).toHaveValue('us')
    await expect(page.getByTestId('find-match-confirm')).toBeDisabled()
    await page.getByTestId('match-title').fill('Tanar of Pellucidar')
    await page.getByTestId('match-author').fill('Edgar Rice Burroughs')
    await page.getByTestId('match-search').click()
    await expect(findMatchResult(page, 'Tanar of Pellucidar (Dramatized)')).toBeVisible()
    await findMatchResult(page, 'Tanar of Pellucidar (Dramatized)').click()
    await expect(page.getByTestId('find-match-confirm')).toBeEnabled()
    await page.getByTestId('find-match-confirm').click()
    await expect(dialog).toHaveCount(0)
    await expect(matchTitle(page, 'Pellucidar')).toHaveText('Tanar of Pellucidar (Dramatized)')
    await expect(importRow(page, 'Pellucidar').locator('.match-author')).toHaveText(
      'Edgar Rice Burroughs',
    )
    await expect(rowCheckbox(page, 'Pellucidar')).toBeChecked()

    // A tagged book: its ASIN, searched on its own.
    await findMatch(page, 'A Princess of Mars')
    await expect(page.getByTestId('match-asin')).toHaveValue('B008DFUGCQ')
    await expect(page.getByTestId('match-title')).toHaveValue('Barsoom')
    await expect(page.getByTestId('match-author')).toHaveValue('Edgar Rice Burroughs')
    await findMatchResult(page, 'A Princess of Mars').click()
    await page.getByTestId('find-match-confirm').click()
    await expect(matchTitle(page, 'A Princess of Mars')).toHaveText('A Princess of Mars')

    // Its current match is tagged as such and can't be confirmed again; Cancel leaves it.
    await findMatch(page, 'A Princess of Mars')
    await expect(page.getByTestId('find-match-current')).toBeVisible()
    await findMatchResult(page, 'A Princess of Mars').click()
    await expect(page.getByTestId('find-match-confirm')).toBeDisabled()
    await page.locator('.cancel-button').click()
    await expect(dialog).toHaveCount(0)
    await expect(matchTitle(page, 'A Princess of Mars')).toHaveText('A Princess of Mars')
  })

  test('Find Match marks a record another folder already uses', async ({ page }) => {
    await openLibraryImport(page)
    await scan(page)
    await startMatchingButton(page).click()
    await expect(matchTitle(page, 'Tanar of Pellucidar')).toHaveText('Tanar of Pellucidar', {
      timeout: 60_000,
    })

    await findMatch(page, 'Pellucidar')
    await page.getByTestId('match-title').fill('Tanar of Pellucidar')
    await page.getByTestId('match-author').fill('Edgar Rice Burroughs')
    await page.getByTestId('match-search').click()
    await expect(
      findMatchResult(page, 'Tanar of Pellucidar')
        .filter({ hasNot: page.getByText('Dramatized') })
        .getByTestId('find-match-in-use'),
    ).toBeVisible()
    await page.keyboard.press('Escape')
    await expect(page.getByTestId('find-match-card')).toHaveCount(0)
  })

  test('Find Match by ASIN: a match by another author is flagged against the author the files name', async ({
    page,
  }) => {
    await openLibraryImport(page)
    await scan(page)
    await findMatch(page, 'Allan Quatermain')
    await expect(page.getByTestId('match-title')).toHaveValue('Allan Quatermain')
    await expect(page.getByTestId('match-author')).toHaveValue('H. Rider Haggard')
    await expect(findMatchResult(page, 'Allan Quatermain')).toBeVisible()
    await page.getByTestId('match-asin').fill('B0C6B525PQ')
    await page.getByTestId('match-search').click()
    await findMatchResult(page, 'Tanar of Pellucidar').click()
    await page.getByTestId('find-match-confirm').click()

    const author = importRow(page, 'Allan Quatermain').locator('.match-author')
    await expect(author).toHaveText('Edgar Rice Burroughs')
    await expect(author).toHaveClass(/author-mismatch/)
    await expect(author).toHaveAttribute('title', 'Detected: H. Rider Haggard')
  })

  test('Find Match searches the configured region, and says when it has no record for an ASIN', async ({
    page,
    listenarr,
  }) => {
    const settings = await listenarr.get<Record<string, unknown>>('/api/v1/configuration/settings')
    await listenarr.send('POST', '/api/v1/configuration/settings', {
      ...settings,
      defaultSearchRegion: 'fr',
    })
    await openLibraryImport(page)
    await scan(page)

    await findMatch(page, 'A Princess of Mars')
    await expect(page.getByTestId('match-region')).toHaveValue('fr')
    await expect(page.getByTestId('match-asin')).toHaveValue('B008DFUGCQ')
    await findMatchResult(page, 'A Princess of Mars').click()
    await page.getByTestId('find-match-confirm').click()
    await expect(matchTitle(page, 'A Princess of Mars')).toHaveText('A Princess of Mars')

    // An ASIN Audible doesn't know.
    await findMatch(page, 'Pellucidar')
    await expect(page.getByTestId('match-region')).toHaveValue('fr')
    await page.getByTestId('match-asin').fill('B0NOTFOUND')
    await page.getByTestId('match-search').click()
    await expect(page.getByText('No record for B0NOTFOUND in Audible France').first()).toBeVisible()
    await expect(page.getByTestId('find-match-confirm')).toBeDisabled()
  })
})
