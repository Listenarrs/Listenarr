import type { Page } from '@playwright/test'
import { expect, libraryAudiobookFixture, rootFolderFixture, settingsFixture, test, type ApiStubs, LIBRARY_PATH } from '../support/test.js'

async function editDestination(page: Page, api: ApiStubs) {
  await page.goto(LIBRARY_PATH)
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

  test('requests volume check when move dialog opens', async ({ page, api }) => {
    await editDestination(page, api)

    await expect(page.locator('input[aria-label="Move files now"]')).toBeChecked()

    await api.wait('checkVolume')
  })

  // The move API and the "change without moving" choice are covered by move-flow.spec.ts; what is
  // specific to the hardlink/copy action is the warning shown when the move would cross a volume.
  test('warns that moving across volumes will break the hardlinks', async ({ page, api }) => {
    await api.stubFileSystemCheckVolume({
      sameVolume: false,
      willBreakHardlinks: true,
      sourceVolume: '/mnt',
      destVolume: '/other',
    })
    await editDestination(page, api)

    await expect(page.locator('input[aria-label="Move files now"]')).toBeChecked()
    await api.wait('checkVolume')
    const warning = page.locator('.hardlink-warning')
    await expect(warning).toBeVisible()
    await expect(warning).toContainText('will break hardlinks')
  })
})
