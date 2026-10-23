/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text;
using System.Text.RegularExpressions;
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
        // (e.g. "Artemis Fowl" for "Artemis", or a different author's "Revival") could otherwise
        // reach automatic download selection. The automatic path scores by quality only, so these
        // results are constrained here before scoring. A loose containment match is not enough —
        // see CanAutomaticallySelectFallback.
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
                .Where(result => CanAutomaticallySelectFallback(result, audiobook))
                .ToList();
        }

        // A conservative gate for author-free fallback results before they reach automatic
        // download selection. Only "Title" and "Author - Title" release layouts (with optional
        // trailing year/format suffixes) are parsed; the complete remaining title must match the
        // book exactly after normalization, and at least one piece of corroborating evidence
        // (parsed author, Artist field, or ASIN) must agree, with any conflicting evidence
        // vetoing. Uncertain layouts and uncorroborated authorless results are deliberately
        // skipped, so this does not recover every title-only release — it refuses to grab a
        // different book (a same-word title like "Artemis Fowl", or a different author's edition).
        private static bool CanAutomaticallySelectFallback(SearchResult result, Audiobook audiobook)
        {
            if (string.IsNullOrWhiteSpace(result.Title))
            {
                return false;
            }

            var parts = Regex.Split(result.Title.Trim(), @"\s+[-–—]\s+").ToList();

            // Drop trailing year / format suffix segments ("... - 2017", "... - [M4B]").
            while (parts.Count > 1 && Regex.IsMatch(
                parts[^1],
                @"^(?:\d{4}|\[?(?:m4b|mp3|flac|aac|ogg|opus)\]?)$",
                RegexOptions.IgnoreCase))
            {
                parts.RemoveAt(parts.Count - 1);
            }

            if (parts.Count is < 1 or > 2)
            {
                return false;
            }

            var parsedAuthor = parts.Count == 2 ? parts[0] : null;
            var parsedTitle = parts[^1];

            if (!string.Equals(
                NormalizeIdentity(parsedTitle),
                NormalizeIdentity(audiobook.Title),
                StringComparison.Ordinal))
            {
                return false;
            }

            // Conflicting ASIN vetoes selection even when another field matches.
            var hasComparableAsin = !string.IsNullOrWhiteSpace(result.Asin)
                && !string.IsNullOrWhiteSpace(audiobook.Asin);
            var asinMatches = hasComparableAsin && string.Equals(
                result.Asin?.Trim(),
                audiobook.Asin?.Trim(),
                StringComparison.OrdinalIgnoreCase);
            if (hasComparableAsin && !asinMatches)
            {
                return false;
            }

            // A parsed "Author - Title" author that doesn't match the book vetoes.
            var authorMatches = parsedAuthor != null && audiobook.Authors != null
                && audiobook.Authors.Any(expected =>
                    !string.IsNullOrWhiteSpace(expected)
                    && NormalizeIdentity(expected) == NormalizeIdentity(parsedAuthor));
            if (parsedAuthor != null && !authorMatches)
            {
                return false;
            }

            // The Artist field can be a provider guess, so a populated field alone is not
            // confirmation — but a present, non-"Unknown" Artist that conflicts vetoes too.
            var hasComparableArtist = !string.IsNullOrWhiteSpace(result.Artist)
                && !string.Equals(result.Artist.Trim(), "Unknown Author", StringComparison.OrdinalIgnoreCase)
                && audiobook.Authors != null
                && audiobook.Authors.Any(expected => !string.IsNullOrWhiteSpace(expected));
            var artistMatches = hasComparableArtist
                && audiobook.Authors!.Any(expected =>
                    !string.IsNullOrWhiteSpace(expected)
                    && NormalizeIdentity(expected) == NormalizeIdentity(result.Artist!));
            if (hasComparableArtist && !artistMatches)
            {
                return false;
            }

            // Require at least one corroborating signal; uncorroborated authorless results skip.
            return authorMatches || artistMatches || asinMatches;
        }

        private static string NormalizeIdentity(string value) =>
            Regex.Replace(
                value.Normalize(NormalizationForm.FormC).ToUpperInvariant(),
                @"[^\p{L}\p{N}]+",
                " ").Trim();

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
