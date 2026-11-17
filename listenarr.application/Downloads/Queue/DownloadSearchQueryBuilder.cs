/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */


namespace Listenarr.Application.Downloads.Queue
{
    internal static class DownloadSearchQueryBuilder
    {
        public static string Build(Audiobook audiobook)
        {
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(audiobook.Title))
            {
                parts.Add(audiobook.Title);
            }

            if (audiobook.Authors != null && audiobook.Authors.Any())
            {
                parts.Add(audiobook.Authors.First());
            }

            return string.Join(" ", parts);
        }

        // Author-free fallback query: release names often omit the author, so the title+author
        // query can return nothing even when the book is available. Title only; the automatic
        // sweep's fallback also appends the series.
        public static string BuildTitleFallback(Audiobook audiobook)
        {
            return audiobook.Title ?? string.Empty;
        }
    }
}
