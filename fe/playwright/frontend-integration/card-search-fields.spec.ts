import type { Page } from '@playwright/test'
import { audiobookFixture, expect, libraryAudiobookFixture, settingsFixture, test } from '../support/test.js'

const audiobookId = '1'
const bookPage = `/audiobooks/${audiobookId}`
const audiobook = audiobookFixture({ id: Number(audiobookId), title: 'Dune', authors: ['Frank Herbert'] })

type SearchFields = {
  asin?: string
  title?: string
  author?: string
  region?: string
}

type SearchRequest = {
  title?: string
  author?: string
  series?: string
  region?: string
}

const field = (page: Page, name: keyof SearchFields) => page.locator(`[data-testid="match-${name}"]`)

const regionField = (page: Page) => page.locator('[data-testid="match-region"]')

const card = (page: Page) => page.locator(`[data-testid="repair-row-${audiobookId}"]`)

const searchButton = (page: Page) =>
  card(page).locator('[data-testid="match-search"], [data-testid="match-retry"]')

const openRepair = async (page: Page) => {
  await page.goto(bookPage)
  await page.locator('button[aria-label="Repair Book"]').click()
  await expect(searchButton(page)).toBeVisible()
}

const fillSearch = async (page: Page, values: SearchFields) => {
  if (values.region) await regionField(page).selectOption(values.region)
  for (const name of ['asin', 'title', 'author'] as const) {
    await field(page, name).fill(values[name] ?? '')
  }
}

const submitSearch = async (page: Page) => {
  await searchButton(page).click()
}

const searchCases: Array<{
  name: string
  fields: SearchFields
  request?: SearchRequest
  lookup?: string
  noRequest?: boolean
  disabled?: boolean
}> = [
  {
    name: 'title and author',
    fields: { title: 'Dune', author: 'Frank Herbert' },
    request: { title: 'Dune', author: 'Frank Herbert' },
  },
  { name: 'title alone', fields: { title: 'Dune' }, request: { title: 'Dune' } },
  {
    name: 'a filled ASIN goes alone',
    fields: { asin: 'B008DFUGCQ', title: 'Dune', author: 'Frank Herbert' },
    lookup: 'B008DFUGCQ',
  },
  { name: 'a filled ASIN in lowercase', fields: { asin: 'b008dfugcq' }, lookup: 'B008DFUGCQ' },
  { name: 'a filled ten-digit ASIN', fields: { asin: '1000000001' }, lookup: '1000000001' },
  { name: 'a filled value that is no ASIN', fields: { asin: 'nope', title: 'Dune' }, noRequest: true },
  { name: 'an ASIN in the title', fields: { title: 'B008DFUGCQ' }, lookup: 'B008DFUGCQ' },
  {
    name: 'an ASIN in the title, with an author',
    fields: { title: 'B008DFUGCQ', author: 'Someone' },
    lookup: 'B008DFUGCQ',
  },
  {
    name: 'a lowercase ASIN in the title',
    fields: { title: 'b008dfugcq', author: 'Someone' },
    lookup: 'B008DFUGCQ',
  },
  {
    name: 'an ISBN-13 with an author is a title',
    fields: { title: '9780547928227', author: 'Tolkien' },
    request: { title: '9780547928227', author: 'Tolkien' },
  },
  {
    name: 'accents',
    fields: { title: 'Les Misérables', author: 'Victor Hugo' },
    request: { title: 'Les Misérables', author: 'Victor Hugo' },
  },
  {
    name: 'quotes',
    fields: { title: 'A "quoted" title', author: "O'Brien" },
    request: { title: 'A "quoted" title', author: "O'Brien" },
  },
  { name: 'no author, no title: Search is off', fields: {}, noRequest: true, disabled: true },
  {
    name: 'only an author: Search is off',
    fields: { author: 'Frank Herbert' },
    noRequest: true,
    disabled: true,
  },
  {
    name: 'spaces trimmed',
    fields: { title: '  Dune  ', author: '  Frank Herbert ' },
    request: { title: 'Dune', author: 'Frank Herbert' },
  },
  {
    name: 'in France',
    fields: { title: 'Vingt mille lieues sous les mers', author: 'Jules Verne', region: 'fr' },
    request: { title: 'Vingt mille lieues sous les mers', author: 'Jules Verne', region: 'fr' },
  },
]

test.describe('Repair / Find Match card fields', () => {
  test.beforeEach(async ({ api }) => {
    await api.stubLibrary([
      libraryAudiobookFixture({ id: Number(audiobookId), title: 'Dune', authors: ['Frank Herbert'] }),
    ])
    await api.stubAudiobook(Number(audiobookId), audiobook)
    await api.stubAudiobookIdentifiers(Number(audiobookId), [])
    await api.stubWeakStorageMissingFiles(Number(audiobookId))
    await api.stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    await api.stubSearch()
    await api.stubMetadata()
  })

  for (const testCase of searchCases) {
    test(testCase.name, async ({ page, api }) => {
      await openRepair(page)
      await fillSearch(page, testCase.fields)

      if (testCase.noRequest) {
        if (testCase.disabled) await expect(searchButton(page)).toBeDisabled()
        else await searchButton(page).click()
        expect(api.calls('search')).toHaveLength(0)
        expect(api.calls('metadata')).toHaveLength(0)
        return
      }

      if (testCase.fields.region) await api.wait('search')
      await submitSearch(page)
      if (testCase.lookup) {
        const { request } = await api.wait('metadata')
        expect(request.url).toContain(testCase.lookup)
        expect(api.calls('search')).toHaveLength(0)
      } else {
        const { request } = await api.wait('search')
        const body: SearchRequest = request.body
        expect(body.title).toBe(testCase.request?.title)
        if (testCase.request?.author) expect(body.author).toBe(testCase.request.author)
        if (testCase.request?.region) expect(body.region).toBe(testCase.request.region)
        expect(Boolean(body.series)).toBe(false)
        expect(api.calls('metadata')).toHaveLength(0)
      }
    })
  }

  test('serves the same search from the dialog without another request', async ({ page, api }) => {
    await openRepair(page)
    await fillSearch(page, { title: 'Dune', author: 'Frank Herbert' })
    await submitSearch(page)
    await api.wait('search')
    await field(page, 'title').fill('Foundation')
    await submitSearch(page)
    await api.wait('search')
    await field(page, 'title').fill('Dune')
    await submitSearch(page)
    await expect.poll(() => api.calls('search').length).toBe(2)
  })

  test('searches again when the same title has a different author', async ({ page, api }) => {
    await openRepair(page)
    await fillSearch(page, { title: 'Dune', author: 'Frank Herbert' })
    await submitSearch(page)
    await api.wait('search')
    await field(page, 'author').fill('Brian Herbert')
    await submitSearch(page)
    const { request } = await api.wait('search')
    const body: SearchRequest = request.body
    expect(body.title).toBe('Dune')
    expect(body.author).toBe('Brian Herbert')
    await expect.poll(() => api.calls('search').length).toBe(2)
  })

  test('shows no record for a 404 metadata lookup', async ({ page, api }) => {
    await api.stubMetadata({ statusCode: 404, body: { message: 'none' } }, 'missingMetadata')
    await openRepair(page)
    await fillSearch(page, { asin: 'B008DFUGCQ' })
    await submitSearch(page)
    await api.wait('missingMetadata')
    await expect(card(page).getByText(/No record for/).first()).toBeVisible()
  })

  test('treats an empty metadata record as no result', async ({ page, api }) => {
    await api.stubMetadata(
      { statusCode: 200, body: { metadata: { asin: 'B008DFUGCQ', title: '' } } },
      'emptyMetadata',
    )
    await openRepair(page)
    await fillSearch(page, { asin: 'B008DFUGCQ' })
    await submitSearch(page)
    await api.wait('emptyMetadata')
    await expect(card(page).locator('[data-testid="find-match-result"]')).toHaveCount(0)
  })

  test('reports a failing search', async ({ page, api }) => {
    await api.stubSearch({ statusCode: 500, body: { message: 'boom' } }, 'failedSearch')
    await openRepair(page)
    await fillSearch(page, { title: 'Dune' })
    await submitSearch(page)
    await api.wait('failedSearch')
    await expect(card(page)).toHaveAttribute('data-match-state', /error|failed/i)
  })

  test('cancels a pending search promptly', async ({ page, api }) => {
    await api.stubSearch({ delay: 4000, statusCode: 200, body: { results: [] } }, 'slowSearch')
    await openRepair(page)
    await fillSearch(page, { title: 'Dune' })
    await field(page, 'title').press('Enter')
    const cancel = card(page).locator('[data-testid="match-cancel-search"]')
    await expect(cancel).toBeVisible()
    await cancel.click()
    // Shorter than the stubbed delay: the button must go because of the cancel, not the reply.
    await expect(page.locator('[data-testid="match-cancel-search"]')).toHaveCount(0, { timeout: 2000 })
  })

  test('trims whitespace around a pasted ASIN before looking it up', async ({ page, api }) => {
    await openRepair(page)
    await field(page, 'asin').pressSequentially(' B008DFUGCQ ')
    await submitSearch(page)

    const { request } = await api.wait('metadata')
    expect(request.url).toContain('/metadata/B008DFUGCQ?region=us')
    expect(api.calls('search')).toHaveLength(0)
  })
})
