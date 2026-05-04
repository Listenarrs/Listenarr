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
using Listenarr.Application.Security;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads
{
    /// <summary>
    /// Responsabilities:
    /// - Make sure any path reported by any download client adapter is mapped using adequate Remote Path Mapping
    /// - Single point of contact for any download client adapter, no download client adapter detail should be visible behind this
    /// - Persistence: Do not persist anything here, it's up to callers to know what they are doing
    /// </summary>
    public class DownloadClientGateway(
        IRemotePathMappingService remotePathMappingService,
        IDownloadClientAdapterFactory factory,
        ILogger<DownloadClientGateway> logger) : IDownloadClientGateway
    {
        private IDownloadClientAdapter ResolveAdapter(DownloadClientConfiguration client)
        {
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            var attemptedKeys = new List<string?> { client.Id, client.Type };
            foreach (var key in attemptedKeys)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                try
                {
                    return factory.GetByIdOrType(key);
                }
                catch (InvalidOperationException)
                {
                    // Try the next key.
                    continue;
                }
            }

            var descriptor = !string.IsNullOrWhiteSpace(client.Name)
                ? $"{client.Name} ({client.Type ?? "unknown"})"
                : client.Type ?? client.Id ?? "unknown";

            var message = $"No download client adapter registered for {LogRedaction.SanitizeText(descriptor)}.";
            logger.LogError(message);
            throw new InvalidOperationException(message);
        }

        public Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            return adapter.TestConnectionAsync(client, ct);
        }

        public async Task<string?> AddAsync(DownloadClientConfiguration client, SearchResult result, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            return await adapter.AddAsync(client, result, ct);
        }

        public Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            // TODO: Remove download from DB here
            var adapter = ResolveAdapter(client);
            return adapter.RemoveAsync(client, id, deleteFiles, ct);
        }

        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            var results = await adapter.GetQueueAsync(client, ct);

            List<QueueItem> translatedResults = [];
            foreach (QueueItem result in results)
            {
                translatedResults.Add(await TranslateQueueItemPathsAsync(client, result));
            }
            return translatedResults;
        }

        public Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            return adapter.GetRecentHistoryAsync(client, limit, ct);
        }

        public Task<bool> MarkItemAsImportedAsync(DownloadClientConfiguration client, string downloadId, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            return adapter.MarkItemAsImportedAsync(client, downloadId, ct);
        }

        public async Task<QueueItem> GetQueueItemAsync(
            DownloadClientConfiguration client,
            Download download,
            QueueItem queueItem,
            CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            var item = await adapter.GetImportItemAsync(client, download, queueItem, null, ct);

            return await TranslateQueueItemPathsAsync(client, item);
        }

        private async Task<QueueItem> TranslateQueueItemPathsAsync(DownloadClientConfiguration client, QueueItem item)
        {
            if (item.RemotePath != null)
            {
                item.LocalPath = await remotePathMappingService.TranslatePathAsync(client, item.RemotePath);
            }

            if (item.ContentPath != null)
            {
                item.ContentPath = await remotePathMappingService.TranslatePathAsync(client, item.ContentPath);
            }

            if (item.SourceFiles != null)
            {
                List<string> sourceFiles = [];
                foreach (string file in item.SourceFiles)
                {
                    sourceFiles.Add(await remotePathMappingService.TranslatePathAsync(client, file));
                }
                item.SourceFiles = sourceFiles;
            }
            else if (item.ContentPath != null)
            {
                // We will try to scan for source files
                // Scan content path: Some client only knows about the directory where the download is put
                if (File.Exists(item.ContentPath))
                {
                    item.SourceFiles = [item.ContentPath];
                }
                else
                {
                    item.SourceFiles = [.. Directory.EnumerateFiles(item.ContentPath, "*.*", SearchOption.AllDirectories)];
                }
            }

            return item;
        }

        public async Task<List<Download>> FetchDownloadsAsync(DownloadClientConfiguration client, List<Download> downloads, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            downloads = await adapter.FetchDownloadsAsync(client, downloads, ct);
            foreach (Download download in downloads)
            {
                download.DownloadPath = await remotePathMappingService.TranslatePathAsync(client, download.DownloadPath);
            }
            return downloads;
        }
    }
}
