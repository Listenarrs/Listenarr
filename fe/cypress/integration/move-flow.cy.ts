/* eslint-disable cypress/unsafe-to-chain-command */
describe('Edit -> Move integration', () => {
  beforeEach(() => {
    // Stub startup config and account checks (no auth)
    cy.intercept('GET', '**/api/v1/configuration/bootstrap', {
      statusCode: 200,
      body: { authenticationRequired: false, apiKey: null, baseUrl: '/' }
    }).as('getStartupConfig')

    cy.intercept('GET', '**/api/v1/account/me', { statusCode: 200, body: { authenticated: false } }).as('getCurrentUser')

    // App settings with outputPath configured
    cy.intercept('GET', '**/api/v1/configuration/settings', {
      statusCode: 200,
      body: {
        outputPath: '/mnt/audiobooks'
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

    // Stub quality profiles / other endpoints minimally
    cy.intercept('GET', '**/api/v1/qualityprofile', { statusCode: 200, body: [] }).as('getProfiles')

    // Capture both filesystem moves and metadata-only destination changes.
    cy.intercept('POST', '**/api/v1/library/1/move', (req) => {
      const body = req.body.moveFiles
        ? { message: 'queued', jobId: 'job-test-1', target: req.body.destinationPath }
        : { message: 'Destination updated' }
      req.reply({ statusCode: 200, body })
    }).as('moveAudiobook')
  })

  it('edits destination, confirms move, enqueues job, and shows toast', () => {
    cy.visit('/')
    cy.contains('Audiobooks', { timeout: 10000 }).should('be.visible')
    cy.contains('Audiobooks', { timeout: 10000 }).click()

    // Wait for library load
    cy.wait('@getStartupConfig')
    cy.wait('@getLibrary')

    // Open edit modal for the single audiobook
    cy.get('button[title="Edit"]').first().click({ force: true })

    // Ensure modal and destination input are visible
    cy.contains('Edit Audiobook: Test Book', { timeout: 10000 }).should('be.visible')
    cy.get('button[aria-label="Edit destination"]').click()
    cy.get('input.relative-input').should('exist').clear().type('New Author/New Book')
    cy.get('button[aria-label="Save destination"]').click()

    // Click Save — this should trigger the confirm dialog
    cy.contains('button', 'Save').click()

    // Confirm dialog should appear; click Move
    cy.get('input[aria-label="Move files now"]').should('be.checked')
    cy.contains('button', 'Move Files').click()

    // Verify the move endpoint carries the source, destination, and selected mode.
    cy.wait('@moveAudiobook').then((interception) => {
      const body = interception.request.body
      expect(body.destinationPath).to.equal('/mnt/audiobooks/New Author/New Book')
      expect(body.sourcePath).to.equal('/mnt/audiobooks/Test Author/Test Book')
      expect(body.moveFiles).to.equal(true)
    })

    // Expect a toast informing the move was queued
    cy.contains('Move queued', { timeout: 5000 }).should('exist')
  })

  it('edits destination and chooses "Change without moving" to update DB only', () => {

    cy.visit('/')
    cy.contains('Audiobooks', { timeout: 10000 }).should('be.visible').click()

    // Wait for library load
    cy.wait('@getStartupConfig')
    cy.wait('@getLibrary')

    // Open edit modal for the single audiobook
    cy.get('button[title="Edit"]').first().click({ force: true })

    // Ensure modal and destination input are visible
    cy.contains('Edit Audiobook: Test Book', { timeout: 10000 }).should('be.visible')
    cy.get('button[aria-label="Edit destination"]').click()
    cy.get('input.relative-input').should('exist').clear().type('New Author/New Book')
    cy.get('button[aria-label="Save destination"]').click()

    // Click Save — this should trigger the confirm dialog
    cy.contains('button', 'Save').click()

    // Confirm dialog should appear; click 'Change without moving'
    cy.contains('label.checkbox-label', 'Move files now').click()
    cy.get('input[aria-label="Move files now"]').should('not.be.checked')
    cy.contains('button', 'Update Path').click()

    cy.wait('@moveAudiobook').then((interception) => {
      expect(interception.request.body.destinationPath).to.equal('/mnt/audiobooks/New Author/New Book')
      expect(interception.request.body.sourcePath).to.equal('/mnt/audiobooks/Test Author/Test Book')
      expect(interception.request.body.moveFiles).to.equal(false)
      expect(interception.request.body.deleteEmptySource).to.equal(false)
    })

    // Expect a toast informing destination updated without moving
    cy.contains('Destination updated', { timeout: 5000 }).should('exist')
  })
})