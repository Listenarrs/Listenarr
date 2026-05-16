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
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models;

namespace Listenarr.Tests.Mocks
{
    /// <summary>
    /// Lightweight test implementation of IDownloadClientGateway used by unit tests.
    /// It attempts to use a provided HttpClient/IHttpClientFactory to handle simple SABnzbd
    /// queue/history requests so tests that register a DelegatingHandlerMock will work.
    /// For other operations it returns conservative defaults.
    /// </summary>
    public class DownloadClientGatewayMock : IDownloadClientGateway
    {
        public List<string> SourceFiles { get; set; } = [];

        public Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            return Task.FromResult((true, "ok"));
        }

        public Task<string?> AddAsync(DownloadClientConfiguration client, SearchResult result, CancellationToken ct = default)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            return Task.FromResult(false);
        }

        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            return [];
        }

        public async Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
        {
            return [];
        }

        public Task<bool> MarkItemAsImportedAsync(DownloadClientConfiguration client, string downloadId, CancellationToken ct = default)
        {
            return Task.FromResult(true);
        }

        public Task<QueueItem> GetQueueItemAsync(DownloadClientConfiguration client, Download download, QueueItem queueItem, CancellationToken ct = default)
        {
            queueItem.SourceFiles = SourceFiles;
            queueItem.LocalPath = download.DownloadPath;

            return Task.FromResult(queueItem);
        }

        public async Task<List<Download>> FetchDownloadsAsync(DownloadClientConfiguration client, List<Download> downloads, CancellationToken cancellationToken = default)
        {
            return [];
        }
    }
}
