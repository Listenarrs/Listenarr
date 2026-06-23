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
        ILogger logger)
    {
        private const string QueueSurface = "GetQueueAsync";
        private const string ItemSurface = "GetItemsAsync";

        public ActiveHistoryIdentity ParseActiveIdentity(
            XElement structElement,
            string title)
        {
            return new ActiveHistoryIdentity(
                ReadActiveScalar(structElement, "NZBID").Trim(),
                title);
        }

        public async Task EnrichQueueAsync(
            DownloadClientConfiguration client,
            string? configuredCategory,
            IReadOnlyList<ActiveHistoryIdentity> activeIdentities,
            List<QueueItem> items,
            CancellationToken cancellationToken)
        {
            try
            {
                var history = await historyReader.ReadAsync(client, cancellationToken);
                AppendHistory(
                    configuredCategory,
                    activeIdentities,
                    history,
                    cancellationToken,
                    entry => TryAppendQueueItem(client, entry, items));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                LogEnrichmentFailure(client, QueueSurface, items.Count, ex);
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
                var history = await historyReader.ReadAsync(client, cancellationToken);
                AppendHistory(
                    configuredCategory,
                    activeIdentities,
                    history,
                    cancellationToken,
                    entry => TryAppendDownloadClientItem(client, entry, items));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                LogEnrichmentFailure(client, ItemSurface, items.Count, ex);
            }
        }

        private static void AppendHistory(
            string? configuredCategory,
            IReadOnlyList<ActiveHistoryIdentity> activeIdentities,
            IReadOnlyList<NzbgetHistoryEntry> history,
            CancellationToken cancellationToken,
            Action<NzbgetHistoryEntry> append)
        {
            var activeCanonicalIds = activeIdentities
                .Where(identity => !string.IsNullOrEmpty(identity.CanonicalNzbId))
                .Select(identity => identity.CanonicalNzbId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var processedHistoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in history)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsHistoryCandidate(
                    entry,
                    configuredCategory,
                    activeIdentities,
                    activeCanonicalIds,
                    processedHistoryIds))
                {
                    continue;
                }

                append(entry);
            }
        }

        private static bool IsHistoryCandidate(
            NzbgetHistoryEntry entry,
            string? configuredCategory,
            IReadOnlyList<ActiveHistoryIdentity> activeIdentities,
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
                return processedHistoryIds.Add(entry.CanonicalNzbId) &&
                    !activeCanonicalIds.Contains(entry.CanonicalNzbId);
            }

            return !activeIdentities.Any(
                active => TitleUtils.AreTitlesSimilar(active.Title, entry.Title));
        }

        private void TryAppendQueueItem(
            DownloadClientConfiguration client,
            NzbgetHistoryEntry entry,
            ICollection<QueueItem> items)
        {
            try
            {
                items.Add(NzbgetResponseMapper.MapHistoryToQueueItem(client, entry));
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

        private void TryAppendDownloadClientItem(
            DownloadClientConfiguration client,
            NzbgetHistoryEntry entry,
            ICollection<DownloadClientItem> items)
        {
            try
            {
                items.Add(NzbgetResponseMapper.MapHistoryToDownloadClientItem(client, entry));
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

        internal sealed record ActiveHistoryIdentity(
            string CanonicalNzbId,
            string Title);
    }
}
