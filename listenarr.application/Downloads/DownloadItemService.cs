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
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Exceptions;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads
{
    public class DownloadItemService(
        IConfigurationService configurationService,
        ILogger<IDownloadItemService> logger,
        IDownloadClientGateway downloadClientGateway) : IDownloadItemService
    {

        public async Task<QueueItem> ResolveImportItemAsync(Download download, CancellationToken ct = default)
        {
            // Get the download client configuration
            var client = await configurationService.GetDownloadClientConfigurationAsync(download.DownloadClientId);
            if (client == null)
            {
                throw new InvalidOperationException($"Download {download.Id} references unknown download client {download.DownloadClientId}");
            }

            var queueItem = new QueueItem
            {
                Id = download.GetClientDownloadItemId() ?? download.Id,
                Title = download.Title ?? "Unknown",
                Status = "completed",
                DownloadClientId = client.Id
            };

            if (!client.IsEnabled)
            {
                logger.LogDebug($"Skipping import item resolution for download {download.Id}: download client {client.Name} ({client.Id}) is disabled");
                return queueItem;
            }

            logger.LogDebug($"Resolving import item for download {download.Id}");

            return await downloadClientGateway.GetQueueItemAsync(
                client,
                download,
                queueItem,
                ct);
        }

        public async Task<List<string>> GetDownloadedFiles(Download download, CancellationToken cancellationToken = default)
        {
            var localPath = download.DownloadPath;
            if (File.Exists(localPath))
            {
                localPath = Path.GetDirectoryName(localPath);
            }

            if (string.IsNullOrEmpty(localPath))
            {
                throw new DownloadProcessingException($"Download {download.Id}: Unable to get the directory where files are supposed to be from: {download.DownloadPath}");
            }

            var importableFiles = Directory.EnumerateFiles(localPath, "*.*", SearchOption.AllDirectories)
                .Select(f => FileUtils.NormalizeStoredPath(f))
                .ToList();
            try
            {
                var downloadClientItem = await ResolveImportItemAsync(download, cancellationToken);
                if (downloadClientItem == null || downloadClientItem.SourceFiles == null || downloadClientItem.SourceFiles.Count == 0)
                {
                    throw new DownloadProcessingException($"Unable to get the client item matching download or no files reported by the download client for download {download.Id}");
                }

                var allowedFiles = new HashSet<string>(
                    downloadClientItem.SourceFiles
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Select(path => FileUtils.NormalizeStoredPath(path)),
                    StringComparer.OrdinalIgnoreCase);

                var filteredFiles = importableFiles
                    .Where(allowedFiles.Contains)
                    .ToList();

                if (filteredFiles.Count == 0)
                {
                    logger.LogWarning($"Download client reported {allowedFiles.Count} related file(s) for download {download.Id}, but none matched the local import candidates under {localPath}");
                }
                else
                {
                    logger.LogInformation($"Scoped directory import for download {download.Id} from {importableFiles.Count} to {filteredFiles.Count} file(s) using the download client's reported file list");
                }
                return filteredFiles;
            }
            catch (Exception exception) when (exception is not (DownloadProcessingException or OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                throw new DownloadProcessingException($"Unknown error while matching download client files for download {download.Id}", exception);
            }
        }
    }
}
