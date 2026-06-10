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
using System.Globalization;

namespace Listenarr.Domain.Models.Naming
{
    /// <summary>
    /// Normalized, source-agnostic metadata used to build audiobook file/folder names.
    /// The various import/rename flows map their own source type (<see cref="Audiobook"/>,
    /// <see cref="AudioMetadata"/>, <see cref="AudibleBookMetadata"/>) into a single
    /// <see cref="NamingContext"/> so that token substitution, sanitization and path assembly
    /// live in exactly one place (the file naming service). Source-specific quirks (e.g. picking an
    /// author from noisy file tags) belong in the <c>From(...)</c> factories; processing belongs in
    /// the naming service.
    /// </summary>
    public sealed record NamingContext
    {
        public IReadOnlyList<string> Authors { get; init; } = [];
        public IReadOnlyList<string> Narrators { get; init; } = [];
        public string? Series { get; init; }
        public string? SeriesNumber { get; init; }
        public string? Title { get; init; }
        public string? Subtitle { get; init; }
        public string? Edition { get; init; }
        public string? Publisher { get; init; }
        public string? Language { get; init; }
        public string? Asin { get; init; }
        public string? Year { get; init; }
        public string? Quality { get; init; }
        public int? DiskNumber { get; init; }
        public int? ChapterNumber { get; init; }

        /// <summary>Build from a library <see cref="Audiobook"/> entity (rename, library add, base-path compute).</summary>
        public static NamingContext From(Audiobook audiobook) => new()
        {
            Authors = audiobook.Authors ?? [],
            Narrators = audiobook.Narrators ?? [],
            Series = audiobook.Series,
            SeriesNumber = audiobook.SeriesNumber,
            Title = audiobook.Title,
            Subtitle = audiobook.Subtitle,
            Edition = audiobook.Edition,
            Publisher = audiobook.Publisher,
            Language = audiobook.Language,
            Asin = audiobook.Asin,
            Year = audiobook.PublishYear,
            Quality = audiobook.Quality,
            DiskNumber = null,
            ChapterNumber = null,
        };

        /// <summary>
        /// Build from file-extracted <see cref="AudioMetadata"/> (import after download, manual import).
        /// The author is taken straight from the Artist tag — matching the historical import behavior —
        /// so a book whose author also narrates it (Artist == Narrator) keeps the author rather than
        /// collapsing to "Unknown Author".
        /// </summary>
        public static NamingContext From(AudioMetadata metadata)
        {
            return new NamingContext
            {
                Authors = string.IsNullOrWhiteSpace(metadata.Artist) ? [] : [metadata.Artist],
                Narrators = string.IsNullOrWhiteSpace(metadata.Narrator) ? [] : [metadata.Narrator!],
                Series = metadata.Series,
                // Match the historical AudioMetadata behavior: fall back to the track number when no
                // explicit series position is present.
                SeriesNumber = FirstNonEmpty(
                    metadata.SeriesPosition?.ToString(CultureInfo.InvariantCulture),
                    metadata.TrackNumber?.ToString(CultureInfo.InvariantCulture)),
                Title = metadata.Title,
                Subtitle = metadata.Subtitle,
                Edition = metadata.Edition,
                Publisher = metadata.Publisher,
                Language = metadata.Language,
                Asin = metadata.Asin,
                Year = metadata.Year?.ToString(CultureInfo.InvariantCulture),
                Quality = FirstNonEmpty(metadata.BitRate.HasValue ? metadata.BitRate + "kbps" : null, metadata.Format),
                DiskNumber = metadata.DiscNumber,
                ChapterNumber = metadata.TrackNumber,
            };
        }

        /// <summary>
        /// Build from provider <see cref="AudibleBookMetadata"/> (library add, path preview). Field coverage
        /// follows <see cref="AudibleBookMetadata.ToAudiobook"/> — a field it does not map is absent here.
        /// </summary>
        public static NamingContext From(AudibleBookMetadata metadata) => From(metadata.ToAudiobook());

        private static string? FirstNonEmpty(params string?[] candidates)
            => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
    }
}
