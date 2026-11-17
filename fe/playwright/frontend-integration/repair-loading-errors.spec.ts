import { audiobookFixture, expect, libraryAudiobookFixture, test, LIBRARY_PATH } from '../support/test.js'

const temporaryId = 201
const deletedId = 202
const availableId = 203

test.describe('Repair Books loading errors (API-stubbed)', () => {
  test.beforeEach(async ({ api }) => {
    const temporaryBook = audiobookFixture({
      id: temporaryId,
      title: 'Temporary Load Failure',
      authors: ['Test Author'],
      asin: 'B000000201',
    })

    await api.stubSettings({ defaultSearchRegion: 'us' })
    await api.stubLibrary([
      libraryAudiobookFixture({
        id: temporaryId,
        title: temporaryBook.title,
        authors: temporaryBook.authors,
        asin: temporaryBook.asin,
      }),
      libraryAudiobookFixture({
        id: deletedId,
        title: 'Deleted During Selection',
        authors: ['Test Author'],
        asin: 'B000000202',
      }),
      libraryAudiobookFixture({
        id: availableId,
        title: 'Available Book',
        authors: ['Test Author'],
        asin: 'B000000203',
      }),
    ])
    await api.stubAudiobookReplies(temporaryId, [
      { statusCode: 503, body: { message: 'Temporary library read failure' } },
      { statusCode: 200, body: temporaryBook },
    ])
    await api.stubAudiobookReplies(deletedId, [{ statusCode: 404, body: { message: 'Audiobook not found' } }])
    await api.stubAudiobookReplies(availableId, [
      {
        statusCode: 200,
        body: audiobookFixture({
          id: availableId,
          title: 'Available Book',
          authors: ['Test Author'],
          asin: 'B000000203',
        }),
      },
    ])
    await api.stubAudiobookIdentifiers(temporaryId, [])
    await api.stubAudiobookIdentifiers(deletedId, [])
    await api.stubAudiobookIdentifiers(availableId, [])
    await api.stubWeakStorageMissingFiles(temporaryId)
    await api.stubWeakStorageMissingFiles(deletedId)
    await api.stubWeakStorageMissingFiles(availableId)
  })

  test('lets a temporary load failure recover while a deleted selection stays unavailable', async ({
    page,
    api,
  }) => {
    const temporaryRow = page.locator(`[data-testid="repair-row-${temporaryId}"]`)
    const deletedRow = page.locator(`[data-testid="repair-row-${deletedId}"]`)
    const availableRow = page.locator(`[data-testid="repair-row-${availableId}"]`)

    await page.goto(LIBRARY_PATH)
    await page.locator('button', { hasText: 'Select All' }).first().click()
    await page.locator('button', { hasText: 'Repair Selected (3)' }).first().click()

    await api.wait('getAudiobook')
    await api.wait('getAudiobook')
    await api.wait('getAudiobook')
    await expect(temporaryRow.locator('[data-testid="repair-load-error"]')).toHaveCount(1)
    await expect(temporaryRow.locator('[data-testid="repair-load-again"]')).toBeEnabled()
    await expect(deletedRow.locator('[data-testid="repair-load-error"]')).toHaveCount(1)
    await expect(deletedRow.locator('[data-testid="repair-load-again"]')).toHaveCount(0)
    await expect(availableRow.locator('[data-testid="match-title"]')).toHaveValue('Available Book')

    await temporaryRow.locator('[data-testid="repair-load-again"]').click()
    await api.wait('getAudiobook')

    await expect(temporaryRow.locator('[data-testid="repair-load-error"]')).toHaveCount(0)
    await expect(temporaryRow.locator('[data-testid="match-title"]')).toHaveValue('Temporary Load Failure')
    await expect(deletedRow.locator('[data-testid="repair-load-error"]')).toHaveCount(1)
    await expect.poll(() => api.calls('getAudiobook').length).toBe(4)
  })
})
