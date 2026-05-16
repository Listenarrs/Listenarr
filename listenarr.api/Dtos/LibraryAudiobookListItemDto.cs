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
namespace Listenarr.Api.Dtos
{
    public class LibraryAudiobookListItemDto
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string[]? Authors { get; set; }
        public string[]? Narrators { get; set; }
        public string? PublishYear { get; set; }
        public string? PublishedDate { get; set; }
        public string? Series { get; set; }
        public string? SeriesNumber { get; set; }
        public string[]? Genres { get; set; }
        public string? Asin { get; set; }
        public string? OpenLibraryId { get; set; }
        public string? Publisher { get; set; }
        public string? Language { get; set; }
        public int? Runtime { get; set; }
        public string? Edition { get; set; }
        public string? ImageUrl { get; set; }
        public bool Monitored { get; set; }
        public string? BasePath { get; set; }
        // Transitional legacy primary file summary retained for filters and upgrade compatibility.
        public string? FilePath { get; set; }
        public long? FileSize { get; set; }
        public int FileCount { get; set; }
        public string? Quality { get; set; }
        public int? QualityProfileId { get; set; }
        public string[]? AuthorAsins { get; set; }
        public bool Wanted { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
