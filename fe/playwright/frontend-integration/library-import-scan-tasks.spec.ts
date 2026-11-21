import type { Page } from '@playwright/test'
import {
  expect,
  libraryImportUnmatchedFixture,
  rootFolderFixture,
  test,
  type ApiStubs,
} from '../support/test.js'

// Library Import scans as tasks of the folder that started them (#1078, PR 1): the task bar and its
// Cancel, replacing a running scan, results kept to their folder, reload, the cog menu and System > Jobs.
const incoming = { id: 1, name: 'Incoming', path: '/incoming' }
const archive = { id: 2, name: 'Archive', path: '/archive' }
// The page follows a job by polling it every 2.5 s (SignalR only speeds it up)
const jobEnd = { timeout: 10_000 }
const signedIn = { authenticated: true, name: 'admin' }

const incomingBook = libraryImportUnmatchedFixture({
  fullPath: '/incoming/Author/Found Book/Found Book.m4b',
  relativePath: 'Author/Found Book',
  bookFolder: '/incoming/Author/Found Book',
  title: 'Found Book',
  author: 'Author',
})

const taskBar = (page: Page) => page.getByTestId('scan-task')
const folderSelect = (page: Page) => page.locator('select.folder-select')
const confirmDialog = (page: Page) => page.locator('.confirm-dialog')

async function openLibraryImport(page: Page, api: ApiStubs, folderId = incoming.id) {
  await page.goto(`/library-import?rootFolder=${folderId}`)
  await api.wait('getRootFolders')
  await expect(folderSelect(page)).toHaveValue(String(folderId))
}

async function startScan(page: Page, api: ApiStubs, jobId: string) {
  await page.getByTestId('scan-button').click()
  await api.wait(`startLibraryImportScan-${jobId}`)
  // The task is kept for the session once it has its job id: a reload before that has nothing to follow
  await expect
    .poll(() =>
      page.evaluate(
        () =>
          JSON.parse(sessionStorage.getItem('listenarr.libraryImport.scanTask') ?? 'null')?.jobId,
      ),
    )
    .toBe(jobId)
}

test.describe('Library Import scan tasks (API-stubbed)', () => {
  test.beforeEach(async ({ api }) => {
    await api.stubRootFolders([
      rootFolderFixture({
        ...incoming,
        isDefault: true,
        canScanFilesystem: true,
        canMutateFilesystem: true,
      }),
      rootFolderFixture({ ...archive, canScanFilesystem: true, canMutateFilesystem: true }),
    ])
    await api.stubLibrary([])
    await api.stubLibraryImportSavedResults(incoming.id, [], 'savedIncoming')
    await api.stubLibraryImportSavedResults(archive.id, [], 'savedArchive')
  })

  test('Cancel scan cancels the job on the server and the bar says so', async ({ page, api }) => {
    await api.stubLibraryImportScanJob(incoming.id, 'scan-a')
    await openLibraryImport(page, api)

    await startScan(page, api, 'scan-a')
    await expect(taskBar(page)).toContainText('Scanning Incoming')
    await expect(taskBar(page)).toContainText('You can browse other folders while this runs.')

    await page.getByTestId('scan-task-cancel').click()
    await api.wait('cancelLibraryImportScan-scan-a')
    await expect(taskBar(page)).toContainText('Scan of Incoming canceled', jobEnd)
  })

  test("a failed scan says so with the server's reason", async ({ page, api }) => {
    const scan = await api.stubLibraryImportScanJob(incoming.id, 'scan-a')
    await openLibraryImport(page, api)
    await startScan(page, api, 'scan-a')

    scan.fail('Root folder is not readable')
    await expect(taskBar(page)).toContainText('Scan of Incoming failed', jobEnd)
    await expect(taskBar(page)).toContainText('Root folder is not readable')
    await expect(page.getByTestId('scan-task-cancel')).toHaveCount(0)
  })

  test('scanning another folder asks first, keeps the scan or replaces it', async ({
    page,
    api,
  }) => {
    await api.stubLibraryImportScanJob(incoming.id, 'scan-a')
    await api.stubLibraryImportScanJob(archive.id, 'scan-b')
    await openLibraryImport(page, api)
    await startScan(page, api, 'scan-a')
    await expect(taskBar(page)).toContainText('Scanning Incoming')

    // The folder list is not locked while a scan runs
    await expect(folderSelect(page)).toBeEnabled()
    await folderSelect(page).selectOption(String(archive.id))
    await page.getByTestId('scan-button').click()
    await expect(confirmDialog(page)).toContainText(
      'A scan of Incoming is running. Cancel it and scan Archive instead?',
    )

    await confirmDialog(page).getByRole('button', { name: 'Keep the current scan' }).click()
    await expect(taskBar(page)).toContainText('Scanning Incoming')
    await expect(folderSelect(page)).toHaveValue(String(archive.id))
    expect(api.calls('cancelLibraryImportScan-scan-a')).toHaveLength(0)
    expect(api.calls('startLibraryImportScan-scan-b')).toHaveLength(0)

    await page.getByTestId('scan-button').click()
    await confirmDialog(page).getByRole('button', { name: 'Cancel it and scan' }).click()
    await api.wait('cancelLibraryImportScan-scan-a')
    await api.wait('startLibraryImportScan-scan-b')
    await expect(taskBar(page)).toContainText('Scanning Archive')
  })

  test("a scan's results go to its folder while another folder is shown", async ({ page, api }) => {
    const scan = await api.stubLibraryImportScanJob(incoming.id, 'scan-a')
    await openLibraryImport(page, api)
    await startScan(page, api, 'scan-a')

    await folderSelect(page).selectOption(String(archive.id))
    await expect(page.getByTestId('results-header')).toContainText('Results for Archive')

    // The server keeps the results of the folder it scanned
    await api.stubLibraryImportSavedResults(incoming.id, [incomingBook], 'savedIncomingAfterScan')
    scan.finish([incomingBook])
    await expect(taskBar(page)).toContainText('Scan of Incoming finished: 1 unmatched book', jobEnd)
    await expect(page.getByTestId('results-header')).toContainText('Results for Archive')
    await expect(page.locator('.import-row', { hasText: 'Found Book' })).toHaveCount(0)

    await page.getByTestId('scan-task-show').click()
    await expect(page.getByTestId('results-header')).toContainText('Results for Incoming')
    await expect(page.locator('.import-row', { hasText: 'Found Book' })).toBeVisible()
    await expect(page).toHaveURL(/[?&]rootFolder=1\b/)
  })

  test('a running scan comes back after a reload and is followed to its end', async ({
    page,
    api,
  }) => {
    const scan = await api.stubLibraryImportScanJob(incoming.id, 'scan-a')
    await openLibraryImport(page, api)
    await startScan(page, api, 'scan-a')
    await expect(taskBar(page)).toContainText('Scanning Incoming')

    await page.reload()
    await expect(taskBar(page)).toContainText('Scanning Incoming')
    expect(api.calls('startLibraryImportScan-scan-a')).toHaveLength(1)

    await api.stubLibraryImportSavedResults(incoming.id, [incomingBook], 'savedIncomingAfterScan')
    scan.finish([incomingBook])
    await expect(taskBar(page)).toContainText('Scan of Incoming finished: 1 unmatched book', jobEnd)
    await expect(page.locator('.import-row', { hasText: 'Found Book' })).toBeVisible()
  })

  test('the cog menu and System > Jobs list the scan and lead back to its results', async ({
    page,
    api,
  }) => {
    // Signed in: the app's authenticated start restores the session's scans on any page
    await api.stubAccount(signedIn)
    const scan = await api.stubLibraryImportScanJob(incoming.id, 'scan-a')
    await openLibraryImport(page, api)
    await startScan(page, api, 'scan-a')
    scan.finish([incomingBook])
    await expect(taskBar(page)).toContainText('Scan of Incoming finished', jobEnd)

    await page.getByTestId('jobs-menu').locator('button').first().click()
    await expect(page.getByTestId('notification-library-import-scan')).toContainText('Incoming')

    await page.goto('/jobs')
    const row = page.getByTestId('job-row').filter({ hasText: 'Scan root dir: /incoming' })
    await expect(row).toHaveCount(1)
    const action = row.getByTestId('job-action')
    await expect(action).toHaveText('See job results')
    await expect(action).toBeEnabled()

    await action.click()
    await expect(page).toHaveURL(/\/library-import\?rootFolder=1\b/)
    await expect(page.getByTestId('results-header')).toContainText('Results for Incoming')
  })

  test('a newer scan of the same folder makes the older one stale on the Jobs page', async ({
    page,
    api,
  }) => {
    // Signed in: the app's authenticated start restores the session's scans on any page
    await api.stubAccount(signedIn)
    const first = await api.stubLibraryImportScanJob(incoming.id, 'scan-a')
    await openLibraryImport(page, api)
    await startScan(page, api, 'scan-a')
    first.finish([])
    await expect(taskBar(page)).toContainText('Scan of Incoming finished', jobEnd)
    // The latest stub answers first: the second click starts the second job
    await api.stubLibraryImportScanJob(incoming.id, 'scan-a2')
    await startScan(page, api, 'scan-a2')

    await page.goto('/jobs')
    const rows = page.getByTestId('job-row').filter({ hasText: 'Scan root dir: /incoming' })
    await expect(rows).toHaveCount(2)
    await expect(
      rows.getByTestId('job-action').filter({ hasText: 'Follow job progress' }),
    ).toBeEnabled()
    const stale = rows.getByTestId('job-action').filter({ hasText: 'See job results' })
    await expect(stale).toBeDisabled()
    // The tooltip is on the button's wrapper: a disabled button shows none in every browser
    const staleWrap = rows.locator('.job-action-wrap').filter({
      has: page.getByTestId('job-action').filter({ hasText: 'See job results' }),
    })
    await expect(staleWrap).toHaveAttribute(
      'title',
      /replaced by a more recent scan of the same folder/,
    )
  })

  test('Settings > Scan for unmatched files asks before replacing a running scan', async ({
    page,
    api,
  }) => {
    await api.stubLibraryImportScanJob(incoming.id, 'scan-a')
    await openLibraryImport(page, api)
    await startScan(page, api, 'scan-a')
    await expect(taskBar(page)).toContainText('Scanning Incoming')

    await page.goto('/settings#rootfolders')
    await page
      .locator('.folder-card', { hasText: 'Archive' })
      .locator('[data-cy="scan-unmatched"]')
      .click()

    await expect(page).toHaveURL(/\/library-import/)
    await expect(confirmDialog(page)).toContainText(
      'A scan of Incoming is running. Cancel it and scan Archive instead?',
    )
  })
})
