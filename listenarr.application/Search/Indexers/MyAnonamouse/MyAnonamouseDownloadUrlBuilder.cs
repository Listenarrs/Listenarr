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
using Listenarr.Application.Common;

namespace Listenarr.Application.Search.Indexers.MyAnonamouse
{
    internal static class MyAnonamouseDownloadUrlBuilder
    {
        public static string Build(string dlHash, string torrentId, Indexer indexer)
        {
            if (string.IsNullOrWhiteSpace(dlHash) && string.IsNullOrWhiteSpace(torrentId))
            {
                return string.Empty;
            }

            var baseUrl = (indexer.Url ?? "https://www.myanonamouse.net").TrimEnd('/');
            var downloadUrl = !string.IsNullOrWhiteSpace(dlHash)
                ? $"{baseUrl}/tor/download.php/{Uri.EscapeDataString(dlHash)}"
                : $"{baseUrl}/tor/download.php?tid={Uri.EscapeDataString(torrentId)}";
            var mamIdLocal = MyAnonamouseHelper.TryGetMamId(indexer.AdditionalSettings);
            if (!string.IsNullOrEmpty(mamIdLocal))
            {
                try
                {
                    mamIdLocal = Uri.UnescapeDataString(mamIdLocal);
                }
                catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
                {
                    // Nothing is logged here: this URL builder is static with no logger; the mam_id is used as stored when it will not unescape.
                }

                var separator = downloadUrl.Contains('?') ? '&' : '?';
                downloadUrl += $"{separator}mam_id={Uri.EscapeDataString(mamIdLocal)}";
            }

            return downloadUrl;
        }
    }
}
