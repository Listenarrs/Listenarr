/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text.RegularExpressions;

namespace Listenarr.Application.Search.Indexers.Common;

public static class IndexerUrlNormalizer
{
    /// <summary>
    /// Normalizes indexer URLs before orchestration so every concrete tester receives the same base shape.
    /// </summary>
    public static string NormalizeIndexerUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return rawUrl ?? string.Empty;
        }

        var url = rawUrl.Trim();

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        while (url.Contains("/api/api", StringComparison.OrdinalIgnoreCase))
        {
            url = url.Replace("/api/api", "/api", StringComparison.OrdinalIgnoreCase);
        }

        var prowlarrProxyPattern = @"/((api/v\d+(?:\.\d+)?/indexer/\d+)|\d+)/api$";
        if (url.EndsWith("/api", StringComparison.OrdinalIgnoreCase) &&
            !Regex.IsMatch(url, prowlarrProxyPattern, RegexOptions.IgnoreCase))
        {
            url = url[..^4];
        }

        return url.TrimEnd('/');
    }

    public static string BuildApiEndpoint(string? rawUrl)
    {
        var target = NormalizeIndexerUrl(rawUrl).TrimEnd('/');
        return target.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? target : target + "/api";
    }
}
