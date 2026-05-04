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
using Listenarr.Application.Notification;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.SignalR
{
    public class DownloadPushService(
        IHubContext<DownloadHub> hubContext,
        IMemoryCache cache,
        ILogger<DownloadPushService> logger) : IDownloadPushService
    {
        // Cache key prefix for pushed download ids
        private const string CachePrefix = "download_push_";

        // TTL for recent pushes (short, e.g. 10s)
        private readonly TimeSpan _recentPushTtl = TimeSpan.FromSeconds(10);

        private static string GetCacheEntry(Download download)
        {
            return CachePrefix + download.Id;
        }

        private Object DownloadToDto(Download download)
        {
            // Broadcast the single download update to all clients
            // Construct a sanitized DTO that omits DownloadPath and removes client-local metadata
            var sanitizedMetadata = (download.Metadata ?? new Dictionary<string, object>())
                .Where(kvp => !string.Equals(kvp.Key, "ClientContentPath", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!);

            return new
            {
                id = download.Id,
                audiobookId = download.AudiobookId,
                title = download.Title,
                artist = download.Artist,
                album = download.Album,
                originalUrl = download.OriginalUrl,
                status = download.Status.ToString(),
                progress = download.Progress,
                totalSize = download.TotalSize,
                downloadedSize = download.DownloadedSize,
                finalPath = download.FinalPath,
                startedAt = download.StartedAt,
                completedAt = download.CompletedAt,
                errorMessage = download.ErrorMessage,
                downloadClientId = download.DownloadClientId,
                metadata = sanitizedMetadata
            };
        }

        public async Task HandlePushAsync(Download download, CancellationToken cancellationToken = default)
        {
            if (cache.Get(GetCacheEntry(download)) == null)
            {
                return;
            }

            try
            {
                var downloadDto = DownloadToDto(download);

                logger.LogInformation("Broadcasting pushed DownloadUpdate for {DownloadId} ({Status})", download.Id, download.Status);
                await hubContext.Clients.All.SendAsync("DownloadUpdate", new[] { downloadDto }, cancellationToken);
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogError(exception, $"Error handling pushed download {download.Id}");
            }

            cache.Set(GetCacheEntry(download), true, _recentPushTtl);

            logger.LogDebug("Handled pushed download {DownloadId} and cached for {Ttl}s", download.Id, _recentPushTtl.TotalSeconds);
        }

        public async Task HandlePushAsync(List<Download> downloads, CancellationToken cancellationToken = default)
        {
            var sanitizedList = downloads
                .Where(download => cache.Get(GetCacheEntry(download)) != null)
                .Select(DownloadToDto)
                .ToList();

            if (sanitizedList.Count <= 0)
            {
                return;
            }

            logger.LogInformation($"Broadcasting DownloadsList with {sanitizedList.Count} items");

            try
            {
                await hubContext.Clients.All.SendAsync("DownloadsList", sanitizedList, cancellationToken);
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogError(exception, "Error handling pushed downloads");
            }

            downloads.ForEach(download =>
            {
                cache.Set(GetCacheEntry(download), true, _recentPushTtl);
            });
        }
    }
}


