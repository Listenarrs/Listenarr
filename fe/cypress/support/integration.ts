import './commands'

beforeEach(() => {
  cy.intercept({ method: 'GET', url: /\/api\/v1\// }, { statusCode: 200, body: [] })
  cy.intercept('GET', '**/api/v1/configuration/bootstrap', {
    statusCode: 200,
    body: { authenticationRequired: false, apiKey: null, baseUrl: '/' },
  })
  cy.intercept('GET', '**/api/v1/configuration/settings', {
    statusCode: 200,
    body: {
      outputPath: '/audiobooks',
      fileNamingPattern: '{Author}/{Title}',
      multiFileNamingPattern: '{Title}-{DiskNumber:00}',
      completedFileAction: 'Move',
      defaultSearchRegion: 'us',
      defaultSearchLanguage: 'english',
    },
  })
  cy.intercept('GET', '**/api/v1/account/me', {
    statusCode: 200,
    body: { authenticated: false },
  })
  cy.intercept('GET', '**/api/v1/system/health', {
    statusCode: 200,
    body: { status: 'healthy', version: 'test' },
  })
  cy.intercept('GET', '**/api/v1/system/ready', {
    statusCode: 200,
    body: { isReady: true, filesystemReady: true, filesystemStatus: 'Ready' },
  })
  cy.intercept('GET', '**/api/v1/download/queue', { statusCode: 200, body: [] })
  cy.intercept('GET', '**/api/v1/configuration/apikey', {
    statusCode: 200,
    body: { apiKey: 'cypress-test-key' },
  })
  cy.intercept('GET', '**/api/v1/antiforgery/token', {
    statusCode: 200,
    body: { token: 'cypress-test-token' },
  })
  cy.intercept({ method: /POST|PUT|PATCH|DELETE/, url: /\/api\/v1\// }, (request) => {
    request.reply({ statusCode: 501, body: { message: 'No Cypress mutation stub was configured.' } })
  })
})