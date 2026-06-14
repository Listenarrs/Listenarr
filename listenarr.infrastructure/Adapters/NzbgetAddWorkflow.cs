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
using Listenarr.Application.Security;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Adapters
{
    internal sealed class NzbgetAddWorkflow(
        INzbUrlResolver nzbUrlResolver,
        NzbgetXmlRpcClient xmlRpcClient,
        NzbgetNzbDownloader nzbDownloader,
        ILogger logger)
    {
        public async Task<string?> AddAsync(DownloadClientConfiguration client, SearchResult result, CancellationToken ct = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (result == null) throw new ArgumentNullException(nameof(result));

            var (nzbUrl, indexerApiKey) = await nzbUrlResolver.ResolveAsync(result, ct);
            if (string.IsNullOrWhiteSpace(nzbUrl))
            {
                throw new ArgumentException("No NZB URL available for NZBGet", nameof(result));
            }

            logger.LogInformation("Using NZBGet JSON-RPC append method");
            return await AddViaJsonRpcAsync(client, result, nzbUrl, indexerApiKey, ct);
        }

        private async Task<string?> AddViaJsonRpcAsync(
            DownloadClientConfiguration client,
            SearchResult result,
            string nzbUrl,
            string? indexerApiKey,
            CancellationToken ct)
        {
            var category = NzbgetRequestPlanner.ResolveCategory(client);
            var priority = NzbgetRequestPlanner.ResolvePriority(client);
            var droneId = Guid.NewGuid().ToString().Replace("-", string.Empty);

            var nzbBytes = await nzbDownloader.DownloadAsync(nzbUrl, indexerApiKey, ct);
            var nzbContentBase64 = Convert.ToBase64String(nzbBytes);
            var nzbFileName = NzbgetRequestPlanner.BuildNzbFileName(result);

            var ppParams = new[]
            {
                new Dictionary<string, object>
                {
                    { "Name", "drone" },
                    { "Value", droneId }
                }
            };

            try
            {
                logger.LogInformation("Calling NZBGet append via XML-RPC for '{Title}'", LogRedaction.SanitizeText(result.Title));
                var appendResult = await xmlRpcClient.CallAsync(client, "append",
                    nzbFileName,
                    nzbContentBase64,
                    category ?? string.Empty,
                    priority,
                    false,
                    false,
                    string.Empty,
                    0,
                    "SCORE",
                    ppParams
                );

                var queueId = int.Parse(appendResult.Element("i4")?.Value ?? appendResult.Element("int")?.Value ?? "0");

                if (queueId <= 0)
                {
                    logger.LogWarning("NZBGet rejected NZB '{Title}', returned ID: {QueueId}", LogRedaction.SanitizeText(result.Title), queueId);
                    return null;
                }

                logger.LogInformation("NZBGet XML-RPC queued '{Title}' with ID {QueueId}, droneId: {DroneId}", LogRedaction.SanitizeText(result.Title), queueId, LogRedaction.SanitizeText(droneId));
                return queueId.ToString();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Failed to add NZB via XML-RPC");
                throw;
            }
        }
    }
}
