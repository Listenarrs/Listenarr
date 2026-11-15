import type { Page } from '@playwright/test'
import {
  audiobookFixture,
  expect,
  repairSearchResultFixture,
  settingsFixture,
  test,
  type ApiStubs,
} from '../support/test.js'

const audiobookId = 1
const oldAsin = 'B000000001'
const selectedMatch = repairSearchResultFixture({
  asin: 'B000000099',
  title: 'New Audible Edition',
  authors: [{ name: 'New Author' }],
})
const audiobook = audiobookFixture({
  id: audiobookId,
  title: 'Old Library Title',
  authors: ['Old Author'],
  asin: oldAsin,
})
const oldIdentifiers = [
  { type: 'Asin', value: oldAsin, region: 'us', isPrimary: true, source: 'Imported' },
  { type: 'Asin', value: selectedMatch.asin, region: 'uk', isPrimary: false, source: 'Manual' },
  { type: 'Isbn', value: '9780000000001', region: null, isPrimary: true, source: 'Imported' },
  { type: 'Isbn', value: '9780000000002', region: null, isPrimary: false, source: 'Manual' },
]
const emptiedRecord = {
  subtitle: '',
  series: '',
  seriesNumber: '',
  seriesMemberships: [],
  description: '',
  publisher: '',
  publishYear: '',
  publishedDate: '',
  language: '',
  version: '',
  edition: '',
  narrators: [],
  genres: [],
  isbn: [],
}

const repairRow = (page: Page) => page.locator(`[data-testid="repair-row-${audiobookId}"]`)
const repairResult = (page: Page) => page.locator(`[data-testid="repair-result-${audiobookId}"]`)
const settingsLink = (page: Page) => page.locator('a', { hasText: 'Settings' }).first()

const successfulRescanReply = () => ({
  statusCode: 200,
  body: {
    message: 'Metadata refreshed',
    audiobookId,
    asin: selectedMatch.asin,
    region: selectedMatch.region,
    audiobook: audiobookFixture({
      ...audiobook,
      title: selectedMatch.title,
      authors: selectedMatch.authors.map((author) => author.name),
      asin: selectedMatch.asin,
    }),
  },
})

async function prepareRepair(
  page: Page,
  api: ApiStubs,
  rescanReplies: Parameters<ApiStubs['stubRepairWrites']>[1] = [successfulRescanReply()],
) {
  await api.stubRepairPage(audiobookId, audiobook, oldIdentifiers)
  await api.stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
  await api.stubMetadata({
    statusCode: 200,
    body: { metadata: { asin: oldAsin, title: audiobook.title } },
  })
  await api.stubSearch({ statusCode: 200, body: { results: [selectedMatch] } }, 'repairSearch')
  await api.stubRepairWrites(audiobookId, rescanReplies)

  await page.goto(`/audiobooks/${audiobookId}`)
  await page.locator('button[aria-label="Repair Book"]').click()
  await expect(repairRow(page)).toBeVisible()
}

async function chooseDifferentMatch(page: Page, api: ApiStubs) {
  const row = repairRow(page)
  await row.locator('[data-testid="match-asin"]').clear()
  await row.locator('[data-testid="match-title"]').fill(selectedMatch.title)
  await row.locator('[data-testid="match-author"]').fill(selectedMatch.authors[0]!.name)
  await row.locator('[data-testid="match-search"]').click()

  const { request } = await api.wait('repairSearch')
  expect(request.body).toEqual({
    mode: 'Advanced',
    title: selectedMatch.title,
    author: selectedMatch.authors[0]!.name,
    region: selectedMatch.region,
    pagination: { page: 1, limit: 50 },
  })
  await page.locator('[data-testid="find-match-result"]', { hasText: selectedMatch.title }).click()
  await expect(
    row.locator('[data-testid="find-match-result"]', { hasText: selectedMatch.title }),
  ).toHaveAttribute('aria-selected', 'true')
}

async function askToRepair(page: Page) {
  const confirm = page.locator('[data-testid="repair-confirm"]')
  await expect(confirm).toBeEnabled()
  await confirm.click()
  await expect(page.locator('.confirm-dialog')).toContainText('Repair 1 book?')
  await expect(page.locator('.confirm-dialog')).toContainText("This can't be undone.")
}

test.describe('Repair Books workflows (API-stubbed)', () => {
  test('confirms a different match and repairs the audiobook', async ({ page, api }) => {
    await prepareRepair(page, api)
    await chooseDifferentMatch(page, api)
    await askToRepair(page)

    await page.locator('.confirm-dialog').locator('button', { hasText: 'Repair 1' }).click()

    expect((await api.wait('saveAudiobookIdentifiers')).request.body).toEqual({
      identifiers: [
        {
          type: 'Asin',
          value: selectedMatch.asin,
          region: selectedMatch.region,
          isPrimary: true,
          source: 'Manual',
        },
        {
          type: 'Asin',
          value: selectedMatch.asin,
          region: 'uk',
          isPrimary: false,
          source: 'Manual',
        },
        {
          type: 'Isbn',
          value: '9780000000002',
          region: null,
          isPrimary: false,
          source: 'Manual',
        },
      ],
    })
    expect((await api.wait('clearAudiobookMetadata')).request.body).toEqual(emptiedRecord)
    const { request } = await api.wait('rescanAudiobookMetadata')
    expect(request.method).toBe('POST')
    expect(request.url).toContain(`/api/v1/library/${audiobookId}/rescan-metadata`)

    await expect(repairResult(page)).toHaveAttribute('data-result-state', 'done')
    await expect(repairResult(page)).toContainText(selectedMatch.title)
    await expect(repairResult(page)).toContainText(selectedMatch.asin)
    await expect(repairResult(page).locator('[data-testid="repair-result-include"]')).toBeEnabled()
    await expect(repairResult(page).locator('[data-testid="repair-result-include"]')).toBeChecked()
    await expect(page.locator('[data-testid="repair-organize"]')).toBeEnabled()
    await expect(page.locator('[data-testid="repair-organize"]')).toContainText('Organize 1')
  })

  test('cancels the repair confirmation without sending mutation requests', async ({ page, api }) => {
    await prepareRepair(page, api)
    await chooseDifferentMatch(page, api)
    await askToRepair(page)

    await page.locator('.confirm-dialog').locator('button', { hasText: 'Cancel' }).click()

    await expect(page.locator('.confirm-dialog')).toHaveCount(0)
    await expect(page.locator('[data-testid="repair-row-1"]')).toBeVisible()
    await expect(page.locator('[data-testid="repair-confirm"]')).toBeEnabled()
    expect(api.calls('saveAudiobookIdentifiers')).toHaveLength(0)
    expect(api.calls('clearAudiobookMetadata')).toHaveLength(0)
    expect(api.calls('rescanAudiobookMetadata')).toHaveLength(0)
  })

  test('offers Retry after a failed rescan and enables Organize when retry succeeds', async ({ page, api }) => {
    await prepareRepair(page, api, [
      { statusCode: 500, body: { message: 'Metadata service unavailable' } },
      successfulRescanReply(),
    ])
    await chooseDifferentMatch(page, api)
    await askToRepair(page)
    await page.locator('.confirm-dialog').locator('button', { hasText: 'Repair 1' }).click()

    const result = repairResult(page)
    const organize = page.locator('[data-testid="repair-organize"]')

    await api.wait('saveAudiobookIdentifiers')
    await api.wait('clearAudiobookMetadata')
    await api.wait('rescanAudiobookMetadata')
    await expect(result).toHaveAttribute('data-result-state', 'warning')
    await expect(result.locator('[data-testid="repair-result-message"]')).toContainText('metadata rescan failed')
    await expect(result.locator('[data-testid="repair-result-retry"]')).toBeVisible()
    await expect(result.locator('[data-testid="repair-result-retry"]')).toBeEnabled()
    await expect(result.locator('[data-testid="repair-result-include"]')).toBeDisabled()
    await expect(organize).toBeDisabled()
    await expect(organize).toContainText('Organize 0')

    await result.locator('[data-testid="repair-result-retry"]').click()
    await api.wait('clearAudiobookMetadata')
    await api.wait('rescanAudiobookMetadata')

    await expect(result).toHaveAttribute('data-result-state', 'done')
    await expect(result).toContainText(selectedMatch.title)
    await expect(result).toContainText(selectedMatch.asin)
    await expect(result.locator('[data-testid="repair-result-retry"]')).toHaveCount(0)
    await expect(result.locator('[data-testid="repair-result-include"]')).toBeEnabled()
    await expect(result.locator('[data-testid="repair-result-include"]')).toBeChecked()
    await expect(organize).toBeEnabled()
    await expect(organize).toContainText('Organize 1')
    await expect.poll(() => api.calls('rescanAudiobookMetadata').length).toBe(2)
  })

  test('blocks leaving during an active repair and allows navigation after Stay', async ({ page, api }) => {
    await prepareRepair(page, api, [{ ...successfulRescanReply(), delay: 1000 }])
    await chooseDifferentMatch(page, api)
    await askToRepair(page)
    await page.locator('.confirm-dialog').locator('button', { hasText: 'Repair 1' }).click()
    await expect(repairResult(page).locator('[data-testid="repair-result-message"]')).toContainText('Repairing')

    await settingsLink(page).dispatchEvent('click')
    await expect(page.locator('.confirm-dialog')).toContainText('Leave while books are repaired?')
    await page.locator('.confirm-dialog').locator('button', { hasText: 'Stay' }).click()
    await expect.poll(() => new URL(page.url()).pathname).toContain(`/audiobooks/${audiobookId}`)

    await api.wait('rescanAudiobookMetadata')
    await expect(repairResult(page)).toHaveAttribute('data-result-state', 'done')
    await settingsLink(page).dispatchEvent('click')
    await expect.poll(() => new URL(page.url()).pathname).toBe('/settings')
    await expect(page.locator('.confirm-dialog')).toHaveCount(0)
  })
})
