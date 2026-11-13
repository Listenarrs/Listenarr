import {
  libraryAudiobookFixture,
  rootFolderFixture,
  settingsFixture,
  stubAudiobookMove,
  stubLibrary,
  stubRootFolders,
  stubSettingsPage,
} from '../support/api-stubs'

describe('Edit -> Move integration', () => {
  beforeEach(() => {
    stubSettingsPage(settingsFixture({ outputPath: '/mnt/audiobooks' }))

    // Stub library endpoint to return a single audiobook
    stubLibrary([libraryAudiobookFixture({
      basePath: '/mnt/audiobooks/Test Author/Test Book',
      monitored: true,
    })])

    stubRootFolders([rootFolderFixture({ path: '/mnt/audiobooks' })])

    stubAudiobookMove(1)
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