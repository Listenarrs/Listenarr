import {
  audiobookFixture,
  libraryAudiobookFixture,
  stubAudiobookIdentifiers,
  stubAudiobookReplies,
  stubLibrary,
  stubSettings,
  stubWeakStorageMissingFiles,
} from '../support/api-stubs'

const temporaryId = 201
const deletedId = 202
const availableId = 203

describe('Repair Books loading errors (API-stubbed)', () => {
  beforeEach(() => {
    cy.clearLocalStorage()

    const temporaryBook = audiobookFixture({
      id: temporaryId,
      title: 'Temporary Load Failure',
      authors: ['Test Author'],
      asin: 'B000000201',
    })

    stubSettings({ defaultSearchRegion: 'us' })
    stubLibrary([
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
    stubAudiobookReplies(temporaryId, [
      { statusCode: 503, body: { message: 'Temporary library read failure' } },
      { statusCode: 200, body: temporaryBook },
    ])
    stubAudiobookReplies(deletedId, [
      { statusCode: 404, body: { message: 'Audiobook not found' } },
    ])
    stubAudiobookReplies(availableId, [
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
    stubAudiobookIdentifiers(temporaryId, [])
    stubAudiobookIdentifiers(deletedId, [])
    stubAudiobookIdentifiers(availableId, [])
    stubWeakStorageMissingFiles(temporaryId)
    stubWeakStorageMissingFiles(deletedId)
    stubWeakStorageMissingFiles(availableId)
  })

  it('lets a temporary load failure recover while a deleted selection stays unavailable', () => {
    cy.visit('/')
    cy.contains('button', 'Select All').click()
    cy.contains('button', 'Repair Selected (3)').click()

    cy.wait('@getAudiobook')
    cy.wait('@getAudiobook')
    cy.wait('@getAudiobook')
    cy.get(`[data-testid="repair-row-${temporaryId}"]`)
      .find('[data-testid="repair-load-error"]')
      .should('exist')
    cy.get(`[data-testid="repair-row-${temporaryId}"]`)
      .find('[data-testid="repair-load-again"]')
      .should('be.enabled')
    cy.get(`[data-testid="repair-row-${deletedId}"]`)
      .find('[data-testid="repair-load-error"]')
      .should('exist')
    cy.get(`[data-testid="repair-row-${deletedId}"]`)
      .find('[data-testid="repair-load-again"]')
      .should('not.exist')
    cy.get(`[data-testid="repair-row-${availableId}"]`)
      .find('[data-testid="match-title"]')
      .should('have.value', 'Available Book')

    cy.get(`[data-testid="repair-row-${temporaryId}"]`)
      .find('[data-testid="repair-load-again"]')
      .click()
    cy.wait('@getAudiobook')

    cy.get(`[data-testid="repair-row-${temporaryId}"]`)
      .find('[data-testid="repair-load-error"]')
      .should('not.exist')
    cy.get(`[data-testid="repair-row-${temporaryId}"]`)
      .find('[data-testid="match-title"]')
      .should('have.value', 'Temporary Load Failure')
    cy.get(`[data-testid="repair-row-${deletedId}"]`)
      .find('[data-testid="repair-load-error"]')
      .should('exist')
    cy.get('@getAudiobook.all').should('have.length', 4)
  })
})