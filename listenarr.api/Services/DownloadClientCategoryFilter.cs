using System;
using System.Collections.Generic;
using System.Linq;
using Listenarr.Domain.Models;

namespace Listenarr.Api.Services;

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
