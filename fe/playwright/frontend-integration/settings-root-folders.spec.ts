import { expect, rootFolderFixture, test } from '../support/test.js'

test.describe('Root Folders Settings', () => {
  test.beforeEach(async ({ page, api }) => {
    await api.stubRootFolders([
      rootFolderFixture({
        name: 'Root1',
        path: 'C:\\root',
        canMutateFilesystem: true,
        canScanFilesystem: true,
      }),
    ])
    await page.goto('/settings#rootfolders')
    await api.wait('getRootFolders')
  })

  test('requests a metadata-only root path change when moving is disabled', async ({ page, api }) => {
    await api.stubRootFolderPathChange(1)

    await page.locator('.folder-card', { hasText: 'Root1' }).locator('[data-cy="edit-root-folder"]').click()

    await page.locator('input[placeholder="Select or enter a path..."]').fill('D:\\newroot')
    await page.getByText('Save', { exact: true }).click()

    // Leave Move files now unchecked and confirm the path update only.
    await page.locator('label.checkbox-label', { hasText: 'Move files now' }).click()
    await expect(page.locator('input[aria-label="Move files now"]')).not.toBeChecked()
    await page.getByText('Confirm New Folder').click()

    const { request } = await api.wait('putRoot')
    expect(request.method).toBe('POST')
    expect(request.body).toMatchObject({
      targetPath: 'D:\\newroot',
      mode: 'metadataOnly',
      desiredName: 'Root1',
      expectedCurrentPath: 'C:\\root',
    })
  })

  test('requests root relocation when Move files now is selected', async ({ page, api }) => {
    await api.stubRootFolderPathChange(1, 'putRootMove')

    await page.locator('.folder-card', { hasText: 'Root1' }).locator('[data-cy="edit-root-folder"]').click()

    await page.locator('input[placeholder="Select or enter a path..."]').fill('E:\\moved')
    await page.getByText('Save', { exact: true }).click()

    const moveFilesNow = page.locator('label.checkbox-label', { hasText: 'Move files now' })
    await moveFilesNow.click()
    await expect(page.locator('input[aria-label="Move files now"]')).not.toBeChecked()
    await moveFilesNow.click()
    await expect(page.locator('input[aria-label="Move files now"]')).toBeChecked()
    await page.getByText('Confirm & Move Files').click()

    const { request } = await api.wait('putRootMove')
    expect(request.method).toBe('POST')
    expect(request.body).toMatchObject({
      targetPath: 'E:\\moved',
      mode: 'relocate',
      desiredName: 'Root1',
      expectedCurrentPath: 'C:\\root',
    })
  })
})
