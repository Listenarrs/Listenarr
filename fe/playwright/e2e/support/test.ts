import { test as base, expect, type APIRequestContext, type Page } from '@playwright/test'
import { withPageCoverage } from '../../support/coverage.js'
import { antiforgeryHeaders, restoreSeed } from '../../support/docker-stack.js'
import { e2eCoverage } from './coverage.js'

export type RootFolder = { id: number; name: string; path: string; isDefault?: boolean }

/** Requests a test sends to the app itself (through the dev server's proxy), around what it does in the browser. */
export class ListenarrApi {
  constructor(private readonly request: APIRequestContext) {}

  async get<T>(path: string): Promise<T> {
    const response = await this.request.get(path)
    expect(response.ok(), `GET ${path}: ${response.status()} ${await response.text()}`).toBe(true)
    return (await response.json()) as T
  }

  async send(method: 'POST' | 'PUT' | 'DELETE', path: string, data?: unknown) {
    const headers = await antiforgeryHeaders(this.request)
    const response = await this.request.fetch(path, { method, data, headers })
    expect(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`).toBe(
      true,
    )
    return response
  }

  rootFolders() {
    return this.get<RootFolder[]>('/api/v1/rootfolders')
  }

  async rootFolder(name: string) {
    const folder = (await this.rootFolders()).find((candidate) => candidate.name === name)
    if (!folder) throw new Error(`The seeded root folder '${name}' is missing.`)
    return folder
  }

  library() {
    return this.get<LibraryBook[]>('/api/v1/library')
  }

  book(id: number) {
    return this.get<LibraryBook>(`/api/v1/library/${id}`)
  }
}

export type LibraryBook = {
  id: number
  title?: string
  asin?: string
  authors?: string[]
  monitored?: boolean
  basePath?: string
  filePath?: string
  fileCount?: number
  files?: { path?: string }[]
}

export const test = base.extend<{ seededStack: void; jsCoverage: void; listenarr: ListenarrApi }>({
  // Every test starts from the seeded database and files.
  seededStack: [
    async ({}, use) => {
      await restoreSeed()
      await use()
    },
    { auto: true, timeout: 180_000 },
  ],
  // V8 coverage of every page the test loads (LISTENARR_COVERAGE=1).
  jsCoverage: [
    async ({ page }, use) => withPageCoverage(page, e2eCoverage, use),
    { auto: true },
  ],
  listenarr: async ({ request }, use) => {
    await use(new ListenarrApi(request))
  },
})

export { expect }

/**
 * The Library Import page, open on its default root folder (E2E Import) or the one the query asks for,
 * once it has loaded that folder's saved results (or started the scan the query asks for).
 */
export async function openLibraryImport(page: Page, query = '') {
  const loaded = page.waitForResponse((response) =>
    /\/api\/v1\/rootfolders\/\d+\/(unmatched|scan-unmatched)$/.test(
      new URL(response.url()).pathname,
    ),
  )
  await page.goto(`/library-import${query}`)
  await loaded
  await expect(page.getByRole('heading', { name: 'Library Import' })).toBeVisible()
}

/** The row of the book in that folder (the last part of its folder in tests/Fixtures/Audiobooks/import-folders.json: `folder.Princess`). */
export const importRow = (page: Page, folder: string) =>
  page
    .locator('.import-row')
    .filter({ has: page.locator('.file-path', { hasText: `/${folder}/` }) })

/** The folder of each row, in the order the table shows them. */
export async function rowFolders(page: Page) {
  const paths = await page.locator('.import-row .file-path').allTextContents()
  return paths.map((path) => path.trim().split('/').slice(-2, -1)[0])
}

export const scanButton = (page: Page) =>
  page.locator('.scan-controls button', { hasText: /^\s*(Scan|Scanning\.\.\.)\s*$/ })
export const startMatchingButton = (page: Page) =>
  page.locator('.footer-center button', { hasText: 'Start Matching' })
export const importButton = (page: Page) => page.locator('.footer-right button')

/** Scans the selected root folder and waits for its results. */
export async function scan(page: Page) {
  await scanButton(page).click()
  await expect(scanButton(page)).toHaveText(/^\s*Scan\s*$/, { timeout: 60_000 })
}
