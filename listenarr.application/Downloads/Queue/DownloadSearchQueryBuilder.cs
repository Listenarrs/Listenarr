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
        /// <summary>
        /// Builds the indexer query for a download search.
        /// </summary>
        /// <remarks>
        /// The query is composed by <see cref="AudiobookSearchQueryBuilder"/> rather than
        /// here, so that this path and the automatic sweep ask indexers the same question.
        /// </remarks>
        public static string Build(Audiobook audiobook)
        {
            return AudiobookSearchQueryBuilder.Build(audiobook);
        }

        // Author-free fallback query: release names often omit the author, so the title+author
        // query can return nothing even when the book is available. The shared query title only,
        // the same as the automatic sweep's fallback: no author, no series.
        public static string BuildTitleFallback(Audiobook audiobook)
        {
            return AudiobookSearchQueryBuilder.BuildQueryTitle(audiobook.Title);
        }
    }
}
