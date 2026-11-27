import type { Locator, Page } from '@playwright/test'
import { audiobookFixture, expect, rootFolderFixture, test } from '../support/test.js'

// Add New → a search result → Add to Library: the modal looks the book up on Audible before it can
// be added. The search's data and the Audible record differ on purpose, so a test can tell which one
// the modal shows. The record requests are held by the test (HeldMetadata) and answered when it says,
// so what the modal shows while one is pending is checked without timing.

const asin = 'B0ADDNEW01'

const searchResult = {
  id: asin,
  asin,
  title: 'Search Title',
  artist: 'Search Author',
  authors: [{ name: 'Search Author' }],
  category: 'Audiobook',
  source: 'Audible',
  format: 'M4B',
  region: 'us',
  language: 'english',
  metadataSource: 'Audible',
  // An enriched result opens the modal with the search's data at once; the modal then fetches the
  // record (otherwise the page fetches it first and opens the modal with it).
  isEnriched: true,
}

const audibleRecord = (region: string) => ({
  asin,
  title: 'Audible Title',
  authors: [{ name: 'Audible Author' }],
  narrators: [{ name: 'Audible Narrator' }],
  description: 'The description of the Audible record.',
  publisher: 'Audible Publisher',
  language: 'english',
  region,
})

type HeldReply = { statusCode: number; body: unknown }

/** Holds every metadata request until `answer` replies to the oldest one still waiting. */
class HeldMetadata {
  readonly urls: string[] = []
  private readonly waiting: ((reply: HeldReply) => void)[] = []

  static async install(page: Page): Promise<HeldMetadata> {
    const held = new HeldMetadata()
    // Registered after the `api` fixture's stubs, so it answers first.
    await page.route('**/api/v1/metadata/**', async (route) => {
      held.urls.push(route.request().url())
      const reply = await new Promise<HeldReply>((resolve) => held.waiting.push(resolve))
      // The page may have closed the modal, and dropped the request, meanwhile.
      await route.fulfill({ status: reply.statusCode, json: reply.body }).catch(() => undefined)
    })
    return held
  }

  private handedOut = 0

  /** Waits for the next request no earlier `next` returned, and returns its URL, leaving it unanswered. */
  async next(): Promise<string> {
    const index = this.handedOut++
    await expect
      .poll(() => this.urls.length, { message: `metadata request ${index + 1}` })
      .toBeGreaterThan(index)
    return this.urls[index]!
  }

  async answer(reply: HeldReply) {
    await expect
      .poll(() => this.waiting.length, { message: 'a held metadata request' })
      .toBeGreaterThan(0)
    this.waiting.shift()!(reply)
  }

  found(region: string) {
    return this.answer({
      statusCode: 200,
      body: { metadata: audibleRecord(region), source: 'Audible' },
    })
  }

  notFound() {
    return this.answer({ statusCode: 404, body: { message: 'not found' } })
  }
}

let metadata: HeldMetadata

const modal = (page: Page) => page.locator('.modal-content').last()
const addButton = (page: Page) =>
  modal(page).locator('button', { hasText: /Add to Library|Adding/ })
const cancelButton = (page: Page) => modal(page).locator('button', { hasText: 'Cancel' })
const loading = (page: Page) => modal(page).getByTestId('metadata-loading')
const notFound = (page: Page) => modal(page).getByTestId('metadata-not-found')
const editMetadataButton = (page: Page) =>
  modal(page).locator('button', { hasText: 'Edit Metadata' })
const qualityProfile = (page: Page) => modal(page).getByTestId('quality-profile-select')

async function openAddModal(page: Page): Promise<string> {
  await page.locator('#unified-search-input').pressSequentially('Search Title')
  await page.locator('button[aria-label="Execute search"]').click()
  await page
    .locator('.title-result-card', { hasText: 'Search Title' })
    .first()
    .locator('button', { hasText: 'Add to Library' })
    .click()
  await expect(modal(page)).toBeVisible()
  return metadata.next()
}

async function expectShowsRecord(dialog: Locator) {
  await expect(dialog.locator('.detail-header h3')).toHaveText('Audible Title')
  await expect(dialog).toContainText('by Audible Author')
  await expect(dialog).not.toContainText('Search Title')
}

test.beforeEach(async ({ page, api }) => {
  await api.stubRootFolders([rootFolderFixture({ id: 1, path: '/audiobooks', isDefault: true })])
  await api.stubQualityProfiles([{ id: 1, name: 'Standard', isDefault: true }])
  await api.stubSearch({ statusCode: 200, body: [searchResult] })
  await api.stubLibraryPathPreview(
    '/audiobooks/Audible Author/Audible Title',
    'Audible Author/Audible Title',
    '/audiobooks',
  )
  await api.stubAddLibrary(
    audiobookFixture({ id: 501, title: 'Audible Title', authors: ['Audible Author'], asin }),
  )
  metadata = await HeldMetadata.install(page)

  await page.goto('/add-new')
  await expect(page.locator('h1', { hasText: 'Add New Audiobook' })).toBeVisible()
})

test.describe('Add New: adding a search result to the library', () => {
  test('looks the book up in the default region and adds the Audible record', async ({
    page,
    api,
  }) => {
    const url = await openAddModal(page)
    expect(url).toContain(`/api/v1/metadata/${asin}?region=us`)

    await metadata.found('us')
    await expectShowsRecord(modal(page))
    await expect(qualityProfile(page)).toHaveValue('1')
    await expect(addButton(page)).toBeEnabled()

    await addButton(page).click()
    const { request } = await api.wait('addAudiobookToLibrary')
    expect(request.body.qualityProfileId).toBe(1)
    expect(request.body.metadata).toMatchObject({
      asin,
      title: 'Audible Title',
      authors: ['Audible Author'],
      region: 'us',
    })
    await expect(page.locator('.modal-content')).toHaveCount(0)
  })

  test('adds the metadata as edited once the record is loaded', async ({ page, api }) => {
    await openAddModal(page)
    await metadata.found('us')
    await expectShowsRecord(modal(page))

    await editMetadataButton(page).click()
    const title = modal(page)
      .locator('.metadata-edit-grid .detail-item', { hasText: 'Title' })
      .first()
      .locator('input')
    await expect(title).toHaveValue('Audible Title')
    await title.fill('Edited Title')

    await addButton(page).click()
    const { request } = await api.wait('addAudiobookToLibrary')
    expect(request.body.metadata).toMatchObject({ asin, title: 'Edited Title', region: 'us' })
  })

  test('cancelling closes the modal without adding anything', async ({ page, api }) => {
    await openAddModal(page)
    await metadata.found('us')
    await expectShowsRecord(modal(page))

    await cancelButton(page).click()
    await expect(page.locator('.modal-content')).toHaveCount(0)
    expect(api.calls('addAudiobookToLibrary')).toHaveLength(0)
  })

  test('without a record in the region, adding is blocked until another region finds it', async ({
    page,
    api,
  }) => {
    await openAddModal(page)
    await metadata.notFound()

    await expect(notFound(page)).toContainText(
      'No Audible record for this ASIN in the United States (US) catalogue',
    )
    await expect(addButton(page)).toBeDisabled()

    await notFound(page).getByTestId('metadata-retry-region').selectOption('uk')
    await notFound(page).getByTestId('metadata-retry').click()
    expect(await metadata.next()).toContain(`/api/v1/metadata/${asin}?region=uk`)
    await metadata.found('uk')

    await expect(notFound(page)).toHaveCount(0)
    await expectShowsRecord(modal(page))
    await expect(addButton(page)).toBeEnabled()
    await addButton(page).click()
    const { request } = await api.wait('addAudiobookToLibrary')
    expect(request.body.metadata).toMatchObject({ asin, title: 'Audible Title', region: 'uk' })
  })

  test('reopening the modal looks the record up again and drops earlier edits', async ({
    page,
  }) => {
    await openAddModal(page)
    await metadata.found('us')
    await editMetadataButton(page).click()
    await modal(page).locator('.metadata-edit-grid input').first().fill('Edited Title')
    await cancelButton(page).click()
    await expect(page.locator('.modal-content')).toHaveCount(0)

    await page
      .locator('.title-result-card', { hasText: 'Search Title' })
      .first()
      .locator('button', { hasText: 'Add to Library' })
      .click()
    expect(await metadata.next()).toContain(`/api/v1/metadata/${asin}?region=us`)
    await metadata.found('us')
    await expectShowsRecord(modal(page))
  })
})

// #1062: while the Audible record loads, the modal shows that it is loading, never the search's data
// it is about to replace, and nothing can be edited or added yet.
test.describe('Add New: the Add modal while the Audible record loads', () => {
  test('shows only a loading state until the record arrives', async ({ page }) => {
    await openAddModal(page)

    await expect(loading(page)).toBeVisible()
    await expect(loading(page)).toContainText('Fetching the Audible record')
    await expect(modal(page)).not.toContainText('Search Title')
    await expect(modal(page)).not.toContainText('Search Author')
    await expect(editMetadataButton(page)).toHaveCount(0)
    await expect(qualityProfile(page)).toHaveCount(0)
    await expect(modal(page).locator('.library-options')).toHaveCount(0)
    await expect(addButton(page)).toBeDisabled()
    await expect(addButton(page).locator('.ph-spin')).toBeVisible()
    await expect(cancelButton(page)).toBeEnabled()

    await metadata.found('us')
    await expect(loading(page)).toHaveCount(0)
    await expectShowsRecord(modal(page))
    await expect(editMetadataButton(page)).toBeVisible()
    await expect(qualityProfile(page)).toBeVisible()
    await expect(addButton(page)).toBeEnabled()
    await expect(addButton(page).locator('.ph-spin')).toHaveCount(0)
  })

  test('can be cancelled while the record loads', async ({ page, api }) => {
    await openAddModal(page)
    await expect(loading(page)).toBeVisible()

    await cancelButton(page).click()
    await expect(page.locator('.modal-content')).toHaveCount(0)
    await metadata.found('us')
    expect(api.calls('addAudiobookToLibrary')).toHaveLength(0)
  })

  test('shows the missing record on its own, without the search data', async ({ page }) => {
    await openAddModal(page)
    await metadata.notFound()

    await expect(notFound(page)).toBeVisible()
    await expect(loading(page)).toHaveCount(0)
    await expect(modal(page)).not.toContainText('Search Title')
    await expect(editMetadataButton(page)).toHaveCount(0)
    await expect(qualityProfile(page)).toHaveCount(0)
    await expect(addButton(page)).toBeDisabled()
  })

  test('shows the loading state again while Search again looks in another region', async ({
    page,
  }) => {
    await openAddModal(page)
    await metadata.notFound()
    await notFound(page).getByTestId('metadata-retry-region').selectOption('uk')
    await notFound(page).getByTestId('metadata-retry').click()
    await metadata.next()

    await expect(loading(page)).toBeVisible()
    await expect(notFound(page)).toHaveCount(0)
    await expect(modal(page)).not.toContainText('Search Title')
    await expect(addButton(page)).toBeDisabled()

    await metadata.found('uk')
    await expect(loading(page)).toHaveCount(0)
    await expectShowsRecord(modal(page))
    await expect(addButton(page)).toBeEnabled()
  })
})
