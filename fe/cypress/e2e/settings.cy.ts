import { apiPath, stubAppShellApi } from '../support/api'

describe('Settings UI - e2e', () => {
  beforeEach(() => {
    stubAppShellApi()

    // Stub startup config to indicate authentication is NOT required so the
    // SPA won't redirect to the login page during tests.
    cy.intercept('GET', apiPath('/configuration/startupconfig'), {
      statusCode: 200,
      body: {
        authenticationRequired: false,
        apiKey: null,
        baseUrl: '/',
      },
    }).as('getStartupConfig')

    // Stub account/me to return an unauthenticated but non-redirecting response
    // (the SPA treats this as not requiring a login here).
    cy.intercept('GET', apiPath('/account/me'), {
      statusCode: 200,
      body: { authenticated: false },
    }).as('getCurrentUser')

    // Intercept the GET for application settings and return the baseline used by the general tab.
    cy.intercept('GET', apiPath('/configuration/settings'), {
      statusCode: 200,
      body: {
        outputPath: '/mnt/audiobooks',
        folderNamingPattern: '{Author}/{Series}/{Title}',
        fileNamingPattern: '{Title}',
        multiFileNamingPattern: '{Title}-{DiskNumber:00}',
        completedFileAction: 'copy',
        maxConcurrentDownloads: 2,
        pollingIntervalSeconds: 30,
        enableOpenLibrarySearch: true,
        defaultSearchRegion: 'us',
        defaultSearchLanguage: 'english',
      },
    }).as('getSettings')

    // Stub other startup endpoints the Settings page loads so Promise.all settles
    cy.intercept('GET', apiPath('/configuration/apis'), { statusCode: 200, body: [] }).as('getApis')
    cy.intercept('GET', apiPath('/configuration/download-clients'), {
      statusCode: 200,
      body: [],
    }).as('getDownloadClients')
    cy.intercept('GET', apiPath('/remotepath'), { statusCode: 200, body: [] }).as(
      'getRemotePathMappings',
    )
    cy.intercept('GET', apiPath('/indexers'), { statusCode: 200, body: [] }).as('getIndexers')
    cy.intercept('GET', apiPath('/qualityprofile'), { statusCode: 200, body: [] }).as(
      'getQualityProfiles',
    )
    cy.intercept('GET', apiPath('/account/admins'), { statusCode: 200, body: [] }).as(
      'getAdminUsers',
    )

    // Intercept save and assert payload
    cy.intercept('POST', apiPath('/configuration/settings'), (req) => {
      req.reply((res) => {
        // Respond with the same payload to simulate persistence
        res.send({ statusCode: 200, body: req.body })
      })
    }).as('saveSettings')

    cy.intercept('POST', apiPath('/configuration/startupconfig'), {
      statusCode: 200,
      body: { success: true },
    }).as('saveStartupConfig')
  })

  // On failure, save the current page HTML and a screenshot to help diagnose
  // what the SPA rendered when the test failed.
  afterEach(function () {
    // Use function() to access `this.currentTest`
    if (this.currentTest && this.currentTest.state === 'failed') {
      const ts = Date.now()
      const htmlPath = `cypress/screenshots/failure-${ts}.html`
      const shotName = `failure-${ts}`
      // Write the full HTML document to the screenshots folder for debugging
      cy.document().then((doc) => {
        const html = doc.documentElement.outerHTML
        cy.writeFile(htmlPath, html)
        cy.log(`Wrote failure HTML to ${htmlPath}`)
      })
      // Also take a screenshot (Cypress will also capture one on failure but we ensure it)
      cy.screenshot(shotName)
    }
  })

  it('updates file naming settings and saves general settings', () => {
    cy.visit('/settings#general', { timeout: 10000 })

    cy.wait(['@getStartupConfig', '@getSettings'], { timeout: 20000 })
    cy.get('.settings-page', { timeout: 20000 }).should('exist')
    cy.url({ timeout: 10000 }).should('include', '/settings')

    cy.get('.general-settings-tab .section-header h3', { timeout: 10000 }).should(
      'contain',
      'General Settings',
    )

    cy.contains('Single File Naming Pattern').parent().find('input').as('singleFilePatternInput')
    cy.get('@singleFilePatternInput').clear()
    cy.get('@singleFilePatternInput').type('{Author} - {Title}', {
      parseSpecialCharSequences: false,
    })
    cy.get('@singleFilePatternInput').blur()

    cy.contains('button', 'Save Settings').click()

    // Confirm save request was made with expected payload
    cy.wait('@saveSettings').then((interception) => {
      const body = interception.request.body
      expect(body.fileNamingPattern).to.equal('{Author} - {Title}')
      expect(body.multiFileNamingPattern).to.equal('{Title}-{DiskNumber:00}')
    })

    cy.contains('Settings saved successfully').should('exist')
  })
})
