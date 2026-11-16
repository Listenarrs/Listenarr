import { apiPath } from '../support/api'

/* eslint-disable cypress/unsafe-to-chain-command */
describe('Edit -> Move flow (E2E)', () => {
  beforeEach(() => {
    // Stub startup config and account checks (no auth)
    cy.intercept('GET', apiPath('/configuration/startupconfig'), {
      statusCode: 200,
      body: { authenticationRequired: false, apiKey: null, baseUrl: '/' },
    }).as('getStartupConfig')

    cy.intercept('GET', apiPath('/account/me'), {
      statusCode: 200,
      body: { authenticated: false },
    }).as('getCurrentUser')

    // App settings with outputPath configured
    cy.intercept('GET', apiPath('/configuration/settings'), {
      statusCode: 200,
      body: {
        outputPath: '/mnt/audiobooks',
      },
    }).as('getSettings')

    // Stub library endpoint to return a single audiobook
    cy.intercept('GET', apiPath('/library'), {
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
          explicit: false,
        },
      ],
    }).as('getLibrary')

    // Stub quality profiles / other endpoints minimally
    cy.intercept('GET', apiPath('/qualityprofile'), { statusCode: 200, body: [] }).as('getProfiles')

    // Capture the PUT update request for assertions
    cy.intercept('PUT', apiPath('/library/1'), (req) => {
      req.reply((res) => {
        // Respond with updated audiobook by echoing payload
        const updated = Object.assign(
          { id: 1, title: 'Test Book', author: 'Test Author' },
          req.body,
        )
        res.send({ statusCode: 200, body: { message: 'ok', audiobook: updated } })
      })
    }).as('updateAudiobook')

    // Capture move request and return job id
    cy.intercept('POST', apiPath('/library/1/move'), (req) => {
      req.reply({ statusCode: 200, body: { message: 'queued', jobId: 'job-test-1' } })
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
    cy.get('button[title="Edit"]').first().should('be.visible').click()

    // Ensure modal and destination input are visible
    cy.get('.modal-container', { timeout: 10000 }).should('exist')
    cy.get('input.relative-input').should('exist').clear().type('New Author/New Book')

    // Click Save — this should trigger the confirm dialog
    cy.contains('Save Changes').click()

    // Confirm dialog should appear; click Move
    cy.get('.confirm-dialog').should('exist')
    cy.get('.confirm-dialog .btn.confirm').contains('Move').click()

    // Ensure update and move endpoints were called with expected payloads
    cy.wait('@updateAudiobook').then((interception) => {
      const body = interception.request.body
      // basePath sent via PUT should be the combined root + relative
      expect(body.basePath).to.equal('/mnt/audiobooks/New Author/New Book')
    })

    cy.wait('@moveAudiobook').then((interception) => {
      const body = interception.request.body
      expect(body.destinationPath).to.equal('/mnt/audiobooks/New Author/New Book')
      expect(body.sourcePath).to.equal('/mnt/audiobooks/Test Author/Test Book')
    })

    // Expect a toast informing the move was queued
    cy.contains('Move queued', { timeout: 5000 }).should('exist')
  })

  it('edits destination and chooses "Change without moving" to update DB only', () => {
    // Override move intercept to fail the test if called
    cy.intercept('POST', apiPath('/library/1/move'), () => {
      throw new Error('Move API should not be called when user selects Change without moving')
    }).as('moveShouldNotBeCalled')

    cy.visit('/')
    cy.contains('Audiobooks', { timeout: 10000 }).should('be.visible').click()

    // Wait for library load
    cy.wait('@getStartupConfig')
    cy.wait('@getLibrary')

    // Open edit modal for the single audiobook
    cy.get('button[title="Edit"]').first().should('be.visible').click()

    // Ensure modal and destination input are visible
    cy.get('.modal-container', { timeout: 10000 }).should('exist')
    cy.get('input.relative-input').should('exist').clear().type('New Author/New Book')

    // Click Save — this should trigger the confirm dialog
    cy.contains('Save Changes').click()

    // Confirm dialog should appear; click 'Change without moving'
    cy.get('.confirm-dialog').should('exist')
    cy.get('.confirm-dialog .btn').contains('Change without moving').click()

    // Ensure update endpoint was called with expected payload
    cy.wait('@updateAudiobook')
      .its('request.body')
      .then((body) => {
        expect(body.basePath).to.equal('/mnt/audiobooks/New Author/New Book')
      })

    // Expect a toast informing destination updated without moving
    cy.contains('Destination updated', { timeout: 5000 }).should('exist')
  })
})
