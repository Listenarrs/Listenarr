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

namespace Listenarr.Domain.Audiobooks
{
    public class AuthorCacheEntry
    {
        [Key]
        public int Id { get; set; }

        public string AuthorName { get; set; } = string.Empty;

        public string AuthorNameNormalized { get; set; } = string.Empty;

        public string? AuthorAsin { get; set; }

        public string Region { get; set; } = "us";

        public string? ImageUrl { get; set; }

        public string? Description { get; set; }

        public List<CachedRelatedAuthor>? SimilarAuthors { get; set; } = new();

        public List<CachedAuthorCatalogBook>? CatalogBooks { get; set; } = new();

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? LastFetchedAt { get; set; }
    }

    public class CachedRelatedAuthor
    {
        public string? Asin { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    public class CachedAuthorCatalogBook
    {
        public string? Asin { get; set; }

        public string Title { get; set; } = string.Empty;

        public string? Subtitle { get; set; }

        public List<string> Authors { get; set; } = new();

        public string? ImageUrl { get; set; }

        public int? Runtime { get; set; }

        public string? Language { get; set; }

        public string? Publisher { get; set; }

        public List<string> Narrators { get; set; } = new();

        public List<string> Genres { get; set; } = new();

        public string? Series { get; set; }

        public string? SeriesNumber { get; set; }

        public string? PublishedDate { get; set; }

        public string? Isbn { get; set; }

        public string? Link { get; set; }

        public string? MetadataSource { get; set; }
    }
}
