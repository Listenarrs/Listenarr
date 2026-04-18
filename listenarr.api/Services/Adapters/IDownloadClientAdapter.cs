using Listenarr.Infrastructure.Models;

namespace Listenarr.Api.Services.Adapters
{
    /// <summary>
    /// Encapsulates all download-client specific operations. Implement an adapter per client to keep
    /// protocol details isolated from the orchestration layer.
    /// Follows IDownloadClient pattern for consistency.
    /// </summary>
    public interface IDownloadClientAdapter
    {
        /// <summary>
        /// Name of the software
        /// </summary>
        string ClientType { get; }
        /// <summary>
        /// Download protocols supported by this software
        /// </summary>
        List<DownloadProtocol> Protocols { get; }
        
        /// <summary>
        /// Specific adapter setup for the NET framework
        /// Can be used to define HTTP layer behavior like retries, error handling, ...
        /// </summary>
        static void AddToServices(IServiceCollection services) {}
        
        /// <summary>
        /// Specific test to ensure the configuration allows Listenarr to query the adapter
        /// </summary>
        Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default);
        
        /// <summary>
        /// Allows to start a transfer in the given download client based on a search result
        /// </summary>
        Task<Download?> AddAsync(DownloadClientConfiguration client, IndexerSearchResult result, CancellationToken ct = default);
        
        /// <summary>
        /// Removes a transfer from the download client
        /// </summary>
        Task<bool> RemoveAsync(DownloadClientConfiguration client, Download download, bool deleteFiles = false, CancellationToken ct = default);
        
        /// <summary>
        /// Returns the list of ongoing transfers and their status from the download client
        /// The list should only contains transfers that are related to ongoing download in Listenarr
        /// </summary>
        Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default);

        /// <summary>
        /// Resolves the actual import item for a completed download.
        /// Called just before import to ensure the most accurate path and metadata.
        /// Some clients (like qBittorrent) require additional queries to determine final paths.
        /// </summary>
        /// <param name="client">Download client configuration</param>
        /// <param name="item">The download client item to resolve</param>
        /// <param name="previousAttempt">Previous import attempt for retry scenarios (can be null)</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Updated item with resolved OutputPath, or original if unable to determine</returns>
        Task<DownloadClientItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            DownloadClientItem item,
            DownloadClientItem? previousAttempt = null,
            CancellationToken ct = default);

        /// <summary>
        /// Marks a download as imported in the client (e.g., changes torrent category to post-import category).
        /// Called after a successful import to allow the client to differentiate imported vs active downloads.
        /// Default implementation is a no-op for clients that don't support this feature.
        /// </summary>
        /// <param name="client">Download client configuration</param>
        /// <param name="download">The client-specific download informations (torrent hash, NZB ID, etc.)</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>True if the operation succeeded or was a no-op</returns>
        Task<bool> MarkItemAsImportedAsync(DownloadClientConfiguration client, Download download, CancellationToken ct = default)
            => Task.FromResult(true); // Default no-op
        
        /// <summary>
        /// Poll every monitored download in the download client and trigger actions based on the updated statuses
        /// Returns a list of updated downloads
        /// </summary>
        public Task<List<Download>> PollAsync(
            DownloadMonitorService service,
            DownloadClientConfiguration client,
            List<Download> downloads,
            ApplicationSettings appSettings,
            CancellationToken ct = default);
        
        [Obsolete("Will be moved to SABnzbd/NZBGet implementations", false)]
        Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default);
        
        /// <summary>
        /// Legacy method (backward compatible)
        /// Returns the list of ongoing transfers and their status from the download client
        /// The list should only contains transfers that are related to ongoing download in Listenarr
        /// </summary>
        [Obsolete("Use GetItemsAsync instead.", false)]
        Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default);
        
        /// <summary>
        /// Legacy method for backward compatibility
        /// Return informations about a specific ongoing download
        /// </summary>
        [Obsolete("Use GetImportItemAsync using DownloadClientItem instead", false)]
        Task<QueueItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            Download download,
            QueueItem queueItem,
            QueueItem? previousAttempt = null,
            CancellationToken ct = default);
    }
}
