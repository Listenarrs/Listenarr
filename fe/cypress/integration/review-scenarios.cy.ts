import {
  audiobookFixture,
  libraryAudiobookFixture,
  libraryImportUnmatchedFixture,
  repairSearchResultFixture,
  rootFolderFixture,
  settingsFixture,
  stubAddLibrary,
  stubAudiobook,
  stubAudiobookIdentifiers,
  stubAudiobookReplies,
  stubLibrary,
  stubLibraryImportSearch,
  stubLibraryImportSavedResults,
  stubManualImport,
  stubMetadata,
  stubRepairPage,
  stubRepairWrites,
  stubRootFolders,
  stubSearchWithReply,
  stubSearch,
  stubSettings,
  stubWeakStorageMissingFiles,
} from '../support/api-stubs'

const audiobookId = 401
const oldAsin = 'B000000401'
const selectedMatch = repairSearchResultFixture({
  asin: 'B000000402',
  title: 'Repaired Edition',
  authors: [{ name: 'Test Author' }],
})
const currentMatch = repairSearchResultFixture({
  asin: oldAsin,
  title: 'Original Book',
  authors: [{ name: 'Test Author' }],
})
const audiobook = audiobookFixture({
  id: audiobookId,
  title: 'Original Book',
  authors: ['Test Author'],
  asin: oldAsin,
})
const successfulRescanReply = {
  statusCode: 200,
  body: {
    message: 'Metadata refreshed',
    audiobookId,
    asin: selectedMatch.asin,
    region: selectedMatch.region,
  },
}

function prepareRepair(
  rescanReplies: { statusCode?: number; body?: unknown; delay?: number }[] = [successfulRescanReply],
  searchResult = selectedMatch,
  audiobookReplies?: Parameters<typeof stubAudiobookReplies>[1],
) {
  stubRepairPage(audiobookId, audiobook, [
    { type: 'Asin', value: oldAsin, region: 'us', isPrimary: true, source: 'Imported' },
  ])
  if (audiobookReplies) stubAudiobookReplies(audiobookId, audiobookReplies)
  stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
  stubMetadata({
    statusCode: 200,
    body: { metadata: { asin: oldAsin, title: audiobook.title } },
  })
  stubSearch({ statusCode: 200, body: { results: [searchResult] } }, 'repairSearch')
  stubRepairWrites(audiobookId, rescanReplies)

  cy.visit(`/audiobooks/${audiobookId}`)
  cy.get('button[aria-label="Repair Book"]').click()
  cy.get(`[data-testid="repair-row-${audiobookId}"]`).should('be.visible')
}

function chooseDifferentMatch(match = selectedMatch) {
  const row = () => cy.get(`[data-testid="repair-row-${audiobookId}"]`)
  row().find('[data-testid="match-asin"]').clear()
  row().find('[data-testid="match-title"]').clear().type(match.title)
  row().find('[data-testid="match-author"]').clear().type(match.authors[0]!.name)
  row().find('[data-testid="match-search"]').click()
  cy.wait('@repairSearch')
  cy.contains('[data-testid="find-match-result"]', match.title).click()
}

describe('Review scenarios (API-stubbed)', () => {
  beforeEach(() => {
    cy.clearLocalStorage()
  })

  it('P01: retries a repeated repair after the rescan cooldown response', () => {
    const successReply = {
      statusCode: 200,
      body: {
        message: 'Metadata refreshed',
        audiobookId,
        asin: currentMatch.asin,
        region: currentMatch.region,
      },
    }
    prepareRepair(
      [
        successReply,
        { statusCode: 429, body: { message: 'Too many requests', retryAfterSeconds: 1 } },
        successReply,
      ],
      currentMatch,
    )
    chooseDifferentMatch(currentMatch)
    cy.get('[data-testid="repair-confirm"]').click()
    cy.get('.confirm-dialog').contains('button', 'Repair 1').click()
    cy.wait('@rescanAudiobookMetadata')
    cy.get(`[data-testid="repair-result-${audiobookId}"]`).should('have.attr', 'data-result-state', 'done')

    cy.get('[data-testid="repair-cancel"]').click()
    chooseDifferentMatch(currentMatch)
    cy.get('[data-testid="repair-confirm"]').click()
    cy.get('.confirm-dialog').contains('button', 'Repair 1').click()

    cy.wait('@rescanAudiobookMetadata')
    cy.get(`[data-testid="repair-result-${audiobookId}"]`)
      .find('[data-testid="repair-result-message"]')
      .should('contain.text', 'Waiting 1 s')
    cy.wait('@rescanAudiobookMetadata')

    cy.get(`[data-testid="repair-result-${audiobookId}"]`)
      .should('have.attr', 'data-result-state', 'done')
      .and('contain.text', currentMatch.title)
    cy.get('@rescanAudiobookMetadata.all').should('have.length', 3)
  })

  it('P07: Escape dismisses the leave confirmation and keeps completed repair results open', () => {
    prepareRepair([{ ...successfulRescanReply, delay: 1000 }])
    chooseDifferentMatch()
    cy.get('[data-testid="repair-confirm"]').click()
    cy.get('.confirm-dialog').contains('button', 'Repair 1').click()
    cy.get(`[data-testid="repair-result-${audiobookId}"]`)
      .find('[data-testid="repair-result-message"]')
      .should('contain.text', 'Repairing')

    cy.contains('a', 'Settings').click({ force: true })
    cy.get('.confirm-dialog').should('contain.text', 'Leave while books are repaired?')
    cy.wait('@rescanAudiobookMetadata')
    cy.get(`[data-testid="repair-result-${audiobookId}"]`).should('have.attr', 'data-result-state', 'done')

    cy.get('body').type('{esc}')

    cy.get('.confirm-dialog').should('not.exist')
    cy.location('pathname').should('eq', `/audiobooks/${audiobookId}`)
    cy.get(`[data-testid="repair-result-${audiobookId}"]`).should('be.visible')
  })

  it('P10: a failed post-rescan audiobook read does not turn a successful repair into an error', () => {
    prepareRepair()
    stubAudiobookReplies(audiobookId, [
      { statusCode: 503, body: { message: 'Readback timed out' } },
    ])
    chooseDifferentMatch()
    cy.get('[data-testid="repair-confirm"]').click()
    cy.get('.confirm-dialog').contains('button', 'Repair 1').click()

    cy.wait('@rescanAudiobookMetadata')
    cy.get('@getAudiobook.all').should(
      (requests: { response?: { statusCode?: number } }[]) => {
        expect(requests.some(({ response }) => response?.statusCode === 503)).to.equal(true)
      },
    )
    cy.get(`[data-testid="repair-result-${audiobookId}"]`)
      .should('have.attr', 'data-result-state', 'done')
      .and('contain.text', selectedMatch.title)
    cy.get(`[data-testid="repair-result-${audiobookId}"]`)
      .find('[data-testid="repair-result-retry"]')
      .should('not.exist')
  })

  it('P02: a slower closed selection cannot replace the books from the next load', () => {
    const firstId = 402
    const secondId = 403
    const firstBook = audiobookFixture({
      id: firstId,
      title: 'Slow First Book',
      authors: ['First Author'],
      asin: 'B000000403',
    })
    const secondBook = audiobookFixture({
      id: secondId,
      title: 'Next Book',
      authors: ['Second Author'],
      asin: 'B000000404',
    })

    stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    stubLibrary([
      libraryAudiobookFixture({ id: firstId, title: firstBook.title, authors: firstBook.authors, asin: firstBook.asin }),
      libraryAudiobookFixture({ id: secondId, title: secondBook.title, authors: secondBook.authors, asin: secondBook.asin }),
    ])
    stubAudiobookReplies(firstId, [{ statusCode: 200, body: firstBook, delay: 1500 }])
    stubAudiobook(secondId, secondBook)
    for (const id of [firstId, secondId]) {
      stubAudiobookIdentifiers(id, [])
      stubWeakStorageMissingFiles(id)
    }

    cy.visit('/')
    cy.contains('.audiobook-item', firstBook.title!).find('.selection-checkbox input').check({ force: true })
    cy.contains('button', 'Repair Selected (1)').click()
    cy.contains('Loading the selected audiobooks').should('be.visible')
    cy.get('body').type('{esc}')
    cy.contains('button', 'Repair Selected (1)').should('be.visible')

    cy.contains('.audiobook-item', firstBook.title!).find('.selection-checkbox input').uncheck({ force: true })
    cy.contains('.audiobook-item', secondBook.title!).find('.selection-checkbox input').check({ force: true })
    cy.contains('button', 'Repair Selected (1)').click()
    cy.get(`[data-testid="repair-row-${secondId}"]`).should('be.visible')
    cy.wait('@getAudiobook')

    cy.get('[data-testid^="repair-row-"]').should('have.length', 1)
    cy.get(`[data-testid="repair-row-${secondId}"]`).should('be.visible')
    cy.get(`[data-testid="repair-row-${firstId}"]`).should('not.exist')
  })

  it('R02: repeated repairs replace the prior ASIN instead of accumulating leftovers', () => {
    const firstTarget = repairSearchResultFixture({
      asin: 'B000000411',
      title: 'First Target',
      authors: [{ name: 'Test Author' }],
      region: 'fr',
    })
    const secondTarget = repairSearchResultFixture({
      asin: 'B000000412',
      title: 'Second Target',
      authors: [{ name: 'Test Author' }],
      region: 'us',
    })
    const targets = [firstTarget, secondTarget, firstTarget]
    let searchIndex = 0

    prepareRepair(
      targets.map((target) => ({
        statusCode: 200,
        body: { message: 'Metadata refreshed', audiobookId, asin: target.asin, region: target.region },
      })),
      selectedMatch,
    )
    stubSearchWithReply(() => ({
      statusCode: 200,
      body: { results: [targets[searchIndex++]!] },
    }), 'repairSearch')

    targets.forEach((target, index) => {
      chooseDifferentMatch(target)
      cy.get('[data-testid="repair-confirm"]').click()
      cy.get('.confirm-dialog').contains('button', 'Repair 1').click()
      cy.wait('@rescanAudiobookMetadata')
      cy.get(`[data-testid="repair-result-${audiobookId}"]`).should('have.attr', 'data-result-state', 'done')

      cy.get('@saveAudiobookIdentifiers.all').then((requests: { request: { body: { identifiers: { type: string; value: string; region?: string; isPrimary: boolean; source: string }[] } } }[]) => {
        const savedIdentifiers = requests.at(-1)!.request.body.identifiers
        const asins = savedIdentifiers.filter((identifier) => identifier.type === 'Asin')
        expect(asins).to.deep.equal([
          { type: 'Asin', value: target.asin, region: target.region, isPrimary: true, source: 'Manual' },
        ])
        if (index < targets.length - 1) stubAudiobookIdentifiers(audiobookId, savedIdentifiers)
      })

      if (index < targets.length - 1) {
        cy.get('[data-testid="repair-cancel"]').click()
        cy.get(`[data-testid="repair-row-${audiobookId}"]`).should('be.visible')
      }
    })

    cy.get('@saveAudiobookIdentifiers.all').should('have.length', 3)
  })

  it('R05: browser Back closes the pending repair question without starting a repair', () => {
    stubRepairPage(audiobookId, audiobook, [
      { type: 'Asin', value: oldAsin, region: 'us', isPrimary: true, source: 'Imported' },
    ])
    stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    stubMetadata({ statusCode: 200, body: { metadata: { asin: oldAsin, title: audiobook.title } } })
    stubSearch({ statusCode: 200, body: { results: [selectedMatch] } }, 'repairSearch')
    stubRepairWrites(audiobookId)

    cy.visit('/')
    cy.get('nav, aside').contains('a', 'Library Import').first().click()
    cy.location('pathname').should('eq', '/library-import')
    cy.get('nav, aside').contains('a', 'Audiobooks').first().click()
    cy.location('pathname').should('eq', '/audiobooks')
    cy.contains('.audiobook-item', audiobook.title!).find('.selection-checkbox input').check({ force: true })
    cy.contains('button', 'Repair Selected (1)').click()
    cy.get(`[data-testid="repair-row-${audiobookId}"]`).should('be.visible')
    chooseDifferentMatch()
    cy.get('[data-testid="repair-confirm"]').click()
    cy.get('.confirm-dialog').should('contain.text', 'Repair 1 book?')

    cy.go('back')

    cy.location('pathname').should('eq', '/library-import')
    cy.get('.confirm-dialog').should('not.exist')
    cy.get('@saveAudiobookIdentifiers.all').should('have.length', 0)
    cy.get('@rescanAudiobookMetadata.all').should('have.length', 0)
  })

  it('R06: leaving during a cooldown lets the in-flight rescan finish', () => {
    const successReply = {
      statusCode: 200,
      body: {
        message: 'Metadata refreshed',
        audiobookId,
        asin: currentMatch.asin,
        region: currentMatch.region,
      },
    }
    prepareRepair([
      successReply,
      { statusCode: 429, body: { message: 'Too many requests', retryAfterSeconds: 1 } },
      successReply,
    ], currentMatch)
    chooseDifferentMatch(currentMatch)
    cy.get('[data-testid="repair-confirm"]').click()
    cy.get('.confirm-dialog').contains('button', 'Repair 1').click()
    cy.wait('@rescanAudiobookMetadata')
    cy.get(`[data-testid="repair-result-${audiobookId}"]`).should('have.attr', 'data-result-state', 'done')

    cy.get('[data-testid="repair-cancel"]').click()
    chooseDifferentMatch(currentMatch)
    cy.get('[data-testid="repair-confirm"]').click()
    cy.get('.confirm-dialog').contains('button', 'Repair 1').click()
    cy.wait('@rescanAudiobookMetadata')
    cy.get(`[data-testid="repair-result-${audiobookId}"] [data-testid="repair-result-message"]`)
      .should('contain.text', 'Waiting 1 s')

    cy.contains('a', 'Settings').click({ force: true })
    cy.get('.confirm-dialog').should('contain.text', 'Leave while books are repaired?')
    cy.get('.confirm-dialog').contains('button', 'Leave').click()
    cy.location('pathname').should('eq', '/settings')
    cy.wait('@rescanAudiobookMetadata')
    cy.get('@rescanAudiobookMetadata.all').should('have.length', 3)
  })

  it('P15: keeps Organize mounted while the post-transition book readback is delayed', () => {
    prepareRepair(
      [successfulRescanReply],
      selectedMatch,
      [
        { statusCode: 200, body: audiobook },
        { statusCode: 200, body: audiobook, delay: 1500 },
      ],
    )
    chooseDifferentMatch()
    cy.get('[data-testid="repair-confirm"]').click()
    cy.get('.confirm-dialog').contains('button', 'Repair 1').click()
    cy.wait('@rescanAudiobookMetadata')
    cy.get(`[data-testid="repair-result-${audiobookId}"]`).should('have.attr', 'data-result-state', 'done')

    cy.get('[data-testid="repair-organize"]').click()

    cy.get('[data-testid="organize-back"]').should('be.visible')
    cy.get('@getAudiobook.all').should((requests: { response?: { statusCode?: number } }[]) => {
      expect(requests).to.have.length(4)
      expect(requests[3]?.response).to.equal(undefined)
    })
    cy.get('[data-testid="organize-back"]').should('be.visible')
    cy.get('@getAudiobook.all').should((requests: { response?: { statusCode?: number } }[]) => {
      expect(requests[3]?.response?.statusCode).to.equal(200)
    })
  })

  it('R08: selects and imports a search result without an ASIN', () => {
    const rootFolderId = 1
    const importedAudiobookId = 408
    const importItem = libraryImportUnmatchedFixture({
      fullPath: '/incoming/Author/Unpublished Notes 1987/01.m4b',
      sourceFiles: ['/incoming/Author/Unpublished Notes 1987/01.m4b'],
      relativePath: 'Author/Unpublished Notes 1987',
      bookFolder: '/incoming/Author/Unpublished Notes 1987',
      title: 'Unpublished Notes 1987',
      author: 'Nobody Known',
    })
    const openLibraryResult = {
      id: 'openlibrary:OL12345W',
      title: 'Unpublished Notes 1987',
      authors: [{ name: 'Nobody Known' }],
      region: 'us',
    }

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
    stubLibraryImportSavedResults(rootFolderId, [importItem])
    stubLibraryImportSearch(() => ({ statusCode: 200, body: [openLibraryResult] }))
    stubAddLibrary(
      audiobookFixture({
        id: importedAudiobookId,
        title: openLibraryResult.title,
        authors: ['Nobody Known'],
      }),
    )
    stubManualImport({ importedCount: 1, totalCount: 1, results: [{ success: true }] })

    cy.visit('/library-import')
    cy.wait('@getRootFolders')
    cy.wait('@getSettings')
    cy.wait('@savedLibraryImportResults')
    cy.get('.import-row').find('button[title="Search for a match"]').click()
    cy.wait('@libraryImportSearch')

    cy.contains('[data-testid="find-match-result"]', openLibraryResult.title)
      .find('.result-asin')
      .should('not.exist')
    cy.contains('[data-testid="find-match-result"]', openLibraryResult.title).click()
    cy.get('[data-testid="find-match-confirm"]').should('be.enabled').click()
    cy.contains('.import-row', openLibraryResult.title).should('be.visible')
    cy.get('.import-row input[type="checkbox"]').should('be.checked')

    cy.get('.import-footer select').eq(1).select('move')
    cy.contains('.footer-right button', 'Import 1 Book').click()

    cy.wait('@addAudiobookToLibrary').its('request.body.searchResult').should((searchResult) => {
      expect(searchResult).to.include({ id: openLibraryResult.id, title: openLibraryResult.title })
      expect(searchResult).not.to.have.property('asin')
    })
    cy.wait('@importManualFiles')
      .its('request.body.items')
      .should('deep.equal', [{ fullPath: importItem.fullPath, matchedAudiobookId: importedAudiobookId }])
    cy.contains('Import complete').should('be.visible')
  })
})