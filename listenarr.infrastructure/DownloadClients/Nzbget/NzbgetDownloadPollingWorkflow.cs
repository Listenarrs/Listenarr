/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text;
using System.Text.Json;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Nzbget
{
    internal sealed class NzbgetDownloadPollingWorkflow(
        IHttpClientFactory httpClientFactory,
        NzbgetHistoryReader historyReader,
        ILogger logger,
        TimeProvider timeProvider,
        string clientType)
    {
        private const long SlowHistoryThresholdMilliseconds = 2_000;
        private const string PollingSurface = "FetchDownloadsAsync";
        public async Task<List<Download>> FetchDownloadsAsync(
            DownloadClientConfiguration client,
            List<Download> downloads,
            CancellationToken cancellationToken)
        {
            logger.LogDebug("Polling NZBGet client {ClientName}", client.Name);
            try
            {
                var trackedById = BuildIdLookup(downloads);
                var matchedDownloads = new HashSet<Download>();
                var activeCanonicalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                await ProcessActiveAsync(
                    client,
                    trackedById,
                    downloads,
                    matchedDownloads,
                    activeCanonicalIds,
                    cancellationToken);

                await ProcessHistoryAsync(
                    client,
                    trackedById,
                    downloads,
                    matchedDownloads,
                    activeCanonicalIds,
                    cancellationToken);

                return downloads;
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                throw new DownloadClientAdapterPollingException(
                    $"Error polling NZBGet client {client.Id}",
                    exception);
            }
        }
        private async Task ProcessActiveAsync(
            DownloadClientConfiguration client,
            IReadOnlyDictionary<string, Download> trackedById,
            IReadOnlyList<Download> trackedDownloads,
            ISet<Download> matchedDownloads,
            ISet<string> activeCanonicalIds,
            CancellationToken cancellationToken)
        {
            var baseUrl = DownloadClientUriBuilder.BuildUri(client, "/jsonrpc");
            var http = httpClientFactory.CreateClient(clientType);
            http.DefaultRequestHeaders.Authorization = NzbgetAuthentication.BuildAuthHeader(client);

            var statusRequest = new
            {
                method = "status",
                id = 2
            };
            var statusJsonContent = JsonSerializer.Serialize(statusRequest);
            using var statusHttpContent = new StringContent(
                statusJsonContent,
                Encoding.UTF8,
                "application/json");
            using var statusResponse = await http.PostAsync(
                baseUrl,
                statusHttpContent,
                cancellationToken);

            if (!statusResponse.IsSuccessStatusCode)
            {
                return;
            }
            var statusJson = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
            using var statusDoc = JsonDocument.Parse(statusJson);
            if (!statusDoc.RootElement.TryGetProperty("result", out _))
            {
                return;
            }

            await ProcessActiveGroupsAsync(
                http,
                baseUrl,
                trackedById,
                trackedDownloads,
                matchedDownloads,
                activeCanonicalIds,
                cancellationToken);
        }
        private async Task ProcessActiveGroupsAsync(
            HttpClient http,
            Uri baseUrl,
            IReadOnlyDictionary<string, Download> trackedById,
            IReadOnlyList<Download> trackedDownloads,
            ISet<Download> matchedDownloads,
            ISet<string> activeCanonicalIds,
            CancellationToken cancellationToken)
        {
            var queueRequest = new
            {
                method = "listgroups",
                id = 3
            };
            var queueJsonContent = JsonSerializer.Serialize(queueRequest);
            using var queueHttpContent = new StringContent(
                queueJsonContent,
                Encoding.UTF8,
                "application/json");
            using var queueResponse = await http.PostAsync(
                baseUrl,
                queueHttpContent,
                cancellationToken);

            if (!queueResponse.IsSuccessStatusCode)
            {
                return;
            }
            var queueJson = await queueResponse.Content.ReadAsStringAsync(cancellationToken);
            using var queueDoc = JsonDocument.Parse(queueJson);
            if (!queueDoc.RootElement.TryGetProperty("result", out var queueResult) ||
                queueResult.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var group in queueResult.EnumerateArray())
            {
                ProcessActiveGroup(
                    group,
                    trackedById,
                    trackedDownloads,
                    matchedDownloads,
                    activeCanonicalIds);
            }
        }
        private void ProcessActiveGroup(
            JsonElement group,
            IReadOnlyDictionary<string, Download> trackedById,
            IReadOnlyList<Download> trackedDownloads,
            ISet<Download> matchedDownloads,
            ISet<string> activeCanonicalIds)
        {
            try
            {
                var nzbId = group.TryGetProperty("NZBID", out var nzbIdProperty)
                    ? nzbIdProperty.GetInt32()
                    : 0;
                var nzbName = group.TryGetProperty("NZBName", out var nameProperty)
                    ? nameProperty.GetString() ?? string.Empty
                    : string.Empty;
                var matchingDownload = FindTrackedDownload(
                    nzbId.ToString(),
                    nzbName,
                    trackedById,
                    trackedDownloads,
                    excludedDownloads: null);

                if (matchingDownload == null)
                {
                    return;
                }
                ApplyActiveProgress(group, matchingDownload);
                matchedDownloads.Add(matchingDownload);
                if (nzbId > 0)
                {
                    activeCanonicalIds.Add(nzbId.ToString());
                }
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogWarning(
                    exception,
                    "Error updating NZBGet queue progress for group");
            }
        }
        private static void ApplyActiveProgress(JsonElement group, Download matchingDownload)
        {
            var status = group.TryGetProperty("Status", out var statusProperty)
                ? statusProperty.GetString() ?? string.Empty
                : string.Empty;
            var fileSizeMb = group.TryGetProperty("FileSizeMB", out var sizeProperty)
                ? sizeProperty.GetString() ?? string.Empty
                : string.Empty;
            var remainingSizeMb = group.TryGetProperty("RemainingSizeMB", out var remainingSizeProperty)
                ? remainingSizeProperty.GetString() ?? string.Empty
                : string.Empty;

            if (!double.TryParse(fileSizeMb, out var totalMb) ||
                !double.TryParse(remainingSizeMb, out var remainingMb))
            {
                return;
            }
            var progress = totalMb > 0 ? (totalMb - remainingMb) / totalMb : 0.0;
            var amountLeft = (long)(remainingMb * 1024 * 1024);
            AdapterUtils.MapDownloadProgress(
                matchingDownload,
                progress,
                amountLeft,
                status);
        }
        private async Task ProcessHistoryAsync(
            DownloadClientConfiguration client,
            IReadOnlyDictionary<string, Download> trackedById,
            IReadOnlyList<Download> trackedDownloads,
            ISet<Download> matchedDownloads,
            ISet<string> activeCanonicalIds,
            CancellationToken cancellationToken)
        {
            var startTimestamp = timeProvider.GetTimestamp();
            var historyCount = 0;
            try
            {
                var history = await historyReader.ReadAsync(client, cancellationToken);
                historyCount = history.Count;
                MergeHistory(
                    client,
                    history,
                    trackedById,
                    trackedDownloads,
                    matchedDownloads,
                    activeCanonicalIds,
                    cancellationToken);
            }
            finally
            {
                var elapsedMilliseconds = (long)timeProvider
                    .GetElapsedTime(startTimestamp)
                    .TotalMilliseconds;
                LogHistoryMeasurement(client, historyCount, elapsedMilliseconds);
            }
        }
        private static void MergeHistory(
            DownloadClientConfiguration client,
            IReadOnlyList<NzbgetHistoryEntry> history,
            IReadOnlyDictionary<string, Download> trackedById,
            IReadOnlyList<Download> trackedDownloads,
            ISet<Download> matchedDownloads,
            ISet<string> activeCanonicalIds,
            CancellationToken cancellationToken)
        {
            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);
            var processedHistoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in history)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsQualifyingHistory(
                    entry,
                    configuredCategory,
                    activeCanonicalIds,
                    processedHistoryIds))
                {
                    continue;
                }

                var matchingDownload = FindHistoryDownload(
                    entry,
                    trackedById,
                    trackedDownloads,
                    matchedDownloads);
                if (matchingDownload == null)
                {
                    continue;
                }
                ApplyHistoryEntry(matchingDownload, entry);
                matchedDownloads.Add(matchingDownload);
            }
        }
        private static bool IsQualifyingHistory(
            NzbgetHistoryEntry entry,
            string? configuredCategory,
            ISet<string> activeCanonicalIds,
            ISet<string> processedHistoryIds)
        {
            if (entry.Outcome == NzbgetHistoryOutcome.Ignored ||
                !DownloadClientCategoryFilter.Matches(configuredCategory, entry.Category))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(entry.CanonicalNzbId))
            {
                if (!processedHistoryIds.Add(entry.CanonicalNzbId) ||
                    activeCanonicalIds.Contains(entry.CanonicalNzbId))
                {
                    return false;
                }
            }
            return true;
        }
        private static void ApplyHistoryEntry(
            Download download,
            NzbgetHistoryEntry entry)
        {
            if (entry.TotalSizeBytes > 0)
            {
                download.TotalSize = entry.TotalSizeBytes;
            }

            if (entry.Outcome == NzbgetHistoryOutcome.Completed)
            {
                AdapterUtils.MapDownloadProgress(download, 100, 0, "success");
                if (!string.IsNullOrWhiteSpace(entry.CompletedPath))
                {
                    download.DownloadPath = entry.CompletedPath;
                }
                return;
            }
            var progress = entry.TotalSizeBytes > 0
                ? entry.DownloadedSizeBytes * 100d / entry.TotalSizeBytes
                : 0d;
            var amountLeft = Math.Max(
                entry.TotalSizeBytes - entry.DownloadedSizeBytes,
                0);
            AdapterUtils.MapDownloadProgress(download, progress, amountLeft, "failure");
            download.ErrorMessage = entry.RawStatus;
            download.Metadata["ClientFailureReason"] = entry.RawStatus;
        }
        private void LogHistoryMeasurement(
            DownloadClientConfiguration client,
            int historyCount,
            long elapsedMilliseconds)
        {
            var clientId = LogRedaction.SanitizeText(
                client.Id ?? client.Name ?? client.Type);
            logger.LogDebug(
                "NZBGet history polling measurement clientId={ClientId} surface={Surface} historyCount={HistoryCount} elapsedMs={ElapsedMs}",
                clientId,
                PollingSurface,
                historyCount,
                elapsedMilliseconds);
            if (elapsedMilliseconds > SlowHistoryThresholdMilliseconds)
            {
                logger.LogWarning(
                    "Slow NZBGet history polling clientId={ClientId} surface={Surface} historyCount={HistoryCount} elapsedMs={ElapsedMs}",
                    clientId,
                    PollingSurface,
                    historyCount,
                    elapsedMilliseconds);
            }
        }
        private static Dictionary<string, Download> BuildIdLookup(
            IEnumerable<Download> downloads)
        {
            var lookup = new Dictionary<string, Download>(StringComparer.OrdinalIgnoreCase);
            foreach (var download in downloads)
            {
                var externalId = download.GetExternalId();
                if (!string.IsNullOrEmpty(externalId))
                {
                    lookup.TryAdd(externalId, download);
                }
            }
            return lookup;
        }
        private static Download? FindTrackedDownload(
            string canonicalNzbId,
            string title,
            IReadOnlyDictionary<string, Download> trackedById,
            IReadOnlyList<Download> trackedDownloads,
            ISet<Download>? excludedDownloads)
        {
            if (!string.IsNullOrEmpty(canonicalNzbId) &&
                trackedById.TryGetValue(canonicalNzbId, out var idMatch) &&
                (excludedDownloads == null || !excludedDownloads.Contains(idMatch)))
            {
                return idMatch;
            }
            return FindTitleMatch(trackedDownloads, title, excludedDownloads);
        }
        private static Download? FindHistoryDownload(
            NzbgetHistoryEntry entry,
            IReadOnlyDictionary<string, Download> trackedById,
            IReadOnlyList<Download> trackedDownloads,
            ISet<Download> matchedDownloads)
        {
            if (!string.IsNullOrEmpty(entry.CanonicalNzbId) &&
                trackedById.TryGetValue(entry.CanonicalNzbId, out var idMatch))
            {
                return matchedDownloads.Contains(idMatch) ? null : idMatch;
            }

            return FindTitleMatch(trackedDownloads, entry.Title, matchedDownloads);
        }
        private static Download? FindTitleMatch(
            IReadOnlyList<Download> trackedDownloads,
            string title,
            ISet<Download>? excludedDownloads)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            foreach (var candidate in trackedDownloads)
            {
                if (excludedDownloads?.Contains(candidate) == true)
                {
                    continue;
                }

                if (TitleUtils.AreTitlesSimilar(candidate.Title, title))
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
