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

namespace Listenarr.Application.Search.Indexers.MyAnonamouse
{
    internal static class MyAnonamouseContributorParser
    {
        public static string? ParseContributorList(string? contributorJson)
        {
            if (string.IsNullOrEmpty(contributorJson))
            {
                return null;
            }

            using var contributorDoc = JsonDocument.Parse(contributorJson);
            var contributors = new List<string>();
            foreach (var prop in contributorDoc.RootElement.EnumerateObject())
            {
                contributors.Add(prop.Value.GetString() ?? "");
            }

            var joined = string.Join(", ", contributors.Where(a => !string.IsNullOrEmpty(a)));
            return string.IsNullOrEmpty(joined) ? null : joined;
        }
    }
}
