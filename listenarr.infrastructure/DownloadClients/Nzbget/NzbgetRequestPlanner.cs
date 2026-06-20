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

namespace Listenarr.Infrastructure.DownloadClients.Nzbget
{
    internal static class NzbgetRequestPlanner
    {
        public static string ResolveCategory(DownloadClientConfiguration client)
        {
            if (client.Settings != null && client.Settings.TryGetValue("category", out var categoryObj))
            {
                var category = categoryObj?.ToString();
                if (!string.IsNullOrWhiteSpace(category))
                {
                    return category;
                }
            }

            return string.Empty;
        }

        public static int ResolvePriority(DownloadClientConfiguration client)
        {
            if (client.Settings != null && client.Settings.TryGetValue("recentPriority", out var priorityObj))
            {
                var priority = priorityObj?.ToString();
                if (!string.IsNullOrWhiteSpace(priority) && !string.Equals(priority, "default", StringComparison.OrdinalIgnoreCase))
                {
                    return priority.ToLowerInvariant() switch
                    {
                        "force" => 100,
                        "high" => 50,
                        "normal" => 0,
                        "low" => -50,
                        _ => 0
                    };
                }
            }

            return 0;
        }

        public static string BuildNzbFileName(SearchResult result)
        {
            if (result == null)
            {
                return "listenarr-download.nzb";
            }

            var rawName = result.Title;
            if (string.IsNullOrWhiteSpace(rawName))
            {
                if (!string.IsNullOrWhiteSpace(result.NzbUrl) && Uri.TryCreate(result.NzbUrl, UriKind.Absolute, out var nzbUri))
                {
                    rawName = Path.GetFileName(nzbUri.AbsolutePath);
                }

                if (string.IsNullOrWhiteSpace(rawName))
                {
                    rawName = result.Id;
                }
            }

            if (string.IsNullOrWhiteSpace(rawName))
            {
                rawName = "listenarr-download";
            }

            var sanitizedChars = rawName.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '.').ToArray();
            var sanitized = new string(sanitizedChars).Trim();
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = "listenarr-download";
            }

            if (!sanitized.EndsWith(".nzb", StringComparison.OrdinalIgnoreCase))
            {
                sanitized += ".nzb";
            }

            return sanitized;
        }

        public static int? TryParseId(string id)
        {
            if (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId))
            {
                return numericId;
            }

            return null;
        }
    }
}
