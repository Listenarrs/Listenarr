describe('General Settings visual check', () => {
  beforeEach(() => {
    cy.intercept('GET', '**/api/v1/configuration/startupconfig', {
      statusCode: 200,
      body: { authenticationRequired: false, apiKey: null, baseUrl: '/' }
    })
    cy.intercept('GET', '**/api/v1/account/me', {
      statusCode: 200,
      body: { authenticated: false }
    })
    cy.intercept('GET', '**/api/v1/configuration/settings', {
      statusCode: 200,
      body: {
        preferUsDomain: false,
        useUsProxy: false,
        usProxyHost: '',
        usProxyPort: 0,
        usProxyUsername: '',
        usProxyPassword: '',
        outputPath: '',
        fileNamingPattern: '{Author}/{Title}',
        completedFileAction: 'Move',
        maxConcurrentDownloads: 2,
        pollingIntervalSeconds: 30
      }
    })
    cy.intercept('GET', '**/api/v1/configuration/apis', { statusCode: 200, body: [] })
    cy.intercept('GET', '**/api/v1/download-clients', { statusCode: 200, body: [] })
    cy.intercept('GET', '**/api/v1/remotepath', { statusCode: 200, body: [] })
    cy.intercept('GET', '**/api/v1/indexers', { statusCode: 200, body: [] })
    cy.intercept('GET', '**/api/v1/qualityprofile', { statusCode: 200, body: [] })
    cy.intercept('GET', '**/api/v1/account/admins', { statusCode: 200, body: [] })
  })

  it('captures the General Settings tab (desktop)', () => {
    // Visit the running dev server (adjust host/port if your dev server uses a different port)
    cy.visit('http://localhost:5173/settings#general')

    // Wait for the main settings panel to appear (increase timeout)
    cy.get('.general-settings-tab', { timeout: 15000 }).should('be.visible')

    // Ensure specific content has rendered: File Naming Pattern
    cy.contains('File Naming Pattern', { timeout: 15000 }).should('be.visible')

    // Take a full-page screenshot
    cy.screenshot('general-settings-fullpage', { capture: 'fullPage' })

    // Also capture the File Management card specifically
    cy.get('.form-section').first().screenshot('general-settings-file-management')
  })
})
