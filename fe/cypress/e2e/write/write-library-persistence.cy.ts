type LibraryBook = { id: number; title: string }
type QualityProfile = { id: number; name: string; isDefault: boolean }

describe('Full-stack E2E library persistence', () => {
  it('shows a real database deletion as unavailable while the other selected books load', () => {
    cy.visit('/')
    cy.get('.audiobook-item').should('have.length', 4)

    cy.request('/api/v1/library').then(({ body }) => {
      const books = body as LibraryBook[]
      const deletedBook = books[0]!
      const remainingBooks = books.slice(1)

      cy.contains('button', 'Select All').click()
      cy.contains('button', `Repair Selected (${books.length})`).should('be.visible')
      cy.request('/api/v1/antiforgery/token').then(({ body: tokenResponse }) => {
        cy.request({
          method: 'DELETE',
          url: `/api/v1/library/${deletedBook.id}`,
          qs: { deleteFiles: false, deleteFolder: false },
          headers: { 'X-XSRF-TOKEN': tokenResponse.token },
        }).its('status').should('eq', 200)
      })

      cy.contains('button', `Repair Selected (${books.length})`).click()
      cy.get(`[data-testid="repair-row-${deletedBook.id}"]`)
        .find('[data-testid="repair-load-error"]')
        .should('be.visible')
      cy.get(`[data-testid="repair-row-${deletedBook.id}"]`)
        .find('[data-testid="repair-load-again"]')
        .should('not.exist')

      for (const book of remainingBooks) {
        cy.get(`[data-testid="repair-row-${book.id}"]`)
          .find('[data-testid="match-title"]')
          .should('have.value', book.title)
      }

      cy.request('/api/v1/library').its('body').should('have.length', books.length - 1)
      cy.request({
        url: `/api/v1/library/${deletedBook.id}`,
        failOnStatusCode: false,
      }).its('status').should('eq', 404)
    })
  })

  it('keeps per-book monitoring and quality profile settings after editing metadata and reloading', () => {
    const updatedSubtitle = 'Persisted Cypress subtitle'

    cy.visit('/')
    cy.request('/api/v1/library').then(({ body }) => {
      const book = (body as LibraryBook[])[0]!
      cy.request('/api/v1/antiforgery/token').then(({ body: tokenResponse }) => {
        cy.request({
          method: 'POST',
          url: '/api/v1/qualityprofile',
          headers: { 'X-XSRF-TOKEN': tokenResponse.token },
          body: {
            name: 'Cypress Persistence Profile',
            isDefault: true,
          },
        }).then(({ body: profileBody }) => {
          const profile = profileBody as QualityProfile

          cy.request({
            method: 'PUT',
            url: `/api/v1/library/${book.id}`,
            headers: { 'X-XSRF-TOKEN': tokenResponse.token },
            body: { monitored: false, qualityProfileId: profile.id },
          }).its('status').should('eq', 200)

          cy.contains('.audiobook-item', book.title)
            .find('button[title="Edit"]')
            .click({ force: true })
          cy.get('input[name="monitored"]').last().should('be.checked')
          cy.get('#quality-profile').should('have.value', String(profile.id))
          cy.get('#metadata-subtitle').clear()
          cy.get('#metadata-subtitle').type(updatedSubtitle)
          cy.get('button[aria-label="Save"]').click()
          cy.get('#metadata-subtitle').should('not.exist')

          cy.reload()
          cy.request(`/api/v1/library/${book.id}`).then(({ body: savedBook }) => {
            expect(savedBook).to.include({
              monitored: false,
              qualityProfileId: profile.id,
              subtitle: updatedSubtitle,
            })
          })

          cy.contains('.audiobook-item', book.title)
            .find('button[title="Edit"]')
            .click({ force: true })
          cy.get('#metadata-subtitle').should('have.value', updatedSubtitle)
          cy.get('#quality-profile').should('have.value', String(profile.id))
          cy.get('input[name="monitored"]').last().should('be.checked')
        })
      })
    })
  })
})