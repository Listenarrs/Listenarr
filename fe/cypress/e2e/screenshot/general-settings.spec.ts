describe('General Settings visual check', () => {
  it('captures the General Settings tab (desktop)', () => {
    // Visit the running dev server (adjust host/port if your dev server uses a different port)
    cy.visit('http://localhost:5173/settings#general')

    // Wait for the main settings panel to appear (increase timeout)
    cy.get('.general-settings-tab', { timeout: 15000 }).should('be.visible')

    // Ensure specific content has rendered: File Naming Pattern
    cy.contains('File Naming Pattern', { timeout: 15000 }).should('be.visible')

    // Small delay to allow fonts/assets to stabilize briefly
    cy.wait(400)

    // Take a full-page screenshot
    cy.screenshot('general-settings-fullpage', { capture: 'fullPage' })

    // Also capture the File Management card specifically
    cy.get('.form-section').first().screenshot('general-settings-file-management')
  })
})