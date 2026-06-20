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

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Qbittorrent
{
    internal sealed class QbittorrentImportItemResolver(ILogger logger)
    {
        public async Task<DownloadClientItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            DownloadClientItem item,
            CancellationToken ct = default)
        {
            var result = item.Clone();

            if (!string.IsNullOrEmpty(result.OutputPath))
            {
                logger.LogDebug("Using existing OutputPath for import: {Path}", result.OutputPath);
                return result;
            }

            var hash = result.DownloadId.ToLowerInvariant();
            var resolved = await ResolveTorrentFilesAsync(client, hash, ct);
            if (resolved == null)
            {
                return result;
            }

            var outputPath = QbittorrentImportPathResolver.ResolveContentPath(resolved.Value.SavePath, resolved.Value.Files);
            if (string.IsNullOrEmpty(outputPath))
            {
                logger.LogWarning("Unable to resolve content path from torrent files for hash {Hash}", hash);
                return result;
            }

            result.OutputPath = outputPath;
            logger.LogInformation("Resolved import path for {Hash}: {Path}", hash, result.OutputPath);
            return result;
        }

        public async Task<QueueItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            Download download,
            QueueItem queueItem,
            CancellationToken ct = default)
        {
            var result = queueItem.Clone();
            string? resolvedExistingContentPath = null;

            if (!string.IsNullOrEmpty(result.ContentPath))
            {
                var localPath = result.ContentPath;
                if (!string.IsNullOrWhiteSpace(localPath))
                {
                    result.ContentPath = localPath;
                    resolvedExistingContentPath = localPath;
                }

                logger.LogDebug("Using existing ContentPath for import: {Path}", result.ContentPath);
            }

            var hash = download.Metadata?.GetValueOrDefault("TorrentHash")?.ToString();
            if (string.IsNullOrWhiteSpace(hash))
            {
                hash = queueItem.Id;
            }

            if (string.IsNullOrEmpty(hash))
            {
                logger.LogWarning("No torrent hash found in download metadata for download {DownloadId}", download.Id);
                return result;
            }

            var resolved = await ResolveTorrentFilesAsync(client, hash, ct);
            if (resolved == null)
            {
                return result;
            }

            var outputPath = QbittorrentImportPathResolver.ResolveContentPath(resolved.Value.SavePath, resolved.Value.Files);
            if (string.IsNullOrEmpty(outputPath) && string.IsNullOrWhiteSpace(resolvedExistingContentPath))
            {
                logger.LogWarning("Unable to resolve content path from torrent files for hash {Hash}", hash);
                return result;
            }

            result.SourceFiles = QbittorrentImportPathResolver.TranslateSourceFiles(
                QbittorrentImportPathResolver.BuildSourceFiles(resolved.Value.SavePath, resolved.Value.Files));
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                result.ContentPath = outputPath;
            }

            logger.LogInformation("Resolved import path for {Hash}: {Path}", hash, result.ContentPath);
            return result;
        }

        private async Task<(string SavePath, List<Dictionary<string, JsonElement>> Files)?> ResolveTorrentFilesAsync(
            DownloadClientConfiguration client,
            string hash,
            CancellationToken ct)
        {
            var baseUrl = DownloadClientUriBuilder.BuildAuthority(client);

            try
            {
                using var httpClient = QbittorrentCookieSession.CreateClient();
                using var loginData = QbittorrentCookieSession.CreateLoginContent(client);

                using var loginResp = await httpClient.PostAsync($"{baseUrl}/api/v2/auth/login", loginData, ct);
                if (!loginResp.IsSuccessStatusCode && loginResp.StatusCode != HttpStatusCode.Forbidden)
                {
                    logger.LogWarning("qBittorrent login failed for import resolution");
                    return null;
                }

                using var filesResp = await httpClient.GetAsync($"{baseUrl}/api/v2/torrents/files?hash={hash}", ct);
                if (!filesResp.IsSuccessStatusCode)
                {
                    logger.LogWarning("Failed to query torrent files for hash {Hash}", hash);
                    return null;
                }

                var filesJson = await filesResp.Content.ReadAsStringAsync(ct);
                var files = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(filesJson);

                if (files == null || !files.Any())
                {
                    logger.LogDebug("No files found for torrent {Hash}", hash);
                    return null;
                }

                using var propsResp = await httpClient.GetAsync($"{baseUrl}/api/v2/torrents/properties?hash={hash}", ct);
                if (!propsResp.IsSuccessStatusCode)
                {
                    logger.LogWarning("Failed to query torrent properties for hash {Hash}", hash);
                    return null;
                }

                var propsJson = await propsResp.Content.ReadAsStringAsync(ct);
                var props = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(propsJson);
                var savePath = props?.TryGetValue("save_path", out var savePathEl) is true
                    ? savePathEl.GetString() ?? string.Empty
                    : string.Empty;

                if (string.IsNullOrEmpty(savePath))
                {
                    logger.LogWarning("No save_path found for torrent {Hash}", hash);
                    return null;
                }

                return (savePath, files);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error resolving import item for torrent {Hash}", hash);
                return null;
            }
        }
    }
}
