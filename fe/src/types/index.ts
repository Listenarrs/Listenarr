/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
export interface BaseSearchResult {
  id: string
  title: string
  artist: string
  album: string
  category: string
  source: string
  sourceLink?: string
  publishedDate: string
  format: string
  score?: number
}

export interface OpenLibraryBook {
  key: string
  title: string
  // OpenLibrary sometimes returns `author_name` as an array, other times a single string
  author_name?: string[] | string
  author_key?: string[]
  first_publish_year?: number
  isbn?: string[]
  edition_key?: string[]
  cover_edition_key?: string
  publisher?: string[]
  cover_i?: number
  edition_count?: number
  language?: string[]
  subject?: string[]
  ebook_access?: 'public' | 'borrowable' | 'printdisabled' | 'no_ebook'
  has_fulltext?: boolean
  public_scan_b?: boolean
  seriesList?: string[]
}

export interface IndexerSearchResult extends BaseSearchResult {
  downloadReference?: string
  size: number
  seeders?: number
  leechers?: number
  magnetLink: string
  torrentUrl: string
  nzbUrl: string
  downloadType: string // "Torrent", "Usenet", or "DDL"
  quality?: string
  resultUrl?: string // Canonical indexer page for the result
}

export interface MetadataSearchResult extends BaseSearchResult {
  description?: string
  subtitle?: string
  publisher?: string
  language?: string
  runtime?: number
  narrator?: string
  imageUrl?: string
  asin?: string
  isbn?: string
  series?: string
  seriesNumber?: string
  seriesAsin?: string
  seriesList?: string[]
  genres?: string[] // Genres from metadata sources (e.g., Audible)
  productUrl?: string // Direct link to Amazon/Audible product page
  isEnriched?: boolean
  metadataSource?: string // Which metadata API enriched this result
  // Audible-style fields (when backend returns Audible-shaped JSON)
  authors?: AudibleAuthor[]
  narrators?: AudibleNarrator[]
  lengthMinutes?: number
  link?: string
  releaseDate?: string
  publishDate?: string
}

// Legacy SearchResult interface - kept for backwards compatibility
// Combines both indexer and metadata properties
export interface SearchResult extends BaseSearchResult {
  downloadReference?: string
  // Indexer-specific properties
  thumbnailRetentionDays?: number
  size: number
  seeders?: number
  leechers?: number
  grabs?: number
  files?: number
  magnetLink: string
  torrentUrl: string
  nzbUrl: string
  downloadType: string // "Torrent", "Usenet", or "DDL"
  quality?: string
  resultUrl?: string // Canonical indexer page for the result

  // Metadata-specific properties
  description?: string
  subtitle?: string
  publisher?: string
  language?: string
  runtime?: number
  narrator?: string
  imageUrl?: string
  asin?: string
  isbn?: string
  series?: string
  seriesNumber?: string
  seriesAsin?: string
  seriesList?: string[]
  genres?: string[] // Genres from metadata sources (e.g., Audible)
  productUrl?: string // Direct link to Amazon/Audible product page
  isEnriched?: boolean
  metadataSource?: string // Which metadata API enriched this result
  // Audible-style fields
  authors?: AudibleAuthor[]
  narrators?: AudibleNarrator[]
  lengthMinutes?: number
  link?: string
  releaseDate?: string
  publishDate?: string
}

export interface Download {
  id: string
  title: string
  artist: string
  album: string
  originalUrl: string
  status:
    | 'Queued'
    | 'Downloading'
    | 'Paused'
    | 'Completed'
    | 'Failed'
    | 'Processing'
    | 'Ready'
    | 'Moved'
    | 'ImportPending'
    | 'ImportBlocked'
  progress: number
  totalSize: number
  downloadedSize: number
  downloadPath: string
  finalPath: string
  startedAt: string
  completedAt?: string
  errorMessage?: string
  downloadClientId: string
  metadata: Record<string, unknown>
  // Optional link to an audiobook record when the download was queued for a specific audiobook
  audiobookId?: number
}

export interface QueueItem {
  id: string
  title: string
  audiobookId?: number
  author?: string
  series?: string
  seriesNumber?: string
  quality: string
  language?: string
  status: string // downloading, paused, queued, completed, failed
  progress: number // 0-100
  size: number // in bytes
  downloaded: number // in bytes
  downloadSpeed: number // bytes per second
  eta?: number // seconds remaining
  indexer?: string
  downloadClient: string
  downloadClientId: string
  downloadClientType: string
  addedAt: string
  errorMessage?: string
  isStaleSnapshot?: boolean
  snapshotState?: string
  snapshotFailureReason?: string
  snapshotAgeSeconds?: number
  snapshotRefreshedAt?: string
  canPause: boolean
  canRemove: boolean
  seeders?: number
  leechers?: number
  ratio?: number
  remotePath?: string // Path as seen by download client
  localPath?: string // Path translated for Listenarr
}

export interface QueueClientStatus {
  clientId: string
  clientName: string
  clientType: string
  snapshotState: string
  isStaleSnapshot: boolean
  isUnavailable: boolean
  snapshotFailureReason?: string
  snapshotAgeSeconds?: number
  snapshotRefreshedAt?: string
  itemCount: number
}

export interface QueueSnapshot {
  items: QueueItem[]
  clients: QueueClientStatus[]
  generatedAt: string
  hasStaleData: boolean
  hasUnavailableClients: boolean
}

export type QueueUpdatePayload = QueueSnapshot | QueueItem[]

export interface ApiConfiguration {
  id: string
  name: string
  baseUrl: string
  apiKey: string
  type: 'torrent' | 'nzb' | 'metadata' | 'search' | 'other'
  isEnabled: boolean
  priority: number
  headers: Record<string, string>
  parameters: Record<string, string>
  rateLimitPerMinute?: string
  createdAt: string
  lastUsed?: string
}

export interface DownloadClientConfiguration {
  id: string
  name: string
  type: 'qbittorrent' | 'transmission' | 'sabnzbd' | 'nzbget'
  host: string
  port: number
  username: string
  password: string
  downloadPath: string
  useSSL: boolean
  isEnabled: boolean
  removeCompletedDownloads?: string // "none", "remove", "remove_and_delete"
  // Client-specific settings. Use `DownloadClientSettings` for typed access
  settings: DownloadClientSettings
  // Optional persisted last test result (true = success, false = failure)
  lastTestSuccessful?: boolean
}

export interface DownloadClientSettings {
  apiKey?: string
  urlBase?: string
  category?: string
  tags?: string
  recentPriority?: string
  olderPriority?: string
  removeCompleted?: boolean
  removeFailed?: boolean
  initialState?: string
  sequentialOrder?: boolean
  firstAndLastFirst?: boolean
  contentLayout?: string
  // Optional mapping to one or more remote path mapping IDs
  remotePathMappingIds?: number[]
  [key: string]: unknown
}

export interface RemotePathMapping {
  id: number
  downloadClientId: string
  name?: string
  remotePath: string
  localPath: string
  createdAt: string
  updatedAt: string
}

export interface RootFolder {
  id: number
  name: string
  path: string
  pathSyntax?: 'Windows' | 'Unix' | null
  isDefault: boolean
  createdAt?: string
  updatedAt?: string | null
  caseSensitivityMode?: 'Auto' | 'Sensitive' | 'Insensitive'
  resolvedCaseSensitivity?: 'Unknown' | 'Sensitive' | 'Insensitive'
  pathIdentityState?: 'Valid' | 'Conflict' | 'Unavailable'
  storageState?:
    | 'Healthy'
    | 'Limited'
    | 'Missing'
    | 'Changed'
    | 'Unavailable'
    | 'Unconfirmed'
    | 'Initializing'
    | 'InitializationFailed'
  storageReason?:
    | 'None'
    | 'PathMissing'
    | 'ForeignPathSyntax'
    | 'AccessDenied'
    | 'IdentityUnsupported'
    | 'IdentityMismatch'
    | 'IdentityUnstable'
    | 'FilesystemSemanticsUnavailable'
    | 'FilesystemSemanticsChanged'
    | 'MutationSemanticsUnproven'
    | 'ReadOnlyFilesystem'
    | 'MutationCapabilityUnavailable'
    | 'NoAuthorizedIdentity'
    | 'InvalidPath'
    | 'Initializing'
    | 'InitializationFailed'
    | 'Unknown'
  storageMessage?: string | null
  storageDetail?: string | null
  canConfirmCurrentFolder?: boolean
  canChangePath?: boolean
  canReadFilesystem?: boolean
  canScanFilesystem?: boolean
  canMutateFilesystem?: boolean
  confirmationToken?: string | null
  activeRelocation?: RootFolderPathChangeResult | null
}

export type RootFolderRelocationSkipReasonCode =
  | 'Unknown'
  | 'InvalidStoredPath'
  | 'SourceSemanticsUnavailable'
  | 'TargetPathInvalid'
  | 'TargetIdentityCollision'
  | 'TargetIdentityUnresolvedConflict'

export interface RootFolderRelocationSkippedItem {
  audiobookId: number
  reasonCode: RootFolderRelocationSkipReasonCode
}

export interface RootFolderMetadataRepairCollisionFile {
  audiobookFileId: number
  audiobookId: number
  relativePath: string
  canRemove: boolean
}

export interface RootFolderMetadataRepairCollisionGroup {
  targetRelativePath: string
  files: RootFolderMetadataRepairCollisionFile[]
}

export interface RootFolderMetadataRepairDetails {
  relocationId: string
  audiobookId: number
  audiobookTitle: string
  reasonCode: RootFolderRelocationSkipReasonCode
  collisionGroups: RootFolderMetadataRepairCollisionGroup[]
}

export interface RootFolderPathChangeResult {
  relocationId?: string | null
  rootFolderId: number | null
  currentPath: string
  targetPath: string
  status: 'Pending' | 'Running' | 'NeedsAttention' | 'Completed' | 'Failed'
  totalJobs: number
  completedJobs: number
  error?: string | null
  targetIdentityEnrollmentState: 'NotRequired' | 'Authorized' | 'Unavailable'
  skippedAudiobookIds?: number[] | null
  mode?: 'Relocate' | 'MetadataOnly'
  skippedItems?: RootFolderRelocationSkippedItem[] | null
  canAbandon?: boolean
}

export interface TranslatePathRequest {
  downloadClientId: string
  remotePath: string
}

export interface TranslatePathResponse {
  downloadClientId: string
  remotePath: string
  localPath: string
  translated: boolean
}

export interface ApplicationSettings {
  version: number
  outputPath: string
  folderNamingPattern: string
  fileNamingPattern: string
  multiFileNamingPattern: string
  enableMetadataProcessing: boolean
  enableCoverArtDownload: boolean
  audnexusApiUrl: string
  maxConcurrentDownloads: number
  unmatchedScanConcurrency?: number
  pollingIntervalSeconds?: number
  // How many seconds a download must be observed as complete by the client before finalization begins
  downloadCompletionStabilitySeconds?: number
  // Retry/backoff settings used by the server when a finalized download's source file is not yet present
  missingSourceRetryInitialDelaySeconds?: number
  missingSourceMaxRetries?: number
  enableNotifications: boolean
  allowedFileExtensions: string[]
  importBlacklistExtensions?: string[]
  // Action to perform for completed downloads.
  completedFileAction?: 'none' | 'move' | 'copy' | 'hardlink/copy'
  // Show completed external downloads (torrents/NZBs) in the Activity view
  showCompletedExternalDownloads?: boolean
  // Failed download handling
  failedDownloadHandlingEnabled?: boolean
  failedDownloadAutoSearch?: boolean
  // Optional admin credentials used when saving settings to create/update an initial admin user
  adminUsername?: string
  adminPassword?: string

  // Notification settings
  webhookUrl?: string
  enabledNotificationTriggers?: string[]
  // New webhook format (multiple webhooks)
  webhooks?: Array<{
    id: string
    name: string
    url: string
    type: 'Pushbullet' | 'Telegram' | 'Slack' | 'Discord' | 'Pushover' | 'NTFY' | 'Zapier'
    triggers: string[]
    isEnabled: boolean
  }>

  // Discord bot integration settings (optional)
  discordBotEnabled?: boolean
  discordApplicationId?: string
  discordGuildId?: string
  // Optional Discord channel id to restrict commands to a single channel
  discordChannelId?: string
  // Stored token (if provided via Settings). Note security implications.
  discordBotToken?: string
  // Command group and subcommand names, resulting in `/group subcommand` usage
  discordCommandGroupName?: string
  discordCommandSubcommandName?: string
  // Optional bot appearance customization
  discordBotUsername?: string
  discordBotAvatar?: string

  // Search behavior settings
  // Enable OpenLibrary augmentation/search
  enableOpenLibrarySearch?: boolean
  defaultSearchRegion?: string
  defaultSearchLanguage?: string
}

export interface ProwlarrImportConnectionSettings {
  url: string
  port?: number
  tagFilter?: string
  hasSavedApiKey: boolean
}

export interface StartupConfig {
  logLevel?: string
  enableSsl?: boolean
  port?: number
  sslPort?: number
  urlBase?: string
  bindAddress?: string
  apiKey?: string
  authenticationMethod?: string
  updateMechanism?: string
  launchBrowser?: boolean
  branch?: string
  instanceName?: string
  syslogPort?: number
  analyticsEnabled?: boolean
  authenticationRequired?: string | boolean
  // PascalCase variant is accepted for compatibility with some server responses
  AuthenticationRequired?: string | boolean
  apiVersion?: string | number
  ApiVersion?: string | number
  sslCertPath?: string
  sslCertPassword?: string
}

export interface StartupConfigDto {
  authenticationRequired?: string | boolean
  AuthenticationRequired?: string | boolean
  apiVersion?: string | number
  ApiVersion?: string | number
}

export interface AudibleBookMetadata {
  title: string
  subtitle?: string
  authors: string[]
  publishedDate?: string
  publishYear?: string
  series?: string
  seriesNumber?: string
  seriesAsin?: string
  seriesMemberships?: AudiobookSeriesMembership[]
  seriesList?: string[]
  description?: string
  genres?: string[]
  tags?: string[]
  narrators?: string[]
  isbn?: string
  asin: string
  searchResult?: SearchResult
  publisher?: string
  language?: string
  runtime?: number
  edition?: string
  version?: string
  imageUrl?: string
  explicit?: boolean
  abridged?: boolean
  source?: string
  sourceLink?: string
  region?: string
  openLibraryId?: string
  metadataSource?: string
  // Optional local mapping to a quality profile ID when viewing in the UI
  qualityProfileId?: number
}

export interface AuthorCatalogBook {
  asin?: string
  title: string
  subtitle?: string
  authors?: string[]
  imageUrl?: string
  runtime?: number
  language?: string
  publisher?: string
  narrators?: string[]
  genres?: string[]
  series?: string
  seriesNumber?: string
  publishedDate?: string
  isbn?: string
  link?: string
  metadataSource?: string
}

export interface AuthorCatalogResponse {
  author: {
    asin?: string
    name: string
    image?: string
  }
  books: AuthorCatalogBook[]
  totalBooks: number
}

export interface RelatedAuthorItem {
  asin?: string
  name: string
}

export interface AuthorLookupResponse {
  asin?: string
  name: string
  image?: string
  cachedPath?: string
  description?: string
  similarAuthors?: RelatedAuthorItem[]
}

export interface SeriesCatalogBook {
  asin?: string
  title: string
  subtitle?: string
  authors?: string[]
  imageUrl?: string
  runtime?: number
  language?: string
  publisher?: string
  narrators?: string[]
  genres?: string[]
  series?: string
  seriesNumber?: string
  publishedDate?: string
  isbn?: string
  link?: string
  metadataSource?: string
}

export interface SeriesCatalogResponse {
  series: {
    asin?: string
    name: string
    image?: string
    description?: string
  }
  books: SeriesCatalogBook[]
  totalBooks: number
}

export interface SeriesLookupResponse {
  asin?: string
  name: string
  image?: string
  cachedPath?: string
  description?: string
  totalBooks?: number
}

export interface MonitoredAuthor {
  id: number
  authorName: string
  authorAsin?: string
  region: string
  language: string
  createdAt: string
  updatedAt: string
  lastCheckedAt?: string
  lastSuccessfulSyncAt?: string
  lastError?: string
}

export interface AuthorMonitoringStatusResponse {
  isMonitored: boolean
  monitoredAuthor?: MonitoredAuthor | null
}

export interface MonitorAuthorResponse {
  message: string
  monitoredAuthor: MonitoredAuthor
  addedCount: number
  existingCount: number
  failedCount: number
  errorMessage?: string
}

export interface MonitoredSeries {
  id: number
  seriesName: string
  seriesAsin?: string
  region: string
  language: string
  createdAt: string
  updatedAt: string
  lastCheckedAt?: string
  lastSuccessfulSyncAt?: string
  lastError?: string
}

export interface SeriesMonitoringStatusResponse {
  isMonitored: boolean
  monitoredSeries?: MonitoredSeries | null
}

export interface MonitorSeriesResponse {
  message: string
  monitoredSeries: MonitoredSeries
  addedCount: number
  existingCount: number
  failedCount: number
  errorMessage?: string
}

export type AudiobookExternalIdentifierType = 'Asin' | 'Isbn' | 'OpenLibraryId'
export type AudiobookExternalIdentifierSource = 'Provider' | 'Imported' | 'Manual'

export interface AudiobookSeriesMembership {
  id?: number
  seriesName: string
  seriesNumber?: string
  seriesAsin?: string
  isPrimary?: boolean
  sortOrder?: number
}

export interface AudiobookExternalIdentifier {
  id: number
  type: AudiobookExternalIdentifierType
  value: string
  valueNormalized: string
  region?: string | null
  isPrimary: boolean
  source: AudiobookExternalIdentifierSource
  createdAt?: string
  updatedAt?: string
}

export interface AudiobookExternalIdentifierInput {
  type: AudiobookExternalIdentifierType
  value: string
  region?: string | null
  isPrimary?: boolean
  source?: AudiobookExternalIdentifierSource
}

export type AudiobookStatus = 'downloading' | 'no-file' | 'quality-mismatch' | 'quality-match'

export interface Audiobook {
  id: number
  title: string
  subtitle?: string
  authors?: string[]
  publishedDate?: string
  publishYear?: string
  series?: string
  seriesNumber?: string
  seriesMemberships?: AudiobookSeriesMembership[]
  description?: string
  genres?: string[]
  tags?: string[]
  narrators?: string[]
  isbn?: string
  asin?: string
  openLibraryId?: string
  publisher?: string
  language?: string
  runtime?: number
  edition?: string
  version?: string
  imageUrl?: string
  explicit?: boolean
  abridged?: boolean
  monitored?: boolean
  filePath?: string
  fileSize?: number
  fileCount?: number
  basePath?: string
  files?: {
    id: number
    path?: string
    size?: number
    durationSeconds?: number
    format?: string
    container?: string
    codec?: string
    bitrate?: number
    sampleRate?: number
    channels?: number
    createdAt?: string
    source?: string
  }[]
  quality?: string
  qualityProfileId?: number
  // Optional list of author ASINs (populated by backend when available)
  authorAsins?: string[]
  identifiers?: AudiobookExternalIdentifier[]
  // Server-computed flag indicating if this audiobook is wanted (monitored and missing files)
  wanted?: boolean
  // Server-computed list status used by slim /library responses.
  status?: AudiobookStatus
}

export interface AudiobookUpdateRequest {
  title?: string
  subtitle?: string
  authors?: string[]
  imageUrl?: string
  publishYear?: string
  publishedDate?: string
  series?: string
  seriesNumber?: string
  seriesMemberships?: AudiobookSeriesMembership[]
  description?: string
  genres?: string[]
  tags?: string[]
  narrators?: string[]
  isbn?: string[]
  asin?: string
  openLibraryId?: string
  publisher?: string
  language?: string
  runtime?: number
  edition?: string
  version?: string
  explicit?: boolean
  abridged?: boolean
  monitored?: boolean
  filePath?: string
  fileSize?: number
  basePath?: string
  quality?: string
  qualityProfileId?: number
}

export interface History {
  id: number
  audiobookId?: number
  audiobookTitle?: string
  eventType: string
  message?: string
  source?: string
  timestamp: string
  notificationSent?: boolean
  data?: string
}

export interface Indexer {
  id: number
  name: string
  type: string // "Torrent" or "Usenet"
  implementation: string // "Newznab", "Torznab", "Custom"
  url: string
  apiKey?: string
  categories?: string
  animeCategories?: string
  tags?: string
  enableRss: boolean
  enableAutomaticSearch: boolean
  enableInteractiveSearch: boolean
  enableAnimeStandardSearch: boolean
  isEnabled: boolean
  priority: number
  minimumAge: number
  retention: number
  maximumSize: number
  additionalSettings?: string
  createdAt: string
  updatedAt: string
  lastTestedAt?: string
  lastTestSuccessful?: boolean
  lastTestError?: string
}

export interface SystemReadiness {
  isReady: boolean
  status: string
  databaseConnected: boolean
  migrationsCurrent: boolean
  errorCode?: string | null
  filesystemReady: boolean
  filesystemStatus: 'Pending' | 'Running' | 'Ready' | 'Failed'
  filesystemPhase?: string | null
  filesystemErrorCode?: string | null
  filesystemErrorMessage?: string | null
}

export interface SystemInfo {
  version: string
  operatingSystem: string
  runtime: string
  uptime: string
  memory: MemoryInfo
  cpu: CpuInfo
  startTime: string
}

export interface MemoryInfo {
  usedBytes: number
  totalBytes: number
  freeBytes: number
  usedPercentage: number
  usedFormatted: string
  totalFormatted: string
  freeFormatted: string
}

export interface CpuInfo {
  usagePercentage: number
  processorCount: number
}

export interface StorageInfo {
  usedBytes: number
  totalBytes: number
  freeBytes: number
  usedPercentage: number
  usedFormatted: string
  totalFormatted: string
  freeFormatted: string
  driveName: string
  status: string
  disks: DiskStorageInfo[]
}

export interface DiskStorageInfo {
  label: string
  path: string
  usedBytes: number
  totalBytes: number
  freeBytes: number
  usedPercentage: number
  usedFormatted: string
  totalFormatted: string
  freeFormatted: string
  status: string
}

export interface ServiceHealth {
  status: string // "healthy", "warning", "error", "unknown"
  version: string
  uptime: string
  downloadClients: DownloadClientHealth
  externalApis: ExternalApiHealth
}

export interface DownloadClientHealth {
  status: string
  connected: number
  total: number
  clients: ClientStatus[]
}

export interface ExternalApiHealth {
  status: string
  connected: number
  total: number
  apis: ApiStatus[]
}

export interface ClientStatus {
  name: string
  status: string // "connected", "disconnected", "unknown"
  type?: string
}

export interface ApiStatus {
  name: string
  status: string // "connected", "disconnected", "unknown"
  enabled: boolean
}

export interface LogEntry {
  id: string
  timestamp: string // ISO date string
  level: string // "Info", "Warning", "Error", "Debug"
  message: string
  exception?: string
  source?: string
}

export interface QualityProfile {
  id?: number
  name: string
  description?: string
  qualities: QualityDefinition[]
  cutoffQuality?: string
  minimumSize?: number // MB (optional - no minimum if not set)
  maximumSize?: number // MB (optional - no maximum if not set)
  preferredFormats?: string[] // e.g., ["m4b", "mp3", "m4a", "flac", "opus"]
  preferredWords?: string[] // Words that increase score
  mustNotContain?: string[] // Instant rejection
  mustContain?: string[] // Must be present
  preferredLanguages?: string[] // e.g., ["English", "Spanish"]
  minimumSeeders?: number
  minimumScore?: number // Minimum score threshold for automatic downloads
  isDefault?: boolean
  preferNewerReleases?: boolean
  maximumAge?: number // days (0 = no limit)
  customGroupNames?: Record<string, string> // Custom names for quality groups by codec
  createdAt?: string
  updatedAt?: string
}

export interface QualityDefinition {
  quality: string // e.g., "320kbps", "192kbps", "lossless"
  allowed: boolean
  priority: number // Lower = higher priority
  codec?: string
  bitrate?: number
  isLossless?: boolean
}

/**
 * Extended quality information for better organization
 * Maps the string identifiers to structured codec/bitrate data
 */
export interface QualityInfo {
  id: string // Unique identifier matching QualityDefinition.quality
  label: string // Display label (e.g., "MP3 320 kbps")
  codec: string // Codec type (MP3, AAC, M4B, OPUS, OGG Vorbis, FLAC)
  bitrate?: number // Bitrate in kbps (optional for lossless)
  isLossless: boolean // Whether codec is lossless
  category: 'lossy' | 'lossless' | 'unknown' // Category for grouping
}

/**
 * Quality group for organizing qualities by category
 */
export interface QualityGroup {
  category: 'lossy' | 'lossless' | 'unknown'
  label: string
  qualities: QualityInfo[]
}

/**
 * Codec definition - represents a codec family (MP3, AAC, FLAC, etc.)
 */
export interface CodecDefinition {
  codec: string // Codec identifier (MP3, AAC, FLAC, etc.)
  label: string // Display label
  isLossless: boolean
  bitrates?: number[] // Available bitrates for lossy codecs
  supportsVBR?: boolean // Whether codec supports variable bitrate
}

/**
 * Quality item for the drag-and-drop UI
 */
export interface QualityItem {
  id: string // Full quality ID (e.g., "MP3 320kbps")
  codec: string // Codec name
  bitrate?: number // Bitrate in kbps
  label: string // Display label
  isLossless: boolean
  enabled: boolean // Whether quality is selected
  priority: number // Position in list (lower = higher priority)
}

export interface QualityScore {
  searchResult: SearchResult
  totalScore: number
  scoreBreakdown: Record<string, number>
  rejectionReasons: string[]
  isRejected: boolean
  // Optional Prowlarr-style composite smart score and breakdown
  smartScore?: number
  smartScoreBreakdown?: Record<string, number>
}

export type SearchSortBy =
  | 'Seeders'
  | 'Leechers'
  | 'Size'
  | 'PublishedDate'
  | 'Title'
  | 'Source'
  | 'Language'
  | 'Quality'
  | 'Grabs'
  | 'Score'

export type SearchSortDirection = 'Ascending' | 'Descending'

// Manual import types (correspond to server ManualImport DTOs)
export interface ManualImportPreviewItem {
  relativePath: string
  fullPath: string
  size: string
  series?: string | null
  season?: string | null
  episodes?: string | null
  quality?: string | null
  languages: string[]
  releaseType?: string
}

export interface ManualImportPreviewResponse {
  items: ManualImportPreviewItem[]
}

export interface ManualImportRequestItem {
  relativePath?: string
  fullPath: string
  matchedAudiobookId?: number
  releaseGroup?: string | null
  qualityProfileId?: number | null
  language?: string | null
  size?: string | null
}

export interface ManualImportRequest {
  path: string
  mode?: 'automatic' | 'interactive'
  action?: 'none' | 'move' | 'copy' | 'hardlink/copy'
  includeCompanionFiles?: boolean
  cleanupEmptySourceFolders?: boolean
  items?: ManualImportRequestItem[]
}

export interface ManualImportResult {
  success: boolean
  sourcePath?: string
  destinationPath?: string
  audiobook?: Audiobook
  error?: string
  skipped?: boolean
  skipReason?: string
}

// Audible API Types
export interface AudibleBookResponse {
  asin?: string
  title?: string
  subtitle?: string
  authors?: AudibleAuthor[]
  narrators?: AudibleNarrator[]
  publisher?: string
  publishDate?: string
  description?: string
  imageUrl?: string
  lengthMinutes?: number
  runtime?: number
  language?: string
  genres?: AudibleGenre[]
  series?: AudibleSeries[]
  explicit?: boolean
  releaseDate?: string
  isbn?: string
  region?: string
  bookFormat?: string
}

export interface AudibleAuthor {
  asin?: string
  name?: string
  region?: string
}

export interface AudibleNarrator {
  name?: string
}

export interface AudibleGenre {
  asin?: string
  name?: string
  type?: string
}

export interface AudibleSeries {
  asin?: string
  name?: string
  position?: string
}

export interface AudibleSearchResponse {
  results?: AudibleSearchResult[]
  totalResults?: number
}

export interface AudibleSearchResult {
  asin?: string
  title?: string
  authors?: AudibleAuthor[]
  imageUrl?: string
  lengthMinutes?: number
  language?: string
  series?: AudibleSeries[]
  publisher?: string
  narrators?: AudibleNarrator[]
  releaseDate?: string
  link?: string
}

/**
 * Response wrapper for search operations that can contain different types of results
 */
export interface SearchResponse {
  indexerResults: IndexerSearchResult[]
  metadataResults: MetadataSearchResult[]
  totalCount: number
}

// Unmatched file scan types

export interface UnmatchedFileItem {
  fullPath: string
  sourceFiles?: string[]
  relativePath: string
  bookFolder: string
  size: number
  fileCount: number
  title?: string
  author?: string
  series?: string
  seriesNumber?: string
  year?: string
  narrator?: string
  description?: string
  coverPath?: string
  asin?: string
  format: string
  duration?: string
}

export interface UnmatchedFilesResponse {
  jobId: string
  status: 'Queued' | 'Processing' | 'Completed' | 'Failed'
  error?: string
  items: UnmatchedFileItem[]
}

export interface SavedUnmatchedResponse {
  lastScannedAt?: string
  items: UnmatchedFileItem[]
}

export interface BulkRenameRequest {
  audiobookIds: number[]
}

export interface FileRenamePreview {
  fileId: number
  currentPath?: string
  newPath?: string
  currentFilename?: string
  newFilename?: string
  changed: boolean
}

export interface RenamePathSemanticsSnapshot {
  syntax: 'Windows' | 'Unix'
  caseSensitivity: 'Unknown' | 'Sensitive' | 'Insensitive'
  requestedMode: 'Auto' | 'Sensitive' | 'Insensitive'
  boundaryPath: string
}

export interface RenamePreview {
  audiobookId: number
  audiobookTitle?: string
  currentFolderPath?: string
  currentFolderSemantics?: RenamePathSemanticsSnapshot
  newFolderPath?: string
  folderChanged: boolean
  fileRenames: FileRenamePreview[]
  hasChanges: boolean
}

export interface FileRenameOperation {
  fileId: number
  currentPath: string
  newPath: string
}

export interface RenameOperation {
  audiobookId: number
  currentFolderPath?: string
  currentFolderSemantics?: RenamePathSemanticsSnapshot
  newFolderPath?: string
  fileRenames: FileRenameOperation[]
}

export interface ExecuteRenameRequest {
  operations: RenameOperation[]
}

export interface FileRenameResultItem {
  fileId: number
  previousPath?: string
  newPath?: string
  success: boolean
  rolledBack: boolean
  error?: string
}

export interface RenameResult {
  audiobookId: number
  success: boolean
  conflict: boolean
  error?: string
  renamedFiles: FileRenameResultItem[]
}
