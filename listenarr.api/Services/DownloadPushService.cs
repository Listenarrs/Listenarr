using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Listenarr.Api.Hubs;
using Listenarr.Domain.Models;

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Service that receives pushed download updates from clients, broadcasts them
    /// and keeps a short-lived cache of recently pushed download ids so the poller
    /// can avoid re-broadcasting the same updates.
    /// </summary>
    public class DownloadPushService
    {
        private readonly IHubContext<DownloadHub> _hubContext;
        private readonly IMemoryCache _cache;
        private readonly ILogger<DownloadPushService> _logger;

        // Cache key prefix for pushed download ids
        private const string CachePrefix = "download_push_";

        // TTL for recent pushes (short, e.g. 10s)
        private readonly TimeSpan _recentPushTtl = TimeSpan.FromSeconds(10);

        public DownloadPushService(IHubContext<DownloadHub> hubContext, IMemoryCache cache, ILogger<DownloadPushService> logger)
        {
            _hubContext = hubContext;
            _cache = cache;
            _logger = logger;
        }

        /// <summary>
        /// Accept a pushed download, broadcast it to connected clients and record it in the recent cache.
        /// </summary>
        public async Task HandlePushAsync(Download download, CancellationToken cancellationToken = default)
        {
            if (download == null) return;

            try
            {
                // Broadcast the single download update to all clients
                // Construct a sanitized DTO that omits DownloadPath and removes client-local metadata
                var sanitizedMetadata = (download.Metadata ?? new Dictionary<string, object>())
                    .Where(kvp => !string.Equals(kvp.Key, "ClientContentPath", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!);

                var downloadDto = new
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

                _logger.LogInformation("Broadcasting pushed DownloadUpdate for {DownloadId} ({Status})", download.Id, download.Status);
                await _hubContext.Clients.All.SendAsync("DownloadUpdate", new[] { downloadDto }, cancellationToken);

                // Mark this download id as recently pushed
                var key = CachePrefix + download.Id;
                _cache.Set(key, true, _recentPushTtl);

                _logger.LogDebug("Handled pushed download {DownloadId} and cached for {Ttl}s", download.Id, _recentPushTtl.TotalSeconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error handling pushed download {DownloadId}", download.Id);
            }
        }

        /// <summary>
        /// Returns true if the given download id was recently pushed by a client.
        /// </summary>
        public bool WasRecentlyPushed(string? downloadId)
        {
            if (string.IsNullOrWhiteSpace(downloadId)) return false;
            var key = CachePrefix + downloadId;
            return _cache.TryGetValue(key, out _);
        }
    }
}


