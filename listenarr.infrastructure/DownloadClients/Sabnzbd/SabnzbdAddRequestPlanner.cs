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

namespace Listenarr.Infrastructure.DownloadClients.Sabnzbd
{
    internal static class SabnzbdAddRequestPlanner
    {
        public static Dictionary<string, string> BuildQueryParams(DownloadClientConfiguration client, SearchResult result, string nzbUrl)
        {
            var queryParams = new Dictionary<string, string>
            {
                { "mode", "addurl" },
                { "name", nzbUrl },
                { "output", "json" },
                { "nzbname", result.Title }
            };

            if (client.Settings != null && client.Settings.TryGetValue("recentPriority", out var priorityObj))
            {
                var priority = priorityObj?.ToString();
                if (!string.IsNullOrEmpty(priority) && priority != "default")
                {
                    queryParams["priority"] = priority switch
                    {
                        "force" => "2",
                        "high" => "1",
                        "normal" => "0",
                        "low" => "-1",
                        _ => "0"
                    };
                }
            }

            queryParams["cat"] = ResolveCategory(client);
            return queryParams;
        }

        public static Dictionary<string, string> BuildFileQueryParams(
            DownloadClientConfiguration client,
            string title)
        {
            var queryParams = new Dictionary<string, string>
            {
                ["mode"] = "addfile",
                ["output"] = "json",
                ["nzbname"] = title,
                ["cat"] = ResolveCategory(client)
            };

            if (client.Settings != null &&
                client.Settings.TryGetValue("recentPriority", out var priorityValue))
            {
                // Default means "leave it to the client", so the parameter is omitted rather than
                // sent as 0. Sending 0 overrides whatever priority the SABnzbd category carries,
                // which is not what choosing Default asks for. BuildQueryParams above already
                // skipped it; this path, the one AddAsync actually uses, did not.
                var priority = priorityValue?.ToString()?.ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(priority) && priority != "default")
                {
                    queryParams["priority"] = priority switch
                    {
                        "force" => "2",
                        "high" => "1",
                        "normal" => "0",
                        "low" => "-1",
                        _ => "0"
                    };
                }
            }

            return queryParams;
        }

        private static string ResolveCategory(DownloadClientConfiguration client)
        {
            var category = "audiobooks";
            if (client.Settings != null && client.Settings.TryGetValue("category", out var categoryObj))
            {
                var configuredCategory = categoryObj?.ToString();
                if (!string.IsNullOrEmpty(configuredCategory))
                {
                    category = configuredCategory;
                }
            }

            return category;
        }
    }
}
