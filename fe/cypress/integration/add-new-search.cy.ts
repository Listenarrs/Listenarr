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

function prepareSearch(region: string, language: string, advanced = false) {
  cy.get(advanced ? '#adv-region' : '#region-select').select(region)
  cy.get(advanced ? '#adv-language' : '#language-select').select(language)
}

function submitSimple(query: string) {
  cy.get('#unified-search-input').clear()
  cy.get('#unified-search-input').type(query, { parseSpecialCharSequences: false })
  cy.get('button[aria-label="Execute search"]').click()
}

function openAdvancedSearch() {
  cy.get('.advanced-btn').click()
  cy.get('#advanced-search').should('be.visible')
}

function fillAdvanced(fields: Record<string, string>) {
  const selectors: Record<string, string> = {
    title: '#adv-title',
    author: '#adv-author',
    series: '#adv-series',
    isbn: '#adv-isbn',
    asin: '#adv-asin',
  }

  for (const [name, value] of Object.entries(fields)) {
    cy.get(selectors[name]).clear()
    cy.get(selectors[name]).type(value, { parseSpecialCharSequences: false })
  }
}

function submitAdvanced() {
  cy.get('button[aria-label="Execute advanced search"]').click()
}

function expectSearchRequest(fields: Record<string, unknown>, absent: string[] = []) {
  cy.wait('@search').then(({ request }) => {
    const body = request.body as Record<string, unknown>
    expect(body).to.include({ mode: 'Advanced' })
    expect(body.pagination).to.deep.equal({ page: 1, limit: 50 })
    for (const [key, value] of Object.entries(fields)) {
      expect(body[key], key).to.deep.equal(value)
    }
    for (const key of absent) {
      expect(body, `${key} should not be sent`).not.to.have.property(key)
    }
  })
}

function expectNoRequests() {
  cy.get('@search.all').should('have.length', 0)
  cy.get('@metadata.all').should('have.length', 0)
}

beforeEach(() => {
  searchResponse = { statusCode: 200, body: [] }
  metadataStatusCode = 404

  cy.intercept('POST', '**/api/v1/search', (request) => request.reply(searchResponse)).as('search')
  cy.intercept('**/api/v1/metadata/**', (request) =>
    request.reply({ statusCode: metadataStatusCode, body: { message: 'not found' } }),
  ).as('metadata')
  cy.visit('/add-new')
  cy.contains('h1', 'Add New Audiobook').should('be.visible')
})

describe('Add New simple search requests', () => {
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
    it(`sends the expected structured request for ${name}`, () => {
      prepareSearch(region, language)
      submitSimple(query)
      expectSearchRequest(fields, absent)
      cy.get('@metadata.all').should('have.length', 0)
    })
  })

  const asinCases: [string, string, string][] = [
    ['ASIN', 'B008DFUGCQ', 'B008DFUGCQ'],
    ['lowercase ASIN', 'b008dfugcq', 'b008dfugcq'],
    ['ASIN prefix', 'ASIN:B008DFUGCQ', 'B008DFUGCQ'],
    ['lowercase ASIN prefix', 'asin:b008dfugcq', 'b008dfugcq'],
    ['ASIN prefix with a space', 'ASIN: B008DFUGCQ', 'B008DFUGCQ'],
    ['ASIN surrounded by spaces', '  B008DFUGCQ  ', 'B008DFUGCQ'],
    ['ten-digit ASIN', '1000000001', '1000000001'],
    ['ASIN ending in X', '100000000X', '100000000X'],
    ['ten-character title starting with B', 'Bloodlines', 'Bloodlines'],
    ['lowercase ten-character title starting with B', 'bloodlines', 'bloodlines'],
  ]

  asinCases.forEach(([name, query, asin]) => {
    it(`looks up ${name} without a structured search`, () => {
      prepareSearch('us', 'english')
      submitSimple(query)
      cy.wait('@metadata').then(({ request }) => {
        expect(request.url).to.include(`/api/v1/metadata/${asin}`)
        expect(request.url).to.include('us')
      })
      cy.get('@search.all').should('have.length', 0)
    })
  })

  it('keeps search disabled for an empty query', () => {
    cy.get('button[aria-label="Execute search"]').should('be.disabled')
    expectNoRequests()
  })

  it('does not request anything when the query contains only spaces', () => {
    cy.get('#unified-search-input').type('    ')
    cy.get('button[aria-label="Execute search"]').should('be.disabled')
    expectNoRequests()
  })

  it('does not search while typing', () => {
    cy.get('#unified-search-input').type('B008DFUGCQ')
    expectNoRequests()
  })

  it('does not search when the region changes', () => {
    cy.get('#unified-search-input').type('Dune')
    cy.get('#region-select').select('fr')
    expectNoRequests()
  })

  it('rejects an invalid ASIN without making a request', () => {
    submitSimple('ASIN:nope')
    cy.get('.error-message').should('contain.text', 'Invalid ASIN')
    expectNoRequests()
  })

  it('handles a failed search response without displaying results', () => {
    searchResponse = { statusCode: 500, body: { message: 'boom' } }
    submitSimple('Dune')
    cy.wait('@search')
    cy.get('.search-results').should('not.exist')
  })

  it('handles a null search response without displaying results', () => {
    searchResponse = { statusCode: 200, body: null }
    submitSimple('Dune')
    expectSearchRequest({ title: 'Dune' })
    cy.get('.search-results').should('not.exist')
  })

  it('handles failed and missing ASIN metadata responses', () => {
    metadataStatusCode = 500
    submitSimple('B008DFUGCQ')
    cy.wait('@metadata')
    cy.get('.search-results').should('not.exist')
  })

  it('handles an ASIN metadata 404 response', () => {
    metadataStatusCode = 404
    submitSimple('B008DFUGCQ')
    cy.wait('@metadata')
    cy.get('.search-results').should('not.exist')
  })
})

describe('Add New advanced search requests', () => {
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
    it(`sends the expected request for ${name}`, () => {
      openAdvancedSearch()
      prepareSearch(region, language, true)
      const inputFields = input ?? Object.fromEntries(
        Object.entries(fields).filter(([key, value]) =>
          ['title', 'author', 'series', 'isbn', 'asin'].includes(key) && typeof value === 'string',
        ),
      ) as Record<string, string>
      fillAdvanced(inputFields)
      submitAdvanced()
      expectSearchRequest(fields, absent)
    })
  })

  it('refuses an empty form and a form containing only spaces', () => {
    openAdvancedSearch()
    cy.get('#adv-title').type('{enter}')
    cy.get('.advanced-search-section .error-message').should('contain.text', 'Please enter a search term')
    expectNoRequests()

    fillAdvanced({ title: '   ', author: ' ', series: '  ', isbn: ' ', asin: '  ' })
    cy.get('#adv-title').type('{enter}')
    cy.get('.advanced-search-section .error-message').should('contain.text', 'Please enter a search term')
    expectNoRequests()
  })

  it('does not retain results when the search endpoint fails', () => {
    searchResponse = { statusCode: 500, body: { message: 'boom' } }
    openAdvancedSearch()
    fillAdvanced({ title: 'Dune' })
    submitAdvanced()
    expectSearchRequest({ title: 'Dune' })
    cy.get('.search-results').should('not.exist')
  })

  it('handles a null search response', () => {
    searchResponse = { statusCode: 200, body: null }
    openAdvancedSearch()
    fillAdvanced({ author: 'Edgar Rice Burroughs', series: 'Barsoom' })
    submitAdvanced()
    expectSearchRequest({ author: 'Edgar Rice Burroughs' }, ['series'])
    cy.get('.search-results').should('not.exist')
  })

  it('filters author results by series across supported response shapes', () => {
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
    openAdvancedSearch()
    prepareSearch('us', 'all', true)
    fillAdvanced({ author: 'Someone', series: 'barsoom' })
    submitAdvanced()
    expectSearchRequest({ author: 'Someone' }, ['series', 'language'])
    cy.get('.title-result-card').should('have.length', 5)
  })

  it('returns to simple search and submits a title query', () => {
    openAdvancedSearch()
    fillAdvanced({ title: 'Dune' })
    submitAdvanced()
    cy.wait('@search')
    cy.get('button[aria-label="Return to simple search"]').click()
    submitSimple('Foundation')
    expectSearchRequest({ title: 'Foundation' })
  })
})