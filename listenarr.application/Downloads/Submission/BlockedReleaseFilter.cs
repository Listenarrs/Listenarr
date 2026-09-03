/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Application.Downloads.Contracts;
using Listenarr.Domain.Downloads;
using Listenarr.Domain.Audiobooks;
using Listenarr.Domain.Search;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Submission
{
    /// <summary>
    /// Drops candidates already blocked for this book, before one of them is selected.
    ///
    /// Filtering here rather than at display time is what stops the loop: a search that
    /// only turns up releases already known to fail for this book ends at the existing
    /// "no acceptable search results" answer, which is a terminal state the pipeline
    /// already has, rather than a new error.
    /// </summary>
    public static class BlockedReleaseFilter
    {
        public static async Task<List<QualityScore>> ExcludeAsync(
            IBlocklistService blocklistService,
            int audiobookId,
            List<QualityScore> scoredResults,
            ILogger logger)
        {
            if (scoredResults.Count == 0)
            {
                return scoredResults;
            }

            var blocked = await blocklistService.GetBlockedIdentifiersAsync(audiobookId);
            if (blocked.Count == 0)
            {
                return scoredResults;
            }

            var blockedSet = new HashSet<string>(blocked, StringComparer.OrdinalIgnoreCase);
            var kept = scoredResults
                .Where(scored => !IsBlocked(scored.SearchResult, blockedSet))
                .ToList();

            var dropped = scoredResults.Count - kept.Count;
            if (dropped > 0)
            {
                logger.LogInformation(
                    "Skipped {Dropped} search result(s) already blocked for audiobook {AudiobookId}",
                    dropped,
                    audiobookId);
            }

            return kept;
        }

        private static bool IsBlocked(SearchResult result, HashSet<string> blockedSet)
        {
            var releaseUrl = FirstNonEmpty(result.NzbUrl, result.TorrentUrl, result.MagnetLink, result.SourceLink);
            // Title and size must be passed here exactly as the blocking side passes them, or the
            // two compute different identities and nothing ever matches. That is the whole of the
            // defect this replaced: the identity was derived from a per-fetch URL, so the write
            // side and the read side agreed on a value that was different every time.
            var identifier = ReleaseIdentity.For(
                TorrentHashFrom(result.MagnetLink),
                releaseUrl,
                result.Title,
                result.Size);
            return identifier is not null && blockedSet.Contains(identifier);
        }

        private static string? FirstNonEmpty(params string?[] candidates)
            => candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        /// <summary>
        /// Pull the info-hash out of a magnet so a torrent is recognised as the same release
        /// even when the indexer hands back a different URL for it than last time.
        /// </summary>
        private static string? TorrentHashFrom(string? magnetLink)
        {
            if (string.IsNullOrWhiteSpace(magnetLink))
            {
                return null;
            }

            const string marker = "xt=urn:btih:";
            var index = magnetLink.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return null;
            }

            var rest = magnetLink[(index + marker.Length)..];
            var end = rest.IndexOf('&');
            return end < 0 ? rest : rest[..end];
        }
    }
}
