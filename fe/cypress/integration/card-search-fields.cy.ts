import {
  audiobookFixture,
  libraryAudiobookFixture,
  settingsFixture,
  stubAudiobook,
  stubLibrary,
  stubLibraryIdentifiers,
  stubMetadata,
  stubQualityProfiles,
  stubSearch,
  stubSettings,
  stubWeakStorageMissingFiles,
} from '../support/api-stubs'

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

const field = (name: keyof SearchFields) => {
  return cy.get(`[data-testid="match-${name}"]`)
}

const regionField = () => cy.get('[data-testid="match-region"]')

const card = () => cy.get(`[data-testid="repair-row-${audiobookId}"]`)

const openRepair = () => {
  cy.visit(bookPage)
  cy.get('button[aria-label="Repair Book"]').click()
  card().find('[data-testid="match-search"], [data-testid="match-retry"]').should('be.visible')
}

const fillSearch = (values: SearchFields) => {
  if (values.region) regionField().select(values.region)
  for (const name of ['asin', 'title', 'author'] as const) {
    const value = values[name]
    field(name).clear()
    if (value) field(name).type(value)
  }
}

const submitSearch = () => {
  card().find('[data-testid="match-search"], [data-testid="match-retry"]').click()
}

const searchCases: Array<{
  name: string
  fields: SearchFields
  request?: SearchRequest
  lookup?: string
  noRequest?: boolean
  disabled?: boolean
}> = [
  { name: 'title and author', fields: { title: 'Dune', author: 'Frank Herbert' }, request: { title: 'Dune', author: 'Frank Herbert' } },
  { name: 'title alone', fields: { title: 'Dune' }, request: { title: 'Dune' } },
  { name: 'a filled ASIN goes alone', fields: { asin: 'B008DFUGCQ', title: 'Dune', author: 'Frank Herbert' }, lookup: 'B008DFUGCQ' },
  { name: 'a filled ASIN in lowercase', fields: { asin: 'b008dfugcq' }, lookup: 'B008DFUGCQ' },
  { name: 'a filled ten-digit ASIN', fields: { asin: '1000000001' }, lookup: '1000000001' },
  { name: 'a filled value that is no ASIN', fields: { asin: 'nope', title: 'Dune' }, noRequest: true },
  { name: 'an ASIN in the title', fields: { title: 'B008DFUGCQ' }, lookup: 'B008DFUGCQ' },
  { name: 'an ASIN in the title, with an author', fields: { title: 'B008DFUGCQ', author: 'Someone' }, lookup: 'B008DFUGCQ' },
  { name: 'a lowercase ASIN in the title', fields: { title: 'b008dfugcq', author: 'Someone' }, lookup: 'b008dfugcq' },
  { name: 'an ISBN-13 with an author is a title', fields: { title: '9780547928227', author: 'Tolkien' }, request: { title: '9780547928227', author: 'Tolkien' } },
  { name: 'accents', fields: { title: 'Les Misérables', author: 'Victor Hugo' }, request: { title: 'Les Misérables', author: 'Victor Hugo' } },
  { name: 'quotes', fields: { title: 'A "quoted" title', author: "O'Brien" }, request: { title: 'A "quoted" title', author: "O'Brien" } },
  { name: 'no author, no title: Search is off', fields: {}, noRequest: true, disabled: true },
  { name: 'only an author: Search is off', fields: { author: 'Frank Herbert' }, noRequest: true, disabled: true },
  { name: 'spaces trimmed', fields: { title: '  Dune  ', author: '  Frank Herbert ' }, request: { title: 'Dune', author: 'Frank Herbert' } },
  { name: 'in France', fields: { title: 'Vingt mille lieues sous les mers', author: 'Jules Verne', region: 'fr' }, request: { title: 'Vingt mille lieues sous les mers', author: 'Jules Verne', region: 'fr' } },
]

describe('Repair / Find Match card fields', () => {
  beforeEach(() => {
    stubLibrary([libraryAudiobookFixture({ id: Number(audiobookId), title: 'Dune', authors: ['Frank Herbert'] })])
    stubAudiobook(Number(audiobookId), audiobook)
    stubLibraryIdentifiers(Number(audiobookId))
    stubWeakStorageMissingFiles(Number(audiobookId))
    stubQualityProfiles()
    stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    stubSearch()
    stubMetadata()
  })

  for (const testCase of searchCases) {
    it(testCase.name, () => {
      openRepair()
      fillSearch(testCase.fields)

      const searchButton = card().find('[data-testid="match-search"], [data-testid="match-retry"]')
      if (testCase.noRequest) {
        if (testCase.disabled) searchButton.should('be.disabled')
        else searchButton.click()
        cy.get('@search.all').should('have.length', 0)
        cy.get('@metadata.all').should('have.length', 0)
        return
      }

      if (testCase.fields.region) cy.wait('@search')
      submitSearch()
      if (testCase.lookup) {
        cy.wait('@metadata').its('request.url').should('include', testCase.lookup)
        cy.get('@search.all').should('have.length', 0)
      } else {
        cy.wait('@search').its('request.body').should((body: SearchRequest) => {
          expect(body.title).to.equal(testCase.request?.title)
          if (testCase.request?.author) expect(body.author).to.equal(testCase.request.author)
          if (testCase.request?.region) expect(body.region).to.equal(testCase.request.region)
          expect(Boolean(body.series)).to.equal(false)
        })
        cy.get('@metadata.all').should('have.length', 0)
      }
    })
  }

  it('serves the same search from the dialog without another request', () => {
    openRepair()
    fillSearch({ title: 'Dune', author: 'Frank Herbert' })
    submitSearch()
    cy.wait('@search')
    field('title').clear().type('Foundation').type('{enter}')
    cy.wait('@search')
    field('title').clear().type('Dune').type('{enter}')
    cy.get('@search.all').should('have.length', 2)
  })

  it('searches again when the same title has a different author', () => {
    openRepair()
    fillSearch({ title: 'Dune', author: 'Frank Herbert' })
    submitSearch()
    cy.wait('@search')
    field('author').clear().type('Brian Herbert').type('{enter}')
    cy.wait('@search').its('request.body').should((body: SearchRequest) => {
      expect(body.title).to.equal('Dune')
      expect(body.author).to.equal('Brian Herbert')
    })
    cy.get('@search.all').should('have.length', 2)
  })

  it('shows no record for a 404 metadata lookup', () => {
    stubMetadata({ statusCode: 404, body: { message: 'none' } }, 'missingMetadata')
    openRepair()
    fillSearch({ asin: 'B008DFUGCQ' })
    submitSearch()
    cy.wait('@missingMetadata')
    card().contains(/No record for/).should('be.visible')
  })

  it('treats an empty metadata record as no result', () => {
    stubMetadata({ statusCode: 200, body: { metadata: { asin: 'B008DFUGCQ', title: '' } } }, 'emptyMetadata')
    openRepair()
    fillSearch({ asin: 'B008DFUGCQ' })
    submitSearch()
    cy.wait('@emptyMetadata')
    card().find('[data-testid="find-match-result"]').should('not.exist')
  })

  it('reports a failing search', () => {
    stubSearch({ statusCode: 500, body: { message: 'boom' } }, 'failedSearch')
    openRepair()
    fillSearch({ title: 'Dune' })
    submitSearch()
    cy.wait('@failedSearch')
    card().should(($card) => {
      expect($card.attr('data-match-state')).to.match(/error|failed/i)
    })
  })

  it('cancels a pending search promptly', () => {
    stubSearch({ delay: 4000, statusCode: 200, body: { results: [] } }, 'slowSearch')
    openRepair()
    fillSearch({ title: 'Dune' })
    field('title').type('{enter}')
    card().find('[data-testid="match-cancel-search"]').should('be.visible').click()
    cy.get('[data-testid="match-cancel-search"]', { timeout: 2000 }).should('not.exist')
  })
})