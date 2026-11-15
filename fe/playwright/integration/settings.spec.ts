import { expect, test } from '@playwright/test'

const settings = {
  outputPath: '/audiobooks',
  folderNamingPattern: '{Author}/{Series}/{Title}',
  fileNamingPattern: '{Author}/{Title}',
  multiFileNamingPattern: '{Title}-{DiskNumber:00}',
  completedFileAction: 'move',
  maxConcurrentDownloads: 2,
  pollingIntervalSeconds: 30,
  defaultSearchRegion: 'us',
  defaultSearchLanguage: 'english',
}

test('updates the folder naming pattern and completed-file action', async ({ page }) => {
  let savedSettings: Record<string, unknown> | undefined

  await page.route('**/api/v1/**', async (route) => {
    const request = route.request()
    const { pathname } = new URL(request.url())

    if (request.method() === 'GET') {
      if (pathname.endsWith('/configuration/bootstrap')) {
        await route.fulfill({ json: { authenticationRequired: false, apiVersion: '1' } })
      } else if (pathname.endsWith('/configuration/settings')) {
        await route.fulfill({ json: settings })
      } else if (pathname.endsWith('/account/me')) {
        await route.fulfill({ json: { authenticated: false } })
      } else if (pathname.endsWith('/system/health')) {
        await route.fulfill({ json: { status: 'healthy', version: 'test' } })
      } else if (pathname.endsWith('/system/ready')) {
        await route.fulfill({ json: { isReady: true, filesystemReady: true, filesystemStatus: 'Ready' } })
      } else if (pathname.endsWith('/configuration/apikey')) {
        await route.fulfill({ json: { apiKey: 'playwright-test-key' } })
      } else if (pathname.endsWith('/antiforgery/token')) {
        await route.fulfill({ json: { token: 'playwright-test-token' } })
      } else {
        await route.fulfill({ json: [] })
      }
      return
    }

    if (request.method() === 'POST' && pathname.endsWith('/configuration/settings')) {
      savedSettings = request.postDataJSON() as Record<string, unknown>
      await route.fulfill({ json: savedSettings })
      return
    }

    await route.fulfill({ status: 501, json: { message: 'No Playwright mutation stub was configured.' } })
  })

  await page.goto('/settings#general')
  await expect(page.getByRole('heading', { name: 'General Settings' })).toBeVisible()
  await page.getByPlaceholder('{Author}/{Series}/{Title}').fill('{Author}/{Title}')
  await page.getByText('Completed File Action').locator('..').locator('select').selectOption('copy')
  await page.getByRole('button', { name: 'Save Settings' }).click()

  await expect(page.getByText('Settings saved successfully')).toBeVisible()
  expect(savedSettings).toMatchObject({
    folderNamingPattern: '{Author}/{Title}',
    completedFileAction: 'copy',
  })
})