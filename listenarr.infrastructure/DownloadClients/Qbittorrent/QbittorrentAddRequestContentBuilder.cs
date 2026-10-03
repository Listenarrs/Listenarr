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
                foreach (var option in AdvancedOptions(addPlan))
                    multipart.Add(new StringContent(option.Value), option.Key);

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
            formData.AddRange(AdvancedOptions(addPlan));

            return new FormUrlEncodedContent(formData);
        }

        /// <summary>
        /// The Advanced Settings section's parameters, in the spelling the client accepts.
        /// </summary>
        /// <remarks>
        /// Verified against qBittorrent 5.2.3, Web API 2.15.1, by adding a torrent with each and
        /// reading the resulting state back:
        ///
        /// - "paused" is ignored from Web API 2.11 onward, where it became "stopped". Both are
        ///   sent, which pauses on 4.x and 5.x without asking the client its version first. The
        ///   older build ignores the name it does not know, as the newer one does.
        /// - "contentLayout" is case sensitive; a lowercase value is dropped and the torrent keeps
        ///   the default layout.
        /// - Only options the user actually chose are sent, so Default keeps the client's own
        ///   preference rather than overriding it with something that looks like a default here.
        ///
        /// Force start is not here. It is not accepted at add time and needs a separate call once
        /// the torrent exists, which the workflow makes.
        /// </remarks>
        private static List<KeyValuePair<string, string>> AdvancedOptions(QbittorrentTorrentAddPlan addPlan)
        {
            var options = new List<KeyValuePair<string, string>>();

            if (addPlan.AddPaused)
            {
                options.Add(new("stopped", "true"));
                options.Add(new("paused", "true"));
            }

            if (addPlan.SequentialDownload)
                options.Add(new("sequentialDownload", "true"));

            if (addPlan.FirstLastPiecePriority)
                options.Add(new("firstLastPiecePrio", "true"));

            if (!string.IsNullOrEmpty(addPlan.ContentLayout))
                options.Add(new("contentLayout", addPlan.ContentLayout));

            return options;
        }
    }
}
