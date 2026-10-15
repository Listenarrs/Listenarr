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

namespace Listenarr.Application.Audiobooks.Catalog
{
    // A library book whose Audible identity is incomplete, so Rescan metadata can't rely on it
    public class LibraryIssueItem
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string[]? Authors { get; set; }
        public string? Series { get; set; }
        public string? SeriesNumber { get; set; }
        public string? ImageUrl { get; set; }
        public string? Asin { get; set; }
        public string? Region { get; set; }
        // missing_title, missing_asin, invalid_asin, missing_region (empty for an ignored book that
        // has none left)
        public string[] Issues { get; set; } = Array.Empty<string>();
        // The user keeps this book out of the issues (and their count)
        public bool Ignored { get; set; }
    }

    public static class LibraryIssueCodes
    {
        public const string MissingTitle = "missing_title";
        public const string MissingAsin = "missing_asin";
        public const string InvalidAsin = "invalid_asin";
        public const string MissingRegion = "missing_region";
    }
}
