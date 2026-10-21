/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */

namespace Listenarr.Application.Search.DomainRotation
{
    /// <summary>
    /// Static seed map of known mirror domains per indexer Implementation string.
    /// Add new indexers here to extend domain rotation support.
    /// </summary>
    public static class IndexerMirrorCatalog
    {
        public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Seeds =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["AnnasArchive"] = ["annas-archive.org", "annas-archive.se", "annas-archive.li", "annas-archive.cc"],
                ["AudioBookBay"] = ["audiobookbay.lu", "audiobookbay.se", "audiobookbay.is", "audiobookbay.fi", "theaudiobookbay.se", "audiobookbay.nl"],
            };

        public static bool IsKnown(string implementation) =>
            Seeds.ContainsKey(implementation);
    }
}
