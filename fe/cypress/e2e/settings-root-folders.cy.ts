import { apiPath } from '../support/api'

/* eslint-disable cypress/unsafe-to-chain-command */
describe('Root Folders Settings', () => {
  beforeEach(() => {
    cy.intercept('GET', apiPath('/rootfolders'), {
      body: [{ id: 1, name: 'Root1', path: 'C:\\root' }],
    }).as('getRoots')
    cy.visit('/settings')
    cy.wait('@getRoots')
  })

  it('renames root without moving (DB-only)', () => {
    cy.intercept('PUT', apiPath('/rootfolders/1*'), (req) => {
      req.reply({ statusCode: 200, body: { id: 1, name: 'Root1', path: 'D:\\newroot' } })
    }).as('putRoot')

    // Open Root Folders in Settings
    cy.contains('Root Folders').click()
    cy.get('[data-cy=root-folder-row-1]').within(() => {
      cy.contains('Edit').click()
    })

    cy.get('input[placeholder="Select or enter a path..."]').clear().type('D\\newroot')
    cy.contains('Save').click()

    // Confirmation modal should appear — choose Change without moving
    cy.contains('Change without moving').click()

    cy.wait('@putRoot').then((interception) => {
      expect(interception.request.url).to.contain('moveFiles=false')
      expect(interception.request.body).to.include({ name: 'Root1', path: 'D\\newroot' })
    })
  })

  it('renames root and queues moves when Move selected', () => {
    cy.intercept('PUT', apiPath('/rootfolders/1*'), (req) => {
      // Simulate backend accepting move request
      req.reply({ statusCode: 200, body: { id: 1, name: 'Root1', path: 'E:\\moved' } })
    }).as('putRootMove')

    cy.contains('Root Folders').click()
    cy.get('[data-cy=root-folder-row-1]').within(() => {
      cy.contains('Edit').click()
    })

    cy.get('input[placeholder="Select or enter a path..."]').clear().type('E\\moved')
    cy.contains('Save').click()

    // Confirmation modal should appear — choose Move
    cy.contains('Move').click()

    cy.wait('@putRootMove').its('request.url').should('contain', 'moveFiles=true')
    cy.wait('@putRootMove')
      .its('request.body')
      .should('include', { name: 'Root1', path: 'E\\moved' })
  })
})
