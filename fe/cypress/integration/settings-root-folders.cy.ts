/* eslint-disable cypress/unsafe-to-chain-command */
describe('Root Folders Settings', () => {
  beforeEach(() => {
    cy.intercept('GET', '**/api/v1/rootfolders', {
      body: [
        {
          id: 1,
          name: 'Root1',
          path: 'C:\\root',
          isDefault: true,
          storageState: 'Healthy',
          canChangePath: true,
          canMutateFilesystem: true,
          canScanFilesystem: true,
        },
      ],
    }).as('getRoots')
    cy.intercept('GET', '**/api/v1/system/ready', {
      body: { filesystemReady: true, filesystemStatus: 'Ready' },
    })
    cy.visit('/settings#rootfolders')
    cy.wait('@getRoots')
  })

  it('requests a metadata-only root path change when moving is disabled', () => {
    cy.intercept('POST', '**/api/v1/rootfolders/1/path-changes', (request) => {
      request.reply({ statusCode: 200, body: { status: 'Completed' } })
    }).as('putRoot')

    cy.contains('.folder-card', 'Root1').within(() => {
      cy.get('[data-cy="edit-root-folder"]').click()
    })

    cy.get('input[placeholder="Select or enter a path..."]').clear().type('D:\\newroot')
    cy.contains('Save').click()

    // Leave Move files now unchecked and confirm the path update only.
    cy.contains('label.checkbox-label', 'Move files now').click()
    cy.get('input[aria-label="Move files now"]').should('not.be.checked')
    cy.contains('Confirm New Folder').click()

    cy.wait('@putRoot').then((interception) => {
      expect(interception.request.method).to.equal('POST')
      expect(interception.request.body).to.include({
        targetPath: 'D:\\newroot',
        mode: 'metadataOnly',
        desiredName: 'Root1',
        expectedCurrentPath: 'C:\\root',
      })
    })
  })

  it('requests root relocation when Move files now is selected', () => {
    cy.intercept('POST', '**/api/v1/rootfolders/1/path-changes', (request) => {
      request.reply({ statusCode: 200, body: { status: 'Completed' } })
    }).as('putRootMove')

    cy.contains('.folder-card', 'Root1').within(() => {
      cy.get('[data-cy="edit-root-folder"]').click()
    })

    cy.get('input[placeholder="Select or enter a path..."]').clear().type('E:\\moved')
    cy.contains('Save').click()

    cy.contains('label.checkbox-label', 'Move files now').click()
    cy.get('input[aria-label="Move files now"]').should('not.be.checked')
    cy.contains('label.checkbox-label', 'Move files now').click()
    cy.get('input[aria-label="Move files now"]').should('be.checked')
    cy.contains('Confirm & Move Files').click()

    cy.wait('@putRootMove').then((interception) => {
      expect(interception.request.method).to.equal('POST')
      expect(interception.request.body).to.include({
        targetPath: 'E:\\moved',
        mode: 'relocate',
        desiredName: 'Root1',
        expectedCurrentPath: 'C:\\root',
      })
    })
  })
})