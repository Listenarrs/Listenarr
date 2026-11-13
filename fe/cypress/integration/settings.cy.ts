import { stubSettingsPage, stubSettingsUpdate } from '../support/api-stubs'

describe('Settings UI integration', () => {
  beforeEach(() => {
    stubSettingsPage()

    stubSettingsUpdate()
  })

  it('updates the folder naming pattern and completed-file action', () => {
    cy.visit('/settings#general')
    cy.get('#app').should('exist')
    cy.wait('@getStartupConfig')
    cy.wait('@getSettings')

    cy.get('input[placeholder="{Author}/{Series}/{Title}"]').clear()
    cy.get('input[placeholder="{Author}/{Series}/{Title}"]').type('{Author}/{Title}', {
      parseSpecialCharSequences: false,
    })
    cy.contains('Completed File Action').parent().find('select').select('copy')
    cy.contains('Save Settings').click()

    cy.wait('@saveSettings').then(({ request }) => {
      expect(request.body.folderNamingPattern).to.equal('{Author}/{Title}')
      expect(request.body.completedFileAction).to.equal('copy')
    })
    cy.contains('Settings saved successfully').should('exist')
  })
})
