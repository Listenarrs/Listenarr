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
namespace Listenarr.Application.Audiobooks
{
    public class AudiobookFileDto
    {
        public int Id { get; set; }
        public string? Path { get; set; }
        public long? Size { get; set; }
        public double? DurationSeconds { get; set; }
        public string? Format { get; set; }
        public string? Container { get; set; }
        public string? Codec { get; set; }
        public int? Bitrate { get; set; }
        public int? SampleRate { get; set; }
        public int? Channels { get; set; }
        public DateTime? CreatedAt { get; set; }
        public string? Source { get; set; }
    }

    public class AudiobookDto
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string? Subtitle { get; set; }
        public string[]? Authors { get; set; }
        public string[]? Narrators { get; set; }
        public string? Asin { get; set; }
        public List<string>? Isbn { get; set; }
        public string? Language { get; set; }
        public List<string>? Genres { get; set; }
        public string[]? Tags { get; set; }
        public string? Description { get; set; }
        public string? PublishYear { get; set; }
        public string? PublishedDate { get; set; }
        public string? Series { get; set; }
        public string? SeriesNumber { get; set; }
        public AudiobookSeriesMembershipDto[]? SeriesMemberships { get; set; }
        public bool? Monitored { get; set; }
        public string? FilePath { get; set; }
        public long? FileSize { get; set; }
        public string? BasePath { get; set; }
        public AudiobookFileDto[]? Files { get; set; }
        public string? ImageUrl { get; set; }
        public string? Quality { get; set; }
        public int? QualityProfileId { get; set; }
        public string? Edition { get; set; }
        public string? Version { get; set; }
        public bool? Abridged { get; set; }
        public bool? Explicit { get; set; }
        public string? FilePathHash { get; set; }
        public bool? Wanted { get; set; }
    }

    public class AudiobookSeriesMembershipDto
    {
        public int Id { get; set; }
        public string? SeriesName { get; set; }
        public string? SeriesNumber { get; set; }
        public string? SeriesAsin { get; set; }
        public bool IsPrimary { get; set; }
        public int SortOrder { get; set; }
    }
}
