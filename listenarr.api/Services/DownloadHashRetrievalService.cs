/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2025 Robbie Davis
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Api.Services.Adapters;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Services
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
        private readonly DownloadHistoryRepository _historyRepo;
        private readonly Dictionary<string, IDownloadClientAdapter> _adapters;

        // Retry configuration with exponential backoff
        private const int MaxRetries = 10;
        private const int MaxBackoffSeconds = 30;
        private const int BaseBackoffSeconds = 2;

        public DownloadHashRetrievalService(
            ILogger<DownloadHashRetrievalService> logger,
            DownloadHistoryRepository historyRepo,
            IDownloadClientAdapter qbittorrentAdapter,
            IDownloadClientAdapter transmissionAdapter,
            IDownloadClientAdapter sabnzbdAdapter,
            IDownloadClientAdapter nzbgetAdapter)
        {
            _logger = logger;
            _historyRepo = historyRepo;

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
                    AreTitlesSimilar(item.Title, query.Title));

                if (match != null && !string.IsNullOrEmpty(match.DownloadId))
                {
                    _logger.LogInformation(
                        "✅ Hash retrieval successful (retry {RetryCount}): DownloadId={DownloadId}, Title={Title}, Match={MatchTitle}",
                        query.RetryCount + 1, match.DownloadId, query.Title, match.Title);

                    // Record successful retrieval in history
                    await _historyRepo.AddAsync(new DownloadHistory
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
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex,
                    "Error during hash retrieval (retry {RetryCount}): Title={Title}, Client={Client}",
                    query.RetryCount + 1, query.Title, client.Name);
                return null;
            }
        }

        /// <summary>
        /// Check if two titles are similar (normalized comparison)
        /// </summary>
        private bool AreTitlesSimilar(string title1, string title2)
        {
            if (string.IsNullOrWhiteSpace(title1) || string.IsNullOrWhiteSpace(title2))
                return false;

            var norm1 = NormalizeTitle(title1);
            var norm2 = NormalizeTitle(title2);

            // Exact match after normalization
            if (string.Equals(norm1, norm2, StringComparison.OrdinalIgnoreCase))
                return true;

            // Fuzzy match with Levenshtein distance (25% threshold)
            var distance = ComputeLevenshteinDistance(norm1, norm2);
            var maxLength = Math.Max(norm1.Length, norm2.Length);
            var similarity = 1.0 - (double)distance / maxLength;

            return similarity >= 0.75; // 75% similarity threshold
        }

        /// <summary>
        /// Normalize title for comparison (remove special chars, extra spaces, etc.)
        /// </summary>
        private string NormalizeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return string.Empty;

            // Remove special characters, normalize spaces
            var normalized = System.Text.RegularExpressions.Regex.Replace(title, @"[^\w\s]", " ");
            normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+", " ");
            return normalized.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Compute Levenshtein distance between two strings
        /// </summary>
        private int ComputeLevenshteinDistance(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1)) return s2?.Length ?? 0;
            if (string.IsNullOrEmpty(s2)) return s1.Length;

            var d = new int[s1.Length + 1, s2.Length + 1];

            for (int i = 0; i <= s1.Length; i++)
                d[i, 0] = i;
            for (int j = 0; j <= s2.Length; j++)
                d[0, j] = j;

            for (int j = 1; j <= s2.Length; j++)
            {
                for (int i = 1; i <= s1.Length; i++)
                {
                    var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }

            return d[s1.Length, s2.Length];
        }

        /// <summary>
        /// Get downloads that need hash retrieval (grabbed but no DownloadId yet)
        /// </summary>
        public async Task<List<DownloadClientItemQuery>> GetPendingHashRetrievalsAsync(CancellationToken ct = default)
        {
            // Get all pending imports (grabbed but not imported)
            var pendingImports = await _historyRepo.GetPendingImportsAsync(ct);

            var queries = new List<DownloadClientItemQuery>();

            foreach (var history in pendingImports)
            {
                // Only process if we don't have a valid DownloadId yet
                // (or if the DownloadId looks like a temporary placeholder)
                if (string.IsNullOrEmpty(history.DownloadId) || 
                    history.DownloadId.StartsWith("temp-") ||
                    history.DownloadId.Length < 10)
                {
                    // Calculate retry count from history events
                    var allEvents = await _historyRepo.GetByDownloadIdAsync(history.DownloadId, ct);
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
            }

            return queries;
        }
    }
}

