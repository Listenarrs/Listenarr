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

namespace Listenarr.Application.Downloads.Queue
{
    public static partial class SearchQueryFallbacks
    {
        [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
        private static partial Regex EditionSuffix();

        public static IReadOnlyList<string> Expand(string primaryQuery, string? title)
        {
            var candidates = new List<string> { primaryQuery };

            if (!string.IsNullOrWhiteSpace(title))
            {
                candidates.Add(title);
                candidates.Add(EditionSuffix().Replace(title, string.Empty));
            }

            return candidates
                .Select(c => c?.Trim() ?? string.Empty)
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
