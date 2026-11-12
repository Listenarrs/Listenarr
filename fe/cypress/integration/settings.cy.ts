describe('Settings UI integration', () => {
  beforeEach(() => {
    // Stub startup config to indicate authentication is NOT required so the
    // SPA won't redirect to the login page during tests.
    cy.intercept('GET', '**/api/v1/configuration/bootstrap', {
      statusCode: 200,
      body: {
        authenticationRequired: false,
        apiKey: null,
        baseUrl: '/',
      }
    }).as('getStartupConfig')


    // Stub account/me to return an unauthenticated but non-redirecting response
    // (the SPA treats this as not requiring a login here).
    cy.intercept('GET', '**/api/v1/account/me', {
      statusCode: 200,
      body: { authenticated: false }
    }).as('getCurrentUser')


    // Intercept the GET for application settings with a deterministic baseline.
    cy.intercept('GET', '**/api/v1/configuration/settings', {
      statusCode: 200,
      body: {
        folderNamingPattern: '{Author}/{Series}/{Title}',
        fileNamingPattern: '{Author}/{Title}',
        multiFileNamingPattern: '{Title}-{DiskNumber:00}',
        completedFileAction: 'move',
        maxConcurrentDownloads: 2,
        pollingIntervalSeconds: 30,
      }
    }).as('getSettings')


    // Stub other startup endpoints the Settings page loads so Promise.all settles
    cy.intercept('GET', '**/api/v1/configuration/apis', { statusCode: 200, body: [] }).as('getApis')
    cy.intercept('GET', '**/api/v1/download-clients', { statusCode: 200, body: [] }).as('getDownloadClients')
    cy.intercept('GET', '**/api/v1/remotepath', { statusCode: 200, body: [] }).as('getRemotePathMappings')
    cy.intercept('GET', '**/api/v1/indexers', { statusCode: 200, body: [] }).as('getIndexers')
    cy.intercept('GET', '**/api/v1/qualityprofile', { statusCode: 200, body: [] }).as('getQualityProfiles')
    cy.intercept('GET', '**/api/v1/account/admins', { statusCode: 200, body: [] }).as('getAdminUsers')

    // Intercept save and assert payload
    cy.intercept('POST', '**/api/v1/configuration/settings', (req) => {
      req.reply((res) => {
        // Respond with the same payload to simulate persistence
        res.send({ statusCode: 200, body: req.body })
      })
    }).as('saveSettings')
  })

  it('updates the folder naming pattern and completed-file action', () => {
    cy.visit('/settings#general')
    cy.get('#app').should('exist')
    cy.wait('@getStartupConfig')
    cy.wait('@getSettings')

    cy.get('input[placeholder="{Author}/{Series}/{Title}"]').clear()
    cy.get('input[placeholder="{Author}/{Series}/{Title}"]').type('{Author}/{Title}', {
      parseSpecialCharSequences: false,
    })
    cy.contains('Completed File Action').parent().find('select').select('copy')
    cy.contains('Save Settings').click()

    cy.wait('@saveSettings').then(({ request }) => {
      expect(request.body.folderNamingPattern).to.equal('{Author}/{Title}')
      expect(request.body.completedFileAction).to.equal('copy')
    })
    cy.contains('Settings saved successfully').should('exist')
  })
})
