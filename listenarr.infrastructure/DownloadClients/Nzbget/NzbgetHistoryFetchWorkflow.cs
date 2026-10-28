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
    internal sealed class NzbgetHistoryFetchWorkflow(
        NzbgetXmlRpcClient xmlRpcClient,
        ILogger<NzbgetAdapter> logger)
    {
        public async Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
        {
            var history = new List<(string Id, string Name)>();
            if (client == null) return history;

            try
            {
                var historyResult = await xmlRpcClient.CallAsync(client, "history", false);
                var arrayData = historyResult.Element("array")?.Element("data");

                if (arrayData == null)
                {
                    return history;
                }

                var count = 0;
                foreach (var valueElement in arrayData.Elements("value"))
                {
                    if (count >= limit) break;

                    var structElement = valueElement.Element("struct");
                    if (structElement != null)
                    {
                        var members = structElement.Elements("member").ToDictionary(
                            m => m.Element("name")?.Value ?? string.Empty,
                            m => m.Element("value")?.Elements().FirstOrDefault()?.Value ?? string.Empty
                        );

                        var entryId = members.GetValueOrDefault("ID", string.Empty);
                        var entryName = members.GetValueOrDefault("NZBName", string.Empty);

                        if (!string.IsNullOrEmpty(entryId) && !string.IsNullOrEmpty(entryName))
                        {
                            history.Add((entryId, entryName));
                            count++;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(ex, "Failed to fetch NZBGet history for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
            }

            return history;
        }
    }
}
