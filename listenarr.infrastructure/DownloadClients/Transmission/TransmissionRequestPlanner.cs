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
using System.Globalization;
using System.Text.Json;

namespace Listenarr.Infrastructure.DownloadClients.Transmission
{
    internal static class TransmissionRequestPlanner
    {
        public static List<string> CollectLabels(DownloadClientConfiguration client)
        {
            var labels = new List<string>();

            if (client.Settings != null && client.Settings.TryGetValue("category", out var categoryObj))
            {
                var category = categoryObj?.ToString();
                if (!string.IsNullOrWhiteSpace(category))
                {
                    labels.Add(category);
                }
            }

            if (client.Settings != null && client.Settings.TryGetValue("tags", out var tagsObj))
            {
                var tags = tagsObj?.ToString();
                if (!string.IsNullOrWhiteSpace(tags))
                {
                    labels.AddRange(tags
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim())
                        .Where(t => !string.IsNullOrEmpty(t)));
                }
            }

            return labels;
        }

        public static object[] ParseTransmissionIds(string id)
        {
            if (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId))
            {
                return new object[] { numericId };
            }

            return new object[] { id };
        }

        public static string? ExtractTorrentIdentifier(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if ((element.TryGetProperty("hash_string", out var hashProp) || element.TryGetProperty("hashString", out hashProp)))
            {
                var hash = hashProp.GetString();
                if (!string.IsNullOrWhiteSpace(hash))
                {
                    return hash;
                }
            }

            if (element.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
            {
                return idProp.GetInt32().ToString(CultureInfo.InvariantCulture);
            }

            return null;
        }
    }
}
