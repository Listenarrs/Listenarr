import type {
  GetApiSourcesGetApiConfigurationsApiV1ConfigurationApisResponses,
  GetDownloadClientGetDownloadClientConfigurationsApiV1DownloadClientsResponses,
  GetDownloadsGetDownloadsApiV1DownloadsResponses,
  GetFileSystemCheckVolumeApiV1FilesystemCheckVolumeResponses,
  GetFileSystemValidatePathApiV1FilesystemValidateResponses,
  GetLibraryGetAudiobookApiV1LibraryIdResponses,
  GetLibraryGetAllApiV1LibraryResponses,
  GetQualityProfileGetAllApiV1QualityprofileResponses,
  GetSettingsGetApplicationSettingsApiV1ConfigurationSettingsResponses,
  GetStartupConfigurationGetBootstrapConfigApiV1ConfigurationBootstrapResponses,
  GetStartupConfigurationGetStartupConfigApiV1ConfigurationStartupconfigResponses,
  GetSystemGetServiceHealthApiV1SystemHealthResponses,
} from './generated-api/types.gen'

type DocumentedResponse<T> = unknown extends T ? never : T
type ApiSourcesResponse = DocumentedResponse<GetApiSourcesGetApiConfigurationsApiV1ConfigurationApisResponses[200]>
type BootstrapResponse = DocumentedResponse<GetStartupConfigurationGetBootstrapConfigApiV1ConfigurationBootstrapResponses[200]>
type DownloadClientsResponse = DocumentedResponse<GetDownloadClientGetDownloadClientConfigurationsApiV1DownloadClientsResponses[200]>
type DownloadsResponse = DocumentedResponse<GetDownloadsGetDownloadsApiV1DownloadsResponses[200]>
type FileSystemCheckVolumeResponse = DocumentedResponse<GetFileSystemCheckVolumeApiV1FilesystemCheckVolumeResponses[200]>
type FileSystemValidateResponse = DocumentedResponse<GetFileSystemValidatePathApiV1FilesystemValidateResponses[200]>
type AudiobookResponse = DocumentedResponse<GetLibraryGetAudiobookApiV1LibraryIdResponses[200]>
type SettingsResponse = DocumentedResponse<GetSettingsGetApplicationSettingsApiV1ConfigurationSettingsResponses[200]>
type StartupConfigResponse = DocumentedResponse<GetStartupConfigurationGetStartupConfigApiV1ConfigurationStartupconfigResponses[200]>
type QualityProfilesResponse = DocumentedResponse<GetQualityProfileGetAllApiV1QualityprofileResponses[200]>
type SystemHealthResponse = DocumentedResponse<GetSystemGetServiceHealthApiV1SystemHealthResponses[200]>
type LibraryResponse = DocumentedResponse<GetLibraryGetAllApiV1LibraryResponses[200]>
type LibraryAudiobook = LibraryResponse[number]
type DownloadRecord = DownloadsResponse[number]
type ApiReply = { statusCode?: number; body?: unknown; delay?: number }
type SearchReply = ApiReply
type MetadataReply = ApiReply

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
}

type RepairIdentifier = {
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

type RootFolder = {
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

export const startupConfigFixture = (overrides: Partial<StartupConfigResponse> = {}): StartupConfigResponse => ({
  apiKey: null,
  urlBase: '/',
  ...overrides,
})

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

export const fileSystemCheckVolumeFixture = (overrides: Partial<FileSystemCheckVolumeResponse> = {}) => ({
  sameVolume: true,
  willBreakHardlinks: false,
  sourceVolume: '/mnt',
  destVolume: '/mnt',
  message: 'Same volume',
  ...overrides,
} satisfies FileSystemCheckVolumeResponse)

export const fileSystemValidateFixture = (overrides: Partial<FileSystemValidateResponse> = {}) => ({
  isValid: true,
  exists: true,
  isWritable: true,
  message: 'Valid',
  ...overrides,
} satisfies FileSystemValidateResponse)

export const systemReadinessFixture = (overrides: Partial<SystemReadiness> = {}): SystemReadiness => ({
  isReady: true,
  filesystemReady: true,
  filesystemStatus: 'Ready',
  ...overrides,
})

const stubJson = (method: 'GET' | 'POST', route: string, body: unknown, alias: string, statusCode = 200) =>
  cy.intercept(method, route, { statusCode, body }).as(alias)

export const stubAccount = () =>
  cy
    .intercept('GET', '**/api/v1/account/me', {
      statusCode: 200,
      body: { authenticated: false },
    })
    .as('getCurrentUser')

export const stubBootstrap = () =>
  cy
    .intercept('GET', '**/api/v1/configuration/bootstrap', {
      statusCode: 200,
      body: { authenticationRequired: false, apiVersion: '1' } satisfies BootstrapResponse,
    })
    .as('getStartupConfig')

export const stubLegacyStartupConfig = (startupConfig = startupConfigFixture()) =>
  cy
    .intercept('GET', '**/api/v1/configuration/startupconfig', {
      statusCode: 200,
      body: startupConfig,
    })
    .as('getLegacyStartupConfig')

export const stubSettings = (settings: Partial<SettingsResponse>) =>
  cy
    .intercept('GET', '**/api/v1/configuration/settings', {
      statusCode: 200,
      body: settings,
    })
    .as('getSettings')

export const stubLibrary = (audiobooks: LibraryResponse) =>
  cy
    .intercept('GET', '**/api/v1/library', {
      statusCode: 200,
      body: audiobooks,
    })
    .as('getLibrary')

export const stubAudiobook = (id: number, audiobook: AudiobookResponse) =>
  cy
    .intercept('GET', `**/api/v1/library/${id}`, {
      statusCode: 200,
      body: audiobook,
    })
    .as('getAudiobook')

export const stubLibraryIdentifiers = (id: number, alias = 'identifiers') =>
  stubJson('GET', `**/api/v1/library/${id}/identifiers`, { audiobookId: id, identifiers: [] }, alias)

export const stubAudiobookIdentifiers = (
  id: number,
  identifiers: RepairIdentifier[],
  alias = 'identifiers',
) =>
  cy.intercept('GET', `**/api/v1/library/${id}/identifiers`, {
    statusCode: 200,
    body: { audiobookId: id, identifiers },
  }).as(alias)

export const stubWeakStorageMissingFiles = (id: number, alias = 'weakStorage') =>
  stubJson('GET', `**/api/v1/library/${id}/weak-storage-missing-files`, { items: [] }, alias)

export const stubRepairPage = (
  id: number,
  audiobook: AudiobookResponse,
  identifiers: RepairIdentifier[],
) => {
  stubLibrary([
    libraryAudiobookFixture({
      id,
      title: audiobook.title,
      authors: audiobook.authors,
      asin: audiobook.asin,
    }),
  ])
  stubAudiobook(id, audiobook)
  stubAudiobookIdentifiers(id, identifiers)
  stubWeakStorageMissingFiles(id)
  stubQualityProfiles()
}

export const stubRepairWrites = (id: number, rescanReplies: ApiReply[] = []) => {
  cy.intercept('PUT', `**/api/v1/library/${id}/identifiers`, (request) =>
    request.reply({
      statusCode: 200,
      body: {
        message: 'Identifiers updated',
        audiobook: { id },
        identifiers: request.body.identifiers,
      },
    }),
  ).as('saveAudiobookIdentifiers')
  cy.intercept('PUT', `**/api/v1/library/${id}`, (request) =>
    request.reply({
      statusCode: 200,
      body: { message: 'Audiobook updated', audiobook: { id, ...request.body } },
    }),
  ).as('clearAudiobookMetadata')

  let rescanIndex = 0
  const replies = rescanReplies.length
    ? rescanReplies
    : [{ statusCode: 200, body: { message: 'Metadata refreshed', audiobookId: id } }]
  cy.intercept('POST', `**/api/v1/library/${id}/rescan-metadata`, (request) => {
    const reply = replies[Math.min(rescanIndex, replies.length - 1)]!
    rescanIndex += 1
    request.reply(reply)
  }).as('rescanAudiobookMetadata')
}

export const stubSearch = (reply: SearchReply = { statusCode: 200, body: { results: [] } }, alias = 'search') =>
  cy.intercept('POST', '**/api/v1/search', reply).as(alias)

export const stubSearchWithReply = (getReply: () => SearchReply, alias = 'search') =>
  cy.intercept('POST', '**/api/v1/search', (request) => request.reply(getReply())).as(alias)

export const stubMetadata = (
  reply: MetadataReply = { statusCode: 200, body: { metadata: { asin: 'B008DFUGCQ', title: 'Dune' } } },
  alias = 'metadata',
) => cy.intercept('**/api/v1/metadata/**', reply).as(alias)

export const stubMetadataWithReply = (getReply: () => MetadataReply, alias = 'metadata') =>
  cy.intercept('**/api/v1/metadata/**', (request) => request.reply(getReply())).as(alias)

export const stubSettingsUpdate = (alias = 'saveSettings') =>
  cy.intercept('POST', '**/api/v1/configuration/settings', (request) =>
    request.reply({ statusCode: 200, body: request.body }),
  ).as(alias)

export const spyLibraryRequest = (alias = 'library') => cy.intercept('GET', '**/api/v1/library').as(alias)

export const spySettingsUpdate = (alias = 'saveSettings') =>
  cy.intercept('POST', '**/api/v1/configuration/settings').as(alias)

export const stubRootFolderPathChange = (id: number, alias = 'putRoot') =>
  stubJson('POST', `**/api/v1/rootfolders/${id}/path-changes`, { status: 'Completed' }, alias)

export const stubAudiobookMove = (id: number, alias = 'moveAudiobook') =>
  cy.intercept('POST', `**/api/v1/library/${id}/move`, (request) => {
    const body = request.body.moveFiles
      ? { message: 'queued', jobId: 'job-test-1', target: request.body.destinationPath }
      : { message: 'Destination updated' }
    request.reply({ statusCode: 200, body })
  }).as(alias)

export const stubFileSystemCheckVolume = (alias = 'checkVolume') =>
  cy.intercept('GET', '**/api/v1/filesystem/check-volume*', {
    statusCode: 200,
    body: fileSystemCheckVolumeFixture(),
  }).as(alias)

export const stubFileSystemValidate = (alias = 'validateManualImportFolder') =>
  cy.intercept('GET', '**/api/v1/filesystem/validate?path=*', {
    statusCode: 200,
    body: fileSystemValidateFixture(),
  }).as(alias)

export const stubManualImportPreview = (
  items: unknown[],
  alias = 'previewManualImport',
  route = '**/api/v1/library/manual-import/preview?path=*',
) => stubJson('GET', route, { items }, alias)

export const stubManualImport = (
  response: { importedCount: number; totalCount: number; results: unknown[] },
  alias = 'importManualFiles',
) => stubJson('POST', '**/api/v1/library/manual-import', response, alias)

export const stubLibraryImportSavedResults = (
  rootFolderId: number,
  items: LibraryImportUnmatchedFile[],
  alias = 'savedLibraryImportResults',
) =>
  cy.intercept('GET', `**/api/v1/rootfolders/${rootFolderId}/unmatched`, {
    statusCode: 200,
    body: { items, lastScannedAt: '2026-01-01T00:00:00Z' },
  }).as(alias)

export const stubLibraryImportScan = (
  rootFolderId: number,
  items: LibraryImportUnmatchedFile[],
  jobId = 'cypress-library-import-scan',
) => {
  stubJson('POST', `**/api/v1/rootfolders/${rootFolderId}/scan-unmatched`, { jobId }, 'startLibraryImportScan')
  cy.intercept('GET', `**/api/v1/rootfolders/unmatched-results/${jobId}`, {
    statusCode: 200,
    body: { jobId, status: 'Completed', items },
  }).as('getLibraryImportScanResults')
}

export const stubLibraryImportSearch = (getReply: () => SearchReply, alias = 'libraryImportSearch') =>
  stubSearchWithReply(getReply, alias)

export const stubIntegrationDefaults = () => {
  cy.intercept({ method: 'GET', url: /\/api\/v1\// }, { statusCode: 200, body: [] })
  stubBootstrap()
  stubSettings(settingsFixture({
    outputPath: '/audiobooks',
    fileNamingPattern: '{Author}/{Title}',
    multiFileNamingPattern: '{Title}-{DiskNumber:00}',
    completedFileAction: 'move',
    defaultSearchRegion: 'us',
    defaultSearchLanguage: 'english',
  }))
  stubAccount()
  stubSystemHealth()
  stubSystemReadiness()
  stubJson('GET', '**/api/v1/download/queue', [], 'getDownloadQueue')
  stubJson('GET', '**/api/v1/configuration/apikey', { apiKey: 'cypress-test-key' }, 'getApiKey')
  stubJson('GET', '**/api/v1/antiforgery/token', { token: 'cypress-test-token' }, 'getAntiforgeryToken')
  cy.intercept({ method: /POST|PUT|PATCH|DELETE/, url: /\/api\/v1\// }, (request) => {
    request.reply({ statusCode: 501, body: { message: 'No Cypress mutation stub was configured.' } })
  })
}

export const stubApiSources = () =>
  cy.intercept('GET', '**/api/v1/configuration/apis', {
    statusCode: 200,
    body: [] satisfies ApiSourcesResponse,
  }).as('getApis')

export const stubDownloadClients = () =>
  cy.intercept('GET', '**/api/v1/download-clients', {
    statusCode: 200,
    body: [] satisfies DownloadClientsResponse,
  }).as('getDownloadClients')

export const stubDownloads = (downloads: DownloadsResponse = [], alias = 'getDownloads') =>
  cy.intercept('GET', '**/api/v1/downloads', {
    statusCode: 200,
    body: downloads,
  }).as(alias)

export const stubQualityProfiles = () =>
  cy.intercept('GET', '**/api/v1/qualityprofile', {
    statusCode: 200,
    body: [] satisfies QualityProfilesResponse,
  }).as('getQualityProfiles')

export const stubSystemHealth = (health: SystemHealthResponse = { status: 'healthy', version: 'test' }) =>
  cy.intercept('GET', '**/api/v1/system/health', {
    statusCode: 200,
    body: health satisfies SystemHealthResponse,
  }).as('getSystemHealth')

export const stubRootFolders = (folders: RootFolder[] = []) =>
  cy.intercept('GET', '**/api/v1/rootfolders', {
    statusCode: 200,
    body: folders,
  }).as('getRootFolders')

export const stubSystemReadiness = (readiness = systemReadinessFixture()) =>
  cy.intercept('GET', '**/api/v1/system/ready', {
    statusCode: 200,
    body: readiness,
  }).as('getSystemReadiness')

export const stubSettingsDependencies = () => {
  stubAccount()
  stubApiSources()
  stubDownloadClients()
  stubJson('GET', '**/api/v1/remotepath', [], 'getRemotePathMappings')
  stubJson('GET', '**/api/v1/indexers', [], 'getIndexers')
  stubQualityProfiles()
  stubJson('GET', '**/api/v1/account/admins', [], 'getAdminUsers')
}

export const stubSettingsPage = (settings = settingsFixture()) => {
  stubBootstrap()
  stubSettings(settings)
  stubSettingsDependencies()
}