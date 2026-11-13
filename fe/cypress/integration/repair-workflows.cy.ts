import {
  audiobookFixture,
  repairSearchResultFixture,
  settingsFixture,
  stubMetadata,
  stubRepairPage,
  stubRepairWrites,
  stubSearch,
  stubSettings,
} from '../support/api-stubs'

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

const repairRow = () => cy.get(`[data-testid="repair-row-${audiobookId}"]`)
const repairResult = () => cy.get(`[data-testid="repair-result-${audiobookId}"]`)

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

function prepareRepair(
  rescanReplies: Parameters<typeof stubRepairWrites>[1] = [successfulRescanReply()],
) {
  stubRepairPage(audiobookId, audiobook, oldIdentifiers)
  stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
  stubMetadata({
    statusCode: 200,
    body: { metadata: { asin: oldAsin, title: audiobook.title } },
  })
  stubSearch({ statusCode: 200, body: { results: [selectedMatch] } }, 'repairSearch')
  stubRepairWrites(audiobookId, rescanReplies)

  cy.visit(`/audiobooks/${audiobookId}`)
  cy.get('button[aria-label="Repair Book"]').click()
  repairRow().should('be.visible')
}

function chooseDifferentMatch() {
  repairRow().find('[data-testid="match-asin"]').clear()
  repairRow().find('[data-testid="match-title"]').clear().type(selectedMatch.title)
  repairRow().find('[data-testid="match-author"]').clear().type(selectedMatch.authors[0]!.name)
  repairRow().find('[data-testid="match-search"]').click()

  cy.wait('@repairSearch').its('request.body').should('deep.equal', {
    mode: 'Advanced',
    title: selectedMatch.title,
    author: selectedMatch.authors[0]!.name,
    region: selectedMatch.region,
    pagination: { page: 1, limit: 50 },
  })
  cy.contains('[data-testid="find-match-result"]', selectedMatch.title).click()
  repairRow()
    .find('[data-testid="find-match-result"]')
    .should('have.attr', 'aria-selected', 'true')
}

function askToRepair() {
  cy.get('[data-testid="repair-confirm"]').should('be.enabled').click()
  cy.get('.confirm-dialog').should('contain.text', 'Repair 1 book?')
  cy.get('.confirm-dialog').should('contain.text', "This can't be undone.")
}

describe('Repair Books workflows (API-stubbed)', () => {
  beforeEach(() => {
    cy.clearLocalStorage()
  })

  it('confirms a different match and repairs the audiobook', () => {
    prepareRepair()
    chooseDifferentMatch()
    askToRepair()

    cy.get('.confirm-dialog').contains('button', 'Repair 1').click()

    cy.wait('@saveAudiobookIdentifiers').its('request.body').should('deep.equal', {
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
    cy.wait('@clearAudiobookMetadata').its('request.body').should('deep.equal', emptiedRecord)
    cy.wait('@rescanAudiobookMetadata').then(({ request }) => {
      expect(request.method).to.equal('POST')
      expect(request.url).to.include(`/api/v1/library/${audiobookId}/rescan-metadata`)
    })

    repairResult()
      .should('have.attr', 'data-result-state', 'done')
      .and('contain.text', selectedMatch.title)
      .and('contain.text', selectedMatch.asin)
    repairResult().find('[data-testid="repair-result-include"]').should('be.enabled').and('be.checked')
    cy.get('[data-testid="repair-organize"]').should('be.enabled').and('contain.text', 'Organize 1')
  })

  it('cancels the repair confirmation without sending mutation requests', () => {
    prepareRepair()
    chooseDifferentMatch()
    askToRepair()

    cy.get('.confirm-dialog').contains('button', 'Cancel').click()

    cy.get('.confirm-dialog').should('not.exist')
    cy.get('[data-testid="repair-row-1"]').should('be.visible')
    cy.get('[data-testid="repair-confirm"]').should('be.enabled')
    cy.get('@saveAudiobookIdentifiers.all').should('have.length', 0)
    cy.get('@clearAudiobookMetadata.all').should('have.length', 0)
    cy.get('@rescanAudiobookMetadata.all').should('have.length', 0)
  })

  it('offers Retry after a failed rescan and enables Organize when retry succeeds', () => {
    prepareRepair([
      { statusCode: 500, body: { message: 'Metadata service unavailable' } },
      successfulRescanReply(),
    ])
    chooseDifferentMatch()
    askToRepair()
    cy.get('.confirm-dialog').contains('button', 'Repair 1').click()

    cy.wait('@saveAudiobookIdentifiers')
    cy.wait('@clearAudiobookMetadata')
    cy.wait('@rescanAudiobookMetadata')
    repairResult().should('have.attr', 'data-result-state', 'warning')
    repairResult().find('[data-testid="repair-result-message"]').should('contain.text', 'metadata rescan failed')
    repairResult().find('[data-testid="repair-result-retry"]').should('be.visible').and('be.enabled')
    repairResult().find('[data-testid="repair-result-include"]').should('be.disabled')
    cy.get('[data-testid="repair-organize"]').should('be.disabled').and('contain.text', 'Organize 0')

    repairResult().find('[data-testid="repair-result-retry"]').click()
    cy.wait('@clearAudiobookMetadata')
    cy.wait('@rescanAudiobookMetadata')

    repairResult()
      .should('have.attr', 'data-result-state', 'done')
      .and('contain.text', selectedMatch.title)
      .and('contain.text', selectedMatch.asin)
    repairResult().find('[data-testid="repair-result-retry"]').should('not.exist')
    repairResult().find('[data-testid="repair-result-include"]').should('be.enabled').and('be.checked')
    cy.get('[data-testid="repair-organize"]').should('be.enabled').and('contain.text', 'Organize 1')
    cy.get('@rescanAudiobookMetadata.all').should('have.length', 2)
  })

  it('blocks leaving during an active repair and allows navigation after Stay', () => {
    prepareRepair([{ ...successfulRescanReply(), delay: 1000 }])
    chooseDifferentMatch()
    askToRepair()
    cy.get('.confirm-dialog').contains('button', 'Repair 1').click()
    repairResult().find('[data-testid="repair-result-message"]').should('contain.text', 'Repairing')

    cy.contains('a', 'Settings').click({ force: true })
    cy.get('.confirm-dialog').should('contain.text', 'Leave while books are repaired?')
    cy.get('.confirm-dialog').contains('button', 'Stay').click()
    cy.location('pathname').should('include', `/audiobooks/${audiobookId}`)

    cy.wait('@rescanAudiobookMetadata')
    repairResult().should('have.attr', 'data-result-state', 'done')
    cy.contains('a', 'Settings').click({ force: true })
    cy.location('pathname').should('eq', '/settings')
    cy.get('.confirm-dialog').should('not.exist')
  })
})