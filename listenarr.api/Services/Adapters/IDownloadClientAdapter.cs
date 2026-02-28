using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Domain.Models;

namespace Listenarr.Api.Services.Adapters
{
    /// <summary>
    /// Encapsulates all download-client specific operations. Implement an adapter per client to keep
    /// protocol details isolated from the orchestration layer.
    /// Follows IDownloadClient pattern for consistency.
    /// </summary>
    public interface IDownloadClientAdapter
    {
        string ClientId { get; }
        string ClientType { get; }
        DownloadProtocol Protocol { get; }

        Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default);
        Task<string?> AddAsync(DownloadClientConfiguration client, SearchResult result, CancellationToken ct = default);
        Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default);
        
        /// <summary>
        /// Legacy method - returns QueueItem list (backward compatible)
        /// Will be deprecated in favor of GetItemsAsync
        /// </summary>
        Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default);
        
        /// <summary>
        /// Returns normalized DownloadClientItem list
        /// This is the preferred method going forward
        /// </summary>
        Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default);
        
        Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default);

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
        /// Legacy method for backward compatibility
        /// </summary>
        Task<QueueItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            Download download,
            QueueItem queueItem,
            QueueItem? previousAttempt = null,
            CancellationToken ct = default);
    }
}
