/**
 * E2E tests for dual file naming pattern feature
 * Tests single-file vs multi-file pattern selection during imports
 */

describe('File Naming Patterns integration', () => {
  beforeEach(() => {
    // Stub startup config to bypass authentication
    cy.intercept('GET', '**/api/v1/configuration/bootstrap', {
      statusCode: 200,
      body: {
        authenticationRequired: false,
        apiKey: null,
        baseUrl: '/',
      }
    }).as('getStartupConfig')

    cy.intercept('GET', '**/api/v1/account/me', {
      statusCode: 200,
      body: { authenticated: false }
    }).as('getCurrentUser')

    // Stub application settings with both patterns configured
    cy.intercept('GET', '**/api/v1/configuration/settings', {
      statusCode: 200,
      body: {
        outputPath: '/audiobooks',
        folderNamingPattern: '{Author}/{Series}/{Title}',
        fileNamingPattern: '{Title}',
        multiFileNamingPattern: '{Title}-{DiskNumber:00}',
        completedFileAction: 'Move',
        maxConcurrentDownloads: 2,
        pollingIntervalSeconds: 30,
        enableMetadataProcessing: true,
        enableCoverArtDownload: true,
      }
    }).as('getSettings')

    // Stub other required endpoints
    cy.intercept('GET', '**/api/v1/configuration/apis', { statusCode: 200, body: [] }).as('getApis')
    cy.intercept('GET', '**/api/v1/download-clients', { statusCode: 200, body: [] }).as('getDownloadClients')
    cy.intercept('GET', '**/api/v1/remotepath', { statusCode: 200, body: [] }).as('getRemotePathMappings')
    cy.intercept('GET', '**/api/v1/indexers', { statusCode: 200, body: [] }).as('getIndexers')
    cy.intercept('GET', '**/api/v1/qualityprofile', { statusCode: 200, body: [] }).as('getQualityProfiles')
    cy.intercept('GET', '**/api/v1/account/admins', { statusCode: 200, body: [] }).as('getAdminUsers')
    cy.intercept('GET', '**/api/v1/library', { statusCode: 200, body: [] }).as('getLibrary')
    cy.intercept('GET', '**/api/v1/downloads', { statusCode: 200, body: [] }).as('getDownloads')
    cy.intercept('GET', '**/api/v1/system/ready', {
      statusCode: 200,
      body: { filesystemReady: true, filesystemStatus: 'Ready' }
    }).as('getSystemReadiness')
  })

  describe('Settings UI - Pattern Configuration', () => {
    it('should display separate inputs for single-file and multi-file patterns', () => {
      cy.visit('/settings#general')
      cy.wait('@getSettings')

      // Check that both pattern inputs exist
      cy.contains('Single File Naming Pattern').should('be.visible')
      cy.contains('Multi-File Naming Pattern').should('be.visible')

      // Verify inputs have correct initial values
      cy.contains('Single File Naming Pattern')
        .parent()
        .find('input')
        .should('have.value', '{Title}')

      cy.contains('Multi-File Naming Pattern')
        .parent()
        .find('input')
        .should('have.value', '{Title}-{DiskNumber:00}')
    })

    it('should show preview for single-file pattern', () => {
      cy.visit('/settings#general')
      cy.wait('@getSettings')

      // Find the single-file pattern section and check preview
      cy.get('.pattern-preview')
        .eq(1)
        .find('code')
        .should('contain', '.ext')
        .and('not.contain', '-01')
    })

    it('should show preview for multi-file pattern with disk numbers', () => {
      cy.visit('/settings#general')
      cy.wait('@getSettings')

      // Find the multi-file pattern section and check preview
      cy.get('.pattern-preview')
        .eq(2)
        .find('code')
        .should('contain', '.ext')
        .and('contain', '-01')
        .and('contain', '-02')
        .and('contain', '-03')
    })

    it('should update single-file pattern independently', () => {
      cy.intercept('POST', '**/api/v1/configuration/settings', (req) => {
        expect(req.body.fileNamingPattern).to.equal('{Author} - {Title}')
        expect(req.body.multiFileNamingPattern).to.equal('{Title}-{DiskNumber:00}') // Should remain unchanged
        req.reply({ statusCode: 200, body: req.body })
      }).as('saveSettings')

      cy.visit('/settings#general')
      cy.wait('@getSettings')

      // Update single-file pattern
      cy.contains('Single File Naming Pattern')
        .parent()
        .find('input')
        .clear()
      cy.contains('Single File Naming Pattern')
        .parent()
        .find('input')
        .type('{Author} - {Title}', { parseSpecialCharSequences: false })
      cy.contains('Single File Naming Pattern').parent().find('input').blur()

      // Save settings
      cy.contains('button', 'Save').click()
      cy.wait('@saveSettings')
    })

    it('should update multi-file pattern independently', () => {
      cy.intercept('POST', '**/api/v1/configuration/settings', (req) => {
        expect(req.body.fileNamingPattern).to.equal('{Title}') // Should remain unchanged
        expect(req.body.multiFileNamingPattern).to.equal('{Title} Part {DiskNumber}')
        req.reply({ statusCode: 200, body: req.body })
      }).as('saveSettings')

      cy.visit('/settings#general')
      cy.wait('@getSettings')

      // Update multi-file pattern
      cy.contains('Multi-File Naming Pattern')
        .parent()
        .find('input')
        .clear()
      cy.contains('Multi-File Naming Pattern')
        .parent()
        .find('input')
        .type('{Title} Part {DiskNumber}', { parseSpecialCharSequences: false })
      cy.contains('Multi-File Naming Pattern').parent().find('input').blur()

      // Save settings
      cy.contains('button', 'Save').click()
      cy.wait('@saveSettings')
    })

    it('should show help button for both patterns', () => {
      cy.visit('/settings#general')
      cy.wait('@getSettings')

      // Check help buttons exist for both patterns
      cy.contains('Single File Naming Pattern')
        .parent()
        .find('button[title="View pattern help"]')
        .should('be.visible')

      cy.contains('Multi-File Naming Pattern')
        .parent()
        .find('button[title="View pattern help"]')
        .should('be.visible')
    })

    it('should open pattern help modal when help button is clicked', () => {
      cy.visit('/settings#general')
      cy.wait('@getSettings')

      // Click help button
      cy.contains('Single File Naming Pattern')
        .parent()
        .find('button[title="View pattern help"]')
        .click()

      // Verify modal opens
      cy.get('.modal-content').contains('h2', 'File Naming Pattern Help').should('be.visible')
      cy.get('.modal-content').contains('code', '{Title}').should('be.visible')
      cy.get('.modal-content').contains('code', '{Author}').should('be.visible')
      cy.get('.modal-content').contains('code', '{DiskNumber').should('exist')
    })
  })

  describe('Manual Import - Pattern Selection', () => {
    it('should interactively import one selected audio file without adding disk numbers', () => {
      cy.intercept('GET', '**/api/v1/filesystem/validate?path=*', {
        statusCode: 200,
        body: { isValid: true, exists: true, isWritable: true, message: 'Valid' },
      }).as('validateManualImportFolder')
      cy.intercept('GET', '**/api/v1/library/manual-import/preview?path=*', {
        statusCode: 200,
        body: {
          items: [
            {
              relativePath: 'The Gunslinger.m4b',
              fullPath: '/incoming/single/The Gunslinger.m4b',
              size: '300 MB',
              quality: 'Lossless',
              languages: ['English'],
            },
          ],
        }
      }).as('previewSingleFile')
      cy.intercept('POST', '**/api/v1/library/manual-import', {
        statusCode: 200,
        body: { importedCount: 1, totalCount: 1, results: [] }
      }).as('importSingleFile')

      cy.visit('/wanted', {
        onBeforeLoad: (window) =>
          window.sessionStorage.setItem('manualImport.recentFolders', JSON.stringify(['/incoming/single'])),
      })
      cy.contains('button', 'Manual Import').click()
      cy.contains('button.recent-item', '/incoming/single').click()
      cy.wait('@validateManualImportFolder')
      cy.get('.top-path input[aria-label="Path"]').should('have.value', '/incoming/single')
      cy.contains('button', 'Interactive Import').click()
      cy.wait('@previewSingleFile').then(({ request }) => {
        expect(new URL(request.url).searchParams.get('path')).to.equal('/incoming/single')
      })
      cy.contains('.preview-row', 'The Gunslinger.m4b').should('be.visible')
      cy.get('.preview-row input[type="checkbox"]').check({ force: true })
      cy.get('.modal-overlay').last().find('button.btn-primary').should('contain.text', 'Import').click()

      cy.wait('@importSingleFile').then(({ request }) => {
        expect(request.body.path).to.equal('/incoming/single')
        expect(request.body.mode).to.equal('interactive')
        expect(request.body.items).to.have.length(1)
        expect(request.body.items[0].relativePath).to.equal('The Gunslinger.m4b')
        expect(request.body.items[0].fullPath).to.equal('/incoming/single/The Gunslinger.m4b')
        expect(request.body.items[0]).not.to.have.property('diskNumber')
      })
      cy.contains('Manual Import - Select Folder').should('not.exist')
    })

    it('should interactively import multiple selected files from the same folder', () => {
      const sourceFiles = [
        {
          relativePath: 'The Way of Kings-01.m4b',
          fullPath: '/incoming/multi/The Way of Kings-01.m4b',
        },
        {
          relativePath: 'The Way of Kings-02.m4b',
          fullPath: '/incoming/multi/The Way of Kings-02.m4b',
        },
        {
          relativePath: 'The Way of Kings-03.m4b',
          fullPath: '/incoming/multi/The Way of Kings-03.m4b',
        },
      ]
      cy.intercept('GET', '**/api/v1/filesystem/validate?path=*', {
        statusCode: 200,
        body: { isValid: true, exists: true, isWritable: true, message: 'Valid' },
      }).as('validateManualImportFolder')
      cy.intercept('GET', '**/api/v1/library/manual-import/preview?path=*', {
        statusCode: 200,
        body: {
          items: sourceFiles.map((file) => ({
            ...file,
            size: '300 MB',
            quality: 'Lossless',
            languages: ['English'],
          })),
        }
      }).as('previewMultiFile')
      cy.intercept('POST', '**/api/v1/library/manual-import', {
        statusCode: 200,
        body: { importedCount: 3, totalCount: 3, results: [] }
      }).as('importMultiFile')

      cy.visit('/wanted', {
        onBeforeLoad: (window) =>
          window.sessionStorage.setItem('manualImport.recentFolders', JSON.stringify(['/incoming/multi'])),
      })
      cy.contains('button', 'Manual Import').click()
      cy.contains('button.recent-item', '/incoming/multi').click()
      cy.wait('@validateManualImportFolder')
      cy.get('.top-path input[aria-label="Path"]').should('have.value', '/incoming/multi')
      cy.contains('button', 'Interactive Import').click()
      cy.wait('@previewMultiFile').then(({ request }) => {
        expect(new URL(request.url).searchParams.get('path')).to.equal('/incoming/multi')
      })
      cy.get('.preview-row').should('have.length', 3)
      cy.get('.preview-header input[type="checkbox"]').check({ force: true })
      cy.get('.preview-row input[type="checkbox"]').each(($checkbox) => {
        cy.wrap($checkbox).should('be.checked')
      })
      cy.get('.modal-overlay').last().find('button.btn-primary').should('contain.text', 'Import').click()

      cy.wait('@importMultiFile').then(({ request }) => {
        expect(request.body.path).to.equal('/incoming/multi')
        expect(request.body.mode).to.equal('interactive')
        expect(request.body.items).to.have.length(3)
        expect(
          request.body.items.map((item: { relativePath: string }) => item.relativePath),
        ).to.deep.equal(sourceFiles.map((file) => file.relativePath))
        request.body.items.forEach((item: { diskNumber?: number }) => {
          expect(item).not.to.have.property('diskNumber')
        })
      })
    })
  })

  describe('Completed download integration', () => {
    const completedDownload = (id: string, title: string, finalPath: string) => ({
      id,
      title,
      artist: 'J.R.R. Tolkien',
      album: '',
      originalUrl: '',
      status: 'Completed',
      progress: 100,
      totalSize: 1024,
      downloadedSize: 1024,
      downloadPath: '',
      finalPath,
      startedAt: '2026-01-01T00:00:00.000Z',
      completedAt: '2026-01-01T00:01:00.000Z',
      metadata: {},
    })

    it('renders the completed single-file download and its final-folder action', () => {
      cy.intercept('GET', '**/api/v1/downloads', {
        statusCode: 200,
        body: [completedDownload('1', 'The Hobbit', '/audiobooks/J.R.R. Tolkien/The Hobbit.m4b')],
      }).as('getDownloads')

      cy.visit('/downloads')
      cy.wait('@getDownloads')
      cy.contains('.tab-button', 'Completed (1)').click()

      cy.get('.download-card').contains('The Hobbit').should('be.visible')
      cy.get('.download-card').contains('.download-status', 'Completed').should('be.visible')
      cy.get('.download-card').contains('button', 'Open Folder').should('be.visible')
    })

    it('renders the completed multi-file download and its final-folder action', () => {
      cy.intercept('GET', '**/api/v1/downloads', {
        statusCode: 200,
        body: [
          completedDownload(
            '2',
            'The Lord of the Rings',
            '/audiobooks/J.R.R. Tolkien/The Lord of the Rings-01.m4b',
          ),
        ],
      }).as('getDownloads')

      cy.visit('/downloads')
      cy.wait('@getDownloads')
      cy.contains('.tab-button', 'Completed (1)').click()

      cy.get('.download-card').contains('The Lord of the Rings').should('be.visible')
      cy.get('.download-card').contains('.download-status', 'Completed').should('be.visible')
      cy.get('.download-card').contains('button', 'Open Folder').should('be.visible')
    })
  })

  describe('Pattern Validation', () => {
    it('should warn if multi-file pattern lacks disk/chapter number', () => {
      cy.visit('/settings#general')
      cy.wait('@getSettings')

      // Change multi-file pattern to one without disk number
      cy.contains('Multi-File Naming Pattern')
        .parent()
        .find('input')
        .clear()
      cy.contains('Multi-File Naming Pattern')
        .parent()
        .find('input')
        .type('{Title}', { parseSpecialCharSequences: false })

      // Preview should show warning
      cy.get('.pattern-preview').eq(2).find('code').should('contain', 'all files would have the same name')
    })

    it('should not warn if multi-file pattern includes disk number', () => {
      cy.visit('/settings#general')
      cy.wait('@getSettings')

      // Multi-file pattern has disk number - should be fine
      cy.get('.pattern-preview').eq(2).find('code').should('not.contain', '⚠️').and('contain', '-01')
    })

    it('should not warn if multi-file pattern includes chapter number', () => {
      cy.visit('/settings#general')
      cy.wait('@getSettings')

      // Change to use chapter numbers
      cy.contains('Multi-File Naming Pattern')
        .parent()
        .find('input')
        .clear()
      cy.contains('Multi-File Naming Pattern')
        .parent()
        .find('input')
        .type('{Title}-Ch{ChapterNumber:00}', { parseSpecialCharSequences: false })

      // Should show proper preview without warning
      cy.get('.pattern-preview').eq(2).find('code').should('not.contain', '⚠️').and('contain', 'Ch01')
    })
  })

  describe('Integration - Full Import Workflow', () => {
    it('should start automatic import for a single-file source folder without requesting a preview', () => {
      cy.intercept('GET', '**/api/v1/filesystem/validate?path=*', {
        statusCode: 200,
        body: { isValid: true, exists: true, isWritable: true, message: 'Valid' },
      }).as('validateAutomaticSingleFolder')
      cy.intercept('GET', '**/api/v1/library/manual-import/preview*', {
        statusCode: 200,
        body: { items: [] }
      }).as('unexpectedSingleFilePreview')
      cy.intercept('POST', '**/api/v1/library/manual-import', {
        statusCode: 200,
        body: { importedCount: 1, totalCount: 1, results: [] }
      }).as('startAutomaticSingleImport')

      cy.visit('/wanted', {
        onBeforeLoad: (window) =>
          window.sessionStorage.setItem(
            'manualImport.recentFolders',
            JSON.stringify(['/incoming/automatic-single']),
          ),
      })
      cy.contains('button', 'Manual Import').click()
      cy.contains('button.recent-item', '/incoming/automatic-single').click()
      cy.wait('@validateAutomaticSingleFolder')
      cy.get('.top-path input[aria-label="Path"]').should('have.value', '/incoming/automatic-single')
      cy.contains('button', 'Automatic Import').should('be.enabled').click()

      cy.wait('@startAutomaticSingleImport').then(({ request }) => {
        expect(request.body).to.deep.equal({ path: '/incoming/automatic-single', mode: 'automatic' })
      })
      cy.get('@unexpectedSingleFilePreview.all').should('have.length', 0)
      cy.contains('Manual Import - Select Folder').should('not.exist')
    })

    it('should start automatic import for a multi-file source folder as one folder-level request', () => {
      cy.intercept('GET', '**/api/v1/filesystem/validate?path=*', {
        statusCode: 200,
        body: { isValid: true, exists: true, isWritable: true, message: 'Valid' },
      }).as('validateAutomaticMultiFolder')
      cy.intercept('GET', '**/api/v1/library/manual-import/preview*', {
        statusCode: 200,
        body: { items: [] }
      }).as('unexpectedMultiFilePreview')
      cy.intercept('POST', '**/api/v1/library/manual-import', {
        statusCode: 200,
        body: { importedCount: 3, totalCount: 3, results: [] }
      }).as('startAutomaticMultiImport')

      cy.visit('/wanted', {
        onBeforeLoad: (window) =>
          window.sessionStorage.setItem(
            'manualImport.recentFolders',
            JSON.stringify(['/incoming/automatic-multi']),
          ),
      })
      cy.contains('button', 'Manual Import').click()
      cy.contains('button.recent-item', '/incoming/automatic-multi').click()
      cy.wait('@validateAutomaticMultiFolder')
      cy.get('.top-path input[aria-label="Path"]').should('have.value', '/incoming/automatic-multi')
      cy.contains('button', 'Automatic Import').should('be.enabled').click()

      cy.wait('@startAutomaticMultiImport').then(({ request }) => {
        expect(request.body).to.deep.equal({ path: '/incoming/automatic-multi', mode: 'automatic' })
      })
      cy.get('@unexpectedMultiFilePreview.all').should('have.length', 0)
      cy.contains('Manual Import - Select Folder').should('not.exist')
    })
  })
})
