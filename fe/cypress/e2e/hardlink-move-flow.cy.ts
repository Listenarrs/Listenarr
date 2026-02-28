/* eslint-disable cypress/unsafe-to-chain-command */
describe('Hardlink/Copy Move Flow (E2E)', () => {
  beforeEach(() => {
    // Stub startup config and account checks (no auth)
    cy.intercept('GET', '/api/configuration/startupconfig', {
      statusCode: 200,
      body: { authenticationRequired: false, apiKey: null, baseUrl: '/' }
    }).as('getStartupConfig')

    cy.intercept('GET', '/api/account/me', { statusCode: 200, body: { authenticated: false } }).as('getCurrentUser')

    // App settings with outputPath and default file handling mode (Hardlink/Copy)
    cy.intercept('GET', '/api/configuration/settings', {
      statusCode: 200,
      body: {
        outputPath: '/mnt/audiobooks',
        fileNamingPattern: '{Author}/{Title}',
        completedFileAction: 'Hardlink/Copy',
        maxConcurrentDownloads: 2,
        pollingIntervalSeconds: 30
      }
    }).as('getSettings')

    // Stub library endpoint to return a single audiobook
    cy.intercept('GET', '/api/library', {
      statusCode: 200,
      body: [
        {
          id: 1,
          title: 'Test Book',
          author: 'Test Author',
          basePath: '/mnt/audiobooks/Test Author/Test Book',
          monitored: true,
          qualityProfileId: null,
          tags: [],
          abridged: false,
          explicit: false
        }
      ]
    }).as('getLibrary')

    // Stub other endpoints
    cy.intercept('GET', '/api/qualityprofile', { statusCode: 200, body: [] }).as('getProfiles')
    cy.intercept('GET', '/api/configuration/apis', { statusCode: 200, body: [] }).as('getApis')
    cy.intercept('GET', '/api/configuration/download-clients', { statusCode: 200, body: [] }).as('getDownloadClients')

    // Capture the PUT update request for assertions
    cy.intercept('PUT', '/api/library/1', (req) => {
      req.reply((res) => {
        const updated = Object.assign({ id: 1, title: 'Test Book', author: 'Test Author' }, req.body)
        res.send({ statusCode: 200, body: { message: 'ok', audiobook: updated } })
      })
    }).as('updateAudiobook')

    // Capture move request with fileHandling mode
    cy.intercept('POST', '/api/library/1/move', (req) => {
      req.reply({ statusCode: 200, body: { message: 'queued', jobId: 'job-test-1' } })
    }).as('moveAudiobook')

    // Stub volume check endpoint
    cy.intercept('GET', '/api/filesystem/check-volume*', {
      statusCode: 200,
      body: {
        sameVolume: true,
        willBreakHardlinks: false,
        sourceVolume: '/mnt',
        destVolume: '/mnt',
        message: 'Same volume'
      }
    }).as('checkVolume')
  })

  it('displays move confirmation dialog when editing audiobook', () => {
    cy.visit('/')
    cy.contains('Audiobooks', { timeout: 10000 }).should('be.visible')
    cy.contains('Audiobooks', { timeout: 10000 }).click()

    cy.wait('@getStartupConfig')
    cy.wait('@getLibrary')

    // Open edit modal for the single audiobook
    cy.get('button[title="Edit"]').first().should('be.visible').click()

    // Ensure modal and destination input are visible
    cy.get('.modal-container', { timeout: 10000 }).should('exist')
    cy.get('input.relative-input').should('exist').clear().type('New Author/New Book')

    // Click Save Changes to trigger confirm dialog
    cy.contains('Save Changes').click()

    // Move confirmation dialog should appear
    cy.get('.confirm-dialog').should('exist')
    cy.get('.confirm-dialog').contains('Move').should('be.visible')
  })

  it('calls move API when confirming move', () => {
    cy.visit('/')
    cy.contains('Audiobooks', { timeout: 10000 }).should('be.visible')
    cy.contains('Audiobooks', { timeout: 10000 }).click()

    cy.wait('@getStartupConfig')
    cy.wait('@getLibrary')

    // Open edit modal for the single audiobook
    cy.get('button[title="Edit"]').first().should('be.visible').click()

    // Ensure modal and destination input are visible
    cy.get('.modal-container', { timeout: 10000 }).should('exist')
    cy.get('input.relative-input').should('exist').clear().type('New Author/New Book')

    // Click Save Changes to trigger confirm dialog
    cy.contains('Save Changes').click()

    // Confirm move
    cy.get('.confirm-dialog').should('exist')
    cy.get('.confirm-dialog .btn.confirm').contains('Move').click()

    // Verify move API was called
    cy.wait('@moveAudiobook')
    cy.contains('Move queued', { timeout: 5000 }).should('exist')
  })

  it('respects user choice to change without moving', () => {
    cy.visit('/')
    cy.contains('Audiobooks', { timeout: 10000 }).should('be.visible')
    cy.contains('Audiobooks', { timeout: 10000 }).click()

    cy.wait('@getStartupConfig')
    cy.wait('@getLibrary')

    // Open edit modal for the single audiobook
    cy.get('button[title="Edit"]').first().should('be.visible').click()

    // Ensure modal and destination input are visible
    cy.get('.modal-container', { timeout: 10000 }).should('exist')
    cy.get('input.relative-input').should('exist').clear().type('New Author/New Book')

    // Click Save Changes to trigger confirm dialog
    cy.contains('Save Changes').click()

    // Choose "Change without moving"
    cy.get('.confirm-dialog').should('exist')
    cy.get('.confirm-dialog .btn.cancel').contains('Change without moving').click()

    // Verify update was called
    cy.wait('@updateAudiobook')
    cy.contains('Destination updated', { timeout: 5000 }).should('exist')
  })

  it('requests volume check when move dialog opens', () => {
    cy.visit('/')
    cy.contains('Audiobooks', { timeout: 10000 }).should('be.visible')
    cy.contains('Audiobooks', { timeout: 10000 }).click()

    cy.wait('@getStartupConfig')
    cy.wait('@getLibrary')

    // Open edit modal for the single audiobook
    cy.get('button[title="Edit"]').first().should('be.visible').click()

    // Ensure modal and destination input are visible
    cy.get('.modal-container', { timeout: 10000 }).should('exist')
    cy.get('input.relative-input').should('exist').clear().type('New Author/New Book')

    // Click Save Changes to trigger confirm dialog
    cy.contains('Save Changes').click()

    // Move confirmation dialog should appear
    cy.get('.confirm-dialog').should('exist')

    // The volume check API should have been called
    cy.wait('@checkVolume')
  })
})
