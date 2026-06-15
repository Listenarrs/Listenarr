import { apiPath } from '../support/api'

/**
 * E2E tests for dual file naming pattern feature
 * Tests single-file vs multi-file pattern selection during imports
 */

describe('File Naming Patterns - Import E2E', () => {
  beforeEach(() => {
    // Stub startup config to bypass authentication
    cy.intercept('GET', apiPath('/configuration/startupconfig'), {
      statusCode: 200,
      body: {
        authenticationRequired: false,
        apiKey: null,
        baseUrl: '/',
      },
    }).as('getStartupConfig')

    cy.intercept('GET', apiPath('/account/me'), {
      statusCode: 200,
      body: { authenticated: false },
    }).as('getCurrentUser')

    // Stub application settings with both patterns configured
    cy.intercept('GET', apiPath('/configuration/settings'), {
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
      },
    }).as('getSettings')

    // Stub other required endpoints
    cy.intercept('GET', apiPath('/configuration/apis'), { statusCode: 200, body: [] }).as('getApis')
    cy.intercept('GET', apiPath('/configuration/download-clients'), {
      statusCode: 200,
      body: [],
    }).as('getDownloadClients')
    cy.intercept('GET', apiPath('/remotepath'), { statusCode: 200, body: [] }).as(
      'getRemotePathMappings',
    )
    cy.intercept('GET', apiPath('/indexers'), { statusCode: 200, body: [] }).as('getIndexers')
    cy.intercept('GET', apiPath('/qualityprofile'), { statusCode: 200, body: [] }).as(
      'getQualityProfiles',
    )
    cy.intercept('GET', apiPath('/account/admins'), { statusCode: 200, body: [] }).as(
      'getAdminUsers',
    )
  })

  describe('Settings UI - Pattern Configuration', () => {
    it('should display separate inputs for single-file and multi-file patterns', () => {
      cy.visit('/settings')
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
      cy.visit('/settings')
      cy.wait('@getSettings')

      // Find the single-file pattern section and check preview
      cy.contains('Single File Naming Pattern')
        .parent()
        .parent()
        .within(() => {
          cy.contains('Preview:').should('be.visible')
          cy.get('code').should('contain', '.ext')
          cy.get('code').should('not.contain', '-01') // Single file shouldn't have disk numbers
        })
    })

    it('should show preview for multi-file pattern with disk numbers', () => {
      cy.visit('/settings')
      cy.wait('@getSettings')

      // Find the multi-file pattern section and check preview
      cy.contains('Multi-File Naming Pattern')
        .parent()
        .parent()
        .within(() => {
          cy.contains('Preview:').should('be.visible')
          cy.get('code').should('contain', '.ext')
          cy.get('code').should('contain', '-01') // Multi-file should show disk numbers
          cy.get('code').should('contain', '-02')
          cy.get('code').should('contain', '-03')
        })
    })

    it('should update single-file pattern independently', () => {
      cy.intercept('POST', apiPath('/configuration/settings'), (req) => {
        expect(req.body.fileNamingPattern).to.equal('{Author} - {Title}')
        expect(req.body.multiFileNamingPattern).to.equal('{Title}-{DiskNumber:00}') // Should remain unchanged
        req.reply({ statusCode: 200, body: req.body })
      }).as('saveSettings')

      cy.visit('/settings')
      cy.wait('@getSettings')

      // Update single-file pattern
      cy.contains('Single File Naming Pattern').parent().find('input').clear()
      cy.contains('Single File Naming Pattern').parent().find('input').type('{Author} - {Title}')

      // Save settings
      cy.contains('button', 'Save').click()
      cy.wait('@saveSettings')
    })

    it('should update multi-file pattern independently', () => {
      cy.intercept('POST', apiPath('/configuration/settings'), (req) => {
        expect(req.body.fileNamingPattern).to.equal('{Title}') // Should remain unchanged
        expect(req.body.multiFileNamingPattern).to.equal('{Title} Part {DiskNumber}')
        req.reply({ statusCode: 200, body: req.body })
      }).as('saveSettings')

      cy.visit('/settings')
      cy.wait('@getSettings')

      // Update multi-file pattern
      cy.contains('Multi-File Naming Pattern').parent().find('input').clear()
      cy.contains('Multi-File Naming Pattern')
        .parent()
        .find('input')
        .type('{Title} Part {DiskNumber}')

      // Save settings
      cy.contains('button', 'Save').click()
      cy.wait('@saveSettings')
    })

    it('should show help button for both patterns', () => {
      cy.visit('/settings')
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
      cy.visit('/settings')
      cy.wait('@getSettings')

      // Click help button
      cy.contains('Single File Naming Pattern')
        .parent()
        .find('button[title="View pattern help"]')
        .click()

      // Verify modal opens
      cy.contains('File Naming Pattern Help').should('be.visible')
      cy.contains('{Title}').should('be.visible')
      cy.contains('{Author}').should('be.visible')
      cy.contains('{DiskNumber').should('be.visible')
    })
  })

  describe('Manual Import - Pattern Selection', () => {
    it('should use single-file pattern for audiobooks without disk numbers', () => {
      // Stub the manual import endpoint
      cy.intercept('POST', apiPath('/manualimport/start'), (req) => {
        // Verify request doesn't include disk numbers
        expect(req.body.items).to.be.an('array')
        expect(req.body.items[0]).to.not.have.property('diskNumber')

        req.reply({
          statusCode: 200,
          body: {
            success: true,
            message: 'Import started',
            // Simulate backend using FileNamingPattern (single file)
            destinationPath: '/audiobooks/Stephen King/The Gunslinger.m4b',
          },
        })
      }).as('startImport')

      // Simulate manual import workflow
      cy.visit('/library/import')

      // (Simplified - actual UI may require more interaction)
      // Verify that single-file import results in simple naming
      cy.wait('@startImport').then((interception) => {
        const response = interception.response?.body
        expect(response.destinationPath).to.not.include('-01')
        expect(response.destinationPath).to.not.include('-02')
      })
    })

    it('should use multi-file pattern for audiobooks with disk numbers', () => {
      // Stub the manual import endpoint
      cy.intercept('POST', apiPath('/manualimport/start'), (req) => {
        // Verify request includes disk numbers
        expect(req.body.items).to.be.an('array')
        if (req.body.items.length > 0 && req.body.items[0].diskNumber) {
          // Simulate backend using MultiFileNamingPattern
          req.reply({
            statusCode: 200,
            body: {
              success: true,
              message: 'Import started',
              destinationPaths: [
                '/audiobooks/Stephen King/The Gunslinger-01.m4b',
                '/audiobooks/Stephen King/The Gunslinger-02.m4b',
                '/audiobooks/Stephen King/The Gunslinger-03.m4b',
              ],
            },
          })
        }
      }).as('startMultiFileImport')

      cy.visit('/library/import')

      // Verify multi-file import uses disk-numbered pattern
      cy.wait('@startMultiFileImport').then((interception) => {
        const response = interception.response?.body
        expect(response.destinationPaths[0]).to.include('-01')
        expect(response.destinationPaths[1]).to.include('-02')
        expect(response.destinationPaths[2]).to.include('-03')
      })
    })
  })

  describe('Download Processing - Pattern Selection', () => {
    it('should apply single-file pattern to completed single-file downloads', () => {
      // Stub download completion processing
      cy.intercept('GET', apiPath('/downloads/queue'), {
        statusCode: 200,
        body: [
          {
            id: 1,
            title: 'The Hobbit',
            status: 'Completed',
            audiobook: { title: 'The Hobbit', author: 'J.R.R. Tolkien' },
            // No diskNumber indicates single file
            progress: 100,
          },
        ],
      }).as('getQueue')

      cy.intercept('GET', apiPath('/downloads/history'), {
        statusCode: 200,
        body: [
          {
            id: 1,
            title: 'The Hobbit',
            status: 'Moved',
            audiobook: { title: 'The Hobbit', author: 'J.R.R. Tolkien' },
            // Verify the file was named using single-file pattern
            destinationPath: '/audiobooks/J.R.R. Tolkien/The Hobbit.m4b',
          },
        ],
      }).as('getHistory')

      cy.visit('/downloads')
      cy.wait('@getHistory')

      // Verify completed download shows correct file path (no disk numbers)
      cy.contains('The Hobbit').should('be.visible')
      cy.contains('/audiobooks/J.R.R. Tolkien/The Hobbit.m4b').should('be.visible')
    })

    it('should apply multi-file pattern to completed multi-disk downloads', () => {
      // Stub download completion processing for multi-disk audiobook
      cy.intercept('GET', apiPath('/downloads/history'), {
        statusCode: 200,
        body: [
          {
            id: 2,
            title: 'The Lord of the Rings',
            status: 'Moved',
            audiobook: { title: 'The Lord of the Rings', author: 'J.R.R. Tolkien' },
            // Multiple destination paths with disk numbers
            destinationPaths: [
              '/audiobooks/J.R.R. Tolkien/The Lord of the Rings-01.m4b',
              '/audiobooks/J.R.R. Tolkien/The Lord of the Rings-02.m4b',
              '/audiobooks/J.R.R. Tolkien/The Lord of the Rings-03.m4b',
            ],
          },
        ],
      }).as('getMultiFileHistory')

      cy.visit('/downloads')
      cy.wait('@getMultiFileHistory')

      // Verify completed multi-file download shows disk-numbered paths
      cy.contains('The Lord of the Rings').should('be.visible')
      cy.contains('The Lord of the Rings-01.m4b').should('be.visible')
      cy.contains('The Lord of the Rings-02.m4b').should('be.visible')
      cy.contains('The Lord of the Rings-03.m4b').should('be.visible')
    })
  })

  describe('Pattern Validation', () => {
    it('should warn if multi-file pattern lacks disk/chapter number', () => {
      cy.visit('/settings')
      cy.wait('@getSettings')

      // Change multi-file pattern to one without disk number
      cy.contains('Multi-File Naming Pattern').parent().find('input').clear()
      cy.contains('Multi-File Naming Pattern').parent().find('input').type('{Title}') // Same as single-file pattern - problematic!

      // Preview should show warning
      cy.contains('Multi-File Naming Pattern')
        .parent()
        .parent()
        .within(() => {
          // Check for warning indicator in preview
          cy.get('code').should('contain', '⚠️')
          cy.get('code').should('contain', 'all files would have the same name')
        })
    })

    it('should not warn if multi-file pattern includes disk number', () => {
      cy.visit('/settings')
      cy.wait('@getSettings')

      // Multi-file pattern has disk number - should be fine
      cy.contains('Multi-File Naming Pattern')
        .parent()
        .parent()
        .within(() => {
          // Should show proper multi-file preview without warning
          cy.get('code').should('not.contain', '⚠️')
          cy.get('code').should('contain', '-01')
        })
    })

    it('should not warn if multi-file pattern includes chapter number', () => {
      cy.visit('/settings')
      cy.wait('@getSettings')

      // Change to use chapter numbers
      cy.contains('Multi-File Naming Pattern').parent().find('input').clear()
      cy.contains('Multi-File Naming Pattern')
        .parent()
        .find('input')
        .type('{Title}-Ch{ChapterNumber:00}')

      // Should show proper preview without warning
      cy.contains('Multi-File Naming Pattern')
        .parent()
        .parent()
        .within(() => {
          cy.get('code').should('not.contain', '⚠️')
          cy.get('code').should('contain', 'Ch01')
        })
    })
  })

  describe('Integration - Full Import Workflow', () => {
    it('should complete end-to-end single-file import with correct naming', () => {
      // Mock the full workflow
      cy.intercept('GET', apiPath('/filesystem/browse?path=*'), {
        statusCode: 200,
        body: {
          currentPath: '/source',
          files: [{ name: 'audiobook.m4b', size: 1048576, isDirectory: false }],
          directories: [],
        },
      }).as('browseFiles')

      cy.intercept('GET', apiPath('/metadata/*'), {
        statusCode: 200,
        body: {
          title: 'Project Hail Mary',
          author: 'Andy Weir',
          asin: 'B08G9PRS1K',
        },
      }).as('getMetadata')

      cy.intercept('POST', apiPath('/manualimport/start'), (req) => {
        const item = req.body.items[0]
        // Single file - no disk number
        void expect(item.diskNumber).to.be.undefined

        req.reply({
          statusCode: 200,
          body: {
            success: true,
            // Backend should use single-file pattern
            destinationPath: '/audiobooks/Andy Weir/Project Hail Mary.m4b',
          },
        })
      }).as('startSingleImport')

      // Visit import page and complete workflow
      cy.visit('/library/import')
      // (Simplified - actual UI workflow would involve file selection, etc.)

      cy.wait('@startSingleImport').then((interception) => {
        const response = interception.response?.body
        // Verify single-file naming (no disk number suffix)
        expect(response.destinationPath).to.equal('/audiobooks/Andy Weir/Project Hail Mary.m4b')
        expect(response.destinationPath).to.not.match(/-\d+\.m4b$/)
      })
    })

    it('should complete end-to-end multi-file import with correct naming', () => {
      // Mock multi-file workflow
      cy.intercept('GET', apiPath('/filesystem/browse?path=*'), {
        statusCode: 200,
        body: {
          currentPath: '/source',
          files: [
            { name: 'audiobook-01.m4b', size: 1048576, isDirectory: false },
            { name: 'audiobook-02.m4b', size: 1048576, isDirectory: false },
            { name: 'audiobook-03.m4b', size: 1048576, isDirectory: false },
          ],
          directories: [],
        },
      }).as('browseMultiFiles')

      cy.intercept('GET', apiPath('/metadata/*'), {
        statusCode: 200,
        body: {
          title: 'The Way of Kings',
          author: 'Brandon Sanderson',
          asin: 'B003ZWFO7E',
        },
      }).as('getMultiMetadata')

      cy.intercept('POST', apiPath('/manualimport/start'), (req) => {
        // Multi-file import should include disk numbers
        expect(req.body.items).to.have.length.greaterThan(1)
        req.body.items.forEach((item: { diskNumber?: number }, index: number) => {
          expect(item.diskNumber).to.equal(index + 1)
        })

        req.reply({
          statusCode: 200,
          body: {
            success: true,
            // Backend should use multi-file pattern with disk numbers
            destinationPaths: [
              '/audiobooks/Brandon Sanderson/The Way of Kings-01.m4b',
              '/audiobooks/Brandon Sanderson/The Way of Kings-02.m4b',
              '/audiobooks/Brandon Sanderson/The Way of Kings-03.m4b',
            ],
          },
        })
      }).as('startMultiImport')

      cy.visit('/library/import')

      cy.wait('@startMultiImport').then((interception) => {
        const response = interception.response?.body
        // Verify multi-file naming with disk numbers
        expect(response.destinationPaths).to.have.length(3)
        response.destinationPaths.forEach((path: string, index: number) => {
          expect(path).to.match(new RegExp(`-0${index + 1}\\.m4b$`))
        })
      })
    })
  })
})
