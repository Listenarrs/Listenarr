import { chmod, rm } from 'node:fs/promises'
import { hostPath } from '../support/docker-stack.js'
import {
  expect,
  importRow,
  openLibraryImport,
  rowFolders,
  scan,
  scanButton,
  test,
} from './support/test.js'

// The book folders scripts/e2e/generate-library-fixtures.sh writes into the E2E Import root folder, in
// the table's default order: by the title the scan read (the album tag), then by path.
const importBooks = [
  'Allan Quatermain',
  'A Princess of Mars', // Barsoom
  'Twenty Thousand Leagues Under the Sea', // Captain Nemo
  'Pellucidar', // no tags: its folder
  'Tanar of Pellucidar',
]

test.describe('Library Import scan', () => {
  test('scans the default root folder and lists every unmatched book, saved for the next visit', async ({
    page,
  }) => {
    await openLibraryImport(page)
    await expect(page.locator('.folder-select')).toHaveValue(/\d+/)
    await expect(page.locator('.folder-select option:checked')).toHaveText('E2E Import')
    await expect(page.getByRole('heading', { name: 'No scan results yet' })).toBeVisible()

    await scan(page)
    await expect(page.locator('.import-row')).toHaveCount(importBooks.length)
    expect(await rowFolders(page)).toEqual(importBooks)
    await expect(page.locator('.import-row .folder-name')).toHaveText([
      'Allan Quatermain',
      'Barsoom',
      'Captain Nemo',
      'Pellucidar',
      'Tanar of Pellucidar',
    ])
    await expect(page.locator('.scan-meta')).toHaveText(/Last scanned just now\s+·\s+5 unmatched/)
    await expect(page.locator('.summary-pill')).toHaveText('5 items')

    // Tags: the title, author and series the files carry, with the folder when it says otherwise;
    // the folder alone when there are none.
    const princess = importRow(page, 'A Princess of Mars')
    await expect(princess.locator('.folder-meta')).toHaveText('Edgar Rice Burroughs - Barsoom')
    await expect(princess.locator('.folder-origin')).toHaveText('Folder: A Princess of Mars')
    await expect(princess.locator('.format-badge')).toHaveText(/m4b/i)
    await expect(princess.locator('.match-status.unsearched')).toBeVisible()
    await expect(princess.locator('input[type="checkbox"]')).toBeDisabled()
    await expect(importRow(page, 'Pellucidar').locator('.folder-meta')).toHaveCount(0)
    await expect(importRow(page, 'Pellucidar').locator('.folder-origin')).toHaveCount(0)
    await expect(importRow(page, 'Tanar of Pellucidar').locator('.folder-origin')).toHaveCount(0)

    // The three MP3 parts of one book are one row.
    const allan = importRow(page, 'Allan Quatermain')
    await expect(allan.locator('.file-count')).toHaveText('3 files')
    await allan.locator('.grouped-files-summary').click()
    await expect(allan.locator('.grouped-files-summary')).toHaveText(/View grouped files \(3\)/)
    await expect(allan.locator('.grouped-file-label')).toHaveText([
      'Allan Quatermain - Part 01.mp3',
      'Allan Quatermain - Part 02.mp3',
      'Allan Quatermain - Part 03.mp3',
    ])
    await expect(allan.locator('.grouped-file-badge')).toHaveCount(1)

    // The backend keeps the results: the next visit shows them without scanning, also in a browser
    // that has no matches of its own for them, or unreadable ones.
    await page.reload()
    await expect(page.locator('.import-row')).toHaveCount(importBooks.length)
    await expect(page.locator('.scan-meta')).toContainText('5 unmatched')
    const rootFolderId = await page.locator('.folder-select').inputValue()
    await page.evaluate(() => localStorage.clear())
    await page.reload()
    await expect(page.locator('.import-row')).toHaveCount(importBooks.length)
    await page.evaluate(
      (id) => localStorage.setItem(`listenarr-import-matches-${id}`, '{not json'),
      rootFolderId,
    )
    await page.reload()
    await expect(page.locator('.import-row')).toHaveCount(importBooks.length)
    await expect(page.locator('.import-row .match-status.unsearched')).toHaveCount(
      importBooks.length,
    )
  })

  test('a root folder removed meanwhile shows no results', async ({ page, listenarr }) => {
    await openLibraryImport(page)
    const second = await listenarr.rootFolder('E2E Second')
    await listenarr.send('DELETE', `/api/v1/rootfolders/${second.id}`)
    await page.locator('.folder-select').selectOption({ label: 'E2E Second' })
    await expect(page.getByRole('heading', { name: 'No scan results yet' })).toBeVisible()
    await expect(page.locator('.import-row')).toHaveCount(0)
  })

  test('shows how long ago the saved results were scanned', async ({ page }) => {
    await openLibraryImport(page)
    await scan(page)

    const now = Date.now()
    for (const [offset, label] of [
      [5 * 60_000, '5m ago'],
      [3 * 3_600_000, '3h ago'],
      [2 * 86_400_000, '2d ago'],
    ] as const) {
      await page.clock.setFixedTime(now + offset + 1_000)
      await page.reload()
      await expect(page.locator('.scan-meta')).toContainText(`Last scanned ${label}`)
    }
  })

  test('says when a root folder holds nothing to import', async ({ page }) => {
    await openLibraryImport(page)
    await page.locator('.folder-select').selectOption({ label: 'E2E Empty' })
    await expect(page.getByRole('heading', { name: 'No scan results yet' })).toBeVisible()

    await scan(page)
    await expect(page.getByRole('heading', { name: 'All files are in your library' })).toBeVisible()
    await expect(page.locator('.import-row')).toHaveCount(0)
  })

  test("switching root folders shows each folder's own saved results", async ({ page }) => {
    await openLibraryImport(page)
    await page.locator('.folder-select').selectOption({ label: 'E2E Second' })
    await scan(page)
    expect(await rowFolders(page)).toEqual(['Tarzan of the Apes'])

    await page.locator('.folder-select').selectOption({ label: 'E2E Import' })
    await expect(page.getByRole('heading', { name: 'No scan results yet' })).toBeVisible()
    await expect(page.locator('.import-row')).toHaveCount(0)

    await page.locator('.folder-select').selectOption({ label: 'E2E Second' })
    await expect(importRow(page, 'Tarzan of the Apes')).toBeVisible()
    expect(await rowFolders(page)).toEqual(['Tarzan of the Apes'])
  })

  test('a link from Settings scans the folder it names, once', async ({ page, listenarr }) => {
    const second = await listenarr.rootFolder('E2E Second')
    await openLibraryImport(page, `?rootFolder=${second.id}&scan=1`)
    await expect(importRow(page, 'Tarzan of the Apes')).toBeVisible()
    await expect(page.locator('.folder-select option:checked')).toHaveText('E2E Second')
    // The scan request is dropped from the address: a reload shows the saved results, no new scan.
    await expect(page).toHaveURL(new RegExp(`/library-import\\?rootFolder=${second.id}$`))

    let scans = 0
    page.on('request', (request) => {
      if (request.method() === 'POST' && request.url().includes('/scan-unmatched')) scans += 1
    })
    await page.reload()
    await expect(importRow(page, 'Tarzan of the Apes')).toBeVisible()
    await expect(page.locator('.folder-select option:checked')).toHaveText('E2E Second')
    expect(scans).toBe(0)
  })

  test('a link to a removed root folder says so and opens the default folder', async ({ page }) => {
    await openLibraryImport(page, '?rootFolder=999999&scan=1')
    const missing = page.getByTestId('requested-folder-missing')
    await expect(missing).toContainText('This root folder no longer exists.')
    await expect(page.locator('.folder-select option:checked')).toHaveText('E2E Import')
    await expect(page).toHaveURL(/\/library-import$/)
    await expect(page.locator('.import-row')).toHaveCount(0)

    await page.locator('.folder-select').selectOption({ label: 'E2E Second' })
    await expect(missing).toHaveCount(0)
  })

  test('a scan skips an unreadable folder, warns about it and keeps the readable books', async ({
    page,
  }) => {
    const unreadable = hostPath('/e2e/import/Edgar Rice Burroughs/Pellucidar')
    await chmod(unreadable, 0o000)
    try {
      await openLibraryImport(page)
      await scan(page)
      await expect(page.locator('.scan-warning')).toContainText(
        'One path could not be read and was skipped. Other readable library-import results were preserved.',
      )
      await expect(page.locator('.import-row')).toHaveCount(importBooks.length - 1)
      expect(await rowFolders(page)).toEqual(
        importBooks.filter((folder) => folder !== 'Pellucidar'),
      )
    } finally {
      await chmod(unreadable, 0o755)
    }
  })

  test('a scan of a root folder that disappeared fails with the reason', async ({ page }) => {
    await openLibraryImport(page)
    await rm(hostPath('/e2e/import'), { recursive: true, force: true })
    await scanButton(page).click()
    await expect(page.locator('.scan-error')).toBeVisible({ timeout: 60_000 })
    await expect(page.locator('.scan-error')).not.toHaveText(/^\s*$/)
    await expect(scanButton(page)).toBeEnabled()
    await expect(page.locator('.import-row')).toHaveCount(0)
  })

  test('without SignalR, polling reports a scan that failed', async ({ page }) => {
    await page.route('**/hubs/**', (route) => route.abort())
    await page.routeWebSocket(/\/hubs\//, (socket) => socket.close())
    await openLibraryImport(page)
    await rm(hostPath('/e2e/import'), { recursive: true, force: true })
    await scanButton(page).click()
    await expect(page.locator('.scan-error')).toBeVisible({ timeout: 60_000 })
    await expect(page.locator('.import-row')).toHaveCount(0)
  })

  test('without SignalR, the page polls the scan until its results are ready', async ({ page }) => {
    // The realtime hub is unreachable: the scan completion only comes from polling.
    await page.route('**/hubs/**', (route) => route.abort())
    await page.routeWebSocket(/\/hubs\//, (socket) => socket.close())
    await openLibraryImport(page)
    await scan(page)
    await expect(page.locator('.import-row')).toHaveCount(importBooks.length)
  })
})
