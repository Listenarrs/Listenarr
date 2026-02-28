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
  seriesList?: string[]
  genres?: string[] // Genres from metadata sources (e.g., Audimeta)
  productUrl?: string // Direct link to Amazon/Audible product page
  isEnriched?: boolean
  metadataSource?: string // Which metadata API enriched this result
  // Audimeta-style fields (when backend returns Audimeta-shaped JSON)
  authors?: AudimetaAuthor[]
  narrators?: AudimetaNarrator[]
  lengthMinutes?: number
  link?: string
  releaseDate?: string
  publishDate?: string
}

// Legacy SearchResult interface - kept for backwards compatibility
// Combines both indexer and metadata properties
export interface SearchResult extends BaseSearchResult {
  // Indexer-specific properties
    // Number of days to retain generated thumbnails (0 = use server default)
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
  seriesList?: string[]
  genres?: string[] // Genres from metadata sources (e.g., Audimeta)
  productUrl?: string // Direct link to Amazon/Audible product page
  isEnriched?: boolean
  metadataSource?: string // Which metadata API enriched this result
  // Audimeta-style fields
  authors?: AudimetaAuthor[]
  narrators?: AudimetaNarrator[]
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
  status: 'Queued' | 'Downloading' | 'Paused' | 'Completed' | 'Failed' | 'Processing' | 'Ready' | 'Moved'
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
  author?: string
  series?: string
  seriesNumber?: string
  quality: string
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
  canPause: boolean
  canRemove: boolean
  seeders?: number
  leechers?: number
  ratio?: number
  remotePath?: string // Path as seen by download client
  localPath?: string // Path translated for Listenarr
}

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
  isDefault: boolean
  createdAt: string
  updatedAt?: string
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
  outputPath: string
  folderNamingPattern: string
  fileNamingPattern: string
  multiFileNamingPattern: string
  enableMetadataProcessing: boolean
  enableCoverArtDownload: boolean
  audnexusApiUrl: string
  maxConcurrentDownloads: number
  pollingIntervalSeconds?: number
  // How many seconds a download must be observed as complete by the client before finalization begins
  downloadCompletionStabilitySeconds?: number
  // Retry/backoff settings used by the server when a finalized download's source file is not yet present
  missingSourceRetryInitialDelaySeconds?: number
  missingSourceMaxRetries?: number
  enableNotifications: boolean
  allowedFileExtensions: string[]
  // Action to perform for completed downloads: 'Move' or 'Copy'
  completedFileAction?: 'Move' | 'Copy'
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
  sslCertPath?: string
  sslCertPassword?: string
}

export interface AudibleBookMetadata {
  title: string
  subtitle?: string
  authors: string[]
  publishedDate?: string
  publishYear?: string
  series?: string
  seriesNumber?: string
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
  version?: string
  imageUrl?: string
  explicit?: boolean
  abridged?: boolean
  source?: string
  sourceLink?: string
  openLibraryId?: string
  metadataSource?: string
  // Optional local mapping to a quality profile ID when viewing in the UI
  qualityProfileId?: number
}

export type AudiobookExternalIdentifierType = 'Asin' | 'Isbn' | 'OpenLibraryId'
export type AudiobookExternalIdentifierSource = 'Provider' | 'Imported' | 'Manual'

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

export interface Audiobook {
  id: number
  title: string
  subtitle?: string
  authors?: string[]
  publishedDate?: string
  publishYear?: string
  series?: string
  seriesNumber?: string
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
  version?: string
  imageUrl?: string
  explicit?: boolean
  abridged?: boolean
  monitored?: boolean
  filePath?: string
  fileSize?: number
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
  inputMode?: 'move' | 'copy' | 'hardlink/copy'
  items?: ManualImportRequestItem[]
}

export interface ManualImportResult {
  success: boolean
  filePath?: string
  destinationPath?: string
  audiobookId?: number
  audiobookTitle?: string
  error?: string
}

// Audimeta API Types
export interface AudimetaBookResponse {
  asin?: string
  title?: string
  subtitle?: string
  authors?: AudimetaAuthor[]
  narrators?: AudimetaNarrator[]
  publisher?: string
  publishDate?: string
  description?: string
  imageUrl?: string
  lengthMinutes?: number
  runtime?: number
  language?: string
  genres?: AudimetaGenre[]
  series?: AudimetaSeries[]
  explicit?: boolean
  releaseDate?: string
  isbn?: string
  region?: string
  bookFormat?: string
}

export interface AudimetaAuthor {
  asin?: string
  name?: string
  region?: string
}

export interface AudimetaNarrator {
  name?: string
}

export interface AudimetaGenre {
  asin?: string
  name?: string
  type?: string
}

export interface AudimetaSeries {
  asin?: string
  name?: string
  position?: string
}

export interface AudimetaSearchResponse {
  results?: AudimetaSearchResult[]
  totalResults?: number
}

export interface AudimetaSearchResult {
  asin?: string
  title?: string
  authors?: AudimetaAuthor[]
  imageUrl?: string
  lengthMinutes?: number
  language?: string
  series?: AudimetaSeries[]
  publisher?: string
  narrators?: AudimetaNarrator[]
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
