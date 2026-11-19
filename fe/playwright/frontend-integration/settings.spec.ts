import { expect, test } from '../support/test.js'

test.describe('Settings UI integration', () => {
  test.beforeEach(async ({ api }) => {
    await api.stubSettingsPage()
    await api.stubSettingsUpdate()
  })

  test('stubSettings fills in the settings the app expects', async ({ page, api }) => {
    await api.stubSettings({ defaultSearchRegion: 'us' })
    await page.goto('/settings#general')
    await api.wait('getSettings')

    await expect(page.locator('input[placeholder="{Author}/{Series}/{Title}"]'))
      .toHaveValue('{Author}/{Series}/{Title}')
  })

  test('updates the folder naming pattern and completed-file action', async ({ page, api }) => {
    await page.goto('/settings#general')
    await api.wait('getStartupConfig')
    await api.wait('getSettings')

    await page.locator('input[placeholder="{Author}/{Series}/{Title}"]').fill('{Author}/{Title}')
    await page.getByText('Completed File Action').locator('..').locator('select').selectOption('copy')
    await page.getByText('Save Settings').click()

    const { request } = await api.wait('saveSettings')
    expect(request.body.folderNamingPattern).toBe('{Author}/{Title}')
    expect(request.body.completedFileAction).toBe('copy')
    await expect(page.getByText('Settings saved successfully')).toBeVisible()
  })
})
