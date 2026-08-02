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

using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Listenarr.Domain.Audiobooks
{
    public class Audiobook
    {
        [Key]
        public int Id { get; set; }
        public string? Title { get; set; }
        public string? Subtitle { get; set; }
        public List<string>? Authors { get; set; }
        // ASINs for authors resolved from Audible (allows author images to be cached/served)
        public List<string>? AuthorAsins { get; set; }
        public string? ImageUrl { get; set; }
        public string? PublishYear { get; set; }
        public string? PublishedDate { get; set; } // Full ISO 8601 date for calendar/timeline features
        public string? Series { get; set; }
        public string? SeriesNumber { get; set; }
        public List<AudiobookSeriesMembership>? SeriesMemberships { get; set; }
        public string? Description { get; set; }
        public List<string>? Genres { get; set; }
        public List<string>? Tags { get; set; }
        public List<string>? Narrators { get; set; }
        public List<string>? Isbn { get; set; }
        public string? Asin { get; set; }
        // OpenLibrary identifier (OLID) when the audiobook originates from OpenLibrary
        public string? OpenLibraryId { get; set; }
        // Typed external identifiers for robust metadata/image lookup and manual correction.
        public List<AudiobookExternalIdentifier>? ExternalIdentifiers { get; set; }
        public string? Publisher { get; set; }
        public string? Language { get; set; }
        public int? Runtime { get; set; }
        public string? Edition { get; set; }
        public string? Version { get; set; }
        public bool Explicit { get; set; }
        public bool Abridged { get; set; }

        // Monitoring and file management
        public bool Monitored { get; set; } = true;
        // NOTE: single-file properties are deprecated in favor of Files collection
        public string? FilePath { get; set; }
        public long? FileSize { get; set; }

        // Base path for multi-file audiobooks (common root directory).
        // Preserve the persisted filesystem syntax; mutation workflows normalize explicitly
        // using the authoritative path semantics for that root.
        public string? BasePath { get; set; }

        // Multi-file support: store zero or more file records for this audiobook
        public List<AudiobookFile>? Files { get; set; }
        public string? Quality { get; set; }

        // Quality Profile for automatic downloads
        public int? QualityProfileId { get; set; }
        public QualityProfile? QualityProfile { get; set; }

        // Automatic search tracking
        public DateTime? LastSearchTime { get; set; }

        /// <summary>
        /// Create AudioMetadata from the Audiobook as a basic metadata for imported files
        /// </summary>
        /// <returns>AudioMetada based on the audiobook retrieved metadata</returns>
        public AudioMetadata CreateBasicAudioMetadata()
        {
            return new AudioMetadata
            {
                Title = Title ?? string.Empty,
                Subtitle = Subtitle,
                Edition = Edition,
                Artist = (Authors != null && Authors.Any()) ? string.Join(", ", Authors) : string.Empty,
                AlbumArtist = (Authors != null && Authors.Any()) ? string.Join(", ", Authors) : string.Empty,
                Narrator = (Narrators != null && Narrators.Any())
                    ? string.Join(", ", Narrators.Where(n => !string.IsNullOrWhiteSpace(n)))
                    : null,
                Publisher = !string.IsNullOrWhiteSpace(Publisher) ? Publisher : null,
                Language = !string.IsNullOrWhiteSpace(Language) ? Language : null,
                Asin = !string.IsNullOrWhiteSpace(Asin) ? Asin : null,
                Series = Series,
                // Prefer audiobook's publish year when available
                Year = int.TryParse(PublishYear, out var py) ? py : (int?)null,
                // Series position / number.
                // Parsed with InvariantCulture: the source value always uses '.' as the
                // decimal separator, so parsing under the server's culture would read a
                // position of "1.5" as 15 wherever '.' is the group separator.
                // SeriesPositionRaw keeps the original either way -- a position such as
                // "1-4" (an omnibus) is real, but is not a decimal.
                SeriesPosition = !string.IsNullOrWhiteSpace(SeriesNumber)
                    && decimal.TryParse(SeriesNumber, NumberStyles.Number, CultureInfo.InvariantCulture, out var sp)
                        ? sp
                        : (decimal?)null,
                SeriesPositionRaw = !string.IsNullOrWhiteSpace(SeriesNumber) ? SeriesNumber.Trim() : null,
                // Quality string from audiobook record
                // Map into Bitrate/Format heuristically if useful; for now store textual quality
                // We'll put it into AdditionalData so FileNamingService can use Format/Bitrate/Quality
                AdditionalData = new Dictionary<string, object> { { "Quality", Quality ?? string.Empty } }
            };
        }
    }
}
