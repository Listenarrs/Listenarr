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
using Listenarr.Domain.Models;

namespace Listenarr.Application.Search
{
    internal static class MyAnonamouseDownloadUrlBuilder
    {
        public static string Build(string dlHash, Indexer indexer)
        {
            if (string.IsNullOrEmpty(dlHash))
            {
                return string.Empty;
            }

            var baseUrl = (indexer.Url ?? "https://www.myanonamouse.net").TrimEnd('/');
            var downloadUrl = $"{baseUrl}/tor/download.php/{dlHash}";
            var mamIdLocal = MyAnonamouseHelper.TryGetMamId(indexer.AdditionalSettings);
            if (!string.IsNullOrEmpty(mamIdLocal))
            {
                try
                {
                    mamIdLocal = Uri.UnescapeDataString(mamIdLocal);
                }
                catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
                {
                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }

                downloadUrl += $"?mam_id={Uri.EscapeDataString(mamIdLocal)}";
            }

            return downloadUrl;
        }
    }
}
