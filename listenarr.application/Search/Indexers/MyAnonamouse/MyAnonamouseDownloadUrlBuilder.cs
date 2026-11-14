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
using System.Text.Json;
using Listenarr.Application.Common;
using Listenarr.Application.Search.Core;

namespace Listenarr.Application.Search.Indexers.MyAnonamouse
{
    internal static class MyAnonamouseDownloadUrlBuilder
    {
        public static string Build(string dlHash, string torrentId, Indexer indexer, bool isAlreadyFreeleech = false)
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
                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }

                var separator = downloadUrl.Contains('?') ? '&' : '?';
                downloadUrl += $"{separator}mam_id={Uri.EscapeDataString(mamIdLocal)}";
            }

            // MAM spends a freeleech wedge when fl=1 is present on the download URL
            // (same approach as Prowlarr). Only apply when configured and the torrent
            // is not already freeleech / personal freeleech.
            if (ShouldApplyFreeleechWedge(indexer) && !isAlreadyFreeleech)
            {
                var separator = downloadUrl.Contains('?') ? '&' : '?';
                downloadUrl += $"{separator}fl=1";
            }

            return downloadUrl;
        }

        internal static bool ShouldApplyFreeleechWedge(Indexer indexer)
        {
            var wedge = TryGetFreeleechWedge(indexer.AdditionalSettings);
            return wedge is MamFreeleechWedge.Preferred or MamFreeleechWedge.Required;
        }

        internal static bool IsAlreadyFreeleech(JsonElement item)
        {
            // Match Prowlarr: site freeleech or personal freeleech already applied.
            // VIP freeleech (fl_vip) still needs a wedge for non-VIP users, so we do
            // not treat it as already freeleech without knowing the account class.
            return GetJsonBoolean(item, "free") || GetJsonBoolean(item, "personal_freeleech");
        }

        private static MamFreeleechWedge? TryGetFreeleechWedge(string? additionalSettings)
        {
            if (string.IsNullOrWhiteSpace(additionalSettings))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(additionalSettings);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (root.TryGetProperty("mam_options", out var mamOptions) && mamOptions.ValueKind == JsonValueKind.Object)
                {
                    return ParseWedgeProperty(mamOptions);
                }

                return ParseWedgeProperty(root);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static MamFreeleechWedge? ParseWedgeProperty(JsonElement root)
        {
            if (root.TryGetProperty("freeleechWedge", out var wedge)
                && wedge.ValueKind == JsonValueKind.String
                && Enum.TryParse<MamFreeleechWedge>(wedge.GetString() ?? string.Empty, true, out var parsed))
            {
                return parsed;
            }

            return null;
        }

        private static bool GetJsonBoolean(JsonElement item, string propertyName)
        {
            foreach (var prop in item.EnumerateObject())
            {
                if (!string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return prop.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => prop.Value.TryGetInt32(out var n) && n != 0,
                    JsonValueKind.String => bool.TryParse(prop.Value.GetString(), out var b) && b
                        || prop.Value.GetString() is "1",
                    _ => false
                };
            }

            return false;
        }
    }
}
