describe('Full-stack E2E happy path', () => {
  it('loads the empty library through the live API', () => {
    cy.intercept('GET', '**/api/v1/library').as('library')

    cy.visit('/audiobooks')
    cy.wait('@library').then(({ response }) => {
      expect(response?.statusCode).to.equal(200)
      expect(response?.body).to.deep.equal([])
    })

    cy.contains('Audiobooks').should('be.visible')
  })
})