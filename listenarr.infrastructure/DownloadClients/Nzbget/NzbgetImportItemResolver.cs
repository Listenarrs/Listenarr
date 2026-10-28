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
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Nzbget
{
    internal sealed class NzbgetImportItemResolver(
        NzbgetXmlRpcClient xmlRpcClient,
        ILogger logger)
    {
        public async Task<DownloadClientItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            DownloadClientItem item)
        {
            var result = item.Clone();

            if (!string.IsNullOrWhiteSpace(result.OutputPath) &&
                (File.Exists(result.OutputPath) || Directory.Exists(result.OutputPath)))
            {
                return result;
            }

            if (!string.IsNullOrWhiteSpace(result.OutputPath))
            {
                logger.LogDebug(
                    "NZBGet output path {OutputPath} for {Id} does not exist; resolving from history",
                    result.OutputPath,
                    item.DownloadId);
            }

            var members = await FindHistoryEntryAsync(
                client,
                item.DownloadId,
                "ID",
                item.DownloadId,
                StringComparison.OrdinalIgnoreCase);
            if (members == null)
            {
                return result;
            }

            var finalDir = members.GetValueOrDefault("FinalDir", string.Empty);
            var destDir = members.GetValueOrDefault("DestDir", string.Empty);
            var outputPath = !string.IsNullOrWhiteSpace(finalDir) ? finalDir : destDir;
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                logger.LogWarning("No FinalDir or DestDir found for NZBGet download {Id}", item.DownloadId);
                return result;
            }

            result.OutputPath = outputPath;

            logger.LogDebug(
                "Resolved NZBGet content path for {Id}: {ContentPath}",
                item.DownloadId,
                outputPath);

            return result;
        }

        public async Task<QueueItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            QueueItem queueItem)
        {
            var result = queueItem.Clone();

            if (!string.IsNullOrWhiteSpace(result.ContentPath) &&
                (File.Exists(result.ContentPath) || Directory.Exists(result.ContentPath)))
            {
                return result;
            }

            if (!string.IsNullOrWhiteSpace(result.ContentPath))
            {
                logger.LogDebug(
                    "NZBGet content path {ContentPath} for {NzbId} does not exist; resolving from history",
                    result.ContentPath,
                    queueItem.Id);
            }

            var members = await FindHistoryEntryAsync(
                client,
                queueItem.Id,
                "NZBID",
                queueItem.Id,
                StringComparison.Ordinal);
            if (members == null)
            {
                return result;
            }

            var finalDir = members.GetValueOrDefault("FinalDir", string.Empty);
            var destDir = members.GetValueOrDefault("DestDir", string.Empty);
            var contentPath = !string.IsNullOrWhiteSpace(finalDir) ? finalDir : destDir;

            if (string.IsNullOrWhiteSpace(contentPath))
            {
                logger.LogWarning("No FinalDir or DestDir found for NZB {NzbId}", queueItem.Id);
                return result;
            }

            result.ContentPath = contentPath;

            logger.LogDebug(
                "Resolved NZBGet content path for {NzbId}: {ContentPath}",
                queueItem.Id,
                contentPath);

            return result;
        }

        private async Task<Dictionary<string, string>?> FindHistoryEntryAsync(
            DownloadClientConfiguration client,
            string id,
            string idField,
            string logId,
            StringComparison comparison)
        {
            try
            {
                var historyResult = await xmlRpcClient.CallAsync(client, "history", false);
                var arrayData = historyResult.Element("array")?.Element("data");

                if (arrayData == null)
                {
                    logger.LogWarning("Invalid NZBGet history response format");
                    return null;
                }

                foreach (var members in arrayData.Elements("value")
                    .Select(valueElement => valueElement.Element("struct"))
                    .Where(structElement => structElement != null)
                    .Select(structElement => structElement!.Elements("member").ToDictionary(
                        m => m.Element("name")?.Value ?? string.Empty,
                        m => m.Element("value")?.Elements().FirstOrDefault()?.Value ?? string.Empty)))
                {
                    var entryId = members.GetValueOrDefault(idField, string.Empty);
                    if (string.Equals(entryId, id, comparison))
                    {
                        return members;
                    }
                }

                logger.LogWarning("Download {Id} not found in NZBGet history", logId);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Error resolving import item for NZBGet download {Id}", logId);
                return null;
            }
        }
    }
}
