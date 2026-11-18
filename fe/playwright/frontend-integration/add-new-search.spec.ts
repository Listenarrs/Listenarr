import type { Page } from '@playwright/test'
import { expect, test, type ApiStubs } from '../support/test.js'

type SearchCase = {
  name: string
  query: string
  fields: Record<string, unknown>
  input?: Record<string, string>
  region?: string
  language?: string
  absent?: string[]
}

let searchResponse: { statusCode: number; body: object | null; delay?: number }
let metadataStatusCode: number

async function prepareSearch(page: Page, region: string, language: string, advanced = false) {
  await page.locator(advanced ? '#adv-region' : '#region-select').selectOption(region)
  await page.locator(advanced ? '#adv-language' : '#language-select').selectOption(language)
}

async function submitSimple(page: Page, query: string) {
  await page.locator('#unified-search-input').fill(query)
  await page.locator('button[aria-label="Execute search"]').click()
}

async function openAdvancedSearch(page: Page) {
  await page.locator('.advanced-btn').click()
  await expect(page.locator('#advanced-search')).toBeVisible()
}

async function fillAdvanced(page: Page, fields: Record<string, string>) {
  const selectors: Record<string, string> = {
    title: '#adv-title',
    author: '#adv-author',
    series: '#adv-series',
    isbn: '#adv-isbn',
    asin: '#adv-asin',
  }

  for (const [name, value] of Object.entries(fields)) {
    await page.locator(selectors[name]!).fill(value)
  }
}

async function submitAdvanced(page: Page) {
  await page.locator('button[aria-label="Execute advanced search"]').click()
}

async function expectSearchRequest(api: ApiStubs, fields: Record<string, unknown>, absent: string[] = []) {
  const { request } = await api.wait('search')
  const body = request.body as Record<string, unknown>
  expect(body).toMatchObject({ mode: 'Advanced' })
  expect(body.pagination).toEqual({ page: 1, limit: 50 })
  for (const [key, value] of Object.entries(fields)) {
    expect(body[key], key).toEqual(value)
  }
  for (const key of absent) {
    expect(body, `${key} should not be sent`).not.toHaveProperty(key)
  }
}

function expectNoRequests(api: ApiStubs) {
  expect(api.calls('search')).toHaveLength(0)
  expect(api.calls('metadata')).toHaveLength(0)
}

test.beforeEach(async ({ page, api }) => {
  searchResponse = { statusCode: 200, body: [] }
  metadataStatusCode = 404

  await api.stubSearch(() => searchResponse)
  await api.stubMetadata(() => ({ statusCode: metadataStatusCode, body: { message: 'not found' } }))
  await page.goto('/add-new')
  await expect(page.locator('h1', { hasText: 'Add New Audiobook' })).toBeVisible()
})

test.describe('Add New simple search requests', () => {
  const structuredCases: SearchCase[] = [
    { name: 'plain title', query: 'Dune', fields: { title: 'Dune', region: 'us', language: 'english' } },
    { name: 'several words', query: 'The Way of Kings', fields: { title: 'The Way of Kings' } },
    {
      name: 'French region and language',
      query: 'Vingt mille lieues sous les mers',
      region: 'fr',
      language: 'french',
      fields: { title: 'Vingt mille lieues sous les mers', region: 'fr', language: 'french' },
    },
    {
      name: 'all languages omit the language filter',
      query: 'Dune',
      language: 'all',
      fields: { title: 'Dune' },
      absent: ['language'],
    },
    {
      name: 'Japanese region',
      query: '三体',
      region: 'jp',
      language: 'all',
      fields: { title: '三体', region: 'jp' },
      absent: ['language'],
    },
    {
      name: 'German region',
      query: 'Der Schwarm',
      region: 'de',
      language: 'all',
      fields: { title: 'Der Schwarm', region: 'de' },
      absent: ['language'],
    },
    { name: 'leading and trailing spaces are trimmed', query: '   Dune   ', fields: { title: 'Dune' } },
    {
      name: 'TITLE and AUTHOR tokens',
      query: 'TITLE:Dune AUTHOR:Frank Herbert',
      fields: { title: 'Dune', author: 'Frank Herbert' },
    },
    {
      name: 'multi-word TITLE and AUTHOR tokens',
      query: 'TITLE:The Way of Kings AUTHOR:Brandon Sanderson',
      fields: { title: 'The Way of Kings', author: 'Brandon Sanderson' },
    },
    {
      name: 'lowercase tokens',
      query: 'title:dune author:frank herbert',
      fields: { title: 'dune', author: 'frank herbert' },
    },
    {
      name: 'AUTHOR before TITLE',
      query: 'AUTHOR:Frank Herbert TITLE:Dune',
      fields: { title: 'Dune', author: 'Frank Herbert' },
    },
    { name: 'author token alone', query: 'AUTHOR:Brandon Sanderson', fields: { author: 'Brandon Sanderson' } },
    { name: 'series token alone', query: 'SERIES:Barsoom', fields: { series: 'Barsoom' } },
    {
      name: 'title, author, and series tokens',
      query: 'TITLE:A Princess of Mars AUTHOR:Burroughs SERIES:Barsoom',
      fields: { title: 'A Princess of Mars', author: 'Burroughs', series: 'Barsoom' },
    },
    { name: 'accents are preserved', query: 'Les Misérables', fields: { title: 'Les Misérables' } },
    {
      name: 'apostrophe with French region',
      query: "L'Étranger",
      region: 'fr',
      language: 'french',
      fields: { title: "L'Étranger", region: 'fr', language: 'french' },
    },
    {
      name: 'quotes and ampersand are preserved',
      query: 'Harry Potter & the "Stone"',
      fields: { title: 'Harry Potter & the "Stone"' },
    },
    { name: 'percent and slash are preserved', query: '100% / Half', fields: { title: '100% / Half' } },
    {
      name: 'markup is submitted as text',
      query: '<script>alert(1)</script>',
      fields: { title: '<script>alert(1)</script>' },
    },
    { name: 'numbers', query: '1984', fields: { title: '1984' } },
    { name: 'dots', query: '11.22.63', fields: { title: '11.22.63' } },
    { name: 'nine characters are treated as a title', query: 'B008DFUGC', fields: { title: 'B008DFUGC' } },
    { name: 'eleven characters are treated as a title', query: 'B008DFUGCQQ', fields: { title: 'B008DFUGCQQ' } },
    { name: 'a ten-character title not starting with B', query: 'Foundation', fields: { title: 'Foundation' } },
  ]

  structuredCases.forEach(({ name, query, fields, region = 'us', language = 'english', absent = [] }) => {
    test(`sends the expected structured request for ${name}`, async ({ page, api }) => {
      await prepareSearch(page, region, language)
      await submitSimple(page, query)
      await expectSearchRequest(api, fields, absent)
      expect(api.calls('metadata')).toHaveLength(0)
    })
  })

  // The lookup sends the ASIN in uppercase, whatever case it was typed in.
  const asinCases: [string, string, string][] = [
    ['ASIN', 'B008DFUGCQ', 'B008DFUGCQ'],
    ['lowercase ASIN', 'b008dfugcq', 'B008DFUGCQ'],
    ['ASIN prefix', 'ASIN:B008DFUGCQ', 'B008DFUGCQ'],
    ['lowercase ASIN prefix', 'asin:b008dfugcq', 'B008DFUGCQ'],
    ['ASIN prefix with a space', 'ASIN: B008DFUGCQ', 'B008DFUGCQ'],
    ['ASIN surrounded by spaces', '  B008DFUGCQ  ', 'B008DFUGCQ'],
    ['ten-digit ASIN', '1000000001', '1000000001'],
    ['ASIN ending in X', '100000000X', '100000000X'],
    ['ten-character title starting with B', 'Bloodlines', 'BLOODLINES'],
    ['lowercase ten-character title starting with B', 'bloodlines', 'BLOODLINES'],
  ]

  asinCases.forEach(([name, query, asin]) => {
    test(`looks up ${name} without a structured search`, async ({ page, api }) => {
      await prepareSearch(page, 'us', 'english')
      await submitSimple(page, query)
      const { request } = await api.wait('metadata')
      expect(request.url).toContain(`/api/v1/metadata/${asin}`)
      expect(request.url).toContain('us')
      expect(api.calls('search')).toHaveLength(0)
    })
  })

  test('keeps search disabled for an empty query', async ({ page, api }) => {
    await expect(page.locator('button[aria-label="Execute search"]')).toBeDisabled()
    expectNoRequests(api)
  })

  test('does not request anything when the query contains only spaces', async ({ page, api }) => {
    await page.locator('#unified-search-input').pressSequentially('    ')
    await expect(page.locator('button[aria-label="Execute search"]')).toBeDisabled()
    expectNoRequests(api)
  })

  test('does not search while typing', async ({ page, api }) => {
    await page.locator('#unified-search-input').pressSequentially('B008DFUGCQ')
    // Typing alone must not search: the explicit submit is the first and only request.
    await page.locator('button[aria-label="Execute search"]').click()
    const { request } = await api.wait('metadata')
    expect(request.url).toContain('/api/v1/metadata/B008DFUGCQ')
    expect(api.calls('metadata')).toHaveLength(1)
    expect(api.calls('search')).toHaveLength(0)
  })

  test('does not search when the region changes', async ({ page, api }) => {
    await page.locator('#unified-search-input').pressSequentially('Dune')
    await page.locator('#region-select').selectOption('fr')
    // Changing the region must not search on its own: the explicit submit is the only search.
    await page.locator('button[aria-label="Execute search"]').click()
    await api.wait('search')
    expect(api.calls('search')).toHaveLength(1)
    expect(api.calls('metadata')).toHaveLength(0)
  })

  test('rejects an invalid ASIN without making a request', async ({ page, api }) => {
    await submitSimple(page, 'ASIN:nope')
    await expect(page.locator('.error-message')).toContainText('Invalid ASIN')
    expectNoRequests(api)
  })

  test('handles a failed search response without displaying results', async ({ page, api }) => {
    searchResponse = { statusCode: 500, body: { message: 'boom' } }
    await submitSimple(page, 'Dune')
    await api.wait('search')
    await expect(page.locator('.search-results')).toHaveCount(0)
  })

  test('handles a null search response without displaying results', async ({ page, api }) => {
    searchResponse = { statusCode: 200, body: null }
    await submitSimple(page, 'Dune')
    await expectSearchRequest(api, { title: 'Dune' })
    await expect(page.locator('.search-results')).toHaveCount(0)
  })

  test('handles failed and missing ASIN metadata responses', async ({ page, api }) => {
    metadataStatusCode = 500
    await submitSimple(page, 'B008DFUGCQ')
    await api.wait('metadata')
    await expect(page.locator('.search-results')).toHaveCount(0)
  })

  test('handles an ASIN metadata 404 response', async ({ page, api }) => {
    metadataStatusCode = 404
    await submitSimple(page, 'B008DFUGCQ')
    await api.wait('metadata')
    await expect(page.locator('.search-results')).toHaveCount(0)
  })
})

test.describe('Add New advanced search requests', () => {
  const advancedCases: SearchCase[] = [
    { name: 'title', query: '', fields: { title: 'Dune', region: 'us', language: 'english' } },
    { name: 'author', query: '', fields: { author: 'Frank Herbert' } },
    { name: 'title and author', query: '', fields: { title: 'Dune', author: 'Frank Herbert' } },
    {
      name: 'trimmed title and author',
      query: '',
      input: { title: '  Dune  ', author: '  Frank Herbert ' },
      fields: { title: 'Dune', author: 'Frank Herbert' },
    },
    { name: 'series alone', query: '', fields: { series: 'Barsoom' } },
    {
      name: 'series alone with spaces trimmed',
      query: '',
      input: { series: '  Barsoom ' },
      fields: { series: 'Barsoom' },
    },
    {
      name: 'series alone in France',
      query: '',
      region: 'fr',
      language: 'all',
      fields: { series: 'Harry Potter', region: 'fr' },
      absent: ['language'],
    },
    {
      name: 'author takes priority over series',
      query: '',
      input: { author: 'Edgar Rice Burroughs', series: 'Barsoom' },
      fields: { author: 'Edgar Rice Burroughs' },
      absent: ['series'],
    },
    {
      name: 'title takes priority over series',
      query: '',
      input: { title: 'A Princess of Mars', series: 'Barsoom' },
      fields: { title: 'A Princess of Mars' },
      absent: ['series'],
    },
    {
      name: 'title and author with series omit series',
      query: '',
      input: { title: 'A Princess of Mars', author: 'Edgar Rice Burroughs', series: 'Barsoom' },
      fields: { title: 'A Princess of Mars', author: 'Edgar Rice Burroughs' },
      absent: ['series'],
    },
    {
      name: 'ISBN with series omits series',
      query: '',
      input: { isbn: '9780547928227', series: 'Barsoom' },
      fields: { isbn: '9780547928227' },
      absent: ['series'],
    },
    {
      name: 'ASIN with series omits series',
      query: '',
      input: { asin: 'B008DFUGCQ', series: 'Barsoom' },
      fields: { asin: 'B008DFUGCQ' },
      absent: ['series'],
    },
    { name: 'ASIN', query: '', fields: { asin: 'B008DFUGCQ' } },
    {
      name: 'ASIN takes priority over author',
      query: '',
      input: { asin: 'B008DFUGCQ', author: 'Someone' },
      fields: { asin: 'B008DFUGCQ' },
      absent: ['author'],
    },
    { name: 'ten-digit ASIN', query: '', fields: { asin: '1000000001' } },
    { name: 'ISBN-13', query: '', fields: { isbn: '9780547928227' } },
    {
      name: 'title with accents and author',
      query: '',
      fields: { title: 'Les Misérables', author: 'Victor Hugo' },
    },
    {
      name: 'Series: in the title remains title text',
      query: '',
      fields: { title: 'The Wheel of Time Series: Book 1' },
      absent: ['series'],
    },
    {
      name: 'all languages omit the language filter',
      query: '',
      language: 'all',
      fields: { title: 'Dune' },
      absent: ['language'],
    },
    {
      name: 'Japanese region',
      query: '',
      region: 'jp',
      language: 'all',
      fields: { title: '三体', region: 'jp' },
      absent: ['language'],
    },
  ]

  advancedCases.forEach(({ name, fields, input, region = 'us', language = 'english', absent = [] }) => {
    test(`sends the expected request for ${name}`, async ({ page, api }) => {
      await openAdvancedSearch(page)
      await prepareSearch(page, region, language, true)
      const inputFields =
        input ??
        (Object.fromEntries(
          Object.entries(fields).filter(
            ([key, value]) =>
              ['title', 'author', 'series', 'isbn', 'asin'].includes(key) && typeof value === 'string',
          ),
        ) as Record<string, string>)
      await fillAdvanced(page, inputFields)
      await submitAdvanced(page)
      await expectSearchRequest(api, fields, absent)
    })
  })

  test('refuses an empty form and a form containing only spaces', async ({ page, api }) => {
    await openAdvancedSearch(page)
    await page.locator('#adv-title').press('Enter')
    await expect(page.locator('.advanced-search-section .error-message')).toContainText(
      'Please enter a search term',
    )
    expectNoRequests(api)

    await fillAdvanced(page, { title: '   ', author: ' ', series: '  ', isbn: ' ', asin: '  ' })
    await page.locator('#adv-title').press('Enter')
    await expect(page.locator('.advanced-search-section .error-message')).toContainText(
      'Please enter a search term',
    )
    expectNoRequests(api)
  })

  test('does not retain results when the search endpoint fails', async ({ page, api }) => {
    searchResponse = { statusCode: 500, body: { message: 'boom' } }
    await openAdvancedSearch(page)
    await fillAdvanced(page, { title: 'Dune' })
    await submitAdvanced(page)
    await expectSearchRequest(api, { title: 'Dune' })
    await expect(page.locator('.search-results')).toHaveCount(0)
  })

  test('handles a null search response', async ({ page, api }) => {
    searchResponse = { statusCode: 200, body: null }
    await openAdvancedSearch(page)
    await fillAdvanced(page, { author: 'Edgar Rice Burroughs', series: 'Barsoom' })
    await submitAdvanced(page)
    await expectSearchRequest(api, { author: 'Edgar Rice Burroughs' }, ['series'])
    await expect(page.locator('.search-results')).toHaveCount(0)
  })

  test('filters author results by series across supported response shapes', async ({ page, api }) => {
    searchResponse = {
      statusCode: 200,
      body: [
        { title: 'By name', asin: 'B000000001', series: [{ name: 'The Barsoom Series' }] },
        { title: 'By ASIN', asin: 'B000000002', series: [{ Asin: 'barsoom-1' }] },
        { title: 'As text', asin: 'B000000003', series: 'Barsoom' },
        { title: 'Nested', asin: 'B000000004', searchResult: { series: 'Barsoom' } },
        { title: 'In a list of text', asin: 'B000000005', series: ['Barsoom'] },
        { title: 'Elsewhere', asin: 'B000000006', series: 'Discworld' },
        { title: 'No series', asin: 'B000000007' },
      ],
    }
    await openAdvancedSearch(page)
    await prepareSearch(page, 'us', 'all', true)
    await fillAdvanced(page, { author: 'Someone', series: 'barsoom' })
    await submitAdvanced(page)
    await expectSearchRequest(api, { author: 'Someone' }, ['series', 'language'])
    await expect(page.locator('.title-result-card')).toHaveCount(5)
  })

  test('returns to simple search and submits a title query', async ({ page, api }) => {
    await openAdvancedSearch(page)
    await fillAdvanced(page, { title: 'Dune' })
    await submitAdvanced(page)
    await api.wait('search')
    await page.locator('button[aria-label="Return to simple search"]').click()
    await submitSimple(page, 'Foundation')
    await expectSearchRequest(api, { title: 'Foundation' })
  })
})
