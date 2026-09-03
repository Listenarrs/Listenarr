/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Listenarr.Domain.Search;

namespace Listenarr.Domain.Downloads
{
    /// <summary>
    /// Works out the stable identity of a release, the thing a blocklist entry is keyed on.
    ///
    /// The download client's own id is deliberately not used. A qBittorrent info-hash
    /// happens to be both, but a SABnzbd nzo_id is allocated per submission, so keying on
    /// it would produce an entry that never matches the same release again and a blocklist
    /// that silently does nothing on Usenet.
    ///
    /// One rule governs the rest of it. The identity is derived from a <see cref="SearchResult"/>,
    /// once, at the moment the release is grabbed, and stamped onto the <see cref="Download"/> that
    /// the grab creates. The failure path reads that stamp back rather than deriving its own.
    /// Three defects in this feature had the same shape, two sides deriving a key independently
    /// and drifting apart: first a per-fetch Usenet URL that differed between grab and failure,
    /// then a size read from Download.TotalSize, which the queue poller overwrites from the
    /// client's snapshot while the search side still sees the size the indexer advertised.
    /// Deriving in one place is the only arrangement in which the two cannot disagree.
    /// </summary>
    public static class ReleaseIdentity
    {
        /// <summary>
        /// Where the grab-time identity lives on a Download. <see cref="Download.Metadata"/> already
        /// carries ClientDownloadId and TorrentHash, so a release identity is at home there.
        /// </summary>
        public const string MetadataKey = "ReleaseIdentity";

        private const string TorrentHashMetadataKey = "TorrentHash";

        /// <summary>
        /// The identity of a release as the indexer advertised it. This is the only place that
        /// decides which fields of a search result make up the key, so the grab side and the
        /// search-filter side cannot pick different ones.
        /// </summary>
        public static string? For(SearchResult result)
        {
            if (result is null)
            {
                return null;
            }

            return For(
                TorrentHashFrom(result.MagnetLink),
                FirstNonEmpty(result.NzbUrl, result.TorrentUrl, result.MagnetLink, result.SourceLink),
                result.Title,
                result.Size);
        }

        /// <summary>
        /// The identity of a release that has already been grabbed. Prefers the value stamped on
        /// the download when it was created, because every field this could otherwise be
        /// recomputed from is mutable after the grab.
        /// </summary>
        public static string? ForGrabbed(Download download)
        {
            if (download is null)
            {
                return null;
            }

            var stamped = download.GetMetadataString(MetadataKey);
            if (!string.IsNullOrWhiteSpace(stamped))
            {
                return stamped;
            }

            // A download created before the stamp existed. Recomputing is all that is left for it,
            // so prefer the fields that do not move: ExpectedFileSize is copied from the search
            // result and never written again, whereas TotalSize is overwritten from the download
            // client's queue snapshot in QueueItemConverter and three times over in the
            // direct-download worker. Dropping back to TotalSize only when there is no expected
            // size keeps this at least as good as the behaviour it replaces.
            return For(
                download.GetMetadataString(TorrentHashMetadataKey),
                download.OriginalUrl,
                download.Title,
                download.ExpectedFileSize ?? (download.TotalSize > 0 ? download.TotalSize : null));
        }

        public static string? For(string? torrentInfoHash, string? releaseUrl, string? title, long? size)
        {
            // A torrent info-hash is the release, across indexers and across submissions.
            if (!string.IsNullOrWhiteSpace(torrentInfoHash))
            {
                return "btih:" + torrentInfoHash.Trim().ToLowerInvariant();
            }

            // Title and size next, and ahead of the URL, because a Usenet release URL is a
            // per-fetch download link. The indexer mints a new one with a new token every time
            // the release is grabbed, so hashing it produces a different identity on every
            // failure: a fresh blocklist row each time and never a match. That is the failure
            // this class's own summary warns about for client ids, and the URL fallback had the
            // same property.
            //
            // Observed on a live install: one dead Usenet post grabbed several hundred times over
            // half a day for a single book, identical title and identical size to the byte every
            // time, and a different URL hash every time.
            //
            // Size is in the key because a title alone can be shared by genuinely different
            // releases. Two releases agreeing on both the title and the exact byte count are the
            // same release for this purpose.
            var normalizedTitle = NormalizeTitle(title);
            if (normalizedTitle.Length > 0)
            {
                return "name:" + Digest(normalizedTitle + "|" + (size?.ToString(CultureInfo.InvariantCulture) ?? "?"));
            }

            // Last resort, for a release with no usable title. Still better than nothing for a
            // stable direct link, and no worse than the previous behaviour.
            if (string.IsNullOrWhiteSpace(releaseUrl))
            {
                return null;
            }

            return "url:" + Digest(releaseUrl.Trim().ToLowerInvariant());
        }

        /// <summary>
        /// Pull the info-hash out of a magnet so a torrent is recognised as the same release
        /// even when the indexer hands back a different URL for it than last time.
        /// </summary>
        public static string? TorrentHashFrom(string? magnetLink)
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

        private static string? FirstNonEmpty(params string?[] candidates)
            => candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        private static string NormalizeTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            // Collapse whitespace and fold case, so a release that comes back with different
            // spacing or capitalisation is still the same release.
            var collapsed = Regex.Replace(title.Trim(), @"\s+", " ");
            return collapsed.ToLowerInvariant();
        }

        private static string Digest(string value)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(digest).ToLowerInvariant()[..32];
        }
    }
}
