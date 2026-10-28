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
    /// Gateway to handle calls to specific IDownloadClientAdapter
    /// </summary>
    /// <see cref="IDownloadClientAdapter"/>
    public interface IDownloadClientGateway
    {
        Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default);

        Task<DownloadClientSubmissionResult> AddAsync(
            DownloadClientConfiguration client,
            PreparedDownloadSubmission submission,
            CancellationToken ct = default);

        Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default);

        /// <summary>
        /// Given a download client, fetch all ongoing downloads in it
        /// </summary>
        /// <param name="client"></param>
        /// <param name="ct"></param>
        /// <returns>List of ongoing items in the download client related to listenarr downloads</returns>
        Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default);

        Task<QueueItem> GetQueueItemAsync(
            DownloadClientConfiguration client,
            Download download,
            QueueItem queueItem,
            CancellationToken ct = default);

        Task<bool> MarkItemAsImportedAsync(DownloadClientConfiguration client, Download download, CancellationToken ct = default);

        /// <summary>
        /// Given a list of downloads, update their data based on reported download client information
        /// </summary>
        /// <param name="client"></param>
        /// <param name="downloads">List of downloads to update</param>
        /// <param name="cancellationToken"></param>
        /// <returns>Same list with updated values</returns>
        Task<List<Download>> FetchDownloadsAsync(DownloadClientConfiguration client, List<Download> downloads, CancellationToken cancellationToken = default);
    }
}
