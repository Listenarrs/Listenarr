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

            if (!string.IsNullOrWhiteSpace(audiobook.RequiredNarrator))
                parts.Add(audiobook.RequiredNarrator);

            return string.Join(" ", parts);
        }

        /// <summary>
        /// Whether a search result satisfies a book's required narrator, matched against the
        /// result's structured Narrator field or its title text (many indexers only name the
        /// narrator in the title), ignoring punctuation/spacing via <see cref="NarratorNameMatcher"/>.
        /// A blank <paramref name="requiredNarrator"/> always matches.
        /// </summary>
        public static bool MatchesRequiredNarrator(SearchResult result, string? requiredNarrator)
        {
            if (string.IsNullOrWhiteSpace(requiredNarrator))
            {
                return true;
            }

            return NarratorNameMatcher.Contains($"{result.Narrator} {result.Title}", requiredNarrator);
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
