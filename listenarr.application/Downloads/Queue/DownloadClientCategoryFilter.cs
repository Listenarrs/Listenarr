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

namespace Listenarr.Application.Downloads.Queue;

public static class DownloadClientCategoryFilter
{
    public static string? GetConfiguredCategory(DownloadClientConfiguration client)
    {
        if (client?.Settings == null || !client.Settings.TryGetValue("category", out var categoryObj))
        {
            return null;
        }

        var category = categoryObj?.ToString()?.Trim();
        return string.IsNullOrWhiteSpace(category) ? null : category;
    }

    public static bool Matches(string? configuredCategory, string? itemCategory)
    {
        if (string.IsNullOrWhiteSpace(configuredCategory))
        {
            return true;
        }

        return string.Equals(
            configuredCategory.Trim(),
            (itemCategory ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesAny(string? configuredCategory, IEnumerable<string?> itemCategories)
    {
        if (string.IsNullOrWhiteSpace(configuredCategory))
        {
            return true;
        }

        return itemCategories.Any(category => Matches(configuredCategory, category));
    }
}
