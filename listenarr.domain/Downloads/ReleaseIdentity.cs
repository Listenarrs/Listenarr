/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Security.Cryptography;
using System.Text;

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
        public static string? For(string? torrentInfoHash, string? releaseUrl)
        {
            if (!string.IsNullOrWhiteSpace(torrentInfoHash))
            {
                return "btih:" + torrentInfoHash.Trim().ToLowerInvariant();
            }

            if (string.IsNullOrWhiteSpace(releaseUrl))
            {
                return null;
            }

            var normalized = releaseUrl.Trim().ToLowerInvariant();
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            return "url:" + Convert.ToHexString(digest).ToLowerInvariant()[..32];
        }
    }
}
