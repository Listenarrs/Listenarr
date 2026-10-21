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

namespace Listenarr.Domain.Audiobooks
{
    public class AudioMetadata
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public string AlbumArtist { get; set; } = string.Empty;
        public string Genre { get; set; } = string.Empty;
        public int? Year { get; set; }
        public int? TrackNumber { get; set; }
        public int? TrackTotal { get; set; }
        public int? DiscNumber { get; set; }
        public int? DiscTotal { get; set; }
        public TimeSpan Duration { get; set; }
        public string? Format { get; set; }
        // Container (human-friendly): e.g., M4B, MP3, FLAC
        public string? Container { get; set; }
        // Audio codec: e.g., aac, mp3, opus
        public string? Codec { get; set; }
        public int? BitRate { get; set; }
        public int? SampleRate { get; set; }
        public int? Channels { get; set; }
        public string? Isbn { get; set; }
        public string? Asin { get; set; }
        public string? Description { get; set; }
        public string? Subtitle { get; set; }
        public string? Edition { get; set; }
        public string? Narrator { get; set; }
        public string? Publisher { get; set; }
        public DateTime? PublishDate { get; set; }
        public string? Language { get; set; }
        public string? Series { get; set; }
        public decimal? SeriesPosition { get; set; }

        /// <summary>
        /// The series position exactly as the metadata source gave it.
        /// <para>
        /// Audible/Audnexus report a position as a string, and it is not always a number:
        /// an omnibus can sit at "1-4", a prequel at "0", a novella at "1.5". Those values
        /// are meaningful, but they do not all fit in <see cref="SeriesPosition"/>, so a
        /// position that fails to parse would otherwise be indistinguishable from a book
        /// that has no series position at all.
        /// </para>
        /// <para>
        /// Naming prefers this over <see cref="SeriesPosition"/> so that a real position is
        /// never silently replaced by a track number.
        /// </para>
        /// </summary>
        public string? SeriesPositionRaw { get; set; }
        public byte[]? CoverArt { get; set; }
        public string? CoverArtUrl { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; } = [];

        /// <summary>
        /// Update empty attributes with the given metadata
        /// </summary>
        /// <param name="value">New data to complete the current one</param>
        public void Update(AudioMetadata value)
        {
            if (string.IsNullOrWhiteSpace(Title))
            {
                Title = value.Title;
            }
            if (string.IsNullOrWhiteSpace(Artist))
            {
                Artist = value.Artist;
            }
            if (string.IsNullOrWhiteSpace(Album))
            {
                Album = value.Album;
            }

            if (!SeriesPosition.HasValue && value.SeriesPosition.HasValue)
                SeriesPosition = value.SeriesPosition;
            if (string.IsNullOrWhiteSpace(SeriesPositionRaw) && !string.IsNullOrWhiteSpace(value.SeriesPositionRaw))
                SeriesPositionRaw = value.SeriesPositionRaw;
            if (!TrackNumber.HasValue && value.TrackNumber.HasValue)
                TrackNumber = value.TrackNumber;
            if (!DiscNumber.HasValue && value.DiscNumber.HasValue)
                DiscNumber = value.DiscNumber;
            if (!Year.HasValue && value.Year.HasValue)
                Year = value.Year;
            if (!BitRate.HasValue && value.BitRate.HasValue)
                BitRate = value.BitRate;
            if (string.IsNullOrWhiteSpace(Format) && !string.IsNullOrWhiteSpace(value.Format))
                Format = value.Format;
        }
    }
}
