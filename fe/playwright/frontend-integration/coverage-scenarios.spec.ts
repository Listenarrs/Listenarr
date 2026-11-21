import type { Page } from '@playwright/test'
import {
  audiobookFixture,
  expect,
  libraryAudiobookFixture,
  libraryImportSearchResultFixture,
  libraryImportUnmatchedFixture,
  repairSearchResultFixture,
  rootFolderFixture,
  settingsFixture,
  test,
  type ApiStubs,
  type RepairIdentifier,
  LIBRARY_PATH,
} from '../support/test.js'

const repairRow = (page: Page, id: number) => page.locator(`[data-testid="repair-row-${id}"]`)
const repairResult = (page: Page, id: number) => page.locator(`[data-testid="repair-result-${id}"]`)
const libraryBook = (page: Page, title: string) =>
  page
    .locator('.audiobook-item, .audiobook-list-item')
    .filter({ has: page.getByText(title, { exact: true }) })
const confirmRepair = (page: Page, count: number) =>
  page.locator('.confirm-dialog').locator('button', { hasText: `Repair ${count}` }).click()

async function searchForRepairMatch(
  page: Page,
  api: ApiStubs,
  id: number,
  match: ReturnType<typeof repairSearchResultFixture>,
  alias = 'coverageSearch',
  pick = true,
  query = match.title,
) {
  const row = repairRow(page, id)
  await row.locator('[data-testid="match-asin"]').fill('')
  await row.locator('[data-testid="match-title"]').fill(query)
  await row.locator('[data-testid="match-author"]').fill(match.authors[0]!.name)
  await row.locator('[data-testid="match-search"]').click()
  await api.wait(alias)
  const result = row.locator('[data-testid="find-match-result"]', { hasText: match.title }).first()
  await expect(result).toBeVisible()
  if (pick) await result.click()
}

const identifierMatrixCases: {
  name: string
  id: number
  oldAsin: string
  identifiers: RepairIdentifier[]
  match: { asin: string; title: string; region: string }
  expected: RepairIdentifier[]
}[] = [
  {
    name: 'replaces the old record and keeps another Manual ASIN in the same region',
    id: 401,
    oldAsin: 'B000000401',
    identifiers: [
      { type: 'Asin', value: 'B000000401', region: 'us', isPrimary: true, source: 'Manual' },
      { type: 'Asin', value: 'B000000402', region: 'us', isPrimary: false, source: 'Manual' },
    ],
    match: { asin: 'B000000403', title: 'French Record', region: 'fr' },
    expected: [
      { type: 'Asin', value: 'B000000403', region: 'fr', isPrimary: true, source: 'Manual' },
      { type: 'Asin', value: 'B000000402', region: 'us', isPrimary: false, source: 'Manual' },
    ],
  },
  {
    name: 'promotes an existing secondary ASIN without duplicating it',
    id: 402,
    oldAsin: 'B000000404',
    identifiers: [
      { type: 'Asin', value: 'B000000404', region: 'us', isPrimary: true, source: 'Manual' },
      { type: 'Asin', value: 'B000000403', region: 'fr', isPrimary: false, source: 'Manual' },
    ],
    match: { asin: 'B000000403', title: 'French Record', region: 'fr' },
    expected: [{ type: 'Asin', value: 'B000000403', region: 'fr', isPrimary: true, source: 'Manual' }],
  },
  {
    name: 'changes only the region when the ASIN is already stored in another region',
    id: 403,
    oldAsin: 'B000000403',
    identifiers: [{ type: 'Asin', value: 'B000000403', region: 'us', isPrimary: true, source: 'Manual' }],
    match: { asin: 'B000000403', title: 'French Record', region: 'fr' },
    expected: [{ type: 'Asin', value: 'B000000403', region: 'fr', isPrimary: true, source: 'Manual' }],
  },
  {
    name: 'keeps Manual ISBN and OpenLibrary identifiers while changing record',
    id: 404,
    oldAsin: 'B000000405',
    identifiers: [
      { type: 'Asin', value: 'B000000405', region: 'us', isPrimary: true, source: 'Imported' },
      { type: 'Isbn', value: '9781039400740', region: null, isPrimary: true, source: 'Manual' },
      { type: 'OpenLibraryId', value: 'OL7353617M', region: null, isPrimary: true, source: 'Manual' },
    ],
    match: { asin: 'B000000403', title: 'French Record', region: 'fr' },
    expected: [
      { type: 'Asin', value: 'B000000403', region: 'fr', isPrimary: true, source: 'Manual' },
      { type: 'Isbn', value: '9781039400740', region: null, isPrimary: true, source: 'Manual' },
      { type: 'OpenLibraryId', value: 'OL7353617M', region: null, isPrimary: true, source: 'Manual' },
    ],
  },
]

test.describe('ported coverage scenarios (API-stubbed)', () => {
  identifierMatrixCases.forEach(({ name, id, oldAsin, identifiers, match, expected }) => {
    test(`C01: ${name}`, async ({ page, api }) => {
      const audiobook = audiobookFixture({ id, title: 'Identifier Matrix Book', asin: oldAsin })
      const selectedMatch = repairSearchResultFixture({ ...match, authors: [{ name: 'Matrix Author' }] })
      await api.stubRepairPage(id, audiobook, identifiers)
      await api.stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
      await api.stubMetadata({ statusCode: 200, body: { metadata: { asin: oldAsin, title: audiobook.title } } })
      await api.stubSearch({ statusCode: 200, body: { results: [selectedMatch] } }, 'coverageSearch')
      await api.stubRepairWrites(id)

      await page.goto(`/audiobooks/${id}`)
      await page.locator('button[aria-label="Repair Book"]').click()
      await searchForRepairMatch(page, api, id, selectedMatch)
      await page.locator('[data-testid="repair-confirm"]').click()
      await confirmRepair(page, 1)

      const { request } = await api.wait('saveAudiobookIdentifiers')
      expect(request.body.identifiers).toEqual(expected)
      await expect(repairResult(page, id)).toHaveAttribute('data-result-state', 'done')
    })
  })

  test('C02: keeps the newly repaired record when going Back and repairing the same book again', async ({
    page,
    api,
  }) => {
    const id = 405
    const oldAsin = 'B000000406'
    const firstMatch = repairSearchResultFixture({
      asin: 'B000000407',
      title: 'First Replacement',
      authors: [{ name: 'Repeat Author' }],
      region: 'fr',
    })
    const secondMatch = repairSearchResultFixture({
      asin: 'B000000408',
      title: 'Second Replacement',
      authors: [{ name: 'Repeat Author' }],
      region: 'us',
    })
    const audiobook = audiobookFixture({ id, title: 'Repeat Repair Book', authors: ['Repeat Author'], asin: oldAsin })
    const rescanBooks = [firstMatch, secondMatch].map((match) =>
      audiobookFixture({ id, title: match.title, authors: ['Repeat Author'], asin: match.asin }),
    )
    await api.stubRepairPageWithResults(
      id,
      audiobook,
      [{ type: 'Asin', value: oldAsin, region: 'us', isPrimary: true, source: 'Imported' }],
      rescanBooks,
    )
    await api.stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    await api.stubMetadata({ statusCode: 200, body: { metadata: { asin: oldAsin, title: audiobook.title } } })
    let searchIndex = 0
    const matches = [firstMatch, secondMatch]
    await api.stubSearch(
      () => ({ statusCode: 200, body: { results: [matches[searchIndex++]!] } }),
      'repeatRepairSearch',
    )

    await page.goto(`/audiobooks/${id}`)
    await page.locator('button[aria-label="Repair Book"]').click()
    await searchForRepairMatch(page, api, id, firstMatch, 'repeatRepairSearch')
    await page.locator('[data-testid="repair-confirm"]').click()
    await confirmRepair(page, 1)
    await expect(repairResult(page, id)).toHaveAttribute('data-result-state', 'done')

    await page.locator('[data-testid="repair-cancel"]').click()
    await expect(repairRow(page, id)).toBeVisible()
    await expect(repairRow(page, id)).toContainText(firstMatch.asin)
    await searchForRepairMatch(page, api, id, secondMatch, 'repeatRepairSearch')
    await page.locator('[data-testid="repair-confirm"]').click()
    await confirmRepair(page, 1)
    await expect(repairResult(page, id)).toHaveAttribute('data-result-state', 'done')
    await expect.poll(() => api.calls('saveAudiobookIdentifiers').length).toBe(2)
    const requests = api.calls('saveAudiobookIdentifiers')
    expect(requests[0]!.request.body.identifiers).toEqual([
      { type: 'Asin', value: firstMatch.asin, region: 'fr', isPrimary: true, source: 'Manual' },
    ])
    expect(requests[1]!.request.body.identifiers).toEqual([
      { type: 'Asin', value: secondMatch.asin, region: 'us', isPrimary: true, source: 'Manual' },
    ])
  })

  const writeFailureCases = [
    { name: 'identifier save', id: 406, stage: 'identifiers' as const, initialState: 'failed' },
    { name: 'metadata clear', id: 407, stage: 'metadata' as const, initialState: 'warning' },
    { name: 'rescan', id: 408, stage: 'rescan' as const, initialState: 'warning' },
  ]

  writeFailureCases.forEach(({ name, id, stage, initialState }) => {
    test(`C03: retries after a ${name} write failure`, async ({ page, api }) => {
      const oldAsin = `B000000${id}`
      const match = repairSearchResultFixture({
        asin: `B000001${id}`,
        title: `Retry after ${name}`,
        authors: [{ name: 'Retry Author' }],
      })
      const failOnce = [{ statusCode: 500, body: { message: `${name} failed` } }, {}]
      await api.stubRepairPage(
        id,
        audiobookFixture({ id, title: 'Write Failure Book', authors: ['Retry Author'], asin: oldAsin }),
        [{ type: 'Asin', value: oldAsin, region: 'us', isPrimary: true, source: 'Imported' }],
      )
      await api.stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
      await api.stubMetadata({ statusCode: 200, body: { metadata: { asin: oldAsin, title: 'Write Failure Book' } } })
      await api.stubSearch({ statusCode: 200, body: { results: [match] } }, 'coverageSearch')
      await api.stubRepairWrites(id, { [stage]: failOnce })

      await page.goto(`/audiobooks/${id}`)
      await page.locator('button[aria-label="Repair Book"]').click()
      await searchForRepairMatch(page, api, id, match)
      await page.locator('[data-testid="repair-confirm"]').click()
      await confirmRepair(page, 1)
      await expect(repairResult(page, id)).toHaveAttribute('data-result-state', initialState)
      await repairResult(page, id).locator('[data-testid="repair-result-retry"]').click()
      await expect(repairResult(page, id)).toHaveAttribute('data-result-state', 'done')
      const alias =
        stage === 'identifiers'
          ? 'saveAudiobookIdentifiers'
          : stage === 'metadata'
            ? 'clearAudiobookMetadata'
            : 'rescanAudiobookMetadata'
      await expect.poll(() => api.calls(alias).length).toBe(2)
    })
  })

  test('C15: blocks a record selected on one repair card from another card', async ({ page, api }) => {
    const firstId = 415
    const secondId = 416
    const firstTitle = 'First Repair Card'
    const secondTitle = 'Second Repair Card'
    const sharedMatch = repairSearchResultFixture({
      asin: 'B000000417',
      title: 'Shared Candidate',
      authors: [{ name: 'Shared Author' }],
    })
    const books = [
      { id: firstId, title: firstTitle, asin: 'B000000415' },
      { id: secondId, title: secondTitle, asin: 'B000000416' },
    ]
    await api.stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    await api.stubLibrary(books.map((book) => libraryAudiobookFixture({ ...book, authors: ['Shared Author'] })))
    for (const book of books) {
      await api.stubAudiobook(book.id, audiobookFixture({ ...book, authors: ['Shared Author'] }))
      await api.stubAudiobookIdentifiers(book.id, [
        { type: 'Asin', value: book.asin, region: 'us', isPrimary: true, source: 'Imported' },
      ])
      await api.stubWeakStorageMissingFiles(book.id)
      await api.stubRepairWrites(book.id)
    }
    await api.stubSearch({ statusCode: 200, body: { results: [sharedMatch] } }, 'crossCardSearch')

    await page.goto(LIBRARY_PATH)
    await api.wait('getLibrary')
    await expect(libraryBook(page, firstTitle)).toBeVisible()
    await expect(libraryBook(page, secondTitle)).toBeVisible()
    await page.getByTestId('select-all-audiobooks').click()
    await page.locator('button', { hasText: 'Repair Selected (2)' }).click()
    for (const id of [firstId, secondId]) await expect(repairRow(page, id)).toHaveCount(1)
    await searchForRepairMatch(page, api, firstId, sharedMatch, 'crossCardSearch')
    await searchForRepairMatch(
      page,
      api,
      secondId,
      sharedMatch,
      'crossCardSearch',
      false,
      `${sharedMatch.title} second card`,
    )
    const inUse = repairRow(page, secondId).locator('[data-testid="find-match-in-use"]')
    await expect(inUse).toBeVisible()
    await expect(inUse).toHaveAttribute('title', new RegExp(firstTitle))
    await expect(repairRow(page, secondId).locator('[data-testid="match-picked"]')).toHaveCount(0)
    await expect(page.locator('[data-testid="repair-confirm"]')).toContainText('Repair 1')
  })

  test('C18: continues after a selected book becomes unavailable and lets its failed retry finish', async ({
    page,
    api,
  }) => {
    const unavailableId = 418
    const availableId = 419
    const unavailableAsin = 'B000000418'
    const availableAsin = 'B000000419'
    const unavailableMatch = repairSearchResultFixture({
      asin: 'B000000420',
      title: 'Unavailable Book Match',
      authors: [{ name: 'Batch Author' }],
    })
    const availableMatch = repairSearchResultFixture({
      asin: 'B000000421',
      title: 'Available Book Match',
      authors: [{ name: 'Batch Author' }],
    })
    const books = [
      { id: unavailableId, title: 'Unavailable Book', asin: unavailableAsin },
      { id: availableId, title: 'Available Book', asin: availableAsin },
    ]
    await api.stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    await api.stubLibrary(books.map((book) => libraryAudiobookFixture({ ...book, authors: ['Batch Author'] })))
    for (const book of books) {
      await api.stubAudiobook(book.id, audiobookFixture({ ...book, authors: ['Batch Author'] }))
      await api.stubAudiobookIdentifiers(book.id, [
        { type: 'Asin', value: book.asin, region: 'us', isPrimary: true, source: 'Imported' },
      ])
      await api.stubWeakStorageMissingFiles(book.id)
    }
    await api.stubRepairWrites(unavailableId, {
      identifiers: [{ statusCode: 404, body: { message: 'Audiobook not found' } }],
    })
    await api.stubRepairWrites(availableId)
    let searchIndex = 0
    const matches = [unavailableMatch, availableMatch]
    await api.stubSearch(
      () => ({ statusCode: 200, body: { results: [matches[searchIndex++]!] } }),
      'deletedMidRunSearch',
    )

    await page.goto(LIBRARY_PATH)
    await api.wait('getLibrary')
    await expect(libraryBook(page, 'Unavailable Book')).toBeVisible()
    await expect(libraryBook(page, 'Available Book')).toBeVisible()
    await page.getByTestId('select-all-audiobooks').click()
    await page.locator('button', { hasText: 'Repair Selected (2)' }).click()
    await searchForRepairMatch(page, api, unavailableId, unavailableMatch, 'deletedMidRunSearch')
    await searchForRepairMatch(page, api, availableId, availableMatch, 'deletedMidRunSearch')
    await page.locator('[data-testid="repair-confirm"]').click()
    await confirmRepair(page, 2)
    await expect(repairResult(page, unavailableId)).toHaveAttribute('data-result-state', 'failed')
    await expect(repairResult(page, availableId)).toHaveAttribute('data-result-state', 'done')
    await repairResult(page, unavailableId).locator('[data-testid="repair-result-retry"]').click()
    await expect(repairResult(page, unavailableId)).toHaveAttribute('data-result-state', 'failed')
    await page.locator('body').press('Escape')
    await expect(page.locator('.modal-content')).toHaveCount(0)
  })

  test('C13: reports the rescan rate limit and offers Retry without waiting through the cooldown', async ({
    page,
    api,
  }) => {
    const id = 313
    const oldAsin = 'B000000313'
    const match = repairSearchResultFixture({
      asin: 'B000000314',
      title: 'Rate Limited Match',
      authors: [{ name: 'Repair Author' }],
      region: 'us',
    })
    const audiobook = audiobookFixture({ id, title: 'Repair Book', authors: ['Repair Author'], asin: oldAsin })
    const identifiers = [{ type: 'Asin', value: oldAsin, region: 'us', isPrimary: true, source: 'Imported' }]
    await api.stubRepairPage(id, audiobook, identifiers)
    await api.stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    await api.stubMetadata({ statusCode: 200, body: { metadata: { asin: oldAsin, title: audiobook.title } } })
    await api.stubSearch({ statusCode: 200, body: { results: [match] } }, 'rateLimitSearch')
    await api.stubRepairWrites(id, [
      { statusCode: 429, body: { message: 'Too many rescans', retryAfterSeconds: 600 } },
    ])

    await page.goto(`/audiobooks/${id}`)
    await page.locator('button[aria-label="Repair Book"]').click()
    await searchForRepairMatch(page, api, id, match, 'rateLimitSearch')
    await page.locator('[data-testid="repair-confirm"]').click()
    await confirmRepair(page, 1)

    await api.wait('saveAudiobookIdentifiers')
    await api.wait('clearAudiobookMetadata')
    await api.wait('rescanAudiobookMetadata')
    await expect(repairResult(page, id)).toHaveAttribute('data-result-state', 'warning')
    await expect(repairResult(page, id).locator('[data-testid="repair-result-message"]')).toContainText(
      'too many rescans',
    )
    const retry = repairResult(page, id).locator('[data-testid="repair-result-retry"]')
    await expect(retry).toBeVisible()
    await expect(retry).toBeEnabled()
    await expect.poll(() => api.calls('rescanAudiobookMetadata').length).toBe(1)
  })

  test('C14: sends the selected region when repairing to a result from another region', async ({ page, api }) => {
    const id = 314
    const oldAsin = 'B000000315'
    const match = repairSearchResultFixture({
      asin: 'B000000316',
      title: 'Japanese Edition',
      authors: [{ name: 'Japanese Author' }],
      region: 'jp',
    })
    const audiobook = audiobookFixture({
      id,
      title: 'Region Repair Book',
      authors: ['Japanese Author'],
      asin: oldAsin,
    })
    await api.stubRepairPage(id, audiobook, [
      { type: 'Asin', value: oldAsin, region: 'us', isPrimary: true, source: 'Imported' },
    ])
    await api.stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    await api.stubMetadata({ statusCode: 200, body: { metadata: { asin: oldAsin, title: audiobook.title } } })
    await api.stubSearch({ statusCode: 200, body: { results: [match] } }, 'regionRepairSearch')
    await api.stubRepairWrites(id)

    await page.goto(`/audiobooks/${id}`)
    await page.locator('button[aria-label="Repair Book"]').click()
    await repairRow(page, id).locator('[data-testid="match-asin"]').fill('')
    await repairRow(page, id).locator('[data-testid="match-title"]').fill(match.title)
    await repairRow(page, id).locator('[data-testid="match-author"]').fill('Japanese Author')
    await repairRow(page, id).locator('[data-testid="match-region"]').selectOption('jp')
    const search = await api.wait('regionRepairSearch')
    expect(search.request.body).toMatchObject({ region: 'jp' })
    const result = page.locator('[data-testid="find-match-result"]', { hasText: match.title }).first()
    await expect(result).toBeVisible()
    await result.click()
    await page.locator('[data-testid="repair-confirm"]').click()
    await confirmRepair(page, 1)

    const save = await api.wait('saveAudiobookIdentifiers')
    expect(save.request.body).toEqual({
      identifiers: [{ type: 'Asin', value: match.asin, region: 'jp', isPrimary: true, source: 'Manual' }],
    })
    await expect(repairResult(page, id)).toHaveAttribute('data-result-state', 'done')
    await expect(repairResult(page, id)).toContainText(match.asin)
  })

  test('C16: adds a searched title using metadata returned for the selected region', async ({ page, api }) => {
    const asin = 'B000000316'
    const metadata = {
      asin,
      title: 'The Selected Edition',
      authors: [{ name: 'Selected Author' }],
      region: 'fr',
      series: [{ name: 'Selected Series', position: 1 }],
      genres: [{ name: 'Adventure' }],
      language: 'french',
    }
    const result = {
      id: asin,
      title: metadata.title,
      artist: 'Selected Author',
      album: metadata.title,
      category: 'Audiobook',
      source: 'Audible',
      publishedDate: '2024-01-01',
      format: 'M4B',
      authors: metadata.authors,
      asin,
      region: 'fr',
      series: 'Selected Series',
      genres: ['Adventure'],
      language: metadata.language,
      metadataSource: 'Audible',
    }
    await api.stubIntegrationDefaults()
    await api.stubSettings(
      settingsFixture({ defaultSearchRegion: 'fr', defaultSearchLanguage: 'french', outputPath: '/audiobooks' }),
    )
    await api.stubRootFolders([rootFolderFixture({ id: 1, path: '/audiobooks', isDefault: true })])
    await api.stubSearch({ statusCode: 200, body: [result] }, 'addNewSearch')
    await api.stubQualityProfiles([{ id: 1, name: 'Standard', isDefault: true }])
    await api.stubMetadata({ statusCode: 200, body: { metadata } }, 'addNewMetadata')
    await api.stubLibraryPathPreview(
      '/audiobooks/Selected Author/Selected Series/The Selected Edition',
      'Selected Author/Selected Series/The Selected Edition',
      '/audiobooks',
    )
    await api.stubAddLibrary(audiobookFixture({ id: 316, title: metadata.title, authors: ['Selected Author'], asin }))

    await page.goto('/add-new')
    await page.locator('#unified-search-input').pressSequentially('The Selected Edition')
    await page.locator('button[aria-label="Execute search"]').click()
    await api.wait('addNewSearch')
    await page
      .locator('.title-result-card', { hasText: metadata.title })
      .first()
      .locator('button', { hasText: 'Add to Library' })
      .click()
    const metadataCall = await api.wait('addNewMetadata')
    expect(metadataCall.request.url).toContain(`${asin}?region=fr`)
    const preview = await api.wait('previewLibraryPath')
    expect(preview.request.body).toMatchObject({ destinationRoot: '/audiobooks' })
    await expect(page.locator('.path-validation-error')).toHaveCount(0)
    const addButton = page.locator('.modal-content').last().locator('button', { hasText: 'Add to Library' })
    await expect(addButton).toBeEnabled()
    await addButton.click()
    const { request } = await api.wait('addAudiobookToLibrary')
    expect(request.body.qualityProfileId).toBe(1)
    expect(request.body.metadata).toMatchObject({
      asin,
      title: metadata.title,
      region: 'fr',
      series: 'Selected Series',
    })
    expect(request.body.metadata.genres).toEqual(['Adventure'])
    await expect(page.locator('.modal-content')).toHaveCount(0)
  })

  test('C17: keeps the repair dialog and its content within a small viewport', async ({ page, api }) => {
    const id = 317
    const oldAsin = 'B000000317'
    const match = repairSearchResultFixture({ asin: 'B000000318', title: 'Small Screen Match' })
    const audiobook = audiobookFixture({ id, title: 'Small Screen Book', asin: oldAsin })
    await api.stubRepairPage(id, audiobook, [
      { type: 'Asin', value: oldAsin, region: 'us', isPrimary: true, source: 'Imported' },
    ])
    await api.stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    await api.stubMetadata({ statusCode: 200, body: { metadata: { asin: oldAsin, title: audiobook.title } } })
    await api.stubSearch({ statusCode: 200, body: { results: [match] } }, 'smallScreenSearch')
    await api.stubRepairWrites(id)

    await page.setViewportSize({ width: 1024, height: 700 })
    await page.goto(`/audiobooks/${id}`)
    await page.locator('button[aria-label="Repair Book"]').click()
    await expect(async () => {
      const innerWidth = await page.evaluate(() => window.innerWidth)
      const bounds = await page
        .locator('.modal-content')
        .last()
        .evaluate((modal) => {
          const { left, right } = modal.getBoundingClientRect()
          return { left, right }
        })
      expect(bounds.left).toBeGreaterThanOrEqual(-1)
      expect(bounds.right).toBeLessThanOrEqual(innerWidth + 1)
    }).toPass({ timeout: 5000 })
    await repairRow(page, id).locator('[data-testid="match-asin"]').fill('')
    await repairRow(page, id).locator('[data-testid="match-title"]').fill(match.title)
    await repairRow(page, id).locator('[data-testid="match-author"]').fill('Test Author')
    await repairRow(page, id).locator('[data-testid="match-search"]').click()
    await api.wait('smallScreenSearch')
    await expect(page.locator('[data-testid="find-match-result"]', { hasText: match.title }).first()).toBeVisible()
    await expect(page.locator('[data-testid="repair-confirm"]')).toBeVisible()
  })

  test('C19: marks a Find Match record as owned when another library book has files', async ({ page, api }) => {
    const imported = libraryImportUnmatchedFixture({
      fullPath: '/incoming/Author/Unmatched Book/Unmatched Book.m4b',
      relativePath: 'Author/Unmatched Book',
      bookFolder: '/incoming/Author/Unmatched Book',
      title: 'Unmatched Book',
      author: 'Author',
    })
    const result = libraryImportSearchResultFixture({ asin: 'B000000319', title: 'Owned Edition' })
    await api.stubIntegrationDefaults()
    await api.stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    await api.stubRootFolders([rootFolderFixture({ id: 1 })])
    await api.stubLibrary([
      libraryAudiobookFixture({ id: 319, title: 'Existing Book', asin: result.asin, asinRegion: 'us', fileCount: 2 }),
    ])
    await api.stubLibraryImportSavedResults(1, [imported])
    await api.stubSearch(() => ({ statusCode: 200, body: [result] }), 'libraryImportSearch')

    await page.goto('/library-import')
    await api.wait('getRootFolders')
    await api.wait('getSettings')
    await api.wait('savedLibraryImportResults')
    await page.locator('.import-row').locator('button[title="Search for a match"]').click()
    const ownedResult = page.locator('[data-testid="find-match-result"]', { hasText: result.title }).first()
    await expect(ownedResult).toContainText('In use')
    await expect(ownedResult.locator('[data-testid="find-match-in-use"]')).toHaveAttribute(
      'title',
      /Existing Book/,
    )
    await expect(page.locator('[data-testid="find-match-confirm"]')).toBeDisabled()
  })

  test('C19: does not mark a Find Match record as owned by a book without files', async ({ page, api }) => {
    const imported = libraryImportUnmatchedFixture({
      fullPath: '/incoming/Author/Wanted Book/Wanted Book.m4b',
      relativePath: 'Author/Wanted Book',
      bookFolder: '/incoming/Author/Wanted Book',
      title: 'Wanted Book',
      author: 'Author',
    })
    const result = libraryImportSearchResultFixture({ asin: 'B000000320', title: 'Wanted Edition' })
    await api.stubIntegrationDefaults()
    await api.stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    await api.stubRootFolders([rootFolderFixture({ id: 1 })])
    await api.stubLibrary([
      libraryAudiobookFixture({ id: 320, title: 'Empty Book', asin: result.asin, asinRegion: 'us', fileCount: 0 }),
    ])
    await api.stubLibraryImportSavedResults(1, [imported])
    await api.stubSearch(() => ({ statusCode: 200, body: [result] }), 'libraryImportSearch')

    await page.goto('/library-import')
    await api.wait('getRootFolders')
    await api.wait('getSettings')
    await api.wait('savedLibraryImportResults')
    await page.locator('.import-row').locator('button[title="Search for a match"]').click()
    const wantedResult = page.locator('[data-testid="find-match-result"]', { hasText: result.title }).first()
    await expect(wantedResult).not.toContainText('Empty Book')
    await wantedResult.click()
    await expect(page.locator('[data-testid="find-match-confirm"]')).toBeEnabled()
  })
})
