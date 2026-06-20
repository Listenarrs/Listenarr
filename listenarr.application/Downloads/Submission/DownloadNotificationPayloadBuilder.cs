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

namespace Listenarr.Application.Downloads.Submission
{
    internal static class DownloadNotificationPayloadBuilder
    {
        public static async Task<object> BuildBookDownloadingPayloadAsync(
            IAudiobookRepository audiobookRepository,
            int? audiobookId,
            string downloadId,
            SearchResult searchResult,
            DownloadClientConfiguration downloadClient)
        {
            if (audiobookId.HasValue)
            {
                var audiobook = await audiobookRepository.GetByIdAsync(audiobookId.Value);
                return audiobook != null
                    ? new
                    {
                        title = audiobook.Title,
                        authors = audiobook.Authors,
                        asin = audiobook.Asin,
                        publisher = audiobook.Publisher,
                        year = audiobook.PublishYear?.ToString(),
                        publishedDate = audiobook.PublishYear?.ToString(),
                        imageUrl = audiobook.ImageUrl,
                        narrators = audiobook.Narrators,
                        description = audiobook.Description,
                        downloadId = downloadId,
                        source = searchResult.Source ?? "Unknown Source",
                        downloadClient = downloadClient.Name ?? "Unknown Client",
                        size = searchResult.Size
                    }
                    : new
                    {
                        downloadId = downloadId,
                        title = searchResult.Title ?? "Unknown Title",
                        artist = searchResult.Artist ?? "Unknown Artist",
                        album = searchResult.Album ?? "Unknown Album",
                        size = searchResult.Size,
                        source = searchResult.Source ?? "Unknown Source",
                        downloadClient = downloadClient.Name ?? "Unknown Client",
                        audiobookId = audiobookId
                    };
            }

            return new
            {
                downloadId = downloadId,
                title = searchResult.Title ?? "Unknown Title",
                artist = searchResult.Artist ?? "Unknown Artist",
                album = searchResult.Album ?? "Unknown Album",
                size = searchResult.Size,
                source = searchResult.Source ?? "Unknown Source",
                downloadClient = downloadClient.Name ?? "Unknown Client"
            };
        }
    }
}
