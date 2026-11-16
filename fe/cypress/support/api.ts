export const apiPath = (endpoint: string): string => {
  const normalized = endpoint.startsWith('/') ? endpoint : `/${endpoint}`
  return `/api/v*${normalized}`
}

export const stubAppShellApi = (): void => {
  cy.intercept('GET', apiPath('/system/health'), {
    statusCode: 200,
    body: { status: 'healthy', version: '0.0.0-e2e' },
  }).as('getSystemHealth')

  cy.intercept('GET', apiPath('/antiforgery/token'), {
    statusCode: 200,
    body: { token: 'cypress-antiforgery-token' },
  }).as('getAntiforgeryToken')

  cy.intercept('GET', apiPath('/download/queue'), {
    statusCode: 200,
    body: { items: [], totalCount: 0 },
  }).as('getDownloadQueue')

  cy.intercept('GET', apiPath('/library'), {
    statusCode: 200,
    body: [],
  }).as('getLibrary')

  cy.intercept('GET', apiPath('/rootfolders'), {
    statusCode: 200,
    body: [],
  }).as('getRootFolders')
}
