describe('Broken books group (E2E)', () => {
  const books = [
    {
      id: 1,
      title: 'The Clockmaker’s Secret',
      authors: ['Mara Ellison'],
      files: [],
      brokenReasons: ['missing_region'],
      genres: ['Mystery'],
    },
    {
      id: 2,
      title: '',
      authors: ['Jonah Reed'],
      files: [],
      brokenReasons: ['missing_title', 'missing_asin'],
      genres: [],
    },
    {
      id: 3,
      title: 'A Book Ready for Audible',
      authors: ['Riley Hart'],
      files: [],
      brokenReasons: [],
      genres: ['Fiction'],
    },
  ]

  it('shows the group menu and the books with server-provided reasons', () => {
    cy.viewport(1600, 900)
    cy.intercept('GET', '**/api/**', { statusCode: 200, body: [] })
    for (const path of [
      '**/api/configuration/bootstrap',
      '**/api/configuration/startupconfig',
      '**/api/v1/configuration/bootstrap',
      '**/api/v1/configuration/startupconfig',
    ]) {
      cy.intercept('GET', path, {
        statusCode: 200,
        body: { authenticationRequired: false, apiKey: null, baseUrl: '/', apiVersion: '1' },
      })
    }
    for (const path of ['**/api/account/me', '**/api/v1/account/me']) {
      cy.intercept('GET', path, { statusCode: 200, body: { authenticated: false } })
    }
    for (const path of ['**/api/system/ready', '**/api/v1/system/ready']) {
      cy.intercept('GET', path, {
        statusCode: 200,
        body: {
          isReady: true,
          status: 'ready',
          databaseConnected: true,
          migrationsCurrent: true,
          filesystemReady: true,
          filesystemStatus: 'Ready',
          filesystemPhase: null,
          filesystemErrorCode: null,
          filesystemErrorMessage: null,
        },
      })
    }
    cy.intercept('GET', '**/api/v1/library', { statusCode: 200, body: books }).as('library')

    cy.visit('/audiobooks')
    cy.wait('@library')
    cy.get('.audiobook-poster-container').should('have.length', 3)
    cy.contains('The Clockmaker’s Secret').should('exist')
    cy.contains('A Book Ready for Audible').should('exist')
    cy.get('.group-btn').should('be.visible').click()
    cy.get('.group-menu').should('be.visible')
    cy.wait(1000)
    cy.get('[data-testid="group-broken"]').click()
    cy.get('.group-btn').should('contain.text', 'Broken books')
    cy.get('.audiobook-poster-container').should('have.length', 2)
    cy.contains('The Clockmaker’s Secret').should('exist')
    cy.contains('A Book Ready for Audible').should('not.exist')
    cy.get('[data-testid="broken-reasons"]').should('have.length', 2)
    cy.get('.broken-reason').should('have.length', 3)
    cy.contains('[data-testid="broken-reasons"]', 'No region').should('be.visible')
    cy.contains('[data-testid="broken-reasons"]', 'No title')
      .should('be.visible')
      .and('contain.text', 'No ASIN')
    cy.wait(1800)
  })
})