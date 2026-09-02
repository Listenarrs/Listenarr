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

namespace Listenarr.Domain.Downloads
{
    /// <summary>
    /// Works out the stable identity of a release, the thing a blocklist entry is keyed on.
    ///
    /// The download client's own id is deliberately not used. A qBittorrent info-hash
    /// happens to be both, but a SABnzbd nzo_id is allocated per submission, so keying on
    /// it would produce an entry that never matches the same release again and a blocklist
    /// that silently does nothing on Usenet.
    /// </summary>
    public static class ReleaseIdentity
    {
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
