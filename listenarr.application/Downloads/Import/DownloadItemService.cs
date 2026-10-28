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

namespace Listenarr.Application.Downloads.Import
{
    public class DownloadItemService(
        IConfigurationService configurationService,
        ILogger<IDownloadItemService> logger,
        IDownloadClientGateway downloadClientGateway,
        IDirectDownloadImportSourceResolver directDownloadImportSourceResolver) : IDownloadItemService
    {

        public async Task<QueueItem> GetImportItemAsync(Download download, CancellationToken ct = default)
        {
            if (string.Equals(download.DownloadClientId, DirectDownloadMetadataKeys.ClientId, StringComparison.OrdinalIgnoreCase))
            {
                // DDLs are fetched by Listenarr itself and have no external client
                // row to resolve. Infrastructure owns local filesystem discovery.
                return directDownloadImportSourceResolver.Resolve(download);
            }

            // Get the download client configuration
            var client = await configurationService.GetDownloadClientConfigurationAsync(download.DownloadClientId);
            if (client == null)
            {
                throw new InvalidOperationException($"Download {download.Id} references unknown download client {download.DownloadClientId}");
            }

            // FIXME: Queue item responsability should not be here
            var queueItem = new QueueItem
            {
                Id = download.GetExternalId() ?? download.Id,
                Title = download.Title ?? "Unknown",
                Status = "completed",
                DownloadClientId = client.Id,
                LocalPath = download.DownloadPath
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

    }
}
