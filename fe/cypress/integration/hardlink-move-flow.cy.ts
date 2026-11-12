/* eslint-disable cypress/unsafe-to-chain-command */
describe('Hardlink/Copy move integration', () => {
  beforeEach(() => {
    // Stub startup config and account checks (no auth)
    cy.intercept('GET', '**/api/v1/configuration/bootstrap', {
      statusCode: 200,
      body: { authenticationRequired: false, apiKey: null, baseUrl: '/' }
    }).as('getStartupConfig')

    cy.intercept('GET', '**/api/v1/account/me', { statusCode: 200, body: { authenticated: false } }).as('getCurrentUser')

    // App settings with outputPath and default file handling mode (Hardlink/Copy)
    cy.intercept('GET', '**/api/v1/configuration/settings', {
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
    cy.intercept('GET', '**/api/v1/library', {
      statusCode: 200,
      body: [
        {
          id: 1,
          title: 'Test Book',
          author: 'Test Author',
          basePath: '/mnt/audiobooks/Test Author/Test Book',
          rootFolderId: 1,
          monitored: true,
          qualityProfileId: null,
          tags: [],
          abridged: false,
          explicit: false
        }
      ]
    }).as('getLibrary')

    cy.intercept('GET', '**/api/v1/rootfolders', {
      statusCode: 200,
      body: [
        {
          id: 1,
          name: 'Audiobooks',
          path: '/mnt/audiobooks',
          isDefault: true,
          storageState: 'Healthy',
          canChangePath: true,
        },
      ],
    })

    // Stub other endpoints
    cy.intercept('GET', '**/api/v1/qualityprofile', { statusCode: 200, body: [] }).as('getProfiles')
    cy.intercept('GET', '**/api/v1/configuration/apis', { statusCode: 200, body: [] }).as('getApis')
    cy.intercept('GET', '**/api/v1/download-clients', { statusCode: 200, body: [] }).as('getDownloadClients')

    // Capture move requests with the chosen file handling mode.
    cy.intercept('POST', '**/api/v1/library/1/move', (req) => {
      const body = req.body.moveFiles
        ? { message: 'queued', jobId: 'job-test-1', target: req.body.destinationPath }
        : { message: 'Destination updated' }
      req.reply({ statusCode: 200, body })
    }).as('moveAudiobook')

    // Stub volume check endpoint
    cy.intercept('GET', '**/api/v1/filesystem/check-volume*', {
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
    cy.get('button[title="Edit"]').first().click({ force: true })

    // Ensure modal and destination input are visible
    cy.contains('Edit Audiobook: Test Book', { timeout: 10000 }).should('be.visible')
    cy.get('button[aria-label="Edit destination"]').click()
    cy.get('input.relative-input').should('exist').clear().type('New Author/New Book')
    cy.get('button[aria-label="Save destination"]').click()

    // Click Save Changes to trigger confirm dialog
    cy.contains('button', 'Save').click()

    // Move confirmation dialog should appear
    cy.get('input[aria-label="Move files now"]').should('be.checked')
    cy.contains('button', 'Move Files').should('be.visible')
  })

  it('calls move API when confirming move', () => {
    cy.visit('/')
    cy.contains('Audiobooks', { timeout: 10000 }).should('be.visible')
    cy.contains('Audiobooks', { timeout: 10000 }).click()

    cy.wait('@getStartupConfig')
    cy.wait('@getLibrary')

    // Open edit modal for the single audiobook
    cy.get('button[title="Edit"]').first().click({ force: true })

    // Ensure modal and destination input are visible
    cy.contains('Edit Audiobook: Test Book', { timeout: 10000 }).should('be.visible')
    cy.get('button[aria-label="Edit destination"]').click()
    cy.get('input.relative-input').should('exist').clear().type('New Author/New Book')
    cy.get('button[aria-label="Save destination"]').click()

    // Click Save Changes to trigger confirm dialog
    cy.contains('button', 'Save').click()

    // Confirm move
    cy.get('input[aria-label="Move files now"]').should('be.checked')
    cy.contains('button', 'Move Files').click()

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
    cy.get('button[title="Edit"]').first().click({ force: true })

    // Ensure modal and destination input are visible
    cy.contains('Edit Audiobook: Test Book', { timeout: 10000 }).should('be.visible')
    cy.get('button[aria-label="Edit destination"]').click()
    cy.get('input.relative-input').should('exist').clear().type('New Author/New Book')
    cy.get('button[aria-label="Save destination"]').click()

    // Click Save Changes to trigger confirm dialog
    cy.contains('button', 'Save').click()

    // Choose "Change without moving"
    cy.contains('label.checkbox-label', 'Move files now').click()
    cy.get('input[aria-label="Move files now"]').should('not.be.checked')
    cy.contains('button', 'Update Path').click()

    cy.wait('@moveAudiobook').its('request.body.moveFiles').should('equal', false)
    cy.contains('Destination updated', { timeout: 5000 }).should('exist')
  })

  it('requests volume check when move dialog opens', () => {
    cy.visit('/')
    cy.contains('Audiobooks', { timeout: 10000 }).should('be.visible')
    cy.contains('Audiobooks', { timeout: 10000 }).click()

    cy.wait('@getStartupConfig')
    cy.wait('@getLibrary')

    // Open edit modal for the single audiobook
    cy.get('button[title="Edit"]').first().click({ force: true })

    // Ensure modal and destination input are visible
    cy.contains('Edit Audiobook: Test Book', { timeout: 10000 }).should('be.visible')
    cy.get('button[aria-label="Edit destination"]').click()
    cy.get('input.relative-input').should('exist').clear().type('New Author/New Book')
    cy.get('button[aria-label="Save destination"]').click()

    // Click Save Changes to trigger confirm dialog
    cy.contains('button', 'Save').click()

    // Move confirmation dialog should appear
    cy.get('input[aria-label="Move files now"]').should('be.checked')

    // The volume check API should have been called
    cy.wait('@checkVolume')
  })
})
