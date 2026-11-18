import { expect, libraryAudiobookFixture, test, LIBRARY_PATH } from '../support/test.js'

test.describe('Broken books group (API-stubbed)', () => {
  test.beforeEach(async ({ api, page }) => {
    await page.setViewportSize({ width: 1600, height: 900 })
    await api.stubLibrary([
      libraryAudiobookFixture({
        id: 1,
        title: 'The Clockmaker’s Secret',
        authors: ['Mara Ellison'],
        brokenReasons: ['missing_region'],
        genres: ['Mystery'],
      }),
      libraryAudiobookFixture({
        id: 2,
        title: '',
        authors: ['Jonah Reed'],
        brokenReasons: ['missing_title', 'missing_asin'],
        genres: [],
      }),
      libraryAudiobookFixture({
        id: 3,
        title: 'A Book Ready for Audible',
        authors: ['Riley Hart'],
        brokenReasons: [],
        genres: ['Fiction'],
      }),
    ])
  })

  test('shows the group menu and the books with server-provided reasons', async ({ page }) => {
    await page.goto(LIBRARY_PATH)

    const posters = page.locator('.audiobook-poster-container')
    await expect(posters).toHaveCount(3)
    await expect(page.getByText('The Clockmaker’s Secret')).toBeVisible()
    await expect(page.getByText('A Book Ready for Audible')).toBeVisible()

    const groupButton = page.locator('.group-btn')
    await groupButton.click()
    await expect(page.locator('.group-menu')).toBeVisible()
    await page.getByTestId('group-broken').click()
    await expect(groupButton).toContainText('Broken books')

    await expect(posters).toHaveCount(2)
    await expect(page.getByText('The Clockmaker’s Secret')).toBeVisible()
    await expect(page.getByText('A Book Ready for Audible')).toHaveCount(0)

    const reasons = page.getByTestId('broken-reasons')
    await expect(reasons).toHaveCount(2)
    await expect(page.locator('.broken-reason')).toHaveCount(3)
    await expect(reasons.filter({ hasText: 'No region' })).toBeVisible()
    const untitled = reasons.filter({ hasText: 'No title' })
    await expect(untitled).toBeVisible()
    await expect(untitled).toContainText('No ASIN')
  })
})
