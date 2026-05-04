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
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads
{
    /// <summary>
    /// Stage 4: Hash Retrieval Service with exponential backoff
    /// 
    /// Problem: When a torrent/NZB is sent to a download client, the hash/ID isn't
    /// immediately available. The client needs time to process the file.
    /// 
    /// Solution: Retry with exponential backoff:
    /// - 1st retry: 2 seconds after grab
    /// - 2nd retry: 4 seconds (2^2)
    /// - 3rd retry: 8 seconds (2^3)
    /// - 4th retry: 16 seconds (2^4)
    /// - 5th retry: 30 seconds (capped at 30s max)
    /// - Maximum 10 retries over 60 seconds total
    /// </summary>
    public class DownloadHashRetrievalService
    {
        private readonly ILogger<DownloadHashRetrievalService> _logger;
        private readonly IDownloadHistoryRepository _historyRepository;
        private readonly Dictionary<string, IDownloadClientAdapter> _adapters;

        // Retry configuration with exponential backoff
        private const int MaxRetries = 10;
        private const int MaxBackoffSeconds = 30;
        private const int BaseBackoffSeconds = 2;

        public DownloadHashRetrievalService(
            ILogger<DownloadHashRetrievalService> logger,
            IDownloadHistoryRepository historyRepository,
            IDownloadClientAdapter qbittorrentAdapter,
            IDownloadClientAdapter transmissionAdapter,
            IDownloadClientAdapter sabnzbdAdapter,
            IDownloadClientAdapter nzbgetAdapter)
        {
            _logger = logger;
            _historyRepository = historyRepository;

            // Map adapters by protocol type
            _adapters = new Dictionary<string, IDownloadClientAdapter>(StringComparer.OrdinalIgnoreCase)
            {
                ["qbittorrent"] = qbittorrentAdapter,
                ["transmission"] = transmissionAdapter,
                ["sabnzbd"] = sabnzbdAdapter,
                ["nzbget"] = nzbgetAdapter
            };
        }

        /// <summary>
        /// Attempt to retrieve download hash/ID from client for recently grabbed downloads
        /// Returns the DownloadId (hash) if found, null otherwise
        /// </summary>
        public async Task<string?> TryRetrieveHashAsync(
            DownloadClientItemQuery query,
            DownloadClientConfiguration client,
            CancellationToken ct = default)
        {
            if (query == null || client == null)
            {
                return null;
            }

            // Check if we've exceeded retry limits
            if (query.RetryCount >= MaxRetries)
            {
                _logger.LogWarning(
                    "Hash retrieval exceeded max retries ({MaxRetries}) for download: Title={Title}, Client={Client}",
                    MaxRetries, query.Title, client.Name);
                return null;
            }

            // Check if we're within the retry window (60 seconds from grab)
            var elapsed = DateTime.UtcNow - query.AddedDate;
            if (elapsed.TotalSeconds > 60)
            {
                _logger.LogWarning(
                    "Hash retrieval timeout (60s) exceeded for download: Title={Title}, Elapsed={Elapsed:F1}s",
                    query.Title, elapsed.TotalSeconds);
                return null;
            }

            // Calculate exponential backoff delay
            var backoffSeconds = Math.Min(
                MaxBackoffSeconds,
                BaseBackoffSeconds * Math.Pow(2, query.RetryCount));

            // Check if enough time has passed since last retry
            if (query.LastRetry.HasValue)
            {
                var timeSinceLastRetry = DateTime.UtcNow - query.LastRetry.Value;
                if (timeSinceLastRetry.TotalSeconds < backoffSeconds)
                {
                    _logger.LogDebug(
                        "Skipping hash retrieval - backoff not elapsed. Title={Title}, Retry={RetryCount}, NextIn={NextIn:F1}s",
                        query.Title, query.RetryCount, backoffSeconds - timeSinceLastRetry.TotalSeconds);
                    return null;
                }
            }

            // Get the appropriate adapter
            if (!_adapters.TryGetValue(client.Type, out var adapter))
            {
                _logger.LogWarning("No adapter found for client type: {ClientType}", client.Type);
                return null;
            }

            // Skip hash retrieval for disabled clients
            if (!client.IsEnabled)
            {
                _logger.LogDebug("Skipping hash retrieval for disabled client {ClientName}", client.Name);
                return null;
            }

            try
            {
                _logger.LogInformation(
                    "Attempting hash retrieval (retry {RetryCount}/{MaxRetries}) for: Title={Title}, Client={Client}, Backoff={Backoff:F1}s",
                    query.RetryCount + 1, MaxRetries, query.Title, client.Name, backoffSeconds);

                // Get all items from the client
                var items = await adapter.GetItemsAsync(client, ct);

                // Try to find our download by title/name
                var match = items.FirstOrDefault(item =>
                    string.Equals(item.Title, query.Title, StringComparison.OrdinalIgnoreCase) ||
                    TitleUtils.AreTitlesSimilarWithLevenstein(item.Title, query.Title));

                if (match != null && !string.IsNullOrEmpty(match.DownloadId))
                {
                    _logger.LogInformation(
                        "✅ Hash retrieval successful (retry {RetryCount}): DownloadId={DownloadId}, Title={Title}, Match={MatchTitle}",
                        query.RetryCount + 1, match.DownloadId, query.Title, match.Title);

                    // Record successful retrieval in history
                    await _historyRepository.AddAsync(new DownloadHistory
                    {
                        DownloadId = match.DownloadId,
                        EventType = DownloadHistoryEventType.Grabbed,
                        Status = match.Status,
                        EventDate = DateTime.UtcNow,
                        AudiobookId = query.AudiobookId,
                        DownloadClient = client.Name,
                        DownloadClientId = client.Id,
                        Protocol = query.Protocol,
                        Title = match.Title,
                        OutputPath = match.OutputPath,
                        Data = new Dictionary<string, object>
                        {
                            ["HashRetrievalAttempt"] = query.RetryCount + 1,
                            ["HashRetrievalElapsedSeconds"] = elapsed.TotalSeconds,
                            ["OriginalTitle"] = query.Title
                        }
                    }, ct);

                    return match.DownloadId;
                }

                _logger.LogDebug(
                    "Hash not found yet (retry {RetryCount}/{MaxRetries}): Title={Title}, ItemsChecked={ItemCount}",
                    query.RetryCount + 1, MaxRetries, query.Title, items.Count);

                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex,
                    "Error during hash retrieval (retry {RetryCount}): Title={Title}, Client={Client}",
                    query.RetryCount + 1, query.Title, client.Name);
                return null;
            }
        }

        /// <summary>
        /// Get downloads that need hash retrieval (grabbed but no DownloadId yet)
        /// </summary>
        public async Task<List<DownloadClientItemQuery>> GetPendingHashRetrievalsAsync(CancellationToken ct = default)
        {
            // Get all pending imports (grabbed but not imported)
            var pendingImports = await _historyRepository.GetPendingImportsAsync(ct);

            var queries = new List<DownloadClientItemQuery>();

            foreach (var history in pendingImports.Where(h =>
                // Only process if we don't have a valid DownloadId yet
                // (or if the DownloadId looks like a temporary placeholder)
                string.IsNullOrEmpty(h.DownloadId) ||
                h.DownloadId.StartsWith("temp-") ||
                h.DownloadId.Length < 10))
            {
                // Calculate retry count from history events
                var allEvents = await _historyRepository.GetByDownloadIdAsync(history.DownloadId, ct);
                var retryCount = allEvents.Count(e =>
                    e.EventType == DownloadHistoryEventType.Grabbed &&
                    e.Data != null &&
                    e.Data.ContainsKey("HashRetrievalAttempt"));

                var lastRetry = allEvents
                    .Where(e => e.EventType == DownloadHistoryEventType.Grabbed)
                    .OrderByDescending(e => e.EventDate)
                    .FirstOrDefault()?.EventDate;

                queries.Add(new DownloadClientItemQuery
                {
                    DownloadId = history.DownloadId,
                    Title = history.Title,
                    AudiobookId = history.AudiobookId,
                    AddedDate = history.EventDate,
                    DownloadClient = history.DownloadClient,
                    DownloadClientId = history.DownloadClientId,
                    Protocol = history.Protocol,
                    RetryCount = retryCount,
                    LastRetry = lastRetry
                });
            }

            return queries;
        }
    }
}

