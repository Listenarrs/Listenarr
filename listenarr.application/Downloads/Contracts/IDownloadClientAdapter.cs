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

namespace Listenarr.Application.Downloads.Contracts
{
    /// <summary>
    /// Encapsulates all download-client specific operations. Implement an adapter per client to keep
    /// protocol details isolated from the orchestration layer.
    /// Regarding QueueItem:
    /// - Progress is display telemetry in the range 0 to 100 by convention. Completion
    ///   must come from an explicit terminal client state or reliable byte telemetry
    ///   showing no data remains.
    /// - SourceFiles is the source of truth for downloaded files, if it cannot be determined,
    ///   ContentPath should be used instead (as a path either being a directory or a single file),
    ///   gateway will transform that as a SourceFiles list
    /// - Only remote path should be returned, gateway handles the local path mapping
    /// - The adapter must define an external ID used to link listenarr downloads 1-to-1 with
    ///   download client entries/items
    /// </summary>
    public interface IDownloadClientAdapter
    {
        string ClientType { get; }

        DownloadProtocol Protocol { get; }

        List<DownloadProtocol> Protocols => [Protocol];

        Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default);

        /// <summary>
        /// Submit a client-backed download and return the external client item ID.
        /// A successful adapter submission without an external ID is invalid because
        /// active client downloads must be monitorable and reconcilable against the
        /// client queue/history. Direct downloads do not use this adapter contract;
        /// they are internally tracked with DownloadClientId == "DDL".
        /// </summary>
        /// <param name="client"></param>
        /// <param name="submission"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        Task<DownloadClientSubmissionResult> AddAsync(
            DownloadClientConfiguration client,
            PreparedDownloadSubmission submission,
            CancellationToken ct = default);

        Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default);

        /// <summary>
        /// Fetch the full live queue snapshot from the given client.
        /// </summary>
        /// <param name="client">Download client configuration</param>
        /// <param name="ct"></param>
        /// <returns>List of live queue items reported by the client</returns>
        Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default);

        /// <summary>
        /// Given a list of IDs, fetch updates from the given client
        /// </summary>
        /// <param name="client">Download client configuration</param>
        /// <param name="ids">List of IDs to get updates from</param>
        /// <param name="ct"></param>
        /// <returns>List of updated values for the given IDs</returns>
        Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, List<string> ids, CancellationToken ct = default);

        Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default);

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
        /// <param name="id">The client-specific download ID (torrent hash, NZB ID, etc.)</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>True if the operation succeeded or was a no-op</returns>
        Task<bool> MarkItemAsImportedAsync(DownloadClientConfiguration client, string id, CancellationToken ct = default)
            => Task.FromResult(true); // Default no-op
    }
}
