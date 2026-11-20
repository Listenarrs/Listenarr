import type { Page } from '@playwright/test'
import {
  audiobookFixture,
  expect,
  libraryAudiobookFixture,
  libraryImportUnmatchedFixture,
  repairSearchResultFixture,
  rootFolderFixture,
  settingsFixture,
  test,
  type ApiReply,
  type ApiStubs,
  type RepairIdentifier,
  LIBRARY_PATH,
} from '../support/test.js'

const audiobookId = 401
const oldAsin = 'B000000401'
const selectedMatch = repairSearchResultFixture({
  asin: 'B000000402',
  title: 'Repaired Edition',
  authors: [{ name: 'Test Author' }],
})
const currentMatch = repairSearchResultFixture({
  asin: oldAsin,
  title: 'Original Book',
  authors: [{ name: 'Test Author' }],
})
const audiobook = audiobookFixture({
  id: audiobookId,
  title: 'Original Book',
  authors: ['Test Author'],
  asin: oldAsin,
})
const successfulRescanReply = {
  statusCode: 200,
  body: {
    message: 'Metadata refreshed',
    audiobookId,
    asin: selectedMatch.asin,
    region: selectedMatch.region,
  },
}

const repairRow = (page: Page, id = audiobookId) => page.locator(`[data-testid="repair-row-${id}"]`)
const repairResult = (page: Page) => page.locator(`[data-testid="repair-result-${audiobookId}"]`)
const pathname = (page: Page) => new URL(page.url()).pathname
// The modal overlay covers the sidebar, so a real click on it never reaches the link: dispatch the
// click on the link itself, as a forced click would.
const settingsLink = (page: Page) => page.locator('a', { hasText: 'Settings' }).first()

/**
 * The grid checkbox draws its tick from the selection store (the item's `selected` class), not from
 * the input's own state, which the app keeps unchanged. Click it and wait for the class.
 */
async function setSelected(page: Page, title: string, selected: boolean) {
  const item = page.locator('.audiobook-item', { hasText: title })
  await item.locator('.selection-checkbox').dispatchEvent('click')
  if (selected) await expect(item).toHaveClass(/selected/)
  else await expect(item).not.toHaveClass(/selected/)
}

async function prepareRepair(
  page: Page,
  api: ApiStubs,
  rescanReplies: ApiReply[] = [successfulRescanReply],
  searchResult = selectedMatch,
  audiobookReplies?: ApiReply[],
) {
  await api.stubRepairPage(audiobookId, audiobook, [
    { type: 'Asin', value: oldAsin, region: 'us', isPrimary: true, source: 'Imported' },
  ])
  if (audiobookReplies) await api.stubAudiobookReplies(audiobookId, audiobookReplies)
  await api.stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
  await api.stubMetadata({
    statusCode: 200,
    body: { metadata: { asin: oldAsin, title: audiobook.title } },
  })
  await api.stubSearch({ statusCode: 200, body: { results: [searchResult] } }, 'repairSearch')
  await api.stubRepairWrites(audiobookId, rescanReplies)

  await page.goto(`/audiobooks/${audiobookId}`)
  await page.locator('button[aria-label="Repair Book"]').click()
  await expect(repairRow(page)).toBeVisible()
}

async function chooseDifferentMatch(page: Page, api: ApiStubs, match = selectedMatch) {
  const row = repairRow(page)
  await row.locator('[data-testid="match-asin"]').clear()
  await row.locator('[data-testid="match-title"]').fill(match.title)
  await row.locator('[data-testid="match-author"]').fill(match.authors[0]!.name)
  await row.locator('[data-testid="match-search"]').click()
  await api.wait('repairSearch')
  await page.locator('[data-testid="find-match-result"]', { hasText: match.title }).click()
}

async function confirmRepair(page: Page) {
  await page.locator('[data-testid="repair-confirm"]').click()
  await page.locator('.confirm-dialog').locator('button', { hasText: 'Repair 1' }).click()
}

test.describe('Review scenarios (API-stubbed)', () => {
  test('P01: retries a repeated repair after the rescan cooldown response', async ({ page, api }) => {
    const successReply = {
      statusCode: 200,
      body: {
        message: 'Metadata refreshed',
        audiobookId,
        asin: currentMatch.asin,
        region: currentMatch.region,
      },
    }
    await prepareRepair(
      page,
      api,
      [
        successReply,
        { statusCode: 429, body: { message: 'Too many requests', retryAfterSeconds: 1 } },
        successReply,
      ],
      currentMatch,
    )
    await chooseDifferentMatch(page, api, currentMatch)
    await confirmRepair(page)
    await api.wait('rescanAudiobookMetadata')
    await expect(repairResult(page)).toHaveAttribute('data-result-state', 'done')

    await page.locator('[data-testid="repair-cancel"]').click()
    await chooseDifferentMatch(page, api, currentMatch)
    await confirmRepair(page)

    await api.wait('rescanAudiobookMetadata')
    await expect(repairResult(page).locator('[data-testid="repair-result-message"]')).toContainText('Waiting 1 s')
    await api.wait('rescanAudiobookMetadata')

    await expect(repairResult(page)).toHaveAttribute('data-result-state', 'done')
    await expect(repairResult(page)).toContainText(currentMatch.title)
    await expect.poll(() => api.calls('rescanAudiobookMetadata').length).toBe(3)
  })

  test('P07: Escape dismisses the leave confirmation and keeps completed repair results open', async ({
    page,
    api,
  }) => {
    await prepareRepair(page, api, [{ ...successfulRescanReply, delay: 1000 }])
    await chooseDifferentMatch(page, api)
    await confirmRepair(page)
    await expect(repairResult(page).locator('[data-testid="repair-result-message"]')).toContainText('Repairing')

    await settingsLink(page).dispatchEvent('click')
    await expect(page.locator('.confirm-dialog')).toContainText('Leave while books are repaired?')
    await api.wait('rescanAudiobookMetadata')
    await expect(repairResult(page)).toHaveAttribute('data-result-state', 'done')

    await page.keyboard.press('Escape')

    await expect(page.locator('.confirm-dialog')).toHaveCount(0)
    await expect.poll(() => pathname(page)).toBe(`/audiobooks/${audiobookId}`)
    await expect(repairResult(page)).toBeVisible()
  })

  test('P10: a failed post-rescan audiobook read does not turn a successful repair into an error', async ({
    page,
    api,
  }) => {
    await prepareRepair(page, api)
    await api.stubAudiobookReplies(audiobookId, [{ statusCode: 503, body: { message: 'Readback timed out' } }])
    await chooseDifferentMatch(page, api)
    await confirmRepair(page)

    await api.wait('rescanAudiobookMetadata')
    await expect
      .poll(() => api.calls('getAudiobook').some(({ response }) => response?.statusCode === 503))
      .toBe(true)
    await expect(repairResult(page)).toHaveAttribute('data-result-state', 'done')
    await expect(repairResult(page)).toContainText(selectedMatch.title)
    await expect(repairResult(page).locator('[data-testid="repair-result-retry"]')).toHaveCount(0)
  })

  test('P02: a slower closed selection cannot replace the books from the next load', async ({ page, api }) => {
    const firstId = 402
    const secondId = 403
    const firstBook = audiobookFixture({
      id: firstId,
      title: 'Slow First Book',
      authors: ['First Author'],
      asin: 'B000000403',
    })
    const secondBook = audiobookFixture({
      id: secondId,
      title: 'Next Book',
      authors: ['Second Author'],
      asin: 'B000000404',
    })

    await api.stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    await api.stubLibrary([
      libraryAudiobookFixture({
        id: firstId,
        title: firstBook.title,
        authors: firstBook.authors,
        asin: firstBook.asin,
      }),
      libraryAudiobookFixture({
        id: secondId,
        title: secondBook.title,
        authors: secondBook.authors,
        asin: secondBook.asin,
      }),
    ])
    // Registered last, so that 'getAudiobook' records the slow book: the alias keeps one stub's calls.
    await api.stubAudiobook(secondId, secondBook)
    await api.stubAudiobookReplies(firstId, [{ statusCode: 200, body: firstBook, delay: 1500 }])
    for (const id of [firstId, secondId]) {
      await api.stubAudiobookIdentifiers(id, [])
      await api.stubWeakStorageMissingFiles(id)
    }

    const repairSelected = page.locator('button', { hasText: 'Repair Selected (1)' })

    await page.goto(LIBRARY_PATH)
    await setSelected(page, firstBook.title!, true)
    await repairSelected.click()
    await expect(page.getByText('Loading the selected audiobooks').first()).toBeVisible()
    await page.keyboard.press('Escape')
    await expect(repairSelected).toBeVisible()
    await expect(page.locator('.modal-overlay')).toHaveCount(0)

    await setSelected(page, firstBook.title!, false)
    await setSelected(page, secondBook.title!, true)
    await repairSelected.click()
    await expect(repairRow(page, secondId)).toBeVisible()
    await api.wait('getAudiobook')

    await expect(page.locator('[data-testid^="repair-row-"]')).toHaveCount(1)
    await expect(repairRow(page, secondId)).toBeVisible()
    await expect(repairRow(page, firstId)).toHaveCount(0)
  })

  test('R02: repeated repairs replace the prior ASIN instead of accumulating leftovers', async ({ page, api }) => {
    const firstTarget = repairSearchResultFixture({
      asin: 'B000000411',
      title: 'First Target',
      authors: [{ name: 'Test Author' }],
      region: 'fr',
    })
    const secondTarget = repairSearchResultFixture({
      asin: 'B000000412',
      title: 'Second Target',
      authors: [{ name: 'Test Author' }],
      region: 'us',
    })
    const targets = [firstTarget, secondTarget, firstTarget]
    let searchIndex = 0

    await prepareRepair(
      page,
      api,
      targets.map((target) => ({
        statusCode: 200,
        body: { message: 'Metadata refreshed', audiobookId, asin: target.asin, region: target.region },
      })),
      selectedMatch,
    )
    await api.stubSearch(
      () => ({
        statusCode: 200,
        body: { results: [targets[searchIndex++]!] },
      }),
      'repairSearch',
    )

    for (const [index, target] of targets.entries()) {
      await chooseDifferentMatch(page, api, target)
      await confirmRepair(page)
      await api.wait('rescanAudiobookMetadata')
      await expect(repairResult(page)).toHaveAttribute('data-result-state', 'done')

      const savedIdentifiers: RepairIdentifier[] = api.calls('saveAudiobookIdentifiers').at(-1)!.request.body
        .identifiers
      const asins = savedIdentifiers.filter((identifier) => identifier.type === 'Asin')
      expect(asins).toEqual([
        { type: 'Asin', value: target.asin, region: target.region, isPrimary: true, source: 'Manual' },
      ])
      if (index < targets.length - 1) await api.stubAudiobookIdentifiers(audiobookId, savedIdentifiers)

      if (index < targets.length - 1) {
        await page.locator('[data-testid="repair-cancel"]').click()
        await expect(repairRow(page)).toBeVisible()
      }
    }

    await expect.poll(() => api.calls('saveAudiobookIdentifiers').length).toBe(3)
  })

  test('R05: browser Back closes the pending repair question without starting a repair', async ({
    page,
    api,
  }) => {
    await api.stubRepairPage(audiobookId, audiobook, [
      { type: 'Asin', value: oldAsin, region: 'us', isPrimary: true, source: 'Imported' },
    ])
    await api.stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    await api.stubMetadata({ statusCode: 200, body: { metadata: { asin: oldAsin, title: audiobook.title } } })
    await api.stubSearch({ statusCode: 200, body: { results: [selectedMatch] } }, 'repairSearch')
    await api.stubRepairWrites(audiobookId)

    await page.goto(LIBRARY_PATH)
    await page.locator('nav, aside').locator('a', { hasText: 'Library Import' }).first().click()
    await expect.poll(() => pathname(page)).toBe('/library-import')
    await page.locator('nav, aside').locator('a', { hasText: 'Audiobooks' }).first().click()
    await expect.poll(() => pathname(page)).toBe('/audiobooks')
    await setSelected(page, audiobook.title!, true)
    await page.locator('button', { hasText: 'Repair Selected (1)' }).click()
    await expect(repairRow(page)).toBeVisible()
    await chooseDifferentMatch(page, api)
    await page.locator('[data-testid="repair-confirm"]').click()
    await expect(page.locator('.confirm-dialog')).toContainText('Repair 1 book?')

    await page.goBack()

    await expect.poll(() => pathname(page)).toBe('/library-import')
    await expect(page.locator('.confirm-dialog')).toHaveCount(0)
    expect(api.calls('saveAudiobookIdentifiers')).toHaveLength(0)
    expect(api.calls('rescanAudiobookMetadata')).toHaveLength(0)
  })

  test('R06: leaving during a cooldown lets the in-flight rescan finish', async ({ page, api }) => {
    const successReply = {
      statusCode: 200,
      body: {
        message: 'Metadata refreshed',
        audiobookId,
        asin: currentMatch.asin,
        region: currentMatch.region,
      },
    }
    await prepareRepair(
      page,
      api,
      [
        successReply,
        { statusCode: 429, body: { message: 'Too many requests', retryAfterSeconds: 1 } },
        successReply,
      ],
      currentMatch,
    )
    await chooseDifferentMatch(page, api, currentMatch)
    await confirmRepair(page)
    await api.wait('rescanAudiobookMetadata')
    await expect(repairResult(page)).toHaveAttribute('data-result-state', 'done')

    await page.locator('[data-testid="repair-cancel"]').click()
    await chooseDifferentMatch(page, api, currentMatch)
    await confirmRepair(page)
    await api.wait('rescanAudiobookMetadata')
    await expect(
      page.locator(`[data-testid="repair-result-${audiobookId}"] [data-testid="repair-result-message"]`),
    ).toContainText('Waiting 1 s')

    await settingsLink(page).dispatchEvent('click')
    await expect(page.locator('.confirm-dialog')).toContainText('Leave while books are repaired?')
    await page.locator('.confirm-dialog').locator('button', { hasText: 'Leave' }).click()
    await expect.poll(() => pathname(page)).toBe('/settings')
    await api.wait('rescanAudiobookMetadata')
    await expect.poll(() => api.calls('rescanAudiobookMetadata').length).toBe(3)
  })

  test('P15: keeps Organize mounted while the post-transition book readback is delayed', async ({
    page,
    api,
  }) => {
    await prepareRepair(page, api, [successfulRescanReply], selectedMatch, [
      { statusCode: 200, body: audiobook },
      { statusCode: 200, body: audiobook, delay: 1500 },
    ])
    await chooseDifferentMatch(page, api)
    await confirmRepair(page)
    await api.wait('rescanAudiobookMetadata')
    await expect(repairResult(page)).toHaveAttribute('data-result-state', 'done')

    await page.locator('[data-testid="repair-organize"]').click()

    await expect(page.locator('[data-testid="organize-back"]')).toBeVisible()
    await expect(() => {
      const requests = api.calls('getAudiobook')
      expect(requests).toHaveLength(4)
      expect(requests[3]?.response).toBeUndefined()
    }).toPass()
    await expect(page.locator('[data-testid="organize-back"]')).toBeVisible()
    await expect.poll(() => api.calls('getAudiobook')[3]?.response?.statusCode).toBe(200)
  })

  test('R08: selects and imports a search result without an ASIN', async ({ page, api }) => {
    const rootFolderId = 1
    const importedAudiobookId = 408
    const importItem = libraryImportUnmatchedFixture({
      fullPath: '/incoming/Author/Unpublished Notes 1987/01.m4b',
      sourceFiles: ['/incoming/Author/Unpublished Notes 1987/01.m4b'],
      relativePath: 'Author/Unpublished Notes 1987',
      bookFolder: '/incoming/Author/Unpublished Notes 1987',
      title: 'Unpublished Notes 1987',
      author: 'Nobody Known',
    })
    const openLibraryResult = {
      id: 'openlibrary:OL12345W',
      title: 'Unpublished Notes 1987',
      authors: [{ name: 'Nobody Known' }],
      region: 'us',
    }

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
    await api.stubLibraryImportSavedResults(rootFolderId, [importItem])
    await api.stubSearch(() => ({ statusCode: 200, body: [openLibraryResult] }), 'libraryImportSearch')
    await api.stubAddLibrary(
      audiobookFixture({
        id: importedAudiobookId,
        title: openLibraryResult.title,
        authors: ['Nobody Known'],
      }),
    )
    await api.stubManualImport({ importedCount: 1, totalCount: 1, results: [{ success: true }] })

    await page.goto('/library-import')
    await api.wait('getRootFolders')
    await api.wait('getSettings')
    await api.wait('savedLibraryImportResults')
    await page.locator('.import-row').locator('button[title="Search for a match"]').click()
    await api.wait('libraryImportSearch')

    const result = page.locator('[data-testid="find-match-result"]', { hasText: openLibraryResult.title })
    await expect(result.locator('.result-asin')).toHaveCount(0)
    await result.click()
    const confirm = page.locator('[data-testid="find-match-confirm"]')
    await expect(confirm).toBeEnabled()
    await confirm.click()
    await expect(page.locator('.import-row', { hasText: openLibraryResult.title })).toBeVisible()
    await expect(page.locator('.import-row input[type="checkbox"]')).toBeChecked()

    await page.locator('.import-footer select').nth(1).selectOption('move')
    await page.locator('.footer-right button', { hasText: 'Import 1 Book' }).click()

    const { request: addRequest } = await api.wait('addAudiobookToLibrary')
    expect(addRequest.body.searchResult).toMatchObject({
      id: openLibraryResult.id,
      title: openLibraryResult.title,
    })
    expect(addRequest.body.searchResult).not.toHaveProperty('asin')
    const { request: importRequest } = await api.wait('importManualFiles')
    expect(importRequest.body.items).toEqual([
      { fullPath: importItem.fullPath, matchedAudiobookId: importedAudiobookId },
    ])
    await expect(page.getByText('Import complete').first()).toBeVisible()
  })
})
