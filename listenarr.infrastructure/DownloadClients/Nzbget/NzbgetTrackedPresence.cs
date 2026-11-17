/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Nzbget
{
    /// <summary>
    /// Answers one question for orphan cleanup: does NZBGet history still report these
    /// tracked NZBIDs. It reads identity only, never progress, import, or action state.
    /// </summary>
    internal static class NzbgetTrackedPresence
    {
        public static async Task<List<QueueItem>> ReadAsync(
            NzbgetHistoryEnrichmentWorkflow historyEnrichmentWorkflow,
            DownloadClientConfiguration client,
            ISet<string> ids,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            try
            {
                // A warning job keeps its NZBID in history, so it counts as present.
                // Only an explicit delete means the job is gone.
                var history = await historyEnrichmentWorkflow.ReadHistoryWithMeasurementAsync(client, "GetTrackedQueueAsync", cancellationToken, requireCompleteSnapshot: true);
                return history
                    .Where(entry => ids.Contains(entry.CanonicalNzbId) && !entry.RawStatus.StartsWith("DELETED/", StringComparison.OrdinalIgnoreCase))
                    .Select(entry => new QueueItem
                    {
                        Id = entry.CanonicalNzbId,
                        Status = "unknown",
                        CanPause = false,
                        CanRemove = false,
                        DownloadClientId = client.Id,
                        DownloadClient = client.Name,
                        DownloadClientType = "nzbget"
                    }).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(
                    "NZBGet tracked history lookup failed clientId={ClientId} failureType={FailureType}",
                    LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type),
                    ex.GetType().Name);
                throw new DownloadClientAdapterPollingException("Error polling NZBGet tracked history.", ex);
            }
        }
    }
}
