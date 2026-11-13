import {
  libraryAudiobookFixture,
  rootFolderFixture,
  settingsFixture,
  stubAudiobookMove,
  stubFileSystemCheckVolume,
  stubLibrary,
  stubRootFolders,
  stubSettingsPage,
} from '../support/api-stubs'

describe('Hardlink/Copy move integration', () => {
  beforeEach(() => {
    stubSettingsPage(settingsFixture({
      outputPath: '/mnt/audiobooks',
      fileNamingPattern: '{Author}/{Title}',
      completedFileAction: 'hardlink/copy',
    }))

    // Stub library endpoint to return a single audiobook
    stubLibrary([libraryAudiobookFixture({
      basePath: '/mnt/audiobooks/Test Author/Test Book',
      monitored: true,
    })])

    stubRootFolders([rootFolderFixture({ path: '/mnt/audiobooks' })])

    stubAudiobookMove(1)
    stubFileSystemCheckVolume()
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
