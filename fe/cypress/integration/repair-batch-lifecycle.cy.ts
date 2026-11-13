import {
  audiobookFixture,
  libraryAudiobookFixture,
  repairSearchResultFixture,
  stubAudiobook,
  stubAudiobookIdentifiers,
  stubLibrary,
  stubRepairWrites,
  stubSearchWithReply,
  stubSettings,
  stubWeakStorageMissingFiles,
} from '../support/api-stubs'

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

const repairResult = (audiobookId: number) => cy.get(`[data-testid="repair-result-${audiobookId}"]`)

function prepareBatchRepair(
  firstRescanReplies: Parameters<typeof stubRepairWrites>[1] = [],
  secondRescanReplies: Parameters<typeof stubRepairWrites>[1] = [],
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

  stubSettings({ defaultSearchRegion: 'us' })
  stubLibrary([
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
    stubAudiobook(audiobookId, audiobook)
    stubAudiobookIdentifiers(audiobookId, [
      { type: 'Asin', value: oldAsin, region: 'us', isPrimary: true, source: 'Imported' },
    ])
    stubWeakStorageMissingFiles(audiobookId)
  }
  stubRepairWrites(firstAudiobookId, firstRescanReplies)
  stubRepairWrites(secondAudiobookId, secondRescanReplies)

  let searchIndex = 0
  const matches = [firstMatch, secondMatch]
  stubSearchWithReply(() => ({
    statusCode: 200,
    body: { results: [matches[searchIndex++]!] },
  }), 'repairBatchSearch')

  cy.visit('/')
  cy.contains('button', 'Select All').click()
  cy.contains('button', 'Repair Selected (2)').click()
  cy.get(`[data-testid="repair-row-${firstAudiobookId}"]`).should('exist')
  cy.get(`[data-testid="repair-row-${secondAudiobookId}"]`).should('exist')
}

function chooseMatch(audiobookId: number, match: typeof firstMatch) {
  const row = () => cy.get(`[data-testid="repair-row-${audiobookId}"]`)
  row().find('[data-testid="match-asin"]').clear()
  row().find('[data-testid="match-title"]').clear().type(match.title)
  row().find('[data-testid="match-author"]').clear().type(match.authors[0]!.name)
  row().find('[data-testid="match-search"]').click()

  cy.wait('@repairBatchSearch').its('request.body').should('deep.equal', {
    mode: 'Advanced',
    title: match.title,
    author: match.authors[0]!.name,
    region: match.region,
    pagination: { page: 1, limit: 50 },
  })
  cy.contains('[data-testid="find-match-result"]', match.title).click()
  row().find('[data-testid="find-match-result"]').should('have.attr', 'aria-selected', 'true')
}

function confirmBatchRepair() {
  cy.get('[data-testid="repair-confirm"]').should('be.enabled').click()
  cy.get('.confirm-dialog').should('contain.text', 'Repair 2 books?')
  cy.get('.confirm-dialog').contains('button', 'Repair 2').click()
}

describe('Repair Books batch lifecycle (API-stubbed)', () => {
  beforeEach(() => {
    cy.clearLocalStorage()
  })

  it('keeps only successful repairs eligible for Organize until a failed book is retried successfully', () => {
    prepareBatchRepair(
      [firstRescanSuccess],
      [
        { statusCode: 500, body: { message: 'Metadata service unavailable' } },
        secondRescanSuccess,
      ],
    )
    chooseMatch(firstAudiobookId, firstMatch)
    chooseMatch(secondAudiobookId, secondMatch)
    confirmBatchRepair()

    repairResult(firstAudiobookId).should('have.attr', 'data-result-state', 'done')
    repairResult(firstAudiobookId)
      .find('[data-testid="repair-result-include"]')
      .should('be.enabled')
      .and('be.checked')
    repairResult(secondAudiobookId).should('have.attr', 'data-result-state', 'warning')
    repairResult(secondAudiobookId)
      .find('[data-testid="repair-result-message"]')
      .should('contain.text', 'metadata rescan failed')
    repairResult(secondAudiobookId)
      .find('[data-testid="repair-result-include"]')
      .should('be.disabled')
      .and('not.be.checked')
    repairResult(secondAudiobookId)
      .find('[data-testid="repair-result-retry"]')
      .should('be.visible')
      .and('be.enabled')
    cy.get('[data-testid="repair-organize"]')
      .should('be.enabled')
      .and('contain.text', 'Organize 1')

    repairResult(secondAudiobookId).find('[data-testid="repair-result-retry"]').click()

    repairResult(firstAudiobookId).should('have.attr', 'data-result-state', 'done')
    repairResult(secondAudiobookId).should('have.attr', 'data-result-state', 'done')
    repairResult(secondAudiobookId).find('[data-testid="repair-result-retry"]').should('not.exist')
    repairResult(secondAudiobookId)
      .find('[data-testid="repair-result-include"]')
      .should('be.enabled')
      .and('be.checked')
    cy.get('[data-testid="repair-organize"]')
      .should('be.enabled')
      .and('contain.text', 'Organize 2')
    cy.get('@rescanAudiobookMetadata.all').should('have.length', 3)
  })

  it('finishes the in-flight repair after cancel and never sends the queued repair', () => {
    prepareBatchRepair([{ ...firstRescanSuccess, delay: 2500 }])
    chooseMatch(firstAudiobookId, firstMatch)
    chooseMatch(secondAudiobookId, secondMatch)
    confirmBatchRepair()

    repairResult(firstAudiobookId)
      .find('[data-testid="repair-result-message"]')
      .should('contain.text', 'Repairing')
    cy.get('[data-testid="repair-cancel"]').click()

    repairResult(firstAudiobookId).should('have.attr', 'data-result-state', 'done')
    repairResult(secondAudiobookId).should('have.attr', 'data-result-state', 'skipped')
    repairResult(secondAudiobookId)
      .find('[data-testid="repair-result-message"]')
      .should('contain.text', 'Canceled: not repaired')
    repairResult(secondAudiobookId)
      .find('[data-testid="repair-result-include"]')
      .should('be.disabled')
    cy.get('[data-testid="repair-organize"]')
      .should('be.enabled')
      .and('contain.text', 'Organize 1')
    cy.get('@rescanAudiobookMetadata.all').should('have.length', 1)
    cy.get('@rescanAudiobookMetadata.all').then((requests) => {
      expect(requests[0]?.request.url).to.include(`/api/v1/library/${firstAudiobookId}/rescan-metadata`)
    })
  })
})