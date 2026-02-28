using Listenarr.Domain.Models;

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Provides search functionality across multiple indexers and metadata sources
    /// </summary>
    public interface ISearchService
    {
        /// <summary>
        /// Searches configured indexers for audiobook content
        /// </summary>
        /// <param name="query">Search query string</param>
        /// <param name="category">Optional category filter</param>
        /// <param name="apiIds">Optional list of specific API IDs to search</param>
        /// <param name="sortBy">Sort results by seeders, peers, size, or age</param>
        /// <param name="sortDirection">Sort direction (ascending or descending)</param>
        /// <param name="isAutomaticSearch">Whether this is an automatic search (affects logging)</param>
        /// <returns>List of search results from all configured indexers</returns>
        Task<List<SearchResult>> SearchAsync(string query, string? category = null, List<string>? apiIds = null, SearchSortBy sortBy = SearchSortBy.Seeders, SearchSortDirection sortDirection = SearchSortDirection.Descending, bool isAutomaticSearch = false);

        /// <summary>
        /// Performs intelligent search with metadata enrichment
        /// </summary>
        /// <param name="query">Search query string</param>
        /// <param name="candidateLimit">Maximum number of ASIN candidates to collect (default 50)</param>
        /// <param name="returnLimit">Maximum number of results to return (default 10)</param>
        /// <param name="containmentMode">Containment mode: Strict|Relaxed|Off</param>
        /// <param name="requireAuthorAndPublisher">Whether both author and publisher are required</param>
        /// <param name="fuzzyThreshold">Similarity threshold used for fuzzy matching (0.0 - 1.0)</param>
        /// <param name="region">Region code to prefer when querying metadata providers (default: us)</param>
        /// <param name="language">Optional language code to filter metadata results (e.g. en, de)</param>
        /// <param name="ct">Cancellation token to cancel the intelligent search operation.</param>
        /// <returns>Search results enriched with metadata from configured sources</returns>
        Task<List<MetadataSearchResult>> IntelligentSearchAsync(string query, int candidateLimit = 50, int returnLimit = 50, string containmentMode = "Relaxed", bool requireAuthorAndPublisher = false, double fuzzyThreshold = 0.7, string region = "us", string? language = null, CancellationToken ct = default);

        /// <summary>
        /// Searches a specific API/indexer by ID
        /// </summary>
        /// <param name="apiId">The API configuration ID to search</param>
        /// <param name="query">Search query string</param>
        /// <param name="category">Optional category filter</param>
        /// <returns>Search results from the specified API</returns>
        Task<List<SearchResult>> SearchByApiAsync(string apiId, string query, string? category = null);
        /// <summary>
        /// Search a specific indexer and return raw indexer search results (IndexerSearchResult) for indexer-specific consumers.
        /// </summary>
        Task<List<Listenarr.Domain.Models.IndexerSearchResult>> SearchIndexerResultsAsync(string apiId, string query, string? category = null, Listenarr.Api.Models.SearchRequest? request = null);

        /// <summary>
        /// Tests connectivity and authentication for a specific API
        /// </summary>
        /// <param name="apiId">The API configuration ID to test</param>
        /// <returns>True if connection successful, false otherwise</returns>
        Task<bool> TestApiConnectionAsync(string apiId);

        /// <summary>
        /// Searches configured indexers (excludes metadata sources)
        /// </summary>
        /// <param name="query">Search query string</param>
        /// <param name="category">Optional category filter</param>
        /// <param name="sortBy">Sort results by seeders, peers, size, or age</param>
        /// <param name="sortDirection">Sort direction (ascending or descending)</param>
        /// <param name="isAutomaticSearch">Whether this is an automatic search</param>
        /// <param name="request">Optional request object to pass indexer-specific parameters</param>
        /// <returns>List of search results from indexers only</returns>
        Task<List<IndexerSearchResult>> SearchIndexersAsync(string query, string? category = null, SearchSortBy sortBy = SearchSortBy.Seeders, SearchSortDirection sortDirection = SearchSortDirection.Descending, bool isAutomaticSearch = false, Listenarr.Api.Models.SearchRequest? request = null);

        /// <summary>
        /// Gets all enabled metadata sources (Audimeta, Audnexus, OpenLibrary, etc.)
        /// </summary>
        /// <returns>List of enabled metadata source configurations</returns>
        Task<List<ApiConfiguration>> GetEnabledMetadataSourcesAsync();
    }

    /// <summary>
    /// Provides audiobook metadata lookup across configured providers (Audimeta, Audnexus, etc.).
    /// </summary>
    public interface IAudiobookMetadataService
    {
        /// <summary>
        /// Gets audiobook metadata from configured providers in priority order.
        /// </summary>
        /// <param name="asin">ASIN identifier.</param>
        /// <param name="region">Region code (default: us).</param>
        /// <param name="cache">Whether provider caching should be used.</param>
        /// <returns>Metadata payload (provider-specific shape) or null when unavailable.</returns>
        Task<object?> GetMetadataAsync(string asin, string region = "us", bool cache = true);

        /// <summary>
        /// Gets audiobook metadata directly from Audimeta.
        /// </summary>
        /// <param name="asin">ASIN identifier.</param>
        /// <param name="region">Region code (default: us).</param>
        /// <param name="cache">Whether provider caching should be used.</param>
        /// <returns>Audimeta metadata or null when unavailable.</returns>
        Task<AudimetaBookResponse?> GetAudimetaMetadataAsync(string asin, string region = "us", bool cache = true);
    }

    /// <summary>
    /// Manages download lifecycle including starting, monitoring, and processing downloads
    /// </summary>
    public interface IDownloadService
    {
        /// <summary>
        /// Starts a download from a search result
        /// </summary>
        /// <param name="searchResult">The search result to download</param>
        /// <param name="downloadClientId">ID of the download client to use</param>
        /// <param name="audiobookId">Optional audiobook ID to associate with download</param>
        /// <returns>Download ID</returns>
        Task<string> StartDownloadAsync(SearchResult searchResult, string downloadClientId, int? audiobookId = null);

        /// <summary>
        /// Gets all active downloads (queued, downloading, or processing)
        /// </summary>
        /// <returns>List of active downloads</returns>
        Task<List<Download>> GetActiveDownloadsAsync();

        /// <summary>
        /// Gets a specific download by ID
        /// </summary>
        /// <param name="downloadId">The download ID</param>
        /// <returns>Download details or null if not found</returns>
        Task<Download?> GetDownloadAsync(string downloadId);

        /// <summary>
        /// Cancels an active download
        /// </summary>
        /// <param name="downloadId">The download ID to cancel</param>
        /// <returns>True if cancelled successfully, false otherwise</returns>
        Task<bool> CancelDownloadAsync(string downloadId);

        /// <summary>
        /// Updates the status of all active downloads by querying download clients
        /// </summary>
        Task UpdateDownloadStatusAsync();

        /// <summary>
        /// Searches for an audiobook and automatically downloads the best result
        /// </summary>
        /// <param name="audiobookId">The audiobook ID to search and download</param>
        /// <returns>Search and download result details</returns>
        Task<SearchAndDownloadResult> SearchAndDownloadAsync(int audiobookId);

        /// <summary>
        /// Sends a search result to a download client
        /// </summary>
        /// <param name="searchResult">The search result to download</param>
        /// <param name="downloadClientId">Optional download client ID (uses default if not specified)</param>
        /// <param name="audiobookId">Optional audiobook ID to associate</param>
        /// <returns>Download ID</returns>
        Task<string> SendToDownloadClientAsync(SearchResult searchResult, string? downloadClientId = null, int? audiobookId = null);

        /// <summary>
        /// Gets the current download queue from all configured clients
        /// </summary>
        /// <returns>List of queue items from all download clients</returns>
        Task<List<QueueItem>> GetQueueAsync();

        /// <summary>
        /// Removes a download from the queue
        /// </summary>
        /// <param name="downloadId">The download ID to remove</param>
        /// <param name="downloadClientId">Optional specific client ID</param>
        /// <param name="force">If true, removes from database even if client removal fails</param>
        /// <returns>True if removed successfully, false otherwise</returns>
        Task<bool> RemoveFromQueueAsync(string downloadId, string? downloadClientId = null, bool force = false);

        /// <summary>
        /// Processes a completed download (moves files, updates database, triggers notifications)
        /// </summary>
        /// <param name="downloadId">The download ID to process</param>
        /// <param name="finalPath">The final file path after processing</param>
        Task ProcessCompletedDownloadAsync(string downloadId, string finalPath);

        /// <summary>
        /// Reprocesses a previously completed download
        /// </summary>
        /// <param name="downloadId">The download ID to reprocess</param>
        /// <returns>Job ID for the reprocessing task, or null if failed</returns>
        Task<string?> ReprocessDownloadAsync(string downloadId);

        /// <summary>
        /// Reprocesses multiple completed downloads
        /// </summary>
        /// <param name="downloadIds">List of download IDs to reprocess</param>
        /// <returns>List of reprocess results</returns>
        Task<List<ReprocessResult>> ReprocessDownloadsAsync(List<string> downloadIds);

        /// <summary>
        /// Reprocesses all completed downloads matching criteria
        /// </summary>
        /// <param name="includeProcessed">Whether to include already processed downloads</param>
        /// <param name="maxAge">Optional maximum age of downloads to reprocess</param>
        /// <returns>List of reprocess results</returns>
        Task<List<ReprocessResult>> ReprocessAllCompletedDownloadsAsync(bool includeProcessed = false, TimeSpan? maxAge = null);

        /// <summary>
        /// Tests a download client configuration
        /// </summary>
        /// <param name="client">The download client configuration to test</param>
        /// <returns>Tuple containing success flag, message, and optionally the client configuration</returns>
        Task<(bool Success, string Message, DownloadClientConfiguration? Client)> TestDownloadClientAsync(DownloadClientConfiguration client);

        /// <summary>
        /// Retrieve cached torrent bytes and filename for a given download id if available
        /// </summary>
        /// <param name="downloadId">The download ID to look up</param>
        Task<(byte[]? Bytes, string? FileName)> GetCachedTorrentAsync(string downloadId);

        /// <summary>
        /// Retrieve cached announce URLs for a given download id if available
        /// </summary>
        /// <param name="downloadId">The download ID to look up</param>
        Task<System.Collections.Generic.List<string>?> GetCachedAnnouncesAsync(string downloadId);
    }

    /// <summary>
    /// Provides download client communication for qBittorrent, Transmission, SABnzbd, and NZBGet
    /// </summary>
    public interface IDownloadClientService
    {
        /// <summary>
        /// Tests download client connectivity and authentication
        /// </summary>
        /// <param name="client">The download client configuration to test</param>
        /// <returns>Tuple containing success flag, message, and optionally the client configuration</returns>
        Task<(bool Success, string Message, DownloadClientConfiguration? Client)> TestDownloadClientAsync(DownloadClientConfiguration client);

        /// <summary>
        /// Sends a download to qBittorrent client
        /// </summary>
        /// <param name="searchResult">The search result to download</param>
        /// <param name="client">qBittorrent client configuration</param>
        /// <param name="audiobookId">Optional audiobook ID to associate</param>
        /// <returns>Torrent hash or download ID</returns>
        Task<string> SendToQBittorrentAsync(SearchResult searchResult, DownloadClientConfiguration client, int? audiobookId = null);

        /// <summary>
        /// Sends a download to Transmission client
        /// </summary>
        /// <param name="searchResult">The search result to download</param>
        /// <param name="client">Transmission client configuration</param>
        /// <param name="audiobookId">Optional audiobook ID to associate</param>
        /// <returns>Torrent hash or download ID</returns>
        Task<string> SendToTransmissionAsync(SearchResult searchResult, DownloadClientConfiguration client, int? audiobookId = null);

        /// <summary>
        /// Sends a download to SABnzbd client
        /// </summary>
        /// <param name="searchResult">The search result to download</param>
        /// <param name="client">SABnzbd client configuration</param>
        /// <param name="audiobookId">Optional audiobook ID to associate</param>
        /// <returns>NZO ID or download ID</returns>
        Task<string> SendToSABnzbdAsync(SearchResult searchResult, DownloadClientConfiguration client, int? audiobookId = null);

        /// <summary>
        /// Sends a download to NZBGet client
        /// </summary>
        /// <param name="searchResult">The search result to download</param>
        /// <param name="client">NZBGet client configuration</param>
        /// <param name="audiobookId">Optional audiobook ID to associate</param>
        /// <returns>NZB ID or download ID</returns>
        Task<string> SendToNZBGetAsync(SearchResult searchResult, DownloadClientConfiguration client, int? audiobookId = null);

        /// <summary>
        /// Gets the download queue from qBittorrent
        /// </summary>
        /// <param name="client">qBittorrent client configuration</param>
        /// <returns>List of queue items</returns>
        Task<List<QueueItem>> GetQBittorrentQueueAsync(DownloadClientConfiguration client);

        /// <summary>
        /// Gets the download queue from Transmission
        /// </summary>
        /// <param name="client">Transmission client configuration</param>
        /// <returns>List of queue items</returns>
        Task<List<QueueItem>> GetTransmissionQueueAsync(DownloadClientConfiguration client);

        /// <summary>
        /// Gets the download queue from SABnzbd
        /// </summary>
        /// <param name="client">SABnzbd client configuration</param>
        /// <returns>List of queue items</returns>
        Task<List<QueueItem>> GetSABnzbdQueueAsync(DownloadClientConfiguration client);

        /// <summary>
        /// Gets the download queue from NZBGet
        /// </summary>
        /// <param name="client">NZBGet client configuration</param>
        /// <returns>List of queue items</returns>
        Task<List<QueueItem>> GetNZBGetQueueAsync(DownloadClientConfiguration client);

        /// <summary>
        /// Removes a download from qBittorrent by hash
        /// </summary>
        /// <param name="hash">The torrent hash</param>
        /// <param name="client">qBittorrent client configuration</param>
        /// <returns>True if removed successfully, false otherwise</returns>
        Task<bool> RemoveFromQBittorrentAsync(string hash, DownloadClientConfiguration client);

        /// <summary>
        /// Removes a download from Transmission by hash
        /// </summary>
        /// <param name="hash">The torrent hash</param>
        /// <param name="client">Transmission client configuration</param>
        /// <returns>True if removed successfully, false otherwise</returns>
        Task<bool> RemoveFromTransmissionAsync(string hash, DownloadClientConfiguration client);

        /// <summary>
        /// Removes a download from SABnzbd by NZO ID
        /// </summary>
        /// <param name="nzoId">The NZO ID</param>
        /// <param name="client">SABnzbd client configuration</param>
        /// <returns>True if removed successfully, false otherwise</returns>
        Task<bool> RemoveFromSABnzbdAsync(string nzoId, DownloadClientConfiguration client);

        /// <summary>
        /// Removes a download from NZBGet by NZB ID
        /// </summary>
        /// <param name="nzbId">The NZB ID</param>
        /// <param name="client">NZBGet client configuration</param>
        /// <returns>True if removed successfully, false otherwise</returns>
        Task<bool> RemoveFromNZBGetAsync(string nzbId, DownloadClientConfiguration client);
    }

    /// <summary>
    /// Provides metadata retrieval and file tagging for audiobook files
    /// </summary>
    public interface IMetadataService
    {
        /// <summary>
        /// Gets metadata from online sources
        /// </summary>
        /// <param name="title">The audiobook title</param>
        /// <param name="artist">Optional artist/author name</param>
        /// <param name="isbn">Optional ISBN</param>
        /// <returns>Audio metadata or null if not found</returns>
        Task<AudioMetadata?> GetMetadataAsync(string title, string? artist = null, string? isbn = null);

        /// <summary>
        /// Extracts metadata from an audio file using ffprobe
        /// </summary>
        /// <param name="filePath">Path to the audio file</param>
        /// <returns>Extracted audio metadata, or null if extraction failed</returns>
        Task<AudioMetadata?> ExtractFileMetadataAsync(string filePath);

        /// <summary>
        /// Applies metadata tags to an audio file
        /// </summary>
        /// <param name="filePath">Path to the audio file</param>
        /// <param name="metadata">Metadata to apply</param>
        Task ApplyMetadataAsync(string filePath, AudioMetadata metadata);

        /// <summary>
        /// Downloads cover art image from URL
        /// </summary>
        /// <param name="coverArtUrl">URL of the cover art image</param>
        /// <returns>Image data as byte array or null if failed</returns>
        Task<byte[]?> DownloadCoverArtAsync(string coverArtUrl);
    }

    /// <summary>
    /// Handles file processing, organization, and validation for completed downloads
    /// </summary>
    public interface IFileProcessingService
    {
        /// <summary>
        /// Processes a completed download (moves files, applies metadata)
        /// </summary>
        /// <param name="downloadId">The download ID to process</param>
        Task ProcessCompletedDownloadAsync(string downloadId);

        /// <summary>
        /// Organizes a file using configured naming patterns
        /// </summary>
        /// <param name="sourceFilePath">Source file path</param>
        /// <param name="metadata">Audiobook metadata</param>
        /// <returns>Final organized file path</returns>
        Task<string> OrganizeFileAsync(string sourceFilePath, AudioMetadata metadata);

        /// <summary>
        /// Validates that a file is a valid audiobook file
        /// </summary>
        /// <param name="filePath">Path to the file to validate</param>
        /// <returns>True if valid, false otherwise</returns>
        Task<bool> ValidateFileAsync(string filePath);

        /// <summary>
        /// Cleans up old temporary files
        /// </summary>
        Task CleanupTempFilesAsync();
    }

    /// <summary>
    /// Manages application configuration including APIs, download clients, and settings
    /// </summary>
    public interface IConfigurationService
    {
        /// <summary>
        /// Gets all API configurations (indexers and metadata sources)
        /// </summary>
        /// <returns>List of API configurations</returns>
        Task<List<ApiConfiguration>> GetApiConfigurationsAsync();

        /// <summary>
        /// Gets a specific API configuration by ID
        /// </summary>
        /// <param name="id">The API configuration ID</param>
        /// <returns>API configuration or null if not found</returns>
        Task<ApiConfiguration?> GetApiConfigurationAsync(string id);

        /// <summary>
        /// Saves or updates an API configuration
        /// </summary>
        /// <param name="config">The API configuration to save</param>
        /// <returns>The configuration ID</returns>
        Task<string> SaveApiConfigurationAsync(ApiConfiguration config);

        /// <summary>
        /// Deletes an API configuration
        /// </summary>
        /// <param name="id">The API configuration ID to delete</param>
        /// <returns>True if deleted successfully, false otherwise</returns>
        Task<bool> DeleteApiConfigurationAsync(string id);

        /// <summary>
        /// Gets all download client configurations
        /// </summary>
        /// <returns>List of download client configurations</returns>
        Task<List<DownloadClientConfiguration>> GetDownloadClientConfigurationsAsync();

        /// <summary>
        /// Gets a specific download client configuration by ID
        /// </summary>
        /// <param name="id">The download client configuration ID</param>
        /// <returns>Download client configuration or null if not found</returns>
        Task<DownloadClientConfiguration?> GetDownloadClientConfigurationAsync(string id);

        /// <summary>
        /// Saves or updates a download client configuration
        /// </summary>
        /// <param name="config">The download client configuration to save</param>
        /// <returns>The configuration ID</returns>
        Task<string> SaveDownloadClientConfigurationAsync(DownloadClientConfiguration config);

        /// <summary>
        /// Deletes a download client configuration
        /// </summary>
        /// <param name="id">The download client configuration ID to delete</param>
        /// <returns>True if deleted successfully, false otherwise</returns>
        Task<bool> DeleteDownloadClientConfigurationAsync(string id);

        /// <summary>
        /// Gets the application settings
        /// </summary>
        /// <returns>Application settings</returns>
        Task<ApplicationSettings> GetApplicationSettingsAsync();

        /// <summary>
        /// Saves the application settings
        /// </summary>
        /// <param name="settings">The settings to save</param>
        Task SaveApplicationSettingsAsync(ApplicationSettings settings);

        /// <summary>
        /// Gets the startup configuration
        /// </summary>
        /// <returns>Startup configuration</returns>
        Task<StartupConfig> GetStartupConfigAsync();

        /// <summary>
        /// Saves the startup configuration
        /// </summary>
        /// <param name="config">The startup configuration to save</param>
        Task SaveStartupConfigAsync(StartupConfig config);

        /// <summary>
        /// Gets all webhook configurations
        /// </summary>
        /// <returns>List of webhook configurations</returns>
        Task<List<WebhookConfiguration>> GetWebhookConfigurationsAsync();
    }

    /// <summary>
    /// Sends notifications via configured channels (webhooks, email, etc.)
    /// </summary>
    public interface INotificationService
    {
        /// <summary>
        /// Sends a notification when a download completes successfully
        /// </summary>
        /// <param name="download">The completed download</param>
        Task SendDownloadCompletedNotificationAsync(Download download);

        /// <summary>
        /// Sends a notification when a download fails
        /// </summary>
        /// <param name="download">The failed download</param>
        /// <param name="error">The error message</param>
        Task SendDownloadFailedNotificationAsync(Download download, string error);

        /// <summary>
        /// Sends a general system notification
        /// </summary>
        /// <param name="title">Notification title</param>
        /// <param name="message">Notification message</param>
        Task SendSystemNotificationAsync(string title, string message);

        /// <summary>
        /// Sends a notification to a specific webhook
        /// </summary>
        /// <param name="trigger">The event trigger name</param>
        /// <param name="data">The notification data payload</param>
        /// <param name="webhookUrl">The webhook URL to send to</param>
        /// <param name="enabledTriggers">List of enabled triggers for this webhook</param>
        Task SendNotificationAsync(string trigger, object data, string webhookUrl, List<string> enabledTriggers);
    }

    /// <summary>
    /// Converts ISBNs to Amazon ASINs for metadata lookup
    /// </summary>
    public interface IAmazonAsinService
    {
        /// <summary>
        /// Gets the Amazon ASIN for a given ISBN
        /// </summary>
        /// <param name="isbn">The ISBN to convert</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Tuple containing success flag, ASIN, and optional error message</returns>
        Task<(bool Success, string? Asin, string? Error)> GetAsinFromIsbnAsync(string isbn, CancellationToken ct = default);

        /// <summary>
        /// Verify whether a given ASIN's product page contains the provided ISBN.
        /// Returns a tuple indicating whether the verification request succeeded and whether the ISBN was found on the product page.
        /// </summary>
        /// <param name="asin">ASIN to verify</param>
        /// <param name="isbn">ISBN digits to look for</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Tuple (Success, MatchesIsbn, Error)</returns>
        Task<(bool Success, bool MatchesIsbn, string? Error)> VerifyAsinContainsIsbnAsync(string asin, string isbn, CancellationToken ct = default);
    }

    /// <summary>
    /// Searches Open Library for book metadata
    /// </summary>
    public interface IOpenLibraryService
    {
        /// <summary>
        /// Gets ISBNs for a book by title and author
        /// </summary>
        /// <param name="title">Book title</param>
        /// <param name="author">Optional author name</param>
        /// <returns>List of ISBNs</returns>
        Task<List<string>> GetIsbnsForTitleAsync(string title, string? author = null);

        /// <summary>
        /// Searches for books in Open Library
        /// </summary>
        /// <param name="title">Book title</param>
        /// <param name="author">Optional author name</param>
        /// <param name="limit">Maximum number of results</param>
        /// <returns>Search response with book results</returns>
        Task<OpenLibrarySearchResponse> SearchBooksAsync(string title, string? author = null, int limit = 10);
    }

    /// <summary>
    /// Generates file paths using configured naming patterns
    /// </summary>
    public interface IFileNamingService
    {
        /// <summary>
        /// Apply the configured file naming pattern to generate the final file path
        /// </summary>
        /// <param name="metadata">Audiobook metadata</param>
        /// <param name="diskNumber">Optional disk/part number</param>
        /// <param name="chapterNumber">Optional chapter number</param>
        /// <param name="originalExtension">File extension (e.g., ".m4b", ".mp3")</param>
        /// <returns>Full file path using the naming pattern</returns>
        Task<string> GenerateFilePathAsync(AudioMetadata metadata, int? diskNumber = null, int? chapterNumber = null, string originalExtension = ".m4b");

        /// <summary>
        /// Apply the configured file naming pattern to generate the final file path with a specific output path
        /// </summary>
        /// <param name="metadata">Audiobook metadata</param>
        /// <param name="outputPath">Specific output path to use</param>
        /// <param name="diskNumber">Optional disk/part number</param>
        /// <param name="chapterNumber">Optional chapter number</param>
        /// <param name="originalExtension">File extension (e.g., ".m4b", ".mp3")</param>
        /// <returns>Full file path using the naming pattern</returns>
        Task<string> GenerateFilePathAsync(AudioMetadata metadata, string outputPath, int? diskNumber = null, int? chapterNumber = null, string originalExtension = ".m4b");

        /// <summary>
        /// Parse a naming pattern and replace variables with actual values
        /// </summary>
        /// <param name="pattern">The naming pattern template</param>
        /// <param name="variables">Dictionary of variable values</param>
        /// <param name="treatAsFilename">Whether to treat as filename (sanitize invalid chars)</param>
        /// <returns>Final path with variables replaced</returns>
        string ApplyNamingPattern(string pattern, Dictionary<string, object> variables, bool treatAsFilename = false);
    }

    /// <summary>
    /// Manages audio file metadata extraction and database tracking
    /// </summary>
    public interface IAudioFileService
    {
        /// <summary>
        /// Ensure an AudiobookFile record exists for the given audiobook and file path. Extract metadata (ffprobe/taglib) and persist file-level metadata.
        /// Returns true if a new record was created, false if it already existed.
        /// </summary>
        /// <param name="audiobookId">The audiobook ID</param>
        /// <param name="filePath">Path to the audio file</param>
        /// <param name="source">Optional source identifier (e.g., "scan", "import")</param>
        /// <returns>True if a new record was created, false if it already existed</returns>
        Task<bool> EnsureAudiobookFileAsync(int audiobookId, string filePath, string? source = "scan");
    }

    /// <summary>
    /// Import service responsible for moving/copying completed download files into the library/output
    /// and registering audiobook file entries. This centralizes naming, directory creation and
    /// file operations so manual and automatic imports share the same behavior.
    /// </summary>
    public interface IImportService
    {
        /// <summary>
        /// Import a single completed file
        /// </summary>
        Task<ImportResult> ImportSingleFileAsync(string downloadId, int? audiobookId, string sourcePath, ApplicationSettings settings, CancellationToken ct = default);

        /// <summary>
        /// Import multiple files from a directory (multi-file import)
        /// </summary>
        Task<List<ImportResult>> ImportFilesFromDirectoryAsync(string downloadId, int? audiobookId, IEnumerable<string> files, ApplicationSettings settings, CancellationToken ct = default);

        /// <summary>
        /// Reprocess an existing file path (useful for reprocessing completed downloads)
        /// </summary>
        Task<ImportResult> ReprocessExistingFileAsync(string downloadId, int? audiobookId, string sourcePath, ApplicationSettings settings, CancellationToken ct = default);
    }
}
