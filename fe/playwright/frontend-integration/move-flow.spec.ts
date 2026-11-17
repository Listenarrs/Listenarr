import type { Page } from '@playwright/test'
import { expect, libraryAudiobookFixture, rootFolderFixture, settingsFixture, test, type ApiStubs, LIBRARY_PATH } from '../support/test.js'

async function editDestination(page: Page, api: ApiStubs) {
  await page.goto(LIBRARY_PATH)
  await page.getByText('Audiobooks').first().click()
  await api.wait('getStartupConfig')
  await api.wait('getLibrary')

  const audiobook = page
    .locator('.audiobook-item, .audiobook-list-item')
    .filter({ has: page.getByText('Test Book', { exact: true }) })
    .first()
  await expect(audiobook).toBeVisible()
  await audiobook.hover()
  await audiobook.locator('button[title="Edit"]').click()

  await expect(page.getByText('Edit Audiobook: Test Book')).toBeVisible()
  await page.locator('button[aria-label="Edit destination"]').click()
  await page.locator('input.relative-input').fill('New Author/New Book')
  await page.locator('button[aria-label="Save destination"]').click()

  // Saving asks whether the files move too.
  await page.locator('button', { hasText: 'Save' }).click()
}

test.describe('Edit -> Move integration', () => {
  test.beforeEach(async ({ api }) => {
    await api.stubSettingsPage(settingsFixture({ outputPath: '/mnt/audiobooks' }))
    await api.stubLibrary([
      libraryAudiobookFixture({
        basePath: '/mnt/audiobooks/Test Author/Test Book',
        monitored: true,
      }),
    ])
    await api.stubRootFolders([rootFolderFixture({ path: '/mnt/audiobooks' })])
    await api.stubAudiobookMove(1)
  })

  test('edits destination, confirms move, enqueues job, and shows toast', async ({ page, api }) => {
    await editDestination(page, api)

    await expect(page.locator('input[aria-label="Move files now"]')).toBeChecked()
    await page.locator('button', { hasText: 'Move Files' }).click()

    const { request } = await api.wait('moveAudiobook')
    expect(request.body.destinationPath).toBe('/mnt/audiobooks/New Author/New Book')
    expect(request.body.sourcePath).toBe('/mnt/audiobooks/Test Author/Test Book')
    expect(request.body.moveFiles).toBe(true)

    await expect(page.getByText('Move queued')).toBeVisible()
  })

  test('edits destination and chooses "Change without moving" to update DB only', async ({ page, api }) => {
    await editDestination(page, api)

    await page.locator('label.checkbox-label', { hasText: 'Move files now' }).click()
    await expect(page.locator('input[aria-label="Move files now"]')).not.toBeChecked()
    await page.locator('button', { hasText: 'Update Path' }).click()

    const { request } = await api.wait('moveAudiobook')
    expect(request.body.destinationPath).toBe('/mnt/audiobooks/New Author/New Book')
    expect(request.body.sourcePath).toBe('/mnt/audiobooks/Test Author/Test Book')
    expect(request.body.moveFiles).toBe(false)
    expect(request.body.deleteEmptySource).toBe(false)

    await expect(page.getByText('Destination updated')).toBeVisible()
  })
})
