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

using System.Net.Http.Headers;

namespace Listenarr.Infrastructure.DownloadClients.Qbittorrent
{
    internal static class QbittorrentAddRequestContentBuilder
    {
        public static HttpContent Build(QbittorrentTorrentAddPlan addPlan)
        {
            if (addPlan.TorrentFileData != null)
            {
                var multipart = new MultipartFormDataContent();
                multipart.Add(new StringContent(addPlan.SavePath), "savepath");
                if (!string.IsNullOrEmpty(addPlan.Category))
                    multipart.Add(new StringContent(addPlan.Category), "category");
                if (!string.IsNullOrEmpty(addPlan.Tags))
                    multipart.Add(new StringContent(addPlan.Tags), "tags");

                var torrentFileName = string.IsNullOrEmpty(addPlan.FileName) ? "download.torrent" : addPlan.FileName;
                var torrentContent = new ByteArrayContent(addPlan.TorrentFileData);
                torrentContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
                multipart.Add(torrentContent, "torrents", torrentFileName);
                return multipart;
            }

            var url = addPlan.MagnetLink ?? string.Empty;

            var formData = new List<KeyValuePair<string, string>>
            {
                new("urls", url),
                new("savepath", addPlan.SavePath)
            };

            if (!string.IsNullOrEmpty(addPlan.Category))
                formData.Add(new("category", addPlan.Category));
            if (!string.IsNullOrEmpty(addPlan.Tags))
                formData.Add(new("tags", addPlan.Tags));

            return new FormUrlEncodedContent(formData);
        }
    }
}
