import {
  audiobookFixture,
  libraryAudiobookFixture,
  libraryImportSearchResultFixture,
  libraryImportUnmatchedFixture,
  repairSearchResultFixture,
  rootFolderFixture,
  settingsFixture,
  stubAudiobook,
  stubAudiobookIdentifiers,
  stubAddLibrary,
  stubIntegrationDefaults,
  stubLibrary,
  stubLibraryImportSavedResults,
  stubLibraryImportSearch,
  stubLibraryPathPreview,
  stubMetadata,
  stubQualityProfiles,
  stubRepairPage,
  stubRepairPageWithResults,
  stubRepairWrites,
  stubRootFolders,
  stubSearch,
  stubSearchWithReply,
  stubSettings,
  stubWeakStorageMissingFiles,
} from '../support/api-stubs'

const repairRow = (id: number) => cy.get(`[data-testid="repair-row-${id}"]`)
const repairResult = (id: number) => cy.get(`[data-testid="repair-result-${id}"]`)

function searchForRepairMatch(
  id: number,
  match: ReturnType<typeof repairSearchResultFixture>,
  alias = 'coverageSearch',
  pick = true,
  query = match.title,
) {
  const row = () => repairRow(id)
  row().find('[data-testid="match-asin"]').clear()
  row().find('[data-testid="match-title"]').clear().type(query)
  row().find('[data-testid="match-author"]').clear().type(match.authors[0]!.name)
  row().find('[data-testid="match-search"]').click()
  cy.wait(`@${alias}`)
  const result = row().contains('[data-testid="find-match-result"]', match.title).should('be.visible')
  if (pick) result.click()
}

const identifierMatrixCases = [
  {
    name: 'replaces the old record and keeps another Manual ASIN in the same region',
    id: 401,
    oldAsin: 'B000000401',
    identifiers: [
      { type: 'Asin', value: 'B000000401', region: 'us', isPrimary: true, source: 'Manual' },
      { type: 'Asin', value: 'B000000402', region: 'us', isPrimary: false, source: 'Manual' },
    ],
    match: { asin: 'B000000403', title: 'French Record', region: 'fr' },
    expected: [
      { type: 'Asin', value: 'B000000403', region: 'fr', isPrimary: true, source: 'Manual' },
      { type: 'Asin', value: 'B000000402', region: 'us', isPrimary: false, source: 'Manual' },
    ],
  },
  {
    name: 'promotes an existing secondary ASIN without duplicating it',
    id: 402,
    oldAsin: 'B000000404',
    identifiers: [
      { type: 'Asin', value: 'B000000404', region: 'us', isPrimary: true, source: 'Manual' },
      { type: 'Asin', value: 'B000000403', region: 'fr', isPrimary: false, source: 'Manual' },
    ],
    match: { asin: 'B000000403', title: 'French Record', region: 'fr' },
    expected: [
      { type: 'Asin', value: 'B000000403', region: 'fr', isPrimary: true, source: 'Manual' },
    ],
  },
  {
    name: 'changes only the region when the ASIN is already stored in another region',
    id: 403,
    oldAsin: 'B000000403',
    identifiers: [
      { type: 'Asin', value: 'B000000403', region: 'us', isPrimary: true, source: 'Manual' },
    ],
    match: { asin: 'B000000403', title: 'French Record', region: 'fr' },
    expected: [
      { type: 'Asin', value: 'B000000403', region: 'fr', isPrimary: true, source: 'Manual' },
    ],
  },
  {
    name: 'keeps Manual ISBN and OpenLibrary identifiers while changing record',
    id: 404,
    oldAsin: 'B000000405',
    identifiers: [
      { type: 'Asin', value: 'B000000405', region: 'us', isPrimary: true, source: 'Imported' },
      { type: 'Isbn', value: '9781039400740', region: null, isPrimary: true, source: 'Manual' },
      { type: 'OpenLibraryId', value: 'OL7353617M', region: null, isPrimary: true, source: 'Manual' },
    ],
    match: { asin: 'B000000403', title: 'French Record', region: 'fr' },
    expected: [
      { type: 'Asin', value: 'B000000403', region: 'fr', isPrimary: true, source: 'Manual' },
      { type: 'Isbn', value: '9781039400740', region: null, isPrimary: true, source: 'Manual' },
      { type: 'OpenLibraryId', value: 'OL7353617M', region: null, isPrimary: true, source: 'Manual' },
    ],
  },
]

describe('ported coverage scenarios (API-stubbed)', () => {
  beforeEach(() => {
    cy.clearLocalStorage()
  })

  identifierMatrixCases.forEach(({ name, id, oldAsin, identifiers, match, expected }) => {
    it(`C01: ${name}`, () => {
      const audiobook = audiobookFixture({ id, title: 'Identifier Matrix Book', asin: oldAsin })
      const selectedMatch = repairSearchResultFixture({ ...match, authors: [{ name: 'Matrix Author' }] })
      stubRepairPage(id, audiobook, identifiers)
      stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
      stubMetadata({ statusCode: 200, body: { metadata: { asin: oldAsin, title: audiobook.title } } })
      stubSearch({ statusCode: 200, body: { results: [selectedMatch] } }, 'coverageSearch')
      stubRepairWrites(id)

      cy.visit(`/audiobooks/${id}`)
      cy.get('button[aria-label="Repair Book"]').click()
      searchForRepairMatch(id, selectedMatch)
      cy.get('[data-testid="repair-confirm"]').click()
      cy.get('.confirm-dialog').contains('button', 'Repair 1').click()

      cy.wait('@saveAudiobookIdentifiers').its('request.body.identifiers').should('deep.equal', expected)
      repairResult(id).should('have.attr', 'data-result-state', 'done')
    })
  })

  it('C02: keeps the newly repaired record when going Back and repairing the same book again', () => {
    const id = 405
    const oldAsin = 'B000000406'
    const firstMatch = repairSearchResultFixture({
      asin: 'B000000407',
      title: 'First Replacement',
      authors: [{ name: 'Repeat Author' }],
      region: 'fr',
    })
    const secondMatch = repairSearchResultFixture({
      asin: 'B000000408',
      title: 'Second Replacement',
      authors: [{ name: 'Repeat Author' }],
      region: 'us',
    })
    const audiobook = audiobookFixture({ id, title: 'Repeat Repair Book', authors: ['Repeat Author'], asin: oldAsin })
    const rescanBooks = [firstMatch, secondMatch].map((match) =>
      audiobookFixture({ id, title: match.title, authors: ['Repeat Author'], asin: match.asin }),
    )
    stubRepairPageWithResults(
      id,
      audiobook,
      [{ type: 'Asin', value: oldAsin, region: 'us', isPrimary: true, source: 'Imported' }],
      rescanBooks,
    )
    stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    stubMetadata({ statusCode: 200, body: { metadata: { asin: oldAsin, title: audiobook.title } } })
    let searchIndex = 0
    const matches = [firstMatch, secondMatch]
    stubSearchWithReply(
      () => ({ statusCode: 200, body: { results: [matches[searchIndex++]!] } }),
      'repeatRepairSearch',
    )

    cy.visit(`/audiobooks/${id}`)
    cy.get('button[aria-label="Repair Book"]').click()
    searchForRepairMatch(id, firstMatch, 'repeatRepairSearch')
    cy.get('[data-testid="repair-confirm"]').click()
    cy.get('.confirm-dialog').contains('button', 'Repair 1').click()
    repairResult(id).should('have.attr', 'data-result-state', 'done')

    cy.get('[data-testid="repair-cancel"]').click()
    repairRow(id).should('be.visible').and('contain.text', firstMatch.asin)
    searchForRepairMatch(id, secondMatch, 'repeatRepairSearch')
    cy.get('[data-testid="repair-confirm"]').click()
    cy.get('.confirm-dialog').contains('button', 'Repair 1').click()
    repairResult(id).should('have.attr', 'data-result-state', 'done')
    cy.get('@saveAudiobookIdentifiers.all').should('have.length', 2)
    cy.get('@saveAudiobookIdentifiers.all').then((requests) => {
      expect(requests[0]!.request.body.identifiers).to.deep.equal([
        { type: 'Asin', value: firstMatch.asin, region: 'fr', isPrimary: true, source: 'Manual' },
      ])
      expect(requests[1]!.request.body.identifiers).to.deep.equal([
        { type: 'Asin', value: secondMatch.asin, region: 'us', isPrimary: true, source: 'Manual' },
      ])
    })
  })

  const writeFailureCases = [
    { name: 'identifier save', id: 406, stage: 'identifiers' as const, initialState: 'failed' },
    { name: 'metadata clear', id: 407, stage: 'metadata' as const, initialState: 'warning' },
    { name: 'rescan', id: 408, stage: 'rescan' as const, initialState: 'warning' },
  ]

  writeFailureCases.forEach(({ name, id, stage, initialState }) => {
    it(`C03: retries after a ${name} write failure`, () => {
      const oldAsin = `B000000${id}`
      const match = repairSearchResultFixture({
        asin: `B000001${id}`,
        title: `Retry after ${name}`,
        authors: [{ name: 'Retry Author' }],
      })
      const failOnce = [{ statusCode: 500, body: { message: `${name} failed` } }, {}]
      stubRepairPage(id, audiobookFixture({ id, title: 'Write Failure Book', authors: ['Retry Author'], asin: oldAsin }), [
        { type: 'Asin', value: oldAsin, region: 'us', isPrimary: true, source: 'Imported' },
      ])
      stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
      stubMetadata({ statusCode: 200, body: { metadata: { asin: oldAsin, title: 'Write Failure Book' } } })
      stubSearch({ statusCode: 200, body: { results: [match] } }, 'coverageSearch')
      stubRepairWrites(id, { [stage]: failOnce })

      cy.visit(`/audiobooks/${id}`)
      cy.get('button[aria-label="Repair Book"]').click()
      searchForRepairMatch(id, match)
      cy.get('[data-testid="repair-confirm"]').click()
      cy.get('.confirm-dialog').contains('button', 'Repair 1').click()
      repairResult(id).should('have.attr', 'data-result-state', initialState)
      repairResult(id).find('[data-testid="repair-result-retry"]').click()
      repairResult(id).should('have.attr', 'data-result-state', 'done')
      cy.get(`@${stage === 'identifiers' ? 'saveAudiobookIdentifiers' : stage === 'metadata' ? 'clearAudiobookMetadata' : 'rescanAudiobookMetadata'}.all`)
        .should('have.length', 2)
    })
  })

  it('C15: blocks a record selected on one repair card from another card', () => {
    const firstId = 415
    const secondId = 416
    const firstTitle = 'First Repair Card'
    const secondTitle = 'Second Repair Card'
    const sharedMatch = repairSearchResultFixture({
      asin: 'B000000417',
      title: 'Shared Candidate',
      authors: [{ name: 'Shared Author' }],
    })
    const books = [
      { id: firstId, title: firstTitle, asin: 'B000000415' },
      { id: secondId, title: secondTitle, asin: 'B000000416' },
    ]
    stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    stubLibrary(books.map((book) => libraryAudiobookFixture({ ...book, authors: ['Shared Author'] })))
    for (const book of books) {
      stubAudiobook(book.id, audiobookFixture({ ...book, authors: ['Shared Author'] }))
      stubAudiobookIdentifiers(book.id, [{
        type: 'Asin',
        value: book.asin,
        region: 'us',
        isPrimary: true,
        source: 'Imported',
      }])
      stubWeakStorageMissingFiles(book.id)
      stubRepairWrites(book.id)
    }
    stubSearch({ statusCode: 200, body: { results: [sharedMatch] } }, 'crossCardSearch')

    cy.visit('/')
    cy.contains('button', 'Select All').click()
    cy.contains('button', 'Repair Selected (2)').click()
    for (const id of [firstId, secondId]) repairRow(id).should('exist')
    searchForRepairMatch(firstId, sharedMatch, 'crossCardSearch')
    searchForRepairMatch(secondId, sharedMatch, 'crossCardSearch', false, `${sharedMatch.title} second card`)
    repairRow(secondId)
      .find('[data-testid="find-match-in-use"]')
      .should('be.visible')
      .and('have.attr', 'title')
      .and('include', firstTitle)
    repairRow(secondId).find('[data-testid="match-picked"]').should('not.exist')
    cy.get('[data-testid="repair-confirm"]').should('contain.text', 'Repair 1')
  })

  it('C18: continues after a selected book becomes unavailable and lets its failed retry finish', () => {
    const unavailableId = 418
    const availableId = 419
    const unavailableAsin = 'B000000418'
    const availableAsin = 'B000000419'
    const unavailableMatch = repairSearchResultFixture({
      asin: 'B000000420',
      title: 'Unavailable Book Match',
      authors: [{ name: 'Batch Author' }],
    })
    const availableMatch = repairSearchResultFixture({
      asin: 'B000000421',
      title: 'Available Book Match',
      authors: [{ name: 'Batch Author' }],
    })
    const books = [
      { id: unavailableId, title: 'Unavailable Book', asin: unavailableAsin },
      { id: availableId, title: 'Available Book', asin: availableAsin },
    ]
    stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    stubLibrary(books.map((book) => libraryAudiobookFixture({ ...book, authors: ['Batch Author'] })))
    for (const book of books) {
      stubAudiobook(book.id, audiobookFixture({ ...book, authors: ['Batch Author'] }))
      stubAudiobookIdentifiers(book.id, [{
        type: 'Asin',
        value: book.asin,
        region: 'us',
        isPrimary: true,
        source: 'Imported',
      }])
      stubWeakStorageMissingFiles(book.id)
    }
    stubRepairWrites(unavailableId, {
      identifiers: [{ statusCode: 404, body: { message: 'Audiobook not found' } }],
    })
    stubRepairWrites(availableId)
    let searchIndex = 0
    const matches = [unavailableMatch, availableMatch]
    stubSearchWithReply(
      () => ({ statusCode: 200, body: { results: [matches[searchIndex++]!] } }),
      'deletedMidRunSearch',
    )

    cy.visit('/')
    cy.contains('button', 'Select All').click()
    cy.contains('button', 'Repair Selected (2)').click()
    searchForRepairMatch(unavailableId, unavailableMatch, 'deletedMidRunSearch')
    searchForRepairMatch(availableId, availableMatch, 'deletedMidRunSearch')
    cy.get('[data-testid="repair-confirm"]').click()
    cy.get('.confirm-dialog').contains('button', 'Repair 2').click()
    repairResult(unavailableId).should('have.attr', 'data-result-state', 'failed')
    repairResult(availableId).should('have.attr', 'data-result-state', 'done')
    repairResult(unavailableId).find('[data-testid="repair-result-retry"]').click()
    repairResult(unavailableId).should('have.attr', 'data-result-state', 'failed')
    cy.get('body').type('{esc}')
    cy.get('.modal-content').should('not.exist')
  })

  it('C13: reports the rescan rate limit and offers Retry without waiting through the cooldown', () => {
    const id = 313
    const oldAsin = 'B000000313'
    const match = repairSearchResultFixture({
      asin: 'B000000314',
      title: 'Rate Limited Match',
      authors: [{ name: 'Repair Author' }],
      region: 'us',
    })
    const audiobook = audiobookFixture({ id, title: 'Repair Book', authors: ['Repair Author'], asin: oldAsin })
    const identifiers = [{
      type: 'Asin',
      value: oldAsin,
      region: 'us',
      isPrimary: true,
      source: 'Imported',
    }]
    stubRepairPage(id, audiobook, identifiers)
    stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    stubMetadata({ statusCode: 200, body: { metadata: { asin: oldAsin, title: audiobook.title } } })
    stubSearch({ statusCode: 200, body: { results: [match] } }, 'rateLimitSearch')
    stubRepairWrites(id, [{ statusCode: 429, body: { message: 'Too many rescans', retryAfterSeconds: 600 } }])

    cy.visit(`/audiobooks/${id}`)
    cy.get('button[aria-label="Repair Book"]').click()
    searchForRepairMatch(id, match, 'rateLimitSearch')
    cy.get('[data-testid="repair-confirm"]').click()
    cy.get('.confirm-dialog').contains('button', 'Repair 1').click()

    cy.wait('@saveAudiobookIdentifiers')
    cy.wait('@clearAudiobookMetadata')
    cy.wait('@rescanAudiobookMetadata')
    repairResult(id).should('have.attr', 'data-result-state', 'warning')
    repairResult(id).find('[data-testid="repair-result-message"]').should('contain.text', 'too many rescans')
    repairResult(id).find('[data-testid="repair-result-retry"]').should('be.visible').and('be.enabled')
    cy.get('@rescanAudiobookMetadata.all').should('have.length', 1)
  })

  it('C14: sends the selected region when repairing to a result from another region', () => {
    const id = 314
    const oldAsin = 'B000000315'
    const match = repairSearchResultFixture({
      asin: 'B000000316',
      title: 'Japanese Edition',
      authors: [{ name: 'Japanese Author' }],
      region: 'jp',
    })
    const audiobook = audiobookFixture({ id, title: 'Region Repair Book', authors: ['Japanese Author'], asin: oldAsin })
    stubRepairPage(id, audiobook, [{
      type: 'Asin',
      value: oldAsin,
      region: 'us',
      isPrimary: true,
      source: 'Imported',
    }])
    stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    stubMetadata({ statusCode: 200, body: { metadata: { asin: oldAsin, title: audiobook.title } } })
    stubSearch({ statusCode: 200, body: { results: [match] } }, 'regionRepairSearch')
    stubRepairWrites(id)

    cy.visit(`/audiobooks/${id}`)
    cy.get('button[aria-label="Repair Book"]').click()
    repairRow(id).find('[data-testid="match-asin"]').clear()
    repairRow(id).find('[data-testid="match-title"]').clear().type(match.title)
    repairRow(id).find('[data-testid="match-author"]').clear().type('Japanese Author')
    repairRow(id).find('[data-testid="match-region"]').select('jp')
    cy.wait('@regionRepairSearch').its('request.body').should('include', { region: 'jp' })
    cy.contains('[data-testid="find-match-result"]', match.title).should('be.visible').click()
    cy.get('[data-testid="repair-confirm"]').click()
    cy.get('.confirm-dialog').contains('button', 'Repair 1').click()

    cy.wait('@saveAudiobookIdentifiers').its('request.body').should('deep.equal', {
      identifiers: [{
        type: 'Asin',
        value: match.asin,
        region: 'jp',
        isPrimary: true,
        source: 'Manual',
      }],
    })
    repairResult(id).should('have.attr', 'data-result-state', 'done').and('contain.text', match.asin)
  })

  it('C16: adds a searched title using metadata returned for the selected region', () => {
    const asin = 'B000000316'
    const metadata = {
      asin,
      title: 'The Selected Edition',
      authors: [{ name: 'Selected Author' }],
      region: 'fr',
      series: [{ name: 'Selected Series', position: 1 }],
      genres: [{ name: 'Adventure' }],
      language: 'french',
    }
    const result = {
      id: asin,
      title: metadata.title,
      artist: 'Selected Author',
      album: metadata.title,
      category: 'Audiobook',
      source: 'Audible',
      publishedDate: '2024-01-01',
      format: 'M4B',
      authors: metadata.authors,
      asin,
      region: 'fr',
      series: 'Selected Series',
      genres: ['Adventure'],
      language: metadata.language,
      metadataSource: 'Audible',
    }
    stubIntegrationDefaults()
    stubSettings(settingsFixture({ defaultSearchRegion: 'fr', defaultSearchLanguage: 'french', outputPath: '/audiobooks' }))
    stubRootFolders([rootFolderFixture({ id: 1, path: '/audiobooks', isDefault: true })])
    stubSearch({ statusCode: 200, body: [result] }, 'addNewSearch')
    stubMetadata({ statusCode: 200, body: { metadata } }, 'addNewMetadata')
    stubQualityProfiles()
    stubLibraryPathPreview(
      '/audiobooks/Selected Author/Selected Series/The Selected Edition',
      'Selected Author/Selected Series/The Selected Edition',
      '/audiobooks',
    )
    stubAddLibrary(audiobookFixture({ id: 316, title: metadata.title, authors: ['Selected Author'], asin }))

    cy.visit('/add-new')
    cy.get('#unified-search-input').type('The Selected Edition')
    cy.get('button[aria-label="Execute search"]').click()
    cy.wait('@addNewSearch')
    cy.contains('.title-result-card', metadata.title).within(() => {
      cy.contains('button', 'Add to Library').click()
    })
    cy.wait('@addNewMetadata').its('request.url').should('include', `${asin}?region=fr`)
    cy.wait('@previewLibraryPath')
      .its('request.body')
      .should('include', { destinationRoot: '/audiobooks' })
    cy.get('.path-validation-error').should('not.exist')
    cy.get('.modal-content').last().contains('button', 'Add to Library').should('be.enabled').click()
    cy.wait('@addAudiobookToLibrary').then(({ request }) => {
      expect(request.body.metadata).to.include({ asin, title: metadata.title, region: 'fr', series: 'Selected Series' })
      expect(request.body.metadata.genres).to.deep.equal(['Adventure'])
    })
    cy.get('.modal-content').should('not.exist')
  })

  it('C17: keeps the repair dialog and its content within a small viewport', () => {
    const id = 317
    const oldAsin = 'B000000317'
    const match = repairSearchResultFixture({ asin: 'B000000318', title: 'Small Screen Match' })
    const audiobook = audiobookFixture({ id, title: 'Small Screen Book', asin: oldAsin })
    stubRepairPage(id, audiobook, [{
      type: 'Asin',
      value: oldAsin,
      region: 'us',
      isPrimary: true,
      source: 'Imported',
    }])
    stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    stubMetadata({ statusCode: 200, body: { metadata: { asin: oldAsin, title: audiobook.title } } })
    stubSearch({ statusCode: 200, body: { results: [match] } }, 'smallScreenSearch')
    stubRepairWrites(id)

    cy.viewport(1024, 700)
    cy.visit(`/audiobooks/${id}`)
    cy.get('button[aria-label="Repair Book"]').click()
    cy.window().then((appWindow) => {
      cy.get('.modal-content').last().should(($modal) => {
        const bounds = $modal[0]!.getBoundingClientRect()
        expect(bounds.left).to.be.at.least(-1)
        expect(bounds.right).to.be.at.most(appWindow.innerWidth + 1)
      })
    })
    repairRow(id).find('[data-testid="match-asin"]').clear()
    repairRow(id).find('[data-testid="match-title"]').clear().type(match.title)
    repairRow(id).find('[data-testid="match-author"]').clear().type('Test Author')
    repairRow(id).find('[data-testid="match-search"]').click()
    cy.wait('@smallScreenSearch')
    cy.contains('[data-testid="find-match-result"]', match.title).should('be.visible')
    cy.get('[data-testid="repair-confirm"]').should('be.visible')
  })

  it('C19: marks a Find Match record as owned when another library book has files', () => {
    const imported = libraryImportUnmatchedFixture({
      fullPath: '/incoming/Author/Unmatched Book/Unmatched Book.m4b',
      relativePath: 'Author/Unmatched Book',
      bookFolder: '/incoming/Author/Unmatched Book',
      title: 'Unmatched Book',
      author: 'Author',
    })
    const result = libraryImportSearchResultFixture({ asin: 'B000000319', title: 'Owned Edition' })
    stubIntegrationDefaults()
    stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    stubRootFolders([rootFolderFixture({ id: 1 })])
    stubLibrary([libraryAudiobookFixture({ id: 319, title: 'Existing Book', asin: result.asin, asinRegion: 'us', fileCount: 2 })])
    stubLibraryImportSavedResults(1, [imported])
    stubLibraryImportSearch(() => ({ statusCode: 200, body: [result] }))

    cy.visit('/library-import')
    cy.wait('@getRootFolders')
    cy.wait('@getSettings')
    cy.wait('@savedLibraryImportResults')
    cy.get('.import-row').find('button[title="Search for a match"]').click()
    const ownedResult = cy.contains('[data-testid="find-match-result"]', result.title)
    ownedResult.should('contain.text', 'In use')
    ownedResult.find('[data-testid="find-match-in-use"]').should('have.attr', 'title').and('include', 'Existing Book')
    cy.get('[data-testid="find-match-confirm"]').should('be.disabled')
  })

  it('C19: does not mark a Find Match record as owned by a book without files', () => {
    const imported = libraryImportUnmatchedFixture({
      fullPath: '/incoming/Author/Wanted Book/Wanted Book.m4b',
      relativePath: 'Author/Wanted Book',
      bookFolder: '/incoming/Author/Wanted Book',
      title: 'Wanted Book',
      author: 'Author',
    })
    const result = libraryImportSearchResultFixture({ asin: 'B000000320', title: 'Wanted Edition' })
    stubIntegrationDefaults()
    stubSettings(settingsFixture({ defaultSearchRegion: 'us' }))
    stubRootFolders([rootFolderFixture({ id: 1 })])
    stubLibrary([libraryAudiobookFixture({ id: 320, title: 'Empty Book', asin: result.asin, asinRegion: 'us', fileCount: 0 })])
    stubLibraryImportSavedResults(1, [imported])
    stubLibraryImportSearch(() => ({ statusCode: 200, body: [result] }))

    cy.visit('/library-import')
    cy.wait('@getRootFolders')
    cy.wait('@getSettings')
    cy.wait('@savedLibraryImportResults')
    cy.get('.import-row').find('button[title="Search for a match"]').click()
    cy.contains('[data-testid="find-match-result"]', result.title).should('not.contain.text', 'Empty Book')
    cy.contains('[data-testid="find-match-result"]', result.title).click()
    cy.get('[data-testid="find-match-confirm"]').should('be.enabled')
  })
})