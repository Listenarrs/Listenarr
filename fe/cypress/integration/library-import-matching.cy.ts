import {
  libraryImportSearchResultFixture,
  libraryImportUnmatchedFixture,
  rootFolderFixture,
  settingsFixture,
  stubLibrary,
  stubLibraryImportSavedResults,
  stubLibraryImportScan,
  stubLibraryImportSearch,
  stubMetadata,
  stubRootFolders,
  stubSettings,
} from '../support/api-stubs'

const rootFolderId = 1

function openLibraryImport(savedItems: ReturnType<typeof libraryImportUnmatchedFixture>[] = []) {
  stubLibraryImportSavedResults(rootFolderId, savedItems)
  cy.visit('/library-import')
  cy.wait('@getRootFolders')
  cy.wait('@getSettings')
  cy.wait('@savedLibraryImportResults')
}

describe('Library Import browser behavior (API-stubbed)', () => {
  beforeEach(() => {
    cy.clearLocalStorage()
    stubRootFolders([
      rootFolderFixture({
        id: rootFolderId,
        name: 'Incoming',
        path: '/incoming',
        isDefault: true,
        canScanFilesystem: true,
      }),
    ])
    stubLibrary([])
  })

  it('scans unmatched rows, searches each in turn, and continues after a failed search', () => {
    const firstItem = libraryImportUnmatchedFixture({
      fullPath: '/incoming/Author One/First Book/First Book.m4b',
      relativePath: 'Author One/First Book',
      bookFolder: '/incoming/Author One/First Book',
      title: 'First Book',
      author: 'Author One',
    })
    const secondItem = libraryImportUnmatchedFixture({
      fullPath: '/incoming/Author Two/Second Book/Second Book.m4b',
      relativePath: 'Author Two/Second Book',
      bookFolder: '/incoming/Author Two/Second Book',
      title: 'Second Book',
      author: 'Author Two',
    })
    openLibraryImport()
    stubLibraryImportScan(rootFolderId, [firstItem, secondItem])

    cy.contains('button', 'Scan').click()
    cy.wait('@startLibraryImportScan')
    cy.wait('@getLibraryImportScanResults')
    cy.get('.import-row').should('have.length', 2)
    cy.contains('.import-row', 'First Book').should('be.visible')
    cy.contains('.import-row', 'Second Book').should('be.visible')

    const selectedMatch = libraryImportSearchResultFixture({
      asin: 'B000000002',
      id: 'B000000002',
      title: 'Second Book Audible Edition',
      authors: [{ name: 'Author Two' }],
    })
    const otherAuthorResult = libraryImportSearchResultFixture({
      asin: 'B000000003',
      id: 'B000000003',
      title: 'Wrong Author Edition',
      authors: [{ name: 'Another Author' }],
    })
    let searchCount = 0
    stubLibraryImportSearch(() => {
      searchCount += 1
      if (searchCount === 1) {
        return { statusCode: 500, body: { message: 'Search unavailable' } }
      }
      return { statusCode: 200, body: [otherAuthorResult, selectedMatch] }
    })

    cy.contains('.footer-center button', 'Start Matching').click()
    cy.wait('@libraryImportSearch')
      .its('request.body')
      .should('deep.equal', {
        mode: 'Advanced',
        title: 'First Book',
        author: 'Author One',
        region: 'us',
        cap: 5,
      })
    cy.wait('@libraryImportSearch')
      .its('request.body')
      .should('deep.equal', {
        mode: 'Advanced',
        title: 'Second Book',
        author: 'Author Two',
        region: 'us',
        cap: 5,
      })

    cy.get('.import-row').eq(0).should('contain.text', 'No match found')
    cy.contains('.import-row', 'Second Book').should('contain.text', 'Second Book Audible Edition')
    cy.get('.import-row').eq(1).find('input[type="checkbox"]').should('be.checked')
    cy.get('.import-row').eq(0).find('input[type="checkbox"]').should('be.disabled')
  })

  it('restores selected matches after reloading the page', () => {
    const item = libraryImportUnmatchedFixture({
      fullPath: '/incoming/Author/Reload Book/Reload Book.m4b',
      relativePath: 'Author/Reload Book',
      bookFolder: '/incoming/Author/Reload Book',
      title: 'Reload Book',
      author: 'Author',
    })
    const match = libraryImportSearchResultFixture({
      asin: 'B000000010',
      id: 'B000000010',
      title: 'Reload Book Match',
      authors: [{ name: 'Author' }],
    })
    stubLibraryImportSearch(() => ({ statusCode: 200, body: [match] }))
    openLibraryImport([item])

    cy.contains('.footer-center button', 'Start Matching').click()
    cy.wait('@libraryImportSearch')
    cy.contains('.import-row', 'Reload Book Match').should('be.visible')
    cy.get('.import-row input[type="checkbox"]').should('be.checked')

    cy.reload()
    cy.wait('@getRootFolders')
    cy.wait('@getSettings')
    cy.wait('@savedLibraryImportResults')
    cy.contains('.import-row', 'Reload Book Match').should('be.visible')
    cy.get('.import-row input[type="checkbox"]').should('be.checked')
    cy.contains('.footer-center button', 'Start Matching').should('not.exist')
  })

  it('stops queued searches until Start Matching is explicitly restarted', () => {
    const items = ['First', 'Second', 'Third'].map((title, index) =>
      libraryImportUnmatchedFixture({
        fullPath: `/incoming/Author ${index + 1}/${title} Book/${title} Book.m4b`,
        relativePath: `Author ${index + 1}/${title} Book`,
        bookFolder: `/incoming/Author ${index + 1}/${title} Book`,
        title: `${title} Book`,
        author: `Author ${index + 1}`,
      }),
    )
    const match = libraryImportSearchResultFixture({
      asin: 'B000000020',
      id: 'B000000020',
      title: 'Matched Book',
    })
    let searchCount = 0
    stubLibraryImportSearch(() => {
      searchCount += 1
      return {
        statusCode: 200,
        body: [match],
        ...(searchCount === 1 ? { delay: 800 } : {}),
      }
    })
    openLibraryImport(items)

    cy.contains('.footer-center button', 'Start Matching').click()
    cy.get('.footer-center').contains('button', 'Cancel').click()
    cy.wait('@libraryImportSearch')
    cy.get('.import-row .match-status.unsearched').should('have.length', 2)
    cy.wrap(null).should(() => expect(searchCount).to.equal(1))

    cy.contains('.footer-center button', 'Start Matching').click()
    cy.wait('@libraryImportSearch')
    cy.wait('@libraryImportSearch')
    cy.wrap(null).should(() => expect(searchCount).to.equal(3))
    cy.get('.import-row .match-title').should('have.length', 3).and('contain', 'Matched Book')
  })

  it('uses scanned tags and the configured region for Find Match, then applies the picked result', () => {
    const item = libraryImportUnmatchedFixture({
      fullPath: '/incoming/Scanned Author/Scanned Folder/Scanned Title.m4b',
      relativePath: 'Scanned Author/Scanned Folder',
      bookFolder: '/incoming/Scanned Author/Scanned Folder',
      title: 'Scanned Title',
      author: 'Scanned Author',
      asin: 'B000000101',
    })
    const match = libraryImportSearchResultFixture({
      asin: 'B000000101',
      id: 'B000000101',
      title: 'Selected Audible Title',
      authors: [{ name: 'Selected Author' }],
    })
    stubMetadata(
      {
        statusCode: 200,
        body: {
          metadata: {
            asin: match.asin,
            title: match.title,
            authors: match.authors,
          },
        },
      },
      'findMatchAsinMetadata',
    )
    openLibraryImport([item])

    cy.get('.import-row').find('button[title="Search for a match"]').click()
    cy.get('[data-testid="match-title"]').should('have.value', 'Scanned Title')
    cy.get('[data-testid="match-author"]').should('have.value', 'Scanned Author')
    cy.get('[data-testid="match-asin"]').should('have.value', 'B000000101')
    cy.get('[data-testid="match-region"]').should('have.value', 'us')
    cy.wait('@findMatchAsinMetadata').its('request.url').should('include', '/metadata/B000000101?region=us')
    cy.contains('[data-testid="find-match-result"]', 'Selected Audible Title').click()
    cy.get('[data-testid="find-match-confirm"]').should('be.enabled').click()

    cy.contains('.import-row', 'Selected Audible Title').should('be.visible')
    cy.get('.import-row input[type="checkbox"]').should('be.checked')
  })

  it('keeps scan tags ready when the scanned ASIN has no record in the configured region', () => {
    const item = libraryImportUnmatchedFixture({
      fullPath: '/incoming/Scanned Author/Scanned Folder/Scanned Title.m4b',
      relativePath: 'Scanned Author/Scanned Folder',
      bookFolder: '/incoming/Scanned Author/Scanned Folder',
      title: 'Scanned Title',
      author: 'Scanned Author',
      asin: 'B000000101',
    })
    stubSettings(settingsFixture({ defaultSearchRegion: 'fr' }))
    stubMetadata({ statusCode: 404, body: { message: 'No record for ASIN' } }, 'missingFrenchAsin')
    openLibraryImport([item])

    cy.get('.import-row').find('button[title="Search for a match"]').click()
    cy.get('[data-testid="match-title"]').should('have.value', 'Scanned Title')
    cy.get('[data-testid="match-author"]').should('have.value', 'Scanned Author')
    cy.get('[data-testid="match-asin"]').should('have.value', 'B000000101')
    cy.get('[data-testid="match-region"]').should('have.value', 'fr')
    cy.wait('@missingFrenchAsin').its('request.url').should('include', '?region=fr')
    cy.contains('No record for B000000101 in Audible France').should('be.visible')
  })
})