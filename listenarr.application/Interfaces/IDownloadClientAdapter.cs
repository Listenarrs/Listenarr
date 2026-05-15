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
using Listenarr.Domain.Models;

namespace Listenarr.Application.Interfaces
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

        /// <summary>
        /// Returns an identifier for the download
        /// </summary>
        /// <param name="client"></param>
        /// <param name="result"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        Task<string?> AddAsync(DownloadClientConfiguration client, SearchResult result, CancellationToken ct = default);

        Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default);

        /// <summary>
        /// Give a list of ongoing download as queue items, each of them should respect the same constraint as for GetImportItemAsync
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
        /// Retrieves the information about a given download as a queue item
        /// The adapter should return:
        /// - Either a list of files under SourceFiles
        /// - Either a ContentPath that can be a file or a directory if the adapter is unable to tell
        /// </summary>
        Task<QueueItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            Download download,
            QueueItem queueItem,
            QueueItem? previousAttempt = null,
            CancellationToken ct = default);

        /// <summary>
        /// Marks a download as imported in the client (e.g., changes torrent category to post-import category).
        /// Called after a successful import to allow the client to differentiate imported vs active downloads.
        /// Default implementation is a no-op for clients that don't support this feature.
        /// </summary>
        /// <param name="client">Download client configuration</param>
        /// <param name="downloadId">The client-specific download ID (torrent hash, NZB ID, etc.)</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>True if the operation succeeded or was a no-op</returns>
        Task<bool> MarkItemAsImportedAsync(DownloadClientConfiguration client, string downloadId, CancellationToken ct = default)
            => Task.FromResult(true); // Default no-op

        /// <summary>
        /// Given a list of downloads, fetch updates from the given client
        /// This only returns updated values, it does not persists them!
        /// </summary>
        /// <param name="client">Download client configuration</param>
        /// <param name="downloads">List of downloads to be updated</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task<List<Download>> FetchDownloadsAsync(DownloadClientConfiguration client, List<Download> downloads, CancellationToken cancellationToken = default);
    }
}
