import { expect, type Page, type Request } from '@playwright/test'
import type {
  GetDownloadsGetDownloadsApiV1DownloadsResponses,
  GetFileSystemCheckVolumeApiV1FilesystemCheckVolumeResponses,
  GetFileSystemValidatePathApiV1FilesystemValidateResponses,
  GetLibraryGetAudiobookApiV1LibraryIdResponses,
  GetLibraryGetAllApiV1LibraryResponses,
  GetSettingsGetApplicationSettingsApiV1ConfigurationSettingsResponses,
  GetStartupConfigurationGetBootstrapConfigApiV1ConfigurationBootstrapResponses,
  GetSystemGetServiceHealthApiV1SystemHealthResponses,
} from './generated-api/index.js'

type DocumentedResponse<T> = unknown extends T ? never : T
type BootstrapResponse = DocumentedResponse<GetStartupConfigurationGetBootstrapConfigApiV1ConfigurationBootstrapResponses[200]>
type DownloadsResponse = DocumentedResponse<GetDownloadsGetDownloadsApiV1DownloadsResponses[200]>
type FileSystemCheckVolumeResponse = DocumentedResponse<GetFileSystemCheckVolumeApiV1FilesystemCheckVolumeResponses[200]>
type FileSystemValidateResponse = DocumentedResponse<GetFileSystemValidatePathApiV1FilesystemValidateResponses[200]>
type AudiobookResponse = DocumentedResponse<GetLibraryGetAudiobookApiV1LibraryIdResponses[200]>
type SettingsResponse = DocumentedResponse<GetSettingsGetApplicationSettingsApiV1ConfigurationSettingsResponses[200]>
type SystemHealthResponse = DocumentedResponse<GetSystemGetServiceHealthApiV1SystemHealthResponses[200]>
type LibraryResponse = DocumentedResponse<GetLibraryGetAllApiV1LibraryResponses[200]>
type LibraryAudiobook = LibraryResponse[number]
type DownloadRecord = DownloadsResponse[number]
export type ApiReply = { statusCode?: number; body?: unknown; delay?: number }
type RepairWriteReplies = {
  identifiers?: ApiReply[]
  metadata?: ApiReply[]
  rescan?: ApiReply[]
}

type LibraryImportUnmatchedFile = {
  fullPath: string
  sourceFiles: string[]
  relativePath: string
  bookFolder: string
  size: number
  fileCount: number
  title?: string
  author?: string
  asin?: string
  format: string
}

type LibraryImportSearchResult = {
  id: string
  asin: string
  title: string
  authors: { name: string }[]
  region: string
  series?: { name: string; sequence?: number | null }[]
}

export type RepairIdentifier = {
  type: string
  value: string
  region?: string | null
  isPrimary: boolean
  source: string
}

type RepairSearchResult = {
  asin: string
  title: string
  authors: { name: string }[]
  region: string
}

export type RootFolder = {
  id: number
  name: string
  path: string
  isDefault: boolean
  storageState: string
  canChangePath: boolean
  canMutateFilesystem?: boolean
  canScanFilesystem?: boolean
}

type SystemReadiness = {
  isReady: boolean
  filesystemReady: boolean
  filesystemStatus: string
}

export const settingsFixture = (overrides: Partial<SettingsResponse> = {}) => ({
  folderNamingPattern: '{Author}/{Series}/{Title}',
  fileNamingPattern: '{Author}/{Title}',
  multiFileNamingPattern: '{Title}-{DiskNumber:00}',
  completedFileAction: 'move',
  maxConcurrentDownloads: 2,
  pollingIntervalSeconds: 30,
  ...overrides,
} satisfies Partial<SettingsResponse>)

export const audiobookFixture = (overrides: Partial<AudiobookResponse> = {}): AudiobookResponse => ({
  id: 1,
  title: 'Test Book',
  authors: ['Test Author'],
  files: [],
  ...overrides,
})

export const libraryAudiobookFixture = (overrides: Partial<LibraryAudiobook> = {}): LibraryAudiobook => ({
  id: 1,
  title: 'Test Book',
  authors: ['Test Author'],
  ...overrides,
})

export const libraryImportUnmatchedFixture = (
  overrides: Partial<LibraryImportUnmatchedFile> = {},
): LibraryImportUnmatchedFile => ({
  fullPath: '/incoming/Test Author/Test Book/Test Book.m4b',
  sourceFiles: ['/incoming/Test Author/Test Book/Test Book.m4b'],
  relativePath: 'Test Author/Test Book',
  bookFolder: '/incoming/Test Author/Test Book',
  size: 1024,
  fileCount: 1,
  title: 'Test Book',
  author: 'Test Author',
  format: 'M4B',
  ...overrides,
})

export const libraryImportSearchResultFixture = (
  overrides: Partial<LibraryImportSearchResult> = {},
): LibraryImportSearchResult => ({
  id: 'B000000001',
  asin: 'B000000001',
  title: 'Test Book',
  authors: [{ name: 'Test Author' }],
  region: 'us',
  ...overrides,
})

export const repairSearchResultFixture = (overrides: Partial<RepairSearchResult> = {}): RepairSearchResult => ({
  asin: 'B008DFUGCQ',
  title: 'Dune',
  authors: [{ name: 'Frank Herbert' }],
  region: 'us',
  ...overrides,
})

export const downloadFixture = (overrides: Partial<DownloadRecord> = {}): DownloadRecord => ({
  id: '1',
  title: 'Test Download',
  status: 'Queued',
  ...overrides,
})

export const rootFolderFixture = (overrides: Partial<RootFolder> = {}): RootFolder => ({
  id: 1,
  name: 'Audiobooks',
  path: '/audiobooks',
  isDefault: true,
  storageState: 'Healthy',
  canChangePath: true,
  ...overrides,
})

const fileSystemCheckVolumeFixture = (overrides: Partial<FileSystemCheckVolumeResponse> = {}) => ({
  sameVolume: true,
  willBreakHardlinks: false,
  sourceVolume: '/mnt',
  destVolume: '/mnt',
  message: 'Same volume',
  ...overrides,
} satisfies FileSystemCheckVolumeResponse)

const fileSystemValidateFixture = (overrides: Partial<FileSystemValidateResponse> = {}) => ({
  isValid: true,
  exists: true,
  isWritable: true,
  message: 'Valid',
  ...overrides,
} satisfies FileSystemValidateResponse)

const systemReadinessFixture = (overrides: Partial<SystemReadiness> = {}): SystemReadiness => ({
  isReady: true,
  filesystemReady: true,
  filesystemStatus: 'Ready',
  ...overrides,
})

type RecordedRequest = {
  method: string
  url: string
  /** The parsed JSON body, or the raw text when the body is not JSON. */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  body: any
  headers: Record<string, string>
}

type RecordedCall = {
  request: RecordedRequest
  /** The stubbed reply; absent for a spy, which lets an earlier stub answer. */
  response?: { statusCode: number; body: unknown }
}

type Method = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE'
type ReplySource = ApiReply | ((request: RecordedRequest) => ApiReply)
type PendingCall = RecordedCall & { settled: boolean }

const anyApiRoute = /\/api\/v1\//
const waitTimeout = 10_000

function recordRequest(request: Request): RecordedRequest {
  let body: unknown = request.postData()
  try {
    body = request.postDataJSON()
  } catch {
    // Keep the raw text.
  }
  return { method: request.method(), url: request.url(), body: body ?? null, headers: request.headers() }
}

/**
 * Every API route the frontend integration specs stub. A spec asks for the responses it needs through the
 * helpers below and reads what the application sent with `wait(alias)` and `calls(alias)`.
 *
 * As in the browser, the stub registered last answers first. A spy records the request and leaves
 * the answer to the stub registered before it.
 */
export class ApiStubs {
  private readonly recorded = new Map<string, PendingCall[]>()
  private readonly consumed = new Map<string, number>()

  constructor(private readonly page: Page) {}

  /** Every request an alias has received so far, including the ones still waiting for their reply. */
  calls(alias: string): RecordedCall[] {
    return this.recorded.get(alias) ?? []
  }

  /**
   * The next request of an alias that no earlier `wait` returned, once it has been answered.
   * Requests are handed out in the order they arrived, whether they arrived before or after the call.
   */
  async wait(alias: string, timeout = waitTimeout): Promise<RecordedCall> {
    if (!this.recorded.has(alias)) throw new Error(`No API stub is registered as '${alias}'.`)
    const index = this.consumed.get(alias) ?? 0
    this.consumed.set(alias, index + 1)
    await expect
      .poll(() => this.recorded.get(alias)![index]?.settled ?? false, {
        message: `request ${index + 1} to the '${alias}' API stub`,
        timeout,
      })
      .toBe(true)
    return this.recorded.get(alias)![index]!
  }

  private async register(
    method: Method | RegExp | undefined,
    url: string | RegExp,
    alias: string | undefined,
    reply?: ReplySource,
  ) {
    let calls = alias ? this.recorded.get(alias) : undefined
    if (alias && !calls) {
      calls = []
      this.recorded.set(alias, calls)
    }
    const aliasCalls = calls ?? []
    await this.page.route(url, async (route) => {
      const request = route.request()
      const matches =
        method === undefined ||
        (typeof method === 'string' ? request.method() === method : method.test(request.method()))
      if (!matches) return route.fallback()

      const recorded = recordRequest(request)
      const call: PendingCall = { request: recorded, settled: false }
      aliasCalls.push(call)
      if (reply === undefined) {
        call.settled = true
        return route.fallback()
      }

      const { statusCode = 200, body, delay } = typeof reply === 'function' ? reply(recorded) : reply
      if (delay) await new Promise((resolve) => setTimeout(resolve, delay))
      call.response = { statusCode, body }
      try {
        if (body === undefined) await route.fulfill({ status: statusCode })
        else await route.fulfill({ status: statusCode, json: body })
      } catch {
        // The page left or cancelled the request while the reply was delayed.
      } finally {
        call.settled = true
      }
    })
  }

  private stubJson(method: Method, route: string, body: unknown, alias: string, statusCode = 200) {
    return this.register(method, route, alias, { statusCode, body })
  }

  stubAccount() {
    return this.stubJson('GET', '**/api/v1/account/me', { authenticated: false }, 'getCurrentUser')
  }

  stubBootstrap() {
    return this.stubJson(
      'GET',
      '**/api/v1/configuration/bootstrap',
      { authenticationRequired: false, apiVersion: '1' } satisfies BootstrapResponse,
      'getStartupConfig',
    )
  }

  stubSettings(overrides: Partial<SettingsResponse>) {
    // The API returns the whole settings object; a partial override must not become the whole response.
    return this.stubJson('GET', '**/api/v1/configuration/settings', settingsFixture(overrides), 'getSettings')
  }

  stubLibrary(audiobooks: LibraryResponse) {
    return this.stubJson('GET', '**/api/v1/library', audiobooks, 'getLibrary')
  }

  stubAudiobook(id: number, audiobook: AudiobookResponse) {
    return this.stubJson('GET', `**/api/v1/library/${id}`, audiobook, 'getAudiobook')
  }

  stubAudiobookReplies(id: number, replies: ApiReply[]) {
    let replyIndex = 0
    return this.register('GET', `**/api/v1/library/${id}`, 'getAudiobook', () => {
      const reply = replies[Math.min(replyIndex, replies.length - 1)]!
      replyIndex += 1
      return reply
    })
  }

  stubAudiobookIdentifiers(id: number, identifiers: RepairIdentifier[], alias = 'identifiers') {
    return this.stubJson('GET', `**/api/v1/library/${id}/identifiers`, { audiobookId: id, identifiers }, alias)
  }

  stubWeakStorageMissingFiles(id: number, alias = 'weakStorage') {
    return this.stubJson('GET', `**/api/v1/library/${id}/weak-storage-missing-files`, { items: [] }, alias)
  }

  async stubRepairPage(id: number, audiobook: AudiobookResponse, identifiers: RepairIdentifier[]) {
    await this.stubLibrary([
      libraryAudiobookFixture({
        id,
        title: audiobook.title,
        authors: audiobook.authors,
        asin: audiobook.asin,
      }),
    ])
    await this.stubAudiobook(id, audiobook)
    await this.stubAudiobookIdentifiers(id, identifiers)
    await this.stubWeakStorageMissingFiles(id)
  }

  async stubRepairPageWithResults(
    id: number,
    audiobook: AudiobookResponse,
    identifiers: RepairIdentifier[],
    rescanAudiobooks: AudiobookResponse[],
  ) {
    let currentAudiobook = audiobook
    let currentIdentifiers = identifiers
    let rescanIndex = 0
    await this.stubLibrary([
      libraryAudiobookFixture({
        id,
        title: audiobook.title,
        authors: audiobook.authors,
        asin: audiobook.asin,
      }),
    ])
    await this.register('GET', `**/api/v1/library/${id}`, 'getAudiobook', () => ({ body: currentAudiobook }))
    await this.register('GET', `**/api/v1/library/${id}/identifiers`, 'identifiers', () => ({
      body: { audiobookId: id, identifiers: currentIdentifiers },
    }))
    await this.stubWeakStorageMissingFiles(id)
    await this.register('PUT', `**/api/v1/library/${id}/identifiers`, 'saveAudiobookIdentifiers', (request) => {
      currentIdentifiers = request.body.identifiers
      return { body: { message: 'Identifiers updated', audiobook: { id }, identifiers: currentIdentifiers } }
    })
    await this.register('PUT', `**/api/v1/library/${id}`, 'clearAudiobookMetadata', (request) => {
      currentAudiobook = { ...currentAudiobook, ...request.body }
      return { body: { message: 'Audiobook updated', audiobook: currentAudiobook } }
    })
    await this.register('POST', `**/api/v1/library/${id}/rescan-metadata`, 'rescanAudiobookMetadata', () => {
      currentAudiobook = rescanAudiobooks[Math.min(rescanIndex, rescanAudiobooks.length - 1)]!
      rescanIndex += 1
      const primaryAsin = currentIdentifiers.find((identifier) => identifier.type === 'Asin' && identifier.isPrimary)
      return {
        body: {
          message: 'Metadata refreshed',
          audiobookId: id,
          asin: primaryAsin?.value,
          region: primaryAsin?.region,
          audiobook: currentAudiobook,
        },
      }
    })
  }

  async stubRepairWrites(id: number, replies: ApiReply[] | RepairWriteReplies = []) {
    const stages: RepairWriteReplies = Array.isArray(replies) ? { rescan: replies } : replies
    const replyIndexes = new Map<ApiReply[], number>()
    const replyInSequence = (stageReplies: ApiReply[] | undefined, fallbackBody: () => unknown): ApiReply => {
      const sequence = stageReplies?.length ? stageReplies : [{}]
      const index = replyIndexes.get(stageReplies ?? sequence) ?? 0
      replyIndexes.set(stageReplies ?? sequence, index + 1)
      const reply = sequence[Math.min(index, sequence.length - 1)]!
      return {
        statusCode: reply.statusCode ?? 200,
        body: reply.body ?? fallbackBody(),
        ...(reply.delay === undefined ? {} : { delay: reply.delay }),
      }
    }

    await this.register('PUT', `**/api/v1/library/${id}/identifiers`, 'saveAudiobookIdentifiers', (request) =>
      replyInSequence(stages.identifiers, () => ({
        message: 'Identifiers updated',
        audiobook: { id },
        identifiers: request.body.identifiers,
      })),
    )
    await this.register('PUT', `**/api/v1/library/${id}`, 'clearAudiobookMetadata', (request) =>
      replyInSequence(stages.metadata, () => ({
        message: 'Audiobook updated',
        audiobook: { id, ...request.body },
      })),
    )
    await this.register('POST', `**/api/v1/library/${id}/rescan-metadata`, 'rescanAudiobookMetadata', () =>
      replyInSequence(stages.rescan, () => ({ message: 'Metadata refreshed', audiobookId: id })),
    )
  }

  /** `reply` is a fixed reply, or a function called for each request. */
  stubSearch(reply: ReplySource = { statusCode: 200, body: { results: [] } }, alias = 'search') {
    return this.register('POST', '**/api/v1/search', alias, reply)
  }

  stubMetadata(
    reply: ReplySource = { statusCode: 200, body: { metadata: { asin: 'B008DFUGCQ', title: 'Dune' } } },
    alias = 'metadata',
  ) {
    return this.register(undefined, '**/api/v1/metadata/**', alias, reply)
  }

  stubSettingsUpdate(alias = 'saveSettings') {
    return this.register('POST', '**/api/v1/configuration/settings', alias, (request) => ({ body: request.body }))
  }

  stubRootFolderPathChange(id: number, alias = 'putRoot') {
    return this.stubJson('POST', `**/api/v1/rootfolders/${id}/path-changes`, { status: 'Completed' }, alias)
  }

  stubAudiobookMove(id: number, alias = 'moveAudiobook') {
    return this.register('POST', `**/api/v1/library/${id}/move`, alias, (request) => ({
      body: request.body.moveFiles
        ? { message: 'queued', jobId: 'job-test-1', target: request.body.destinationPath }
        : { message: 'Destination updated' },
    }))
  }

  stubFileSystemCheckVolume(overrides: Partial<FileSystemCheckVolumeResponse> = {}, alias = 'checkVolume') {
    return this.stubJson('GET', '**/api/v1/filesystem/check-volume*', fileSystemCheckVolumeFixture(overrides), alias)
  }

  stubFileSystemValidate(alias = 'validateManualImportFolder') {
    return this.stubJson('GET', '**/api/v1/filesystem/validate?path=*', fileSystemValidateFixture(), alias)
  }

  stubManualImportPreview(
    items: unknown[],
    alias = 'previewManualImport',
    route = '**/api/v1/library/manual-import/preview?path=*',
  ) {
    return this.stubJson('GET', route, { items }, alias)
  }

  stubManualImport(
    response: { importedCount: number; totalCount: number; results: unknown[] },
    alias = 'importManualFiles',
  ) {
    return this.stubJson('POST', '**/api/v1/library/manual-import', response, alias)
  }

  stubAddLibrary(audiobook: AudiobookResponse, alias = 'addAudiobookToLibrary') {
    return this.stubJson('POST', '**/api/v1/library/add', { message: 'Audiobook added', audiobook }, alias)
  }

  stubQualityProfiles(profiles: { id: number; name: string; isDefault: boolean }[]) {
    return this.stubJson('GET', '**/api/v1/qualityprofile', profiles, 'getQualityProfiles')
  }

  stubLibraryPathPreview(fullPath: string, relativePath: string, root: string, alias = 'previewLibraryPath') {
    return this.stubJson('POST', '**/api/v1/library/preview-path', { fullPath, relativePath, root }, alias)
  }

  stubLibraryImportSavedResults(
    rootFolderId: number,
    items: LibraryImportUnmatchedFile[],
    alias = 'savedLibraryImportResults',
  ) {
    return this.stubJson(
      'GET',
      `**/api/v1/rootfolders/${rootFolderId}/unmatched`,
      { items, lastScannedAt: '2026-01-01T00:00:00Z' },
      alias,
    )
  }

  async stubLibraryImportScan(
    rootFolderId: number,
    items: LibraryImportUnmatchedFile[],
    jobId = 'integration-library-import-scan',
  ) {
    await this.stubJson(
      'POST',
      `**/api/v1/rootfolders/${rootFolderId}/scan-unmatched`,
      { jobId },
      'startLibraryImportScan',
    )
    await this.stubJson(
      'GET',
      `**/api/v1/rootfolders/unmatched-results/${jobId}`,
      { jobId, status: 'Completed', items },
      'getLibraryImportScanResults',
    )
  }

  /** What every integration test starts from; the `api` fixture registers it before the test body runs. */
  async stubIntegrationDefaults() {
    await this.register('GET', anyApiRoute, undefined, { body: [] })
    await this.stubBootstrap()
    await this.stubSettings(
      settingsFixture({
        outputPath: '/audiobooks',
        fileNamingPattern: '{Author}/{Title}',
        multiFileNamingPattern: '{Title}-{DiskNumber:00}',
        completedFileAction: 'move',
        defaultSearchRegion: 'us',
        defaultSearchLanguage: 'english',
      }),
    )
    await this.stubAccount()
    await this.stubSystemHealth()
    await this.stubSystemReadiness()
    await this.stubJson('GET', '**/api/v1/download/queue', [], 'getDownloadQueue')
    await this.stubJson('GET', '**/api/v1/configuration/apikey', { apiKey: 'integration-test-key' }, 'getApiKey')
    await this.stubJson('GET', '**/api/v1/antiforgery/token', { token: 'integration-test-token' }, 'getAntiforgeryToken')
    await this.register(/POST|PUT|PATCH|DELETE/, anyApiRoute, undefined, {
      statusCode: 501,
      body: { message: 'No mutation stub was configured.' },
    })
  }

  stubDownloads(downloads: DownloadsResponse = [], alias = 'getDownloads') {
    return this.stubJson('GET', '**/api/v1/downloads', downloads, alias)
  }

  stubSystemHealth(health: SystemHealthResponse = { status: 'healthy', version: 'test' }) {
    return this.stubJson('GET', '**/api/v1/system/health', health, 'getSystemHealth')
  }

  stubRootFolders(folders: RootFolder[] = []) {
    return this.stubJson('GET', '**/api/v1/rootfolders', folders, 'getRootFolders')
  }

  stubSystemReadiness(readiness = systemReadinessFixture()) {
    return this.stubJson('GET', '**/api/v1/system/ready', readiness, 'getSystemReadiness')
  }

  /** The settings page's own data: the defaults already answer bootstrap, account, and every list with []. */
  stubSettingsPage(settings = settingsFixture()) {
    return this.stubSettings(settings)
  }
}
