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
using Listenarr.Domain.Models;

namespace Listenarr.Application.Downloads
{
    internal static class DownloadRecordFactory
    {
        public static Download CreateQueuedDownload(
            string downloadId,
            SearchResult searchResult,
            DownloadClientConfiguration downloadClient,
            string downloadClientId,
            int? audiobookId)
        {
            return new Download
            {
                Id = downloadId,
                AudiobookId = audiobookId,
                Title = searchResult.Title ?? string.Empty,
                Artist = searchResult.Artist ?? string.Empty,
                Album = searchResult.Album ?? string.Empty,
                Language = searchResult.Language,
                OriginalUrl = !string.IsNullOrEmpty(searchResult.MagnetLink) ? searchResult.MagnetLink : (searchResult.TorrentUrl ?? searchResult.NzbUrl ?? string.Empty),
                Progress = 0,
                TotalSize = searchResult.Size,
                DownloadedSize = 0,
                DownloadPath = downloadClient.DownloadPath ?? string.Empty,
                FinalPath = string.Empty,
                StartedAt = DateTime.UtcNow,
                DownloadClientId = downloadClientId,
                Metadata = new Dictionary<string, object>
                {
                    ["Source"] = searchResult.Source ?? string.Empty,
                    ["Seeders"] = searchResult.Seeders ?? 0,
                    ["Quality"] = searchResult.Quality ?? string.Empty,
                    ["Language"] = searchResult.Language ?? string.Empty,
                    ["DownloadType"] = searchResult.DownloadType
                }
            };
        }
    }
}
