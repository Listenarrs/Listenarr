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
    /// <summary>
    /// Simplified search result DTO for advanced searches (metadata-focused, without torrent/NZB fields)
    /// Reduces payload size by excluding indexer-specific properties
    /// </summary>
    public class SimpleSearchResultDto
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string? Subtitle { get; set; }
        public string? Description { get; set; }
        public string? Publisher { get; set; }
        public string? Language { get; set; }
        public int? Runtime { get; set; }
        public string? Narrator { get; set; }
        public string? ImageUrl { get; set; }
        public string? Asin { get; set; }
        public string? Isbn { get; set; }
        public string? Series { get; set; }
        public string? SeriesNumber { get; set; }
        public string? ProductUrl { get; set; }
        public string? PublishedDate { get; set; }
        public string? PublishYear { get; set; }
        public List<string>? Genres { get; set; }
        public bool IsEnriched { get; set; }
        public string? MetadataSource { get; set; }
        public string Source { get; set; } = string.Empty;
        public string? SourceLink { get; set; }
        public int Score { get; set; }
    }
}
