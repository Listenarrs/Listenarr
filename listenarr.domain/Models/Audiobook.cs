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

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Listenarr.Domain.Utils;

namespace Listenarr.Domain.Models
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

        // Base path for multi-file audiobooks (common root directory)
        public string? BasePath {
            get;
            set
            {
                field = FileUtils.NormalizeStoredPath(value);
            }
        }

        // Multi-file support: store zero or more file records for this audiobook
        public List<AudiobookFile>? Files { get; set; }
        public string? Quality { get; set; }

        // Quality Profile for automatic downloads
        public int? QualityProfileId { get; set; }
        public QualityProfile? QualityProfile { get; set; }

        // Automatic search tracking
        public DateTime? LastSearchTime { get; set; }
    }
}

