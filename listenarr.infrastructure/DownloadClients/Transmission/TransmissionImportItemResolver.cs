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

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Transmission
{
    internal sealed class TransmissionImportItemResolver(
        TransmissionRpcClient rpcClient,
        ILogger logger)
    {
        public async Task<DownloadClientItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            DownloadClientItem item,
            CancellationToken ct = default)
        {
            var result = item.Clone();

            if (!string.IsNullOrEmpty(result.OutputPath))
            {
                var localPath = result.OutputPath;
                if (TransmissionImportPathResolver.IsExistingLocalPath(localPath))
                {
                    result.OutputPath = localPath;
                    return result;
                }
            }

            var torrent = await TryGetTorrentAsync(client, item.DownloadId, includeFiles: false, ct);
            if (torrent == null)
            {
                return result;
            }

            var downloadDir = torrent.Value.TryGetProperty("downloadDir", out var dirProp) ? dirProp.GetString() : null;
            var name = torrent.Value.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;

            if (string.IsNullOrEmpty(downloadDir) || string.IsNullOrEmpty(name))
            {
                logger.LogWarning("Missing downloadDir or name for torrent {TorrentId}", item.DownloadId);
                return result;
            }

            var contentPath = TransmissionImportPathResolver.BuildContentPath(downloadDir, name)!;
            var localContentPath = contentPath;
            result.OutputPath = localContentPath;

            logger.LogDebug(
                "Resolved Transmission content path for {TorrentId}: {ContentPath}",
                item.DownloadId,
                localContentPath);

            return result;
        }

        public async Task<QueueItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            QueueItem queueItem,
            CancellationToken ct = default)
        {
            var result = queueItem.Clone();
            string? resolvedExistingContentPath = null;

            if (!string.IsNullOrEmpty(result.ContentPath))
            {
                var localPath = result.ContentPath;
                if (TransmissionImportPathResolver.IsExistingLocalPath(localPath))
                {
                    result.ContentPath = localPath;
                    resolvedExistingContentPath = localPath;
                }
            }

            var torrent = await TryGetTorrentAsync(client, queueItem.Id, includeFiles: true, ct);
            if (torrent == null)
            {
                return result;
            }

            var downloadDir = torrent.Value.TryGetProperty("downloadDir", out var dirProp) ? dirProp.GetString() : null;
            var name = torrent.Value.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;

            if ((string.IsNullOrEmpty(downloadDir) || string.IsNullOrEmpty(name)) && string.IsNullOrWhiteSpace(resolvedExistingContentPath))
            {
                logger.LogWarning("Missing downloadDir or name for torrent {TorrentId}", queueItem.Id);
                return result;
            }

            var contentPath = TransmissionImportPathResolver.BuildContentPath(downloadDir, name, resolvedExistingContentPath);
            string? localContentPath = resolvedExistingContentPath;
            if (!string.IsNullOrWhiteSpace(contentPath))
            {
                localContentPath = contentPath;
                result.ContentPath = localContentPath;
            }

            if (torrent.Value.TryGetProperty("files", out var filesElement))
            {
                result.SourceFiles = TransmissionImportPathResolver.BuildSourceFiles(downloadDir, filesElement);
            }

            logger.LogDebug(
                "Resolved Transmission content path for {TorrentId}: {ContentPath}",
                queueItem.Id,
                localContentPath);

            return result;
        }

        private async Task<JsonElement?> TryGetTorrentAsync(
            DownloadClientConfiguration client,
            string torrentId,
            bool includeFiles,
            CancellationToken ct)
        {
            var fields = includeFiles
                ? new[] { "id", "name", "downloadDir", "files" }
                : new[] { "id", "name", "downloadDir" };
            var payload = new
            {
                method = "torrent-get",
                arguments = new
                {
                    ids = TransmissionRequestPlanner.ParseTransmissionIds(torrentId),
                    fields
                },
                tag = 5
            };

            try
            {
                var response = await rpcClient.InvokeAsync(client, payload, ct);
                if (!response.TryGetProperty("arguments", out var args) ||
                    !args.TryGetProperty("torrents", out var torrents) ||
                    torrents.ValueKind != JsonValueKind.Array)
                {
                    logger.LogWarning("Failed to query Transmission for torrent {TorrentId}", torrentId);
                    return null;
                }

                var torrent = torrents.EnumerateArray().FirstOrDefault();
                if (torrent.ValueKind == JsonValueKind.Undefined)
                {
                    logger.LogWarning("Torrent {TorrentId} not found in Transmission", torrentId);
                    return null;
                }

                return torrent.Clone();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Error resolving import item for Transmission torrent {TorrentId}", torrentId);
                return null;
            }
        }
    }
}
