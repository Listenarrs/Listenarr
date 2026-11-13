import {
  spyManualImport,
  stubMetadata,
} from '../../support/api-stubs'

const asin = 'B008DFUGCQ'
const title = 'A Princess of Mars'
const author = 'Edgar Rice Burroughs'
const sourcePath = '/e2e/import/Edgar Rice Burroughs/A Princess of Mars/A Princess of Mars.m4b'

describe('Full-stack E2E generated audio import', () => {
  it('scans, matches, and moves a generated tagged audiobook into the library', () => {
    stubMetadata({
      statusCode: 200,
      body: {
        source: 'audible',
        metadata: {
          asin,
          title,
          authors: [{ name: author }],
          series: [{ name: 'Barsoom', sequence: 1 }],
        },
      },
    }, 'generatedAudioMetadata')
    spyManualImport()

    cy.request('/api/v1/rootfolders').then(({ body: rootFolders }) => {
      const importFolder = rootFolders.find((folder: { name: string }) => folder.name === 'Cypress Import')
      const libraryFolder = rootFolders.find((folder: { name: string }) => folder.name === 'Cypress Library')
      if (!importFolder || !libraryFolder) throw new Error('The E2E filesystem root folders are missing.')

      cy.visit('/library-import')
      cy.get('.folder-select').select(String(importFolder.id))
      cy.contains('button', 'Scan').click()
      cy.contains('button', 'Scanning...', { timeout: 30_000 }).should('not.exist')
      cy.contains('.import-row', title, { timeout: 30_000 }).should('be.visible')

      cy.get('.import-row').find('button[title="Search for a match"]').click()
      cy.wait('@generatedAudioMetadata')
      cy.contains('[data-testid="find-match-result"]', title).click()
      cy.get('[data-testid="find-match-confirm"]').should('be.enabled').click()
      cy.get('.import-row input[type="checkbox"]').should('be.checked')

      cy.get('.import-footer select').eq(1).select('move')
      cy.get('.destination-select').select(String(libraryFolder.id))
      cy.contains('.footer-right button', 'Import 1 Book').click()
      cy.wait('@importManualFiles').then(({ response }) => {
        if (
          response?.statusCode !== 200 ||
          !response.body?.results?.every((result: { success: boolean }) => result.success)
        ) {
          throw new Error(`Manual import failed: ${JSON.stringify(response?.body)}`)
        }
      })
      cy.contains('Import complete').should('be.visible')

      cy.request('/api/v1/library').then(({ body: books }) => {
        const importedBook = books.find((book: { asin?: string }) => book.asin === asin)
        if (!importedBook) throw new Error('The imported audiobook was not persisted in the library.')
        cy.request(`/api/v1/library/${importedBook.id}`).then(({ body: savedBook }) => {
          const storedPath = savedBook.files?.[0]?.path ?? savedBook.filePath
          expect(storedPath, 'persisted audiobook file path').to.be.a('string').and.include('/e2e/library/')
          const destinationPath = storedPath.startsWith('/') ? storedPath : `${savedBook.basePath}/${storedPath}`
          cy.task('assertAudioFileMoved', { sourcePath, destinationPath }).should('eq', null)
        })
      })
    })
  })
})