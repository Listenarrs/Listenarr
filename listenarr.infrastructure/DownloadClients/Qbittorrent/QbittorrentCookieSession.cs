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

namespace Listenarr.Infrastructure.DownloadClients.Qbittorrent
{
    internal static class QbittorrentCookieSession
    {
        public static HttpClient CreateClient()
        {
            var cookieJar = new CookieContainer();
            var handler = new HttpClientHandler
            {
                CookieContainer = cookieJar,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.All
            };

            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        }

        public static FormUrlEncodedContent CreateLoginContent(DownloadClientConfiguration client)
        {
            return new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("username", client.Username ?? string.Empty),
                new KeyValuePair<string, string>("password", client.Password ?? string.Empty)
            });
        }
    }
}
