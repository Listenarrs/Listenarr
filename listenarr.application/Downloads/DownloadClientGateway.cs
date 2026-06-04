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
using Listenarr.Domain.Common;
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
        internal IDownloadClientAdapter ResolveAdapter(DownloadClientConfiguration client)
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

            // TODO: Handle download persistence here instead of DownloadService
        }

        public Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            // FIXME: Responsability of removing the download from DB should be here
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

        public async Task<bool> MarkItemAsImportedAsync(DownloadClientConfiguration client, Download download, CancellationToken ct = default)
        {
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            if (download == null)
            {
                throw new ArgumentNullException(nameof(download));
            }

            var externalId = download.GetExternalId();
            if (string.IsNullOrEmpty(externalId))
            {
                return true;
            }

            if (!client.IsEnabled)
            {
                logger.LogDebug(
                    "Skipping mark imported for download {DownloadId}: download client {ClientId} is disabled",
                    download.Id,
                    client.Id);
                return true;
            }

            var adapter = ResolveAdapter(client);
            return await adapter.MarkItemAsImportedAsync(client, externalId, ct);
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

        /// <summary>
        /// Handles path mapping of queue item
        /// Make sure all path are localy accessible after processing and
        /// that a proper list of sanitized source files is produced
        /// </summary>
        /// <param name="client">Download client configuration to use for path mapping</param>
        /// <param name="item">Queue item to translate/sanitize</param>
        /// <returns></returns>
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

            // FIXME: https://github.com/Listenarrs/Listenarr/issues/592
            // We havent yet decided of the responsibility of download client adapter
            // As a result, we cannot assume an empty sourceFiles means there are no source files downloaded
            // and so, we try to populate it as if it was null
            // When the issue is tackled, we might want to keep the empty list when the adapter gives an empty list
            if (item.SourceFiles != null && item.SourceFiles.Count > 0)
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
                // Scan content path: Some clients are not able to tell if they have a file or a directory downloaded
                // So we make sure it's either one or the other and log if it's not
                if (File.Exists(item.ContentPath))
                {
                    item.SourceFiles = [item.ContentPath];
                }
                else
                {
                    // We will try to scan for source files
                    try
                    {
                        item.SourceFiles = [.. Directory
                            .EnumerateFiles(item.ContentPath, "*.*", SearchOption.AllDirectories)
                            .Select(f => FileUtils.NormalizeStoredPath(f))];
                    }
                    catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                    {
                        logger.LogWarning($"Download client {client.Id} reported no source files and content path scanning failed for item {item.Title} with path {item.ContentPath}");
                        logger.LogDebug($"Reason: {exception.Message}");
                        item.SourceFiles = [];
                    }
                }
            }
            else
            {
                logger.LogWarning($"Download client {client.Id} reported no source files and no content path for item {item.Title}");
                item.SourceFiles = [];
            }

            // Remove duplicates if any
            item.SourceFiles = new HashSet<string>(item.SourceFiles, StringComparer.OrdinalIgnoreCase).ToList();

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
