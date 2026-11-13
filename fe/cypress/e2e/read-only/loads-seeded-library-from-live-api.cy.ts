import { spyLibraryRequest } from '../../support/api-stubs'

describe('Full-stack read-only E2E', () => {
  it('loads the seeded library through the live API', () => {
    spyLibraryRequest()

    cy.visit('/audiobooks')
    cy.wait('@library').then(({ response }) => {
      expect(response?.statusCode).to.equal(200)
      expect(response?.body).to.have.length(4)
      expect(response?.body.map((book: { title: string }) => book.title).sort()).to.deep.equal([
        'Pride and Prejudice',
        'Robinson Crusoe',
        'The Strange Case of Dr Jekyll and Mr Hyde',
        'Twenty Thousand Leagues Under the Sea',
      ])
      const twentyThousandLeagues = response?.body.find(
        (book: { title: string }) => book.title === 'Twenty Thousand Leagues Under the Sea',
      )
      expect(twentyThousandLeagues?.authors).to.deep.equal(['Jules Verne'])
    })

    cy.contains('Audiobooks').should('be.visible')
    cy.get('.audiobook-item').should('have.length', 4)
  })
})