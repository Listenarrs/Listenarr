/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2025 Robbie Davis
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

namespace Listenarr.Domain.Models
{
    public class AudibleBookMetadata
    {
        // Use single canonical ASIN property to avoid JSON property name collisions
        public string? Asin { get; set; }
        public string? Source { get; set; } // "Audible" or "Amazon" to track metadata source
        public string? Title { get; set; }
        public string? Subtitle { get; set; }
        public List<string>? Authors { get; set; }
        public string? ImageUrl { get; set; }
        public string? PublishYear { get; set; }
        public string? PublishedDate { get; set; } // Full date (YYYY-MM-DD) for calendar/timeline features
        public string? Series { get; set; }
        public string? SeriesNumber { get; set; }
        public string? Description { get; set; }
        public List<string>? Genres { get; set; }
        public List<string>? Tags { get; set; }
        public List<string>? Narrators { get; set; }
        public List<string> Isbn { get; set; } = new();
        // OpenLibrary identifier when metadata originates from OpenLibrary
        public string? OpenLibraryId { get; set; }
        // (Asin moved to top to be the canonical ASIN property)
        public string? Publisher { get; set; }
        public string? Language { get; set; }
        public int? Runtime { get; set; }
        public string? Version { get; set; }
        public bool Explicit { get; set; }
        public bool Abridged { get; set; }
        // Legacy fields for compatibility
        public string? Author { get; set; }
        public string? Narrator { get; set; }
    }
}

