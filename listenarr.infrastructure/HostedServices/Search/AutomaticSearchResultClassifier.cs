/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.HostedServices.Search
{
    internal sealed class AutomaticSearchResultClassifier
    {
        private readonly ILogger _logger;

        public AutomaticSearchResultClassifier(ILogger logger)
        {
            _logger = logger;
        }

        public string BuildSearchQuery(Audiobook audiobook)
        {
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(audiobook.Title))
                parts.Add(audiobook.Title);

            if (audiobook.Authors != null && audiobook.Authors.Any())
                parts.Add(audiobook.Authors.First());

            if (!string.IsNullOrEmpty(audiobook.Series))
                parts.Add(audiobook.Series);

            return string.Join(" ", parts);
        }

        // Author-free fallback query: many release names omit the author, so the full
        // title+author query can return nothing even when the book is available. A title
        // (+ series) query recovers those. Callers must constrain the broadened results to
        // titles that still match the book before grabbing.
        public string BuildTitleFallbackSearchQuery(Audiobook audiobook)
        {
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(audiobook.Title))
                parts.Add(audiobook.Title);

            if (!string.IsNullOrEmpty(audiobook.Series))
                parts.Add(audiobook.Series);

            return string.Join(" ", parts);
        }

        // The author-free fallback broadens the search, so a same-title/different-book release
        // could come back. The automatic path scores by quality only (relevance comes from the
        // query), so constrain fallback results to titles that still match the book before they
        // reach scoring — otherwise an unrelated release could be grabbed.
        public List<SearchResult> ConstrainFallbackResultsToTitle(
            List<SearchResult> fallbackResults,
            Audiobook audiobook)
        {
            if (fallbackResults == null || fallbackResults.Count == 0
                || string.IsNullOrWhiteSpace(audiobook.Title))
            {
                return new List<SearchResult>();
            }

            return fallbackResults
                .Where(result => TitleUtils.IsMatchingTitle(result.Title, audiobook.Title))
                .ToList();
        }

        public bool IsTorrentResult(SearchResult result)
        {
            if (!string.IsNullOrEmpty(result.DownloadType))
            {
                if (string.Equals(result.DownloadType, DirectDownloadMetadataKeys.ClientId, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                else if (result.DownloadType == "Torrent")
                {
                    return true;
                }
                else if (result.DownloadType == "Usenet")
                {
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(result.NzbUrl))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(result.MagnetLink) || !string.IsNullOrEmpty(result.TorrentUrl))
            {
                return true;
            }

            _logger.LogWarning("Unable to determine result type for '{Title}' from source '{Source}'. No MagnetLink, TorrentUrl, or NzbUrl found. Defaulting to NZB.",
                result.Title, result.Source);
            return false;
        }
    }
}
