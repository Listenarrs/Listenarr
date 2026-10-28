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

namespace Listenarr.Application.Downloads.Processing
{
    internal static class DownloadDuplicateGuard
    {
        public static async Task<bool> HasActiveDownloadAsync(
            int audiobookId,
            IConfigurationService configurationService,
            IDownloadRepository downloadRepository)
        {
            var downloadClients = await configurationService.GetDownloadClientConfigurationsAsync();
            var enabledClientIds = downloadClients
                .Where(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.Id))
                .Select(c => c.Id)
                .ToHashSet();

            var allDownloads = await downloadRepository.GetAllAsync();
            return allDownloads
                .Any(d => d.AudiobookId == audiobookId &&
                          (d.Status == DownloadStatus.Queued ||
                           d.Status == DownloadStatus.Downloading ||
                           d.Status == DownloadStatus.ImportPending) &&
                          (string.Equals(d.DownloadClientId, DirectDownloadMetadataKeys.ClientId, StringComparison.OrdinalIgnoreCase) ||
                           (!string.IsNullOrEmpty(d.DownloadClientId) && enabledClientIds.Contains(d.DownloadClientId))));
        }
    }
}
