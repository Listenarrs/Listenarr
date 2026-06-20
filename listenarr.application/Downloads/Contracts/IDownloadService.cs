
namespace Listenarr.Application.Downloads.Contracts
{
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

        Task<string> SendToDownloadClientAsync(
            TrustedDownloadCandidate candidate,
            string? downloadClientId = null,
            int? audiobookId = null);

        /// <summary>
        /// Removes a download from the queue
        /// </summary>
        /// <param name="downloadId">The download ID to remove</param>
        /// <param name="downloadClientId">Optional specific client ID</param>
        /// <param name="force">If true, removes from database even if client removal fails</param>
        /// <returns>True if removed successfully, false otherwise</returns>
        Task<bool> RemoveFromQueueAsync(string downloadId, string? downloadClientId = null, bool force = false);

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
        /// Retrieve cached torrent bytes and filename for a given download id if available
        /// </summary>
        /// <param name="downloadId">The download ID to look up</param>
        Task<(byte[]? Bytes, string? FileName)> GetCachedTorrentAsync(string downloadId);

        /// <summary>
        /// Retrieve cached announce URLs for a given download id if available
        /// </summary>
        /// <param name="downloadId">The download ID to look up</param>
        Task<List<string>?> GetCachedAnnouncesAsync(string downloadId);

        /// <summary>
        /// Allows to modify a download by giving an updated one
        /// </summary>
        /// <param name="download">The download with updated informations</param>
        Task UpdateAsync(Download download);
    }
}
