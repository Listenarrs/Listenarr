import type { Page } from '@playwright/test'
import { downloadFixture, expect, settingsFixture, test } from '../support/test.js'

/**
 * Frontend integration tests for dual file naming pattern feature
 * Tests single-file vs multi-file pattern selection during imports
 */

/** The form row (label, input, help button, preview) of a naming pattern. */
const patternRow = (page: Page, label: string) => page.locator('label', { hasText: label }).locator('..')

const recentFolders = (page: Page, folders: string[]) =>
  page.addInitScript(
    (value) => window.sessionStorage.setItem('manualImport.recentFolders', value),
    JSON.stringify(folders),
  )

test.describe('File Naming Patterns integration', () => {
  test.beforeEach(async ({ api }) => {
    await api.stubSettingsPage(
      settingsFixture({
        outputPath: '/audiobooks',
        fileNamingPattern: '{Title}',
        enableMetadataProcessing: true,
        enableCoverArtDownload: true,
      }),
    )

    await api.stubLibrary([])
    await api.stubDownloads()
  })

  test.describe('Settings UI - Pattern Configuration', () => {
    test('should display separate inputs for single-file and multi-file patterns', async ({ page, api }) => {
      await page.goto('/settings#general')
      await api.wait('getSettings')

      // Check that both pattern inputs exist
      await expect(page.locator('label', { hasText: 'Single File Naming Pattern' })).toBeVisible()
      await expect(page.locator('label', { hasText: 'Multi-File Naming Pattern' })).toBeVisible()

      // Verify inputs have correct initial values
      await expect(patternRow(page, 'Single File Naming Pattern').locator('input')).toHaveValue('{Title}')

      await expect(patternRow(page, 'Multi-File Naming Pattern').locator('input')).toHaveValue(
        '{Title}-{DiskNumber:00}',
      )
    })

    test('should show preview for single-file pattern', async ({ page, api }) => {
      await page.goto('/settings#general')
      await api.wait('getSettings')

      // Find the single-file pattern section and check preview
      const preview = page.locator('.pattern-preview').nth(1).locator('code')
      await expect(preview).toContainText('.ext')
      await expect(preview).not.toContainText('-01')
    })

    test('should show preview for multi-file pattern with disk numbers', async ({ page, api }) => {
      await page.goto('/settings#general')
      await api.wait('getSettings')

      // Find the multi-file pattern section and check preview
      const preview = page.locator('.pattern-preview').nth(2).locator('code')
      await expect(preview).toContainText('.ext')
      await expect(preview).toContainText('-01')
      await expect(preview).toContainText('-02')
      await expect(preview).toContainText('-03')
    })

    test('should update single-file pattern independently', async ({ page, api }) => {
      await api.stubSettingsUpdate()

      await page.goto('/settings#general')
      await api.wait('getSettings')

      // Update single-file pattern
      const input = patternRow(page, 'Single File Naming Pattern').locator('input')
      await input.fill('{Author} - {Title}')
      await input.blur()

      // Save settings
      await page.locator('button', { hasText: 'Save' }).first().click()
      const { request } = await api.wait('saveSettings')
      expect(request.body.fileNamingPattern).toBe('{Author} - {Title}')
      expect(request.body.multiFileNamingPattern).toBe('{Title}-{DiskNumber:00}')
    })

    test('should update multi-file pattern independently', async ({ page, api }) => {
      await api.stubSettingsUpdate()

      await page.goto('/settings#general')
      await api.wait('getSettings')

      // Update multi-file pattern
      const input = patternRow(page, 'Multi-File Naming Pattern').locator('input')
      await input.fill('{Title} Part {DiskNumber}')
      await input.blur()

      // Save settings
      await page.locator('button', { hasText: 'Save' }).first().click()
      const { request } = await api.wait('saveSettings')
      expect(request.body.fileNamingPattern).toBe('{Title}')
      expect(request.body.multiFileNamingPattern).toBe('{Title} Part {DiskNumber}')
    })

    test('should show help button for both patterns', async ({ page, api }) => {
      await page.goto('/settings#general')
      await api.wait('getSettings')

      // Check help buttons exist for both patterns
      await expect(
        patternRow(page, 'Single File Naming Pattern').locator('button[title="View pattern help"]'),
      ).toBeVisible()

      await expect(
        patternRow(page, 'Multi-File Naming Pattern').locator('button[title="View pattern help"]'),
      ).toBeVisible()
    })

    test('should open pattern help modal when help button is clicked', async ({ page, api }) => {
      await page.goto('/settings#general')
      await api.wait('getSettings')

      // Click help button
      await patternRow(page, 'Single File Naming Pattern').locator('button[title="View pattern help"]').click()

      // Verify modal opens
      const modal = page.locator('.modal-content')
      await expect(modal.locator('h2', { hasText: 'File Naming Pattern Help' })).toBeVisible()
      await expect(modal.locator('code', { hasText: '{Title}' }).first()).toBeVisible()
      await expect(modal.locator('code', { hasText: '{Author}' }).first()).toBeVisible()
      await expect(modal.locator('code', { hasText: '{DiskNumber' })).not.toHaveCount(0)
    })
  })

  test.describe('Manual Import - Pattern Selection', () => {
    test('should interactively import one selected audio file without adding disk numbers', async ({
      page,
      api,
    }) => {
      await api.stubFileSystemValidate()
      await api.stubManualImportPreview(
        [
          {
            relativePath: 'The Gunslinger.m4b',
            fullPath: '/incoming/single/The Gunslinger.m4b',
            size: '300 MB',
            quality: 'Lossless',
            languages: ['English'],
          },
        ],
        'previewSingleFile',
      )
      await api.stubManualImport({ importedCount: 1, totalCount: 1, results: [] }, 'importSingleFile')

      await recentFolders(page, ['/incoming/single'])
      await page.goto('/wanted')
      await page.locator('button', { hasText: 'Manual Import' }).click()
      await page.locator('button.recent-item', { hasText: '/incoming/single' }).click()
      await api.wait('validateManualImportFolder')
      await expect(page.locator('.top-path input[aria-label="Path"]')).toHaveValue('/incoming/single')
      await page.locator('button', { hasText: 'Interactive Import' }).click()
      const preview = await api.wait('previewSingleFile')
      expect(new URL(preview.request.url).searchParams.get('path')).toBe('/incoming/single')
      await expect(page.locator('.preview-row', { hasText: 'The Gunslinger.m4b' })).toBeVisible()
      await page.locator('.preview-row .input-checkbox').click()
      const importButton = page.locator('.modal-overlay').last().locator('button.btn-primary')
      await expect(importButton).toContainText('Import')
      await importButton.click()

      const { request } = await api.wait('importSingleFile')
      expect(request.body.path).toBe('/incoming/single')
      expect(request.body.mode).toBe('interactive')
      expect(request.body.items).toHaveLength(1)
      expect(request.body.items[0].relativePath).toBe('The Gunslinger.m4b')
      expect(request.body.items[0].fullPath).toBe('/incoming/single/The Gunslinger.m4b')
      expect(request.body.items[0]).not.toHaveProperty('diskNumber')
      await expect(page.getByText('Manual Import - Select Folder')).toHaveCount(0)
    })

    test('should interactively import multiple selected files from the same folder', async ({ page, api }) => {
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
      await api.stubFileSystemValidate()
      await api.stubManualImportPreview(
        sourceFiles.map((file) => ({
          ...file,
          size: '300 MB',
          quality: 'Lossless',
          languages: ['English'],
        })),
        'previewMultiFile',
      )
      await api.stubManualImport({ importedCount: 3, totalCount: 3, results: [] }, 'importMultiFile')

      await recentFolders(page, ['/incoming/multi'])
      await page.goto('/wanted')
      await page.locator('button', { hasText: 'Manual Import' }).click()
      await page.locator('button.recent-item', { hasText: '/incoming/multi' }).click()
      await api.wait('validateManualImportFolder')
      await expect(page.locator('.top-path input[aria-label="Path"]')).toHaveValue('/incoming/multi')
      await page.locator('button', { hasText: 'Interactive Import' }).click()
      const preview = await api.wait('previewMultiFile')
      expect(new URL(preview.request.url).searchParams.get('path')).toBe('/incoming/multi')
      await expect(page.locator('.preview-row')).toHaveCount(3)
      await page.locator('.preview-header .input-checkbox').click()
      const rowCheckboxes = page.locator('.preview-row input[type="checkbox"]')
      await expect(rowCheckboxes).toHaveCount(3)
      for (const checkbox of await rowCheckboxes.all()) {
        await expect(checkbox).toBeChecked()
      }
      const importButton = page.locator('.modal-overlay').last().locator('button.btn-primary')
      await expect(importButton).toContainText('Import')
      await importButton.click()

      const { request } = await api.wait('importMultiFile')
      expect(request.body.path).toBe('/incoming/multi')
      expect(request.body.mode).toBe('interactive')
      expect(request.body.items).toHaveLength(3)
      expect(request.body.items.map((item: { relativePath: string }) => item.relativePath)).toEqual(
        sourceFiles.map((file) => file.relativePath),
      )
      request.body.items.forEach((item: { diskNumber?: number }) => {
        expect(item).not.toHaveProperty('diskNumber')
      })
    })
  })

  test.describe('Completed download integration', () => {
    const completedDownload = (id: string, title: string, finalPath: string) =>
      downloadFixture({
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

    test('renders the completed single-file download and its final-folder action', async ({ page, api }) => {
      await api.stubDownloads([completedDownload('1', 'The Hobbit', '/audiobooks/J.R.R. Tolkien/The Hobbit.m4b')])

      await page.goto('/downloads')
      await api.wait('getDownloads')
      await page.locator('.tab-button', { hasText: 'Completed (1)' }).click()

      const card = page.locator('.download-card')
      await expect(card.getByText('The Hobbit').first()).toBeVisible()
      await expect(card.locator('.download-status', { hasText: 'Completed' })).toBeVisible()
      await card.locator('button', { hasText: 'Open Folder' }).click()
      await expect(page.locator('.toast-message')).toHaveText(
        'Would open folder: /audiobooks/J.R.R. Tolkien/The Hobbit.m4b',
      )
    })

    test('renders the completed multi-file download and its final-folder action', async ({ page, api }) => {
      await api.stubDownloads([
        completedDownload(
          '2',
          'The Lord of the Rings',
          '/audiobooks/J.R.R. Tolkien/The Lord of the Rings-01.m4b',
        ),
      ])

      await page.goto('/downloads')
      await api.wait('getDownloads')
      await page.locator('.tab-button', { hasText: 'Completed (1)' }).click()

      const card = page.locator('.download-card')
      await expect(card.getByText('The Lord of the Rings').first()).toBeVisible()
      await expect(card.locator('.download-status', { hasText: 'Completed' })).toBeVisible()
      await card.locator('button', { hasText: 'Open Folder' }).click()
      await expect(page.locator('.toast-message')).toHaveText(
        'Would open folder: /audiobooks/J.R.R. Tolkien/The Lord of the Rings-01.m4b',
      )
    })
  })

  test.describe('Pattern Validation', () => {
    test('should warn if multi-file pattern lacks disk/chapter number', async ({ page, api }) => {
      await page.goto('/settings#general')
      await api.wait('getSettings')

      // Change multi-file pattern to one without disk number
      await patternRow(page, 'Multi-File Naming Pattern').locator('input').fill('{Title}')

      // Preview should show warning
      await expect(page.locator('.pattern-preview').nth(2).locator('code')).toContainText(
        'all files would have the same name',
      )
    })

    test('should not warn if multi-file pattern includes disk number', async ({ page, api }) => {
      await page.goto('/settings#general')
      await api.wait('getSettings')

      // Multi-file pattern has disk number - should be fine
      const preview = page.locator('.pattern-preview').nth(2).locator('code')
      await expect(preview).not.toContainText('⚠️')
      await expect(preview).toContainText('-01')
    })

    test('should not warn if multi-file pattern includes chapter number', async ({ page, api }) => {
      await page.goto('/settings#general')
      await api.wait('getSettings')

      // Change to use chapter numbers
      await patternRow(page, 'Multi-File Naming Pattern').locator('input').fill('{Title}-Ch{ChapterNumber:00}')

      // Should show proper preview without warning
      const preview = page.locator('.pattern-preview').nth(2).locator('code')
      await expect(preview).not.toContainText('⚠️')
      await expect(preview).toContainText('Ch01')
    })
  })

  test.describe('Integration - Full Import Workflow', () => {
    test('should start automatic import for a single-file source folder without requesting a preview', async ({
      page,
      api,
    }) => {
      await api.stubFileSystemValidate('validateAutomaticSingleFolder')
      await api.stubManualImportPreview(
        [],
        'unexpectedSingleFilePreview',
        '**/api/v1/library/manual-import/preview*',
      )
      await api.stubManualImport({ importedCount: 1, totalCount: 1, results: [] }, 'startAutomaticSingleImport')

      await recentFolders(page, ['/incoming/automatic-single'])
      await page.goto('/wanted')
      await page.locator('button', { hasText: 'Manual Import' }).click()
      await page.locator('button.recent-item', { hasText: '/incoming/automatic-single' }).click()
      await api.wait('validateAutomaticSingleFolder')
      await expect(page.locator('.top-path input[aria-label="Path"]')).toHaveValue('/incoming/automatic-single')
      const automaticImport = page.locator('button', { hasText: 'Automatic Import' })
      await expect(automaticImport).toBeEnabled()
      await automaticImport.click()

      const { request } = await api.wait('startAutomaticSingleImport')
      expect(request.body).toEqual({ path: '/incoming/automatic-single', mode: 'automatic' })
      expect(api.calls('unexpectedSingleFilePreview')).toHaveLength(0)
      await expect(page.getByText('Manual Import - Select Folder')).toHaveCount(0)
    })

    test('should start automatic import for a multi-file source folder as one folder-level request', async ({
      page,
      api,
    }) => {
      await api.stubFileSystemValidate('validateAutomaticMultiFolder')
      await api.stubManualImportPreview(
        [],
        'unexpectedMultiFilePreview',
        '**/api/v1/library/manual-import/preview*',
      )
      await api.stubManualImport({ importedCount: 3, totalCount: 3, results: [] }, 'startAutomaticMultiImport')

      await recentFolders(page, ['/incoming/automatic-multi'])
      await page.goto('/wanted')
      await page.locator('button', { hasText: 'Manual Import' }).click()
      await page.locator('button.recent-item', { hasText: '/incoming/automatic-multi' }).click()
      await api.wait('validateAutomaticMultiFolder')
      await expect(page.locator('.top-path input[aria-label="Path"]')).toHaveValue('/incoming/automatic-multi')
      const automaticImport = page.locator('button', { hasText: 'Automatic Import' })
      await expect(automaticImport).toBeEnabled()
      await automaticImport.click()

      const { request } = await api.wait('startAutomaticMultiImport')
      expect(request.body).toEqual({ path: '/incoming/automatic-multi', mode: 'automatic' })
      expect(api.calls('unexpectedMultiFilePreview')).toHaveLength(0)
      await expect(page.getByText('Manual Import - Select Folder')).toHaveCount(0)
    })
  })
})
