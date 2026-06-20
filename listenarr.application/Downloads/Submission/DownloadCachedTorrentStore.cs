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

using Listenarr.Application.Common;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Submission
{
    public sealed class DownloadCachedTorrentStore
    {
        private static readonly TimeSpan CacheSlidingExpiration = TimeSpan.FromMinutes(30);

        private readonly IMemoryCache _cache;
        private readonly ILogger<DownloadCachedTorrentStore> _logger;

        public DownloadCachedTorrentStore(IMemoryCache cache, ILogger<DownloadCachedTorrentStore> logger)
        {
            _cache = cache;
            _logger = logger;
        }

        public Task<(byte[]? Bytes, string? FileName)> GetCachedTorrentAsync(string downloadId)
        {
            var cacheKey = BuildCacheKey(downloadId);
            var bytes = _cache.Get<byte[]>(cacheKey + ":bytes");
            var name = _cache.Get<string>(cacheKey + ":name");
            return Task.FromResult((bytes, name));
        }

        public Task<List<string>?> GetCachedAnnouncesAsync(string downloadId)
        {
            try
            {
                if (string.IsNullOrEmpty(downloadId)) return Task.FromResult<List<string>?>(null);

                var cacheKey = BuildCacheKey(downloadId);
                var announces = _cache.Get<List<string>>(cacheKey + ":announces");
                if (announces != null && announces.Count > 0)
                {
                    return Task.FromResult<List<string>?>(announces);
                }

                var bytes = _cache.Get<byte[]>(cacheKey + ":bytes");
                if (bytes != null)
                {
                    var extracted = MyAnonamouseHelper.ExtractAnnounceUrls(bytes);
                    if (extracted != null && extracted.Count > 0)
                    {
                        CacheAnnounces(downloadId, extracted);
                        return Task.FromResult<List<string>?>(extracted);
                    }
                }

                return Task.FromResult<List<string>?>(null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to retrieve cached announces for download {DownloadId} (non-fatal)", downloadId);
                return Task.FromResult<List<string>?>(null);
            }
        }

        public void CacheTorrent(string downloadId, byte[] torrentBytes, string fileName)
        {
            var cacheKey = BuildCacheKey(downloadId);
            var options = CreateOptions();
            _cache.Set(cacheKey + ":bytes", torrentBytes, options);
            _cache.Set(cacheKey + ":name", fileName, CreateOptions());
        }

        public void CacheAnnounces(string downloadId, List<string> announces)
        {
            var cacheKey = BuildCacheKey(downloadId);
            _cache.Set(cacheKey + ":announces", announces, CreateOptions());
        }

        public void LogCachedAnnounces(string title, IReadOnlyCollection<string>? announces)
        {
            var count = announces?.Count ?? 0;
            var unique = count > 0 ? string.Join(", ", announces?.Take(10) ?? Enumerable.Empty<string>()) : "(none)";
            _logger.LogInformation(
                "Cached MyAnonamouse torrent announces for '{Title}' - count={Count}: {Announces}",
                title,
                count,
                LogRedaction.RedactText(unique, LogRedaction.GetSensitiveValuesFromEnvironment()));
        }

        private static MemoryCacheEntryOptions CreateOptions()
        {
            return new MemoryCacheEntryOptions { SlidingExpiration = CacheSlidingExpiration };
        }

        private static string BuildCacheKey(string downloadId)
        {
            return $"mam:cachedtorrent:{downloadId}";
        }
    }
}
