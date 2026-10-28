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
using System.Xml.Linq;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Nzbget
{
    internal sealed class NzbgetHistoryEnrichmentWorkflow(
        NzbgetHistoryReader historyReader,
        ILogger logger,
        TimeProvider timeProvider)
    {
        private const long SlowHistoryThresholdMilliseconds = 2_000;
        private const string QueueSurface = "GetQueueAsync";
        private const string ItemSurface = "GetItemsAsync";

        public ActiveHistoryIdentity ParseActiveIdentity(
            XElement structElement,
            string exposedId,
            string title)
        {
            var canonicalNzbId = ReadActiveScalar(structElement, "NZBID").Trim();
            var groupId = ReadActiveScalar(structElement, "GroupID").Trim();
            var lastId = ReadActiveScalar(structElement, "LastID").Trim();
            var rawStatus = ReadActiveScalar(structElement, "Status").Trim();
            var matchIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddIfNotEmpty(matchIds, canonicalNzbId);
            AddIfNotEmpty(matchIds, groupId);
            AddIfNotEmpty(matchIds, lastId);
            AddIfNotEmpty(matchIds, exposedId);

            return new ActiveHistoryIdentity(
                canonicalNzbId,
                exposedId,
                title,
                rawStatus,
                matchIds);
        }

        public async Task EnrichQueueAsync(
            DownloadClientConfiguration client,
            string? configuredCategory,
            IReadOnlyList<ActiveHistoryIdentity> activeIdentities,
            List<QueueItem> items,
            CancellationToken cancellationToken,
            IReadOnlyCollection<string>? monitoredIds = null)
        {
            var monitoredIdSet = BuildMonitoredIdSet(monitoredIds);
            var isMonitorPoll = monitoredIdSet.Count > 0;
            var historyRequired = IsHistoryRequired(activeIdentities, monitoredIdSet);

            // History is authoritative for completed/failed NZBGet outcomes and import paths,
            // but it is not needed for ordinary active listgroups rows. Skipping optional
            // history here keeps active progress updates working when history is flaky.
            if (!historyRequired)
            {
                logger.LogDebug(
                    "Skipping NZBGet history enrichment for active monitored items on client {ClientName}",
                    LogRedaction.SanitizeText(client.Name ?? client.Id));
                return;
            }

            try
            {
                var history = await ReadHistoryWithMeasurementAsync(client, QueueSurface, cancellationToken);
                AppendHistory(
                    client,
                    QueueSurface,
                    configuredCategory,
                    activeIdentities,
                    history,
                    cancellationToken,
                    entry => TryMergeOrAppendQueueItem(client, entry, activeIdentities, items));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                LogEnrichmentFailure(client, QueueSurface, items.Count, ex);
                if (isMonitorPoll && historyRequired)
                {
                    throw new DownloadClientAdapterPollingException("Error polling NZBGet history.", ex);
                }
            }
        }

        public async Task EnrichItemsAsync(
            DownloadClientConfiguration client,
            string? configuredCategory,
            IReadOnlyList<ActiveHistoryIdentity> activeIdentities,
            List<DownloadClientItem> items,
            CancellationToken cancellationToken)
        {
            try
            {
                var history = await ReadHistoryWithMeasurementAsync(client, ItemSurface, cancellationToken);
                AppendHistory(
                    client,
                    ItemSurface,
                    configuredCategory,
                    activeIdentities,
                    history,
                    cancellationToken,
                    entry => TryMergeOrAppendDownloadClientItem(client, entry, activeIdentities, items));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                LogEnrichmentFailure(client, ItemSurface, items.Count, ex);
            }
        }

        private async Task<IReadOnlyList<NzbgetHistoryEntry>> ReadHistoryWithMeasurementAsync(
            DownloadClientConfiguration client,
            string surface,
            CancellationToken cancellationToken)
        {
            var startTimestamp = timeProvider.GetTimestamp();
            var historyCount = 0;
            try
            {
                var history = await historyReader.ReadAsync(client, cancellationToken);
                historyCount = history.Count;
                return history;
            }
            finally
            {
                var elapsedMilliseconds = (long)timeProvider
                    .GetElapsedTime(startTimestamp)
                    .TotalMilliseconds;
                LogHistoryMeasurement(client, surface, historyCount, elapsedMilliseconds);
            }
        }

        private void AppendHistory(
            DownloadClientConfiguration client,
            string surface,
            string? configuredCategory,
            IReadOnlyList<ActiveHistoryIdentity> activeIdentities,
            IReadOnlyList<NzbgetHistoryEntry> history,
            CancellationToken cancellationToken,
            Action<NzbgetHistoryEntry> append)
        {
            var processedHistoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matchedTerminalActiveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in history)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsHistoryCandidate(
                    entry,
                    configuredCategory,
                    activeIdentities,
                    processedHistoryIds,
                    matchedTerminalActiveIds))
                {
                    continue;
                }

                LogFailedHistoryEntry(client, surface, entry);
                append(entry);
            }

            LogUnmatchedTerminalActiveItems(client, surface, activeIdentities, matchedTerminalActiveIds);
        }

        private static bool IsHistoryCandidate(
            NzbgetHistoryEntry entry,
            string? configuredCategory,
            IReadOnlyList<ActiveHistoryIdentity> activeIdentities,
            ISet<string> processedHistoryIds,
            ISet<string> matchedTerminalActiveIds)
        {
            if (entry.Outcome == NzbgetHistoryOutcome.Ignored ||
                !DownloadClientCategoryFilter.Matches(configuredCategory, entry.Category))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(entry.CanonicalNzbId) &&
                !processedHistoryIds.Add(entry.CanonicalNzbId))
            {
                return false;
            }

            var activeMatch = FindActiveIdentity(activeIdentities, entry);

            // Active listgroups records are progress telemetry. They suppress older
            // history only while they still look like active work. If NZBGet reports
            // a terminal-looking active status, history becomes authoritative because
            // that is where the final outcome and FinalDir/DestDir are available.
            if (activeMatch == null)
            {
                return true;
            }

            if (!activeMatch.HasTerminalClientStatus)
            {
                return false;
            }

            matchedTerminalActiveIds.Add(activeMatch.ExposedId);
            return true;
        }

        private void TryMergeOrAppendQueueItem(
            DownloadClientConfiguration client,
            NzbgetHistoryEntry entry,
            IReadOnlyList<ActiveHistoryIdentity> activeIdentities,
            IList<QueueItem> items)
        {
            try
            {
                var historyItem = NzbgetResponseMapper.MapHistoryToQueueItem(client, entry);
                var activeMatch = FindTerminalActiveIdentity(activeIdentities, entry);
                if (activeMatch != null)
                {
                    var index = items.ToList().FindIndex(item => activeMatch.Matches(item.Id));
                    if (index >= 0)
                    {
                        // Prefer the history row once NZBGet reports a terminal-looking
                        // active status. Active telemetry does not own final state or
                        // import paths, but history has the completed destination.
                        items[index] = historyItem;
                        return;
                    }
                }

                items.Add(historyItem);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(
                    "Failed to map NZBGet queue history entry clientId={ClientId} surface={Surface} failureType={FailureType}",
                    LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type),
                    QueueSurface,
                    ex.GetType().Name);
            }
        }

        private void TryMergeOrAppendDownloadClientItem(
            DownloadClientConfiguration client,
            NzbgetHistoryEntry entry,
            IReadOnlyList<ActiveHistoryIdentity> activeIdentities,
            IList<DownloadClientItem> items)
        {
            try
            {
                var historyItem = NzbgetResponseMapper.MapHistoryToDownloadClientItem(client, entry);
                var activeMatch = FindTerminalActiveIdentity(activeIdentities, entry);
                if (activeMatch != null)
                {
                    var index = items.ToList().FindIndex(item => activeMatch.Matches(item.DownloadId));
                    if (index >= 0)
                    {
                        // Normalized item callers need the same terminal-history preference
                        // so import-facing fields do not come from active telemetry.
                        items[index] = historyItem;
                        return;
                    }
                }

                items.Add(historyItem);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(
                    "Failed to map NZBGet item history entry clientId={ClientId} surface={Surface} failureType={FailureType}",
                    LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type),
                    ItemSurface,
                    ex.GetType().Name);
            }
        }

        private void LogFailedHistoryEntry(
            DownloadClientConfiguration client,
            string surface,
            NzbgetHistoryEntry entry)
        {
            if (entry.Outcome != NzbgetHistoryOutcome.Failed)
            {
                return;
            }

            logger.LogWarning(
                "NZBGet history reported failure for {NzbId}: Status={Status}, FinalDir={FinalDir}, DestDir={DestDir}, Title={Title}, Category={Category}, ClientId={ClientId}, Surface={Surface}",
                LogRedaction.SanitizeText(entry.CanonicalNzbId),
                LogRedaction.SanitizeText(entry.RawStatus),
                LogRedaction.SanitizeFilePath(entry.FinalDir),
                LogRedaction.SanitizeFilePath(entry.DestDir),
                LogRedaction.SanitizeText(entry.Title),
                LogRedaction.SanitizeText(entry.Category),
                LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type),
                surface);
        }

        private void LogHistoryMeasurement(
            DownloadClientConfiguration client,
            string surface,
            int historyCount,
            long elapsedMilliseconds)
        {
            var clientId = LogRedaction.SanitizeText(
                client.Id ?? client.Name ?? client.Type);
            logger.LogDebug(
                "NZBGet history polling measurement clientId={ClientId} surface={Surface} historyCount={HistoryCount} elapsedMs={ElapsedMs}",
                clientId,
                surface,
                historyCount,
                elapsedMilliseconds);
            if (elapsedMilliseconds > SlowHistoryThresholdMilliseconds)
            {
                logger.LogWarning(
                    "Slow NZBGet history polling clientId={ClientId} surface={Surface} historyCount={HistoryCount} elapsedMs={ElapsedMs}",
                    clientId,
                    surface,
                    historyCount,
                    elapsedMilliseconds);
            }
        }

        private void LogEnrichmentFailure(
            DownloadClientConfiguration client,
            string surface,
            int activeCount,
            Exception exception)
        {
            logger.LogWarning(
                "NZBGet history enrichment failed clientId={ClientId} surface={Surface} activeCount={ActiveCount} failureType={FailureType}",
                LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type),
                surface,
                activeCount,
                exception.GetType().Name);
        }

        private void LogUnmatchedTerminalActiveItems(
            DownloadClientConfiguration client,
            string surface,
            IReadOnlyList<ActiveHistoryIdentity> activeIdentities,
            ISet<string> matchedTerminalActiveIds)
        {
            foreach (var active in activeIdentities.Where(active =>
                active.HasTerminalClientStatus && !matchedTerminalActiveIds.Contains(active.ExposedId)))
            {
                logger.LogDebug(
                    "NZBGet active item {DownloadId} reported terminal status {Status} on {Surface}, but no matching history entry was available; keeping it as active telemetry until history reports final state",
                    LogRedaction.SanitizeText(active.ExposedId),
                    LogRedaction.SanitizeText(active.RawStatus),
                    surface);
            }
        }

        private static string ReadActiveScalar(XElement structElement, string name)
        {
            return structElement.Elements("member")
                .FirstOrDefault(member => string.Equals(
                    member.Element("name")?.Value,
                    name,
                    StringComparison.Ordinal))?
                .Element("value")?
                .Elements()
                .FirstOrDefault()?
                .Value ?? string.Empty;
        }

        private static ActiveHistoryIdentity? FindTerminalActiveIdentity(
            IReadOnlyList<ActiveHistoryIdentity> activeIdentities,
            NzbgetHistoryEntry entry)
        {
            var activeMatch = FindActiveIdentity(activeIdentities, entry);

            return activeMatch != null && activeMatch.HasTerminalClientStatus
                ? activeMatch
                : null;
        }

        private static ActiveHistoryIdentity? FindActiveIdentity(
            IReadOnlyList<ActiveHistoryIdentity> activeIdentities,
            NzbgetHistoryEntry entry)
        {
            if (!string.IsNullOrEmpty(entry.CanonicalNzbId))
            {
                // When history has a canonical NZBID, do not fall back to title.
                // A different history NZBID with a similar title is a distinct item
                // and should remain visible beside the active row.
                return activeIdentities.FirstOrDefault(active => active.Matches(entry.CanonicalNzbId));
            }

            return activeIdentities.FirstOrDefault(active => TitleUtils.AreTitlesSimilar(active.Title, entry.Title));
        }

        private static void AddIfNotEmpty(ISet<string> values, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }

        private static HashSet<string> BuildMonitoredIdSet(IReadOnlyCollection<string>? monitoredIds)
        {
            return monitoredIds?
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        }

        private static bool IsHistoryRequired(
            IReadOnlyList<ActiveHistoryIdentity> activeIdentities,
            ISet<string> monitoredIds)
        {
            if (monitoredIds.Count == 0)
            {
                return true;
            }

            var hasMissingTrackedId = monitoredIds.Any(id =>
                !activeIdentities.Any(active => active.Matches(id)));
            if (hasMissingTrackedId)
            {
                return true;
            }

            return activeIdentities.Any(active =>
                active.HasTerminalClientStatus && active.MatchesAny(monitoredIds));
        }

        internal sealed record ActiveHistoryIdentity(
            string CanonicalNzbId,
            string ExposedId,
            string Title,
            string RawStatus,
            IReadOnlySet<string> MatchIds)
        {
            public bool HasTerminalClientStatus => IsTerminalClientStatus(RawStatus);

            public bool Matches(string? id)
            {
                return !string.IsNullOrWhiteSpace(id) && MatchIds.Contains(id);
            }

            public bool MatchesAny(ISet<string> ids)
            {
                return MatchIds.Any(ids.Contains);
            }

            private static bool IsTerminalClientStatus(string? status)
            {
                var normalizedStatus = (status ?? string.Empty).Trim().ToUpperInvariant();
                return normalizedStatus.StartsWith("SUCCESS", StringComparison.Ordinal) ||
                    normalizedStatus.StartsWith("FAILURE", StringComparison.Ordinal) ||
                    normalizedStatus.StartsWith("FAILED", StringComparison.Ordinal);
            }
        }
    }
}
