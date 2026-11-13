import {
  audiobookFixture,
  libraryImportUnmatchedFixture,
  rootFolderFixture,
  settingsFixture,
  stubAddLibrary,
  stubLibrary,
  stubLibraryImportSavedResults,
  stubManualImport,
  stubMetadata,
  stubRootFolders,
  stubSettings,
} from '../support/api-stubs'

const rootFolderId = 1
const audiobookId = 301
const asin = 'B000000301'

describe('Library Import apply flow (API-stubbed)', () => {
  beforeEach(() => {
    cy.clearLocalStorage()

    stubRootFolders([
      rootFolderFixture({
        id: rootFolderId,
        name: 'Library',
        path: '/library',
        isDefault: true,
        canScanFilesystem: true,
      }),
    ])
    stubSettings(settingsFixture({ outputPath: '/library', defaultSearchRegion: 'us' }))
    stubLibrary([])

    const item = libraryImportUnmatchedFixture({
      fullPath: '/incoming/Edgar Rice Burroughs/Barsoom/A Princess of Mars.m4b',
      sourceFiles: ['/incoming/Edgar Rice Burroughs/Barsoom/A Princess of Mars.m4b'],
      relativePath: 'Edgar Rice Burroughs/Barsoom',
      bookFolder: '/incoming/Edgar Rice Burroughs/Barsoom',
      title: 'A Princess of Mars',
      author: 'Edgar Rice Burroughs',
      asin,
    })
    stubLibraryImportSavedResults(rootFolderId, [item])
    stubMetadata(
      {
        statusCode: 200,
        body: {
          source: 'audible',
          metadata: {
            asin,
            title: 'A Princess of Mars',
            authors: [{ name: 'Edgar Rice Burroughs' }],
            series: [{ name: 'Barsoom', sequence: 1 }],
          },
        },
      },
      'princessMetadata',
    )
    stubAddLibrary(
      audiobookFixture({
        id: audiobookId,
        title: 'A Princess of Mars',
        authors: ['Edgar Rice Burroughs'],
        asin,
      }),
    )
    stubManualImport({
      importedCount: 1,
      totalCount: 1,
      results: [{ success: true }],
    })
  })

  it('adds the matched book and moves its source files into the selected library folder', () => {
    cy.visit('/library-import')
    cy.wait('@getRootFolders')
    cy.wait('@getSettings')
    cy.wait('@savedLibraryImportResults')

    cy.get('.import-row').find('button[title="Search for a match"]').click()
    cy.wait('@princessMetadata')
    cy.contains('[data-testid="find-match-result"]', 'A Princess of Mars').click()
    cy.get('[data-testid="find-match-confirm"]').should('be.enabled').click()
    cy.get('.import-row input[type="checkbox"]').should('be.checked')

    cy.get('.import-footer select').eq(1).select('move')
    cy.contains('.footer-right button', 'Import 1 Book').click()

    cy.wait('@princessMetadata')
    cy.wait('@addAudiobookToLibrary')
      .its('request.body')
      .should('include', {
        monitored: true,
        autoSearch: false,
        destinationPath: '/library',
      })
      .its('searchResult')
      .should('include', { asin, title: 'A Princess of Mars', region: 'us' })
    cy.wait('@importManualFiles')
      .its('request.body')
      .should('deep.equal', {
        path: '/incoming/Edgar Rice Burroughs/Barsoom',
        mode: 'interactive',
        action: 'move',
        includeCompanionFiles: true,
        cleanupEmptySourceFolders: true,
        items: [
          {
            fullPath: '/incoming/Edgar Rice Burroughs/Barsoom/A Princess of Mars.m4b',
            matchedAudiobookId: audiobookId,
          },
        ],
      })
    cy.contains('Import complete').should('be.visible')
  })
})