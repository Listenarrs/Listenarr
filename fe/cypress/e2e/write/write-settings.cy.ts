import { spySettingsUpdate } from '../../support/api-stubs'

describe('Full-stack E2E write path', () => {
  it('saves a settings change and reads it back', () => {
    spySettingsUpdate()
    cy.visit('/settings#general')
    cy.contains('h3', 'General Settings').should('be.visible')

    const notifications = () =>
      cy.contains('strong', 'Enable Notifications').closest('label.input-checkbox')
    notifications().find('input[type="checkbox"]').should('not.be.checked')
    notifications().click()
    notifications().find('input[type="checkbox"]').should('be.checked')

    cy.contains('button', 'Save Settings').click()
    cy.wait('@saveSettings').its('response.statusCode').should('eq', 200)
    cy.reload()
    cy.contains('strong', 'Enable Notifications')
      .closest('label.input-checkbox')
      .find('input[type="checkbox"]')
      .should('be.checked')
  })

  it('restores seeded settings and books before the next write test', () => {
    cy.visit('/settings#general')
    cy.contains('h3', 'General Settings').should('be.visible')
    cy.contains('strong', 'Enable Notifications')
      .closest('label.input-checkbox')
      .find('input[type="checkbox"]')
      .should('not.be.checked')

    cy.request('/api/v1/library').its('body').should('have.length', 4)
  })
})