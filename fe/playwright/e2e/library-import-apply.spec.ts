import { rm } from 'node:fs/promises'
import type { Page } from '@playwright/test'
import { fileExists, hostPath } from '../support/docker-stack.js'
import {
  expect,
  importButton,
  importRow,
  openLibraryImport,
  rowFolders,
  scan,
  startMatchingButton,
  test,
  type LibraryBook,
  type ListenarrApi,
} from './support/test.js'
import { folder, importBook } from './support/import-folders.js'

const libraryRoot = '/e2e/library'
// The files of the books in E2E Import (tests/Fixtures/Audiobooks/import-folders.json).
const source = {
  princess: importBook('Princess').files[0]!,
  tanar: importBook('Tanar').files[0]!,
  allan: importBook('Allan').files,
  leagues: importBook('Leagues').files[0]!,
}

const monitorSelect = (page: Page) => page.getByRole('combobox', { name: 'Monitor:' })
const importActionSelect = (page: Page) => page.getByRole('combobox', { name: /^On import:/ })

/** Scans E2E Import and matches its books; Pellucidar stays unmatched. */
async function scanAndMatch(page: Page) {
  await openLibraryImport(page)
  await scan(page)
  await startMatchingButton(page).click()
  await expect(page.locator('.import-row .match-title')).toHaveCount(4, { timeout: 60_000 })
  await expect(page.locator('.processing-label')).toHaveCount(0)
}

/** Every file path the library holds for a book, as the container sees it. */
function bookFiles(book: LibraryBook) {
  const paths = (book.files ?? []).map((file) => file.path ?? '')
  if (paths.length === 0 && book.filePath) paths.push(book.filePath)
  return paths.map((path) => (path.startsWith('/') ? path : `${book.basePath}/${path}`))
}

async function libraryBook(listenarr: ListenarrApi, asin: string) {
  const book = (await listenarr.library()).find((candidate) => candidate.asin === asin)
  if (!book) throw new Error(`No library book has ASIN ${asin}.`)
  return listenarr.book(book.id)
}

test.describe('Library Import apply', () => {
  test('imports the matched books in place, unmonitored; a book the library holds elsewhere stays on the page', async ({
    page,
    listenarr,
  }) => {
    await scanAndMatch(page)
    await expect(page.locator('.footer-left')).toContainText(
      'Files will be added to the library without being moved or copied',
    )
    await monitorSelect(page).selectOption('none')
    await importButton(page).click()
    await expect(importButton(page)).toHaveText(/Importing 4 Books\.\.\./)
    await expect(page.getByText('Import complete').first()).toBeVisible({ timeout: 60_000 })
    await expect(page.getByText(/3 books imported - \d+ metadata lookups/).first()).toBeVisible()
    // In place, a file can only join a book in its own folder: the seeded Twenty Thousand Leagues lives
    // in the library's output folder, so its file is not registered and its row stays.
    await expect(page.getByText('Import errors').first()).toBeVisible()
    await expect.poll(() => rowFolders(page)).toEqual([folder.Leagues, folder.Pellucidar])

    // Each book's files are registered where they are.
    for (const [asin, paths] of [
      ['B008DFUGCQ', [source.princess]],
      ['B0C6B525PQ', [source.tanar]],
      ['B0096QR7Z0', source.allan],
    ] as const) {
      const book = await libraryBook(listenarr, asin)
      expect(book.monitored).toBe(false)
      expect(bookFiles(book).sort()).toEqual([...paths].sort())
    }
    for (const path of [source.princess, source.tanar, ...source.allan])
      expect(await fileExists(path)).toBe(true)
    const library = await listenarr.library()
    expect(library).toHaveLength(7)
    expect(library.filter((book) => book.asin === 'B01FKWL15A')).toHaveLength(1)
    expect(bookFiles(await libraryBook(listenarr, 'B01FKWL15A'))).toEqual([])

    // A new scan only finds the books that weren't imported.
    await scan(page)
    await expect.poll(() => rowFolders(page)).toEqual([folder.Leagues, folder.Pellucidar])
  })

  test('moves the selected books into the chosen root folder', async ({ page, listenarr }) => {
    await scanAndMatch(page)
    for (const book of [folder.Princess, folder.Leagues]) {
      await importRow(page, book).locator('input[type="checkbox"]').uncheck()
    }
    await importActionSelect(page).selectOption('move')
    await page.locator('.destination-select').selectOption({ label: libraryRoot })
    await expect(importButton(page)).toHaveText('Import 2 Books')
    await importButton(page).click()
    await expect(importButton(page)).toHaveText(/Importing 2 Books\.\.\./)
    await expect(page.getByText('Import complete').first()).toBeVisible({ timeout: 60_000 })
    await expect
      .poll(() => rowFolders(page))
      .toEqual([folder.Princess, folder.Leagues, folder.Pellucidar])

    for (const [asin, sources] of [
      ['B0C6B525PQ', [source.tanar]],
      ['B0096QR7Z0', source.allan],
    ] as const) {
      const book = await libraryBook(listenarr, asin)
      expect(book.monitored).toBe(true)
      const files = bookFiles(book)
      expect(files).toHaveLength(sources.length)
      for (const file of files) {
        expect(file.startsWith(`${libraryRoot}/`)).toBe(true)
        expect(await fileExists(file)).toBe(true)
      }
      for (const path of sources) expect(await fileExists(path)).toBe(false)
    }
    // The emptied folder stays: it is inside a root folder, which only Listenarr's managed cleanup may change.
    expect(await fileExists(importBook('Allan').directory)).toBe(true)
    await expect(page.getByText('Import completed with warnings').first()).toBeVisible()
    expect(await fileExists(source.princess)).toBe(true)
  })

  test('hardlinks or copies a book into the chosen root folder and keeps its source', async ({
    page,
    listenarr,
  }) => {
    await scanAndMatch(page)
    await page.locator('thead input[type="checkbox"]').uncheck()
    await importRow(page, folder.Allan).locator('input[type="checkbox"]').check()
    await importActionSelect(page).selectOption('hardlink/copy')
    await page.locator('.destination-select').selectOption({ label: libraryRoot })
    await importButton(page).click()
    await expect(page.getByText('Import complete').first()).toBeVisible({ timeout: 60_000 })

    const files = bookFiles(await libraryBook(listenarr, 'B0096QR7Z0'))
    expect(files).toHaveLength(3)
    for (const file of files) {
      expect(file.startsWith(`${libraryRoot}/`)).toBe(true)
      expect(await fileExists(file)).toBe(true)
    }
    for (const path of source.allan) expect(await fileExists(path)).toBe(true)
  })

  test('moves a book the library already holds into the chosen root folder', async ({
    page,
    listenarr,
  }) => {
    const before = await libraryBook(listenarr, 'B01FKWL15A')
    await scanAndMatch(page)
    await page.locator('thead input[type="checkbox"]').uncheck()
    await importRow(page, folder.Leagues).locator('input[type="checkbox"]').check()
    await importActionSelect(page).selectOption('move')
    await page.locator('.destination-select').selectOption({ label: libraryRoot })
    await importButton(page).click()
    await expect(page.getByText('Import complete').first()).toBeVisible({ timeout: 60_000 })

    const after = await libraryBook(listenarr, 'B01FKWL15A')
    expect(after.id).toBe(before.id)
    const [file] = bookFiles(after)
    expect(file?.startsWith(`${libraryRoot}/`)).toBe(true)
    expect(await fileExists(file!)).toBe(true)
    expect(await fileExists(source.leagues)).toBe(false)
  })

  test('a match whose Audible record is gone is not imported: the row says why and asks for a new match', async ({
    page,
    listenarr,
  }) => {
    await openLibraryImport(page)
    await scan(page)
    // A search result whose product record Audible doesn't have.
    await importRow(page, folder.Tanar).locator('button[title="Search for a match"]').click()
    await page
      .getByTestId('find-match-result')
      .filter({ hasText: 'Tanar of Pellucidar (Dramatized)' })
      .click()
    await page.getByTestId('find-match-confirm').click()
    await expect(importRow(page, folder.Tanar).locator('.match-title')).toHaveText(
      'Tanar of Pellucidar (Dramatized)',
    )

    await importButton(page).click()
    await expect(page.getByText('Import errors').first()).toBeVisible({ timeout: 60_000 })
    await expect(importRow(page, folder.Tanar).locator('.match-error')).toHaveText(
      'Audible United States (US) has no record for B0TANARDRM. Use "Search for a match" to pick the book again, in the right region.',
    )
    await expect(importRow(page, folder.Tanar).locator('input[type="checkbox"]')).toBeDisabled()
    expect((await listenarr.library()).some((book) => book.asin === 'B0TANARDRM')).toBe(false)

    // The row survives a reload with its reason; a new match clears it.
    await page.reload()
    await expect(importRow(page, folder.Tanar).locator('.match-error')).toContainText(
      'has no record for B0TANARDRM',
    )
    await importRow(page, folder.Tanar).locator('button[title="Search for a match"]').click()
    await page.getByTestId('find-match-result').filter({ hasNotText: 'Dramatized' }).first().click()
    await page.getByTestId('find-match-confirm').click()
    await expect(importRow(page, folder.Tanar).locator('.match-error')).toHaveCount(0)
    await importButton(page).click()
    await expect(page.getByText('Import complete').first()).toBeVisible({ timeout: 60_000 })
    await expect(importRow(page, folder.Tanar)).toHaveCount(0)
  })

  test('a match saved before matches had a region is imported from the configured region', async ({
    page,
    listenarr,
  }) => {
    await scanAndMatch(page)
    await page.locator('thead input[type="checkbox"]').uncheck()
    await importRow(page, folder.Tanar).locator('input[type="checkbox"]').check()
    // What an older version saved: the same match, without its region.
    const rootFolderId = await page.locator('.folder-select').inputValue()
    await page.evaluate((id) => {
      const key = `listenarr-import-matches-${id}`
      const saved = JSON.parse(localStorage.getItem(key) ?? '{}') as Record<
        string,
        { selectedMatch?: { region?: string } }
      >
      for (const entry of Object.values(saved)) delete entry.selectedMatch?.region
      localStorage.setItem(key, JSON.stringify(saved))
    }, rootFolderId)
    await page.reload()
    await expect(importButton(page)).toHaveText('Import 1 Book')
    await importButton(page).click()
    await expect(page.getByText('Import complete').first()).toBeVisible({ timeout: 60_000 })
    expect(bookFiles(await libraryBook(listenarr, 'B0C6B525PQ'))).toEqual([source.tanar])
  })

  test('a book whose files vanished before the import fails and stays on the page', async ({
    page,
  }) => {
    await scanAndMatch(page)
    await page.locator('thead input[type="checkbox"]').uncheck()
    await importRow(page, folder.Princess).locator('input[type="checkbox"]').check()
    await importRow(page, folder.Tanar).locator('input[type="checkbox"]').check()
    await rm(hostPath(source.princess))

    await importButton(page).click()
    await expect(page.getByText('Import complete').first()).toBeVisible({ timeout: 60_000 })
    await expect(page.getByText('1 book imported').first()).toBeVisible()
    await expect(page.getByText('Import errors').first()).toBeVisible()
    await expect(importRow(page, folder.Tanar)).toHaveCount(0)
    await expect(importRow(page, folder.Princess).locator('input[type="checkbox"]')).toBeChecked()
  })
})
