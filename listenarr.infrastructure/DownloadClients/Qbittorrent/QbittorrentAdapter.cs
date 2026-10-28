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
using System.Text.Json;
using Listenarr.Infrastructure.Torrents;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Qbittorrent
{
    /// <summary>
    /// qBittorrent protocol facade.
    /// Keep client-specific workflows behind this adapter so every supported
    /// download client exposes the same thin IDownloadClientAdapter surface.
    /// </summary>
    public class QbittorrentAdapter : IDownloadClientAdapter
    {
        public string ClientId => DownloadClientTypes.Qbittorrent;
        public string ClientType => DownloadClientTypes.Qbittorrent;
        public DownloadProtocol Protocol => DownloadProtocol.Torrent;

        private readonly QbittorrentConnectionTester _connectionTester;
        private readonly QbittorrentAddWorkflow _addWorkflow;
        private readonly QbittorrentImportMarkerWorkflow _importMarkerWorkflow;
        private readonly QbittorrentRemovalWorkflow _removalWorkflow;
        private readonly QbittorrentQueueFetchWorkflow _queueFetchWorkflow;
        private readonly QbittorrentItemFetchWorkflow _itemFetchWorkflow;
        private readonly QbittorrentImportItemResolver _importItemResolver;

        internal QbittorrentAdapter(
            QbittorrentConnectionTester connectionTester,
            QbittorrentAddWorkflow addWorkflow,
            QbittorrentImportMarkerWorkflow importMarkerWorkflow,
            QbittorrentRemovalWorkflow removalWorkflow,
            QbittorrentQueueFetchWorkflow queueFetchWorkflow,
            QbittorrentItemFetchWorkflow itemFetchWorkflow,
            QbittorrentImportItemResolver importItemResolver)
        {
            _connectionTester = connectionTester ?? throw new ArgumentNullException(nameof(connectionTester));
            _addWorkflow = addWorkflow ?? throw new ArgumentNullException(nameof(addWorkflow));
            _importMarkerWorkflow = importMarkerWorkflow ?? throw new ArgumentNullException(nameof(importMarkerWorkflow));
            _removalWorkflow = removalWorkflow ?? throw new ArgumentNullException(nameof(removalWorkflow));
            _queueFetchWorkflow = queueFetchWorkflow ?? throw new ArgumentNullException(nameof(queueFetchWorkflow));
            _itemFetchWorkflow = itemFetchWorkflow ?? throw new ArgumentNullException(nameof(itemFetchWorkflow));
            _importItemResolver = importItemResolver ?? throw new ArgumentNullException(nameof(importItemResolver));
        }

        internal QbittorrentAdapter(IHttpClientFactory httpFactory, ITorrentFileDownloader torrentFileDownloader, ILogger<QbittorrentAdapter> logger)
        {
            ArgumentNullException.ThrowIfNull(httpFactory);
            _ = torrentFileDownloader ?? throw new ArgumentNullException(nameof(torrentFileDownloader));
            ArgumentNullException.ThrowIfNull(logger);

            var authSession = new QbittorrentAuthSession(logger);
            _connectionTester = new QbittorrentConnectionTester(httpFactory, logger, ClientType);
            _addWorkflow = new QbittorrentAddWorkflow(httpFactory, authSession, logger, ClientType);
            _importMarkerWorkflow = new QbittorrentImportMarkerWorkflow(httpFactory, logger, ClientType);
            _removalWorkflow = new QbittorrentRemovalWorkflow(httpFactory, logger, ClientType);
            _queueFetchWorkflow = new QbittorrentQueueFetchWorkflow(httpFactory, authSession, logger, ClientType);
            _itemFetchWorkflow = new QbittorrentItemFetchWorkflow(httpFactory, authSession, logger, ClientType);
            _importItemResolver = new QbittorrentImportItemResolver(logger);
        }

        public Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
            => _connectionTester.TestConnectionAsync(client, ct);

        public Task<DownloadClientSubmissionResult> AddAsync(
            DownloadClientConfiguration client,
            PreparedDownloadSubmission submission,
            CancellationToken ct = default)
            => _addWorkflow.AddAsync(client, submission, ct);

        public Task<bool> MarkItemAsImportedAsync(DownloadClientConfiguration client, string downloadId, CancellationToken ct = default)
            => _importMarkerWorkflow.MarkItemAsImportedAsync(client, downloadId, ct);

        public Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
            => _removalWorkflow.RemoveAsync(client, id, deleteFiles, ct);

        public Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, List<string> ids, CancellationToken ct = default)
            => _queueFetchWorkflow.GetQueueAsync(client, ids, ct);

        public Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
            => GetQueueAsync(client, [], ct);

        public Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
            => Task.FromResult(new List<(string Id, string Name)>());

        public Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default)
            => _itemFetchWorkflow.GetItemsAsync(client, ct);

        /// <summary>
        /// Get import item from DownloadClientItem.
        /// </summary>
        public Task<DownloadClientItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            DownloadClientItem item,
            DownloadClientItem? previousAttempt = null,
            CancellationToken ct = default)
            => _importItemResolver.GetImportItemAsync(client, item, ct);

        /// <summary>
        /// LEGACY: Resolves the actual import item for a completed download.
        /// Matches GetImportItem pattern.
        /// </summary>
        public Task<QueueItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            Download download,
            QueueItem queueItem,
            QueueItem? previousAttempt = null,
            CancellationToken ct = default)
            => _importItemResolver.GetImportItemAsync(client, download, queueItem, ct);

        internal static string ResolveTorrentContentPath(
            string savePath,
            List<Dictionary<string, JsonElement>> files)
        {
            return QbittorrentImportPathResolver.ResolveContentPath(savePath, files);
        }
    }
}
