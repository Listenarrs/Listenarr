import {
  rootFolderFixture,
  stubRootFolderPathChange,
  stubRootFolders,
  stubSystemReadiness,
} from '../support/api-stubs'

describe('Root Folders Settings', () => {
  beforeEach(() => {
    stubRootFolders([rootFolderFixture({
      name: 'Root1',
      path: 'C:\\root',
      canMutateFilesystem: true,
      canScanFilesystem: true,
    })])
    stubSystemReadiness()
    cy.visit('/settings#rootfolders')
    cy.wait('@getRootFolders')
  })

  it('requests a metadata-only root path change when moving is disabled', () => {
    stubRootFolderPathChange(1)

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
    stubRootFolderPathChange(1, 'putRootMove')

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