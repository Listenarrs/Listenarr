import { cp, mkdir } from 'node:fs/promises'
import { hostPath } from '../support/docker-stack.js'
import {
  expect,
  importRow,
  openLibraryImport,
  rowFolders,
  scan,
  startMatchingButton,
  test,
} from './support/test.js'

const header = (page: import('@playwright/test').Page, name: string) =>
  page.locator('.import-table thead th', { hasText: name })

test.describe('Library Import table', () => {
  test('sorts the books by each column, both ways', async ({ page }) => {
    await openLibraryImport(page)
    await scan(page)
    await startMatchingButton(page).click()
    await expect(page.locator('.import-row .match-title')).toHaveCount(4, { timeout: 60_000 })
    await expect(page.locator('.summary-text')).toHaveText('Sorted by Book (ascending)')
    await expect(header(page, 'Book')).toHaveAttribute('aria-sort', 'ascending')

    await header(page, 'Book').locator('.sort-button').click()
    await expect(header(page, 'Book')).toHaveAttribute('aria-sort', 'descending')
    // By the title the scan read.
    await expect
      .poll(() => rowFolders(page))
      .toEqual([
        'Tanar of Pellucidar',
        'Pellucidar',
        'Twenty Thousand Leagues Under the Sea', // Captain Nemo
        'A Princess of Mars', // Barsoom
        'Allan Quatermain',
      ])

    await header(page, 'Path').locator('.sort-button').click()
    await expect(page.locator('.summary-text')).toHaveText('Sorted by Path (ascending)')
    await expect(header(page, 'Book')).toHaveAttribute('aria-sort', 'none')
    await expect
      .poll(() => rowFolders(page))
      .toEqual([
        'A Princess of Mars', // Edgar Rice Burroughs/
        'Pellucidar',
        'Tanar of Pellucidar',
        'Allan Quatermain', // H. Rider Haggard/
        'Twenty Thousand Leagues Under the Sea', // Jules Verne/
      ])

    // Format, then the number of files.
    await header(page, 'Format').locator('.sort-button').click()
    await header(page, 'Format').locator('.sort-button').click()
    await expect(page.locator('.summary-text')).toHaveText('Sorted by Format (descending)')
    await expect.poll(async () => (await rowFolders(page))[0]).toBe('Allan Quatermain')

    // By match title; the unmatched book last.
    await header(page, 'Match').locator('.sort-button').click()
    await expect
      .poll(() => rowFolders(page))
      .toEqual([
        'A Princess of Mars',
        'Allan Quatermain',
        'Tanar of Pellucidar',
        'Twenty Thousand Leagues Under the Sea',
        'Pellucidar',
      ])
    await header(page, 'Match').locator('.sort-button').click()
    await expect.poll(async () => (await rowFolders(page)).at(-1)).toBe('A Princess of Mars')
  })

  test('on a narrow screen, the sort is chosen from lists', async ({ page }) => {
    await page.setViewportSize({ width: 600, height: 900 })
    await openLibraryImport(page)
    await scan(page)
    const [sort, order] = [
      page.locator('.mobile-sort-control select').nth(0),
      page.locator('.mobile-sort-control select').nth(1),
    ]
    await expect(sort).toBeVisible()
    await sort.selectOption('path')
    await order.selectOption('desc')
    await expect(page.locator('.summary-text')).toHaveText('Sorted by Path (descending)')
    await expect
      .poll(async () => (await rowFolders(page))[0])
      .toBe('Twenty Thousand Leagues Under the Sea')

    // Columns can't be resized there: the header row carrying the resize handles is hidden on a narrow
    // screen, so there is no edge to drag.
    await expect(page.locator('.import-table thead')).toBeHidden()
    await expect(header(page, 'Book')).toBeHidden()
    for (const handle of await page.locator('.resize-handle').all()) {
      await expect(handle).toBeHidden()
    }
  })

  test('columns resize by dragging their edge, within limits, and keep their width', async ({
    page,
  }) => {
    await openLibraryImport(page)
    await scan(page)
    const bookColumn = page.locator('.import-table col').nth(1)
    await expect(bookColumn).toHaveAttribute('style', /width: 260px/)

    const handle = header(page, 'Book').locator('.resize-handle')
    const box = (await handle.boundingBox())!
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2)
    await page.mouse.down()
    await expect(handle).toHaveClass(/active/)
    await page.mouse.move(box.x + 100, box.y + box.height / 2, { steps: 5 })
    await page.mouse.up()
    await expect(handle).not.toHaveClass(/active/)
    await expect(bookColumn).toHaveAttribute('style', /width: 3[45]\dpx/)
    const resized = await bookColumn.getAttribute('style')

    // Not narrower than its minimum.
    const formatHandle = header(page, 'Format').locator('.resize-handle')
    const formatBox = (await formatHandle.boundingBox())!
    await page.mouse.move(formatBox.x + formatBox.width / 2, formatBox.y + formatBox.height / 2)
    await page.mouse.down()
    await page.mouse.move(formatBox.x - 400, formatBox.y + formatBox.height / 2, { steps: 5 })
    await page.mouse.up()
    await expect(page.locator('.import-table col').nth(3)).toHaveAttribute('style', /width: 96px/)

    await page.reload()
    await expect(bookColumn).toHaveAttribute('style', resized!)
    await expect(page.locator('.import-table col').nth(3)).toHaveAttribute('style', /width: 96px/)

    // Saved widths that don't parse are ignored; out-of-range ones are brought back within limits.
    await page.evaluate(() =>
      localStorage.setItem('listenarr.libraryImport.columnWidths.v1', '{not json'),
    )
    await page.reload()
    await expect(bookColumn).toHaveAttribute('style', /width: 260px/)
    await page.evaluate(() =>
      localStorage.setItem(
        'listenarr.libraryImport.columnWidths.v1',
        JSON.stringify({ folder: 5000, path: 'wide', format: 10 }),
      ),
    )
    await page.reload()
    await expect(bookColumn).toHaveAttribute('style', /width: 960px/)
    await expect(page.locator('.import-table col').nth(2)).toHaveAttribute('style', /width: 380px/)
    await expect(page.locator('.import-table col').nth(3)).toHaveAttribute('style', /width: 96px/)
  })

  test('Choose another folder adds a root folder and shows it', async ({ page, listenarr }) => {
    await mkdir(hostPath('/e2e/extra'), { recursive: true })
    await cp(hostPath('/e2e/second'), hostPath('/e2e/extra'), {
      recursive: true,
    })
    await openLibraryImport(page)
    const folderCount = (await listenarr.rootFolders()).length
    await expect(page.locator('.folder-select option')).toHaveCount(folderCount)

    // Closing the dialog changes nothing.
    await page.locator('.scan-controls button', { hasText: 'Choose another folder...' }).click()
    await expect(page.getByRole('heading', { name: 'Add Root Folder' })).toBeVisible()
    await page.getByRole('button', { name: 'Cancel' }).click()
    await expect(page.getByRole('heading', { name: 'Add Root Folder' })).toHaveCount(0)
    await expect(page.locator('.folder-select option')).toHaveCount(folderCount)

    await page.locator('.scan-controls button', { hasText: 'Choose another folder...' }).click()
    await page.getByPlaceholder('Enter a name for this root folder').fill('E2E Extra')
    await page.locator('#root-path').fill('/e2e/extra')
    await page.getByRole('button', { name: 'Save' }).click()
    await expect(page.getByRole('heading', { name: 'Add Root Folder' })).toHaveCount(0)
    await expect(page.locator('.folder-select option')).toHaveCount(folderCount + 1)
    await expect(page.locator('.folder-select option:checked')).toHaveText('E2E Extra')
    expect((await listenarr.rootFolders()).map((folder) => folder.name)).toContain('E2E Extra')

    await scan(page)
    await expect.poll(() => rowFolders(page)).toEqual(['Tarzan of the Apes'])
    await expect(importRow(page, 'Tarzan of the Apes').locator('.file-path')).toHaveText(
      '/e2e/extra/Edgar Rice Burroughs/Tarzan of the Apes/Tarzan of the Apes.m4b',
    )
  })
})
