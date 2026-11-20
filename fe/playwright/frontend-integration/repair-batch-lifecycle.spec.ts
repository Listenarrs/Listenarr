import type { Page } from '@playwright/test'
import {
  audiobookFixture,
  expect,
  libraryAudiobookFixture,
  repairSearchResultFixture,
  test,
  type ApiStubs,
  LIBRARY_PATH,
} from '../support/test.js'

type RepairReplies = Parameters<ApiStubs['stubRepairWrites']>[1]

const firstAudiobookId = 101
const secondAudiobookId = 102
const firstOldAsin = 'B000000101'
const secondOldAsin = 'B000000102'
const firstMatch = repairSearchResultFixture({
  asin: 'B000000201',
  title: 'First Repaired Edition',
  authors: [{ name: 'First Author' }],
})
const secondMatch = repairSearchResultFixture({
  asin: 'B000000202',
  title: 'Second Repaired Edition',
  authors: [{ name: 'Second Author' }],
})
const firstRescanSuccess = {
  statusCode: 200,
  body: {
    message: 'Metadata refreshed',
    audiobookId: firstAudiobookId,
    asin: firstMatch.asin,
    region: firstMatch.region,
  },
}
const secondRescanSuccess = {
  statusCode: 200,
  body: {
    message: 'Metadata refreshed',
    audiobookId: secondAudiobookId,
    asin: secondMatch.asin,
    region: secondMatch.region,
  },
}

const repairResult = (page: Page, audiobookId: number) =>
  page.locator(`[data-testid="repair-result-${audiobookId}"]`)

async function prepareBatchRepair(
  page: Page,
  api: ApiStubs,
  firstRescanReplies: RepairReplies = [],
  secondRescanReplies: RepairReplies = [],
) {
  const firstAudiobook = audiobookFixture({
    id: firstAudiobookId,
    title: 'First Old Title',
    authors: ['First Author'],
    asin: firstOldAsin,
  })
  const secondAudiobook = audiobookFixture({
    id: secondAudiobookId,
    title: 'Second Old Title',
    authors: ['Second Author'],
    asin: secondOldAsin,
  })

  await api.stubSettings({ defaultSearchRegion: 'us' })
  await api.stubLibrary([
    libraryAudiobookFixture({
      id: firstAudiobookId,
      title: firstAudiobook.title,
      authors: firstAudiobook.authors,
      asin: firstOldAsin,
    }),
    libraryAudiobookFixture({
      id: secondAudiobookId,
      title: secondAudiobook.title,
      authors: secondAudiobook.authors,
      asin: secondOldAsin,
    }),
  ])

  for (const [audiobookId, audiobook, oldAsin] of [
    [firstAudiobookId, firstAudiobook, firstOldAsin],
    [secondAudiobookId, secondAudiobook, secondOldAsin],
  ] as const) {
    await api.stubAudiobook(audiobookId, audiobook)
    await api.stubAudiobookIdentifiers(audiobookId, [
      { type: 'Asin', value: oldAsin, region: 'us', isPrimary: true, source: 'Imported' },
    ])
    await api.stubWeakStorageMissingFiles(audiobookId)
  }
  await api.stubRepairWrites(firstAudiobookId, firstRescanReplies)
  await api.stubRepairWrites(secondAudiobookId, secondRescanReplies)

  let searchIndex = 0
  const matches = [firstMatch, secondMatch]
  await api.stubSearch(
    () => ({
      statusCode: 200,
      body: { results: [matches[searchIndex++]!] },
    }),
    'repairBatchSearch',
  )

  await page.goto(LIBRARY_PATH)
  await page.locator('button', { hasText: 'Select All' }).first().click()
  await page.locator('button', { hasText: 'Repair Selected (2)' }).click()
  await expect(page.locator(`[data-testid="repair-row-${firstAudiobookId}"]`)).toHaveCount(1)
  await expect(page.locator(`[data-testid="repair-row-${secondAudiobookId}"]`)).toHaveCount(1)
}

async function chooseMatch(page: Page, api: ApiStubs, audiobookId: number, match: typeof firstMatch) {
  const row = page.locator(`[data-testid="repair-row-${audiobookId}"]`)
  await row.locator('[data-testid="match-asin"]').clear()
  await row.locator('[data-testid="match-title"]').fill(match.title)
  await row.locator('[data-testid="match-author"]').fill(match.authors[0]!.name)
  await row.locator('[data-testid="match-search"]').click()

  const { request } = await api.wait('repairBatchSearch')
  expect(request.body).toEqual({
    mode: 'Advanced',
    title: match.title,
    author: match.authors[0]!.name,
    region: match.region,
    pagination: { page: 1, limit: 50 },
  })
  await page.locator('[data-testid="find-match-result"]', { hasText: match.title }).click()
  await expect(
    row.locator('[data-testid="find-match-result"]', { hasText: match.title }),
  ).toHaveAttribute('aria-selected', 'true')
}

async function confirmBatchRepair(page: Page) {
  const confirm = page.locator('[data-testid="repair-confirm"]')
  await expect(confirm).toBeEnabled()
  await confirm.click()
  await expect(page.locator('.confirm-dialog')).toContainText('Repair 2 books?')
  await page.locator('.confirm-dialog').locator('button', { hasText: 'Repair 2' }).click()
}

test.describe('Repair Books batch lifecycle (API-stubbed)', () => {
  test('keeps only successful repairs eligible for Organize until a failed book is retried successfully', async ({
    page,
    api,
  }) => {
    await prepareBatchRepair(
      page,
      api,
      [firstRescanSuccess],
      [{ statusCode: 500, body: { message: 'Metadata service unavailable' } }, secondRescanSuccess],
    )
    await chooseMatch(page, api, firstAudiobookId, firstMatch)
    await chooseMatch(page, api, secondAudiobookId, secondMatch)
    await confirmBatchRepair(page)

    const first = repairResult(page, firstAudiobookId)
    const second = repairResult(page, secondAudiobookId)
    const organize = page.locator('[data-testid="repair-organize"]')

    await expect(first).toHaveAttribute('data-result-state', 'done')
    await expect(first.locator('[data-testid="repair-result-include"]')).toBeEnabled()
    await expect(first.locator('[data-testid="repair-result-include"]')).toBeChecked()
    await expect(second).toHaveAttribute('data-result-state', 'warning')
    await expect(second.locator('[data-testid="repair-result-message"]')).toContainText('metadata rescan failed')
    await expect(second.locator('[data-testid="repair-result-include"]')).toBeDisabled()
    await expect(second.locator('[data-testid="repair-result-include"]')).not.toBeChecked()
    await expect(second.locator('[data-testid="repair-result-retry"]')).toBeVisible()
    await expect(second.locator('[data-testid="repair-result-retry"]')).toBeEnabled()
    await expect(organize).toBeEnabled()
    await expect(organize).toContainText('Organize 1')

    await second.locator('[data-testid="repair-result-retry"]').click()

    await expect(first).toHaveAttribute('data-result-state', 'done')
    await expect(second).toHaveAttribute('data-result-state', 'done')
    await expect(second.locator('[data-testid="repair-result-retry"]')).toHaveCount(0)
    await expect(second.locator('[data-testid="repair-result-include"]')).toBeEnabled()
    await expect(second.locator('[data-testid="repair-result-include"]')).toBeChecked()
    await expect(organize).toBeEnabled()
    await expect(organize).toContainText('Organize 2')
    await expect.poll(() => api.calls('rescanAudiobookMetadata').length).toBe(3)
  })

  test('finishes the in-flight repair after cancel and never sends the queued repair', async ({ page, api }) => {
    await prepareBatchRepair(page, api, [{ ...firstRescanSuccess, delay: 2500 }])
    await chooseMatch(page, api, firstAudiobookId, firstMatch)
    await chooseMatch(page, api, secondAudiobookId, secondMatch)
    await confirmBatchRepair(page)

    const first = repairResult(page, firstAudiobookId)
    const second = repairResult(page, secondAudiobookId)
    const organize = page.locator('[data-testid="repair-organize"]')

    await expect(first.locator('[data-testid="repair-result-message"]')).toContainText('Repairing')
    await page.locator('[data-testid="repair-cancel"]').click()

    await expect(first).toHaveAttribute('data-result-state', 'done')
    await expect(second).toHaveAttribute('data-result-state', 'skipped')
    await expect(second.locator('[data-testid="repair-result-message"]')).toContainText('Canceled: not repaired')
    await expect(second.locator('[data-testid="repair-result-include"]')).toBeDisabled()
    await expect(organize).toBeEnabled()
    await expect(organize).toContainText('Organize 1')
    await expect.poll(() => api.calls('rescanAudiobookMetadata').length).toBe(1)
    expect(api.calls('rescanAudiobookMetadata')[0]?.request.url).toContain(
      `/api/v1/library/${firstAudiobookId}/rescan-metadata`,
    )
  })
})
