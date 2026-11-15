import type { Page } from '@playwright/test'
import { expect, libraryAudiobookFixture, rootFolderFixture, settingsFixture, test, type ApiStubs } from '../support/test.js'

async function editDestination(page: Page, api: ApiStubs) {
  await page.goto('/')
  await expect(page.getByText('Audiobooks').first()).toBeVisible({ timeout: 10000 })
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

  await expect(page.getByText('Edit Audiobook: Test Book').first()).toBeVisible({ timeout: 10000 })
  await page.locator('button[aria-label="Edit destination"]').click()
  await expect(page.locator('input.relative-input')).toHaveCount(1)
  await page.locator('input.relative-input').fill('New Author/New Book')
  await page.locator('button[aria-label="Save destination"]').click()

  // Saving asks whether the files move too.
  await page.locator('button', { hasText: 'Save' }).click()
}

test.describe('Hardlink/Copy move integration', () => {
  test.beforeEach(async ({ api }) => {
    await api.stubSettingsPage(
      settingsFixture({
        outputPath: '/mnt/audiobooks',
        fileNamingPattern: '{Author}/{Title}',
        completedFileAction: 'hardlink/copy',
      }),
    )
    await api.stubLibrary([
      libraryAudiobookFixture({
        basePath: '/mnt/audiobooks/Test Author/Test Book',
        monitored: true,
      }),
    ])
    await api.stubRootFolders([rootFolderFixture({ path: '/mnt/audiobooks' })])
    await api.stubAudiobookMove(1)
    await api.stubFileSystemCheckVolume()
  })

  test('displays move confirmation dialog when editing audiobook', async ({ page, api }) => {
    await editDestination(page, api)

    await expect(page.locator('input[aria-label="Move files now"]')).toBeChecked()
    await expect(page.locator('button', { hasText: 'Move Files' }).first()).toBeVisible()
  })

  test('calls move API when confirming move', async ({ page, api }) => {
    await editDestination(page, api)

    await expect(page.locator('input[aria-label="Move files now"]')).toBeChecked()
    await page.locator('button', { hasText: 'Move Files' }).first().click()

    await api.wait('moveAudiobook')
    await expect(page.getByText('Move queued')).not.toHaveCount(0)
  })

  test('respects user choice to change without moving', async ({ page, api }) => {
    await editDestination(page, api)

    await page.locator('label.checkbox-label', { hasText: 'Move files now' }).first().click()
    await expect(page.locator('input[aria-label="Move files now"]')).not.toBeChecked()
    await page.locator('button', { hasText: 'Update Path' }).first().click()

    const { request } = await api.wait('moveAudiobook')
    expect(request.body.moveFiles).toBe(false)
    await expect(page.getByText('Destination updated')).not.toHaveCount(0)
  })

  test('requests volume check when move dialog opens', async ({ page, api }) => {
    await editDestination(page, api)

    await expect(page.locator('input[aria-label="Move files now"]')).toBeChecked()

    await api.wait('checkVolume')
  })
})
