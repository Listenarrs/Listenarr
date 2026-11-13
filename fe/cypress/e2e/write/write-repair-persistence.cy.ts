import { repairSearchResultFixture, stubRepairRescan, stubSearch } from '../../support/api-stubs'

type LibraryBook = {
  id: number
  title: string
  authors: string[]
}
type QualityProfile = { id: number }

describe('Full-stack E2E repair persistence', () => {
  it('fails a deleted book but continues and persists the next selected book', () => {
    const deletedMatch = repairSearchResultFixture({
      asin: 'B099CY0001',
      title: 'Cypress Deleted Match',
      authors: [{ name: 'Cypress Test Author' }],
      region: 'us',
    })
    const repairedMatch = repairSearchResultFixture({
      asin: 'B099CY0002',
      title: 'Cypress Persisted Repair',
      authors: [{ name: 'Cypress Test Author' }],
      region: 'us',
    })

    cy.request('/api/v1/library').then(({ body }) => {
      const [deletedBook, repairedBook] = body as LibraryBook[]
      if (!deletedBook || !repairedBook) throw new Error('The E2E seed needs two library books.')

      stubSearch({ statusCode: 200, body: { results: [deletedMatch, repairedMatch] } }, 'repairSearch')
      stubRepairRescan(repairedBook.id, {
        body: {
          message: 'Metadata refreshed',
          audiobookId: repairedBook.id,
          asin: repairedMatch.asin,
          region: repairedMatch.region,
          audiobook: {
            ...repairedBook,
            title: repairedMatch.title,
            authors: repairedMatch.authors.map((author) => author.name),
          },
        },
      })

      cy.visit('/')
      cy.contains('.audiobook-item', deletedBook.title)
        .find('.selection-checkbox')
        .click({ force: true })
      cy.contains('.audiobook-item', repairedBook.title)
        .find('.selection-checkbox')
        .click({ force: true })
      cy.contains('button', 'Repair Selected (2)').click()

      for (const { book, match } of [
        { book: deletedBook, match: deletedMatch },
        { book: repairedBook, match: repairedMatch },
      ]) {
        const row = () => cy.get(`[data-testid="repair-row-${book.id}"]`)
        row().find('[data-testid="match-asin"]').clear()
        row().find('[data-testid="match-title"]').clear().type(match.title)
        row().find('[data-testid="match-author"]').clear().type(match.authors[0]!.name)
        row().find('[data-testid="match-search"]').click()
        cy.wait('@repairSearch')
        row().contains('[data-testid="find-match-result"]', match.title).click()
        row()
          .contains('[data-testid="find-match-result"]', match.title)
          .should('have.attr', 'aria-selected', 'true')
      }

      cy.request('/api/v1/antiforgery/token').then(({ body: tokenResponse }) => {
        cy.request({
          method: 'DELETE',
          url: `/api/v1/library/${deletedBook.id}`,
          qs: { deleteFiles: false, deleteFolder: false },
          headers: { 'X-XSRF-TOKEN': tokenResponse.token },
        }).its('status').should('eq', 200)
      })

      cy.get('[data-testid="repair-confirm"]').click()
      cy.get('.confirm-dialog').should('contain.text', 'Repair 2 books?')
      cy.get('.confirm-dialog .btn.confirm').click()

      cy.get(`[data-testid="repair-result-${deletedBook.id}"]`)
        .should('have.attr', 'data-result-state', 'failed')
      cy.get(`[data-testid="repair-result-${repairedBook.id}"]`)
        .should('have.attr', 'data-result-state', 'done')

      cy.request(`/api/v1/library/${repairedBook.id}/identifiers`).then(({ body: identifiersResponse }) => {
        const identifiers = identifiersResponse.identifiers as { value: string; region: string; isPrimary: boolean }[]
        if (
          !identifiers.some(
            (identifier) =>
              identifier.value.toUpperCase() === repairedMatch.asin.toUpperCase() &&
              identifier.region.toLowerCase() === repairedMatch.region.toLowerCase() &&
              identifier.isPrimary,
          )
        ) {
          throw new Error('The repaired ASIN was not persisted as the primary US identifier.')
        }
      })
      cy.request('/api/v1/library').its('body').should('have.length', 3)
      cy.request({ url: `/api/v1/library/${deletedBook.id}`, failOnStatusCode: false })
        .its('status')
        .should('eq', 404)

      cy.get(`[data-testid="repair-result-${deletedBook.id}"]`)
        .find('[data-testid="repair-result-retry"]')
        .click()
      cy.get(`[data-testid="repair-result-${deletedBook.id}"]`)
        .should('have.attr', 'data-result-state', 'failed')
    })
  })

  it('keeps monitoring and quality-profile settings after a record-change repair', () => {
    const match = repairSearchResultFixture({
      asin: 'B099CY0003',
      title: 'Cypress Repair Settings',
      authors: [{ name: 'Cypress Test Author' }],
      region: 'us',
    })

    cy.request('/api/v1/library').then(({ body }) => {
      const book = (body as LibraryBook[])[0]
      if (!book) throw new Error('The E2E seed needs a library book.')

      cy.request('/api/v1/antiforgery/token').then(({ body: tokenResponse }) => {
        cy.request({
          method: 'POST',
          url: '/api/v1/qualityprofile',
          headers: { 'X-XSRF-TOKEN': tokenResponse.token },
          body: { name: 'Cypress Repair Profile', isDefault: true },
        }).then(({ body: profileBody }) => {
          const profile = profileBody as QualityProfile
          cy.request({
            method: 'PUT',
            url: `/api/v1/library/${book.id}`,
            headers: { 'X-XSRF-TOKEN': tokenResponse.token },
            body: { monitored: false, qualityProfileId: profile.id },
          }).its('status').should('eq', 200)

          stubSearch({ statusCode: 200, body: { results: [match] } }, 'repairSearch')
          stubRepairRescan(book.id, {
            body: {
              message: 'Metadata refreshed',
              audiobookId: book.id,
              asin: match.asin,
              region: match.region,
              audiobook: {
                ...book,
                title: match.title,
                authors: match.authors.map((author) => author.name),
              },
            },
          })

          cy.visit('/')
          cy.contains('.audiobook-item', book.title)
            .find('.selection-checkbox')
            .click({ force: true })
          cy.contains('button', 'Repair Selected (1)').click()
          const row = () => cy.get(`[data-testid="repair-row-${book.id}"]`)
          row().find('[data-testid="match-asin"]').clear()
          row().find('[data-testid="match-title"]').clear().type(match.title)
          row().find('[data-testid="match-author"]').clear().type(match.authors[0]!.name)
          row().find('[data-testid="match-search"]').click()
          cy.wait('@repairSearch')
          row().contains('[data-testid="find-match-result"]', match.title).click()
          cy.get('[data-testid="repair-confirm"]').click()
          cy.get('.confirm-dialog .btn.confirm').click()
          cy.get(`[data-testid="repair-result-${book.id}"]`)
            .should('have.attr', 'data-result-state', 'done')

          cy.request(`/api/v1/library/${book.id}`).then(({ body: savedBook }) => {
            expect(savedBook).to.include({ monitored: false, qualityProfileId: profile.id })
          })
        })
      })
    })
  })
})