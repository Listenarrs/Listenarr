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

using System.Text.Json.Serialization;

namespace Listenarr.Domain.Search
{
    /// <summary>
    /// Base class for all search results with common properties
    /// </summary>
    public abstract class BaseSearchResult
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        // Backwards-compatibility: some tests and callers expect `Author` property name.
        public string Author { get => Artist; set => Artist = value; }
        public string Album { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string? SourceLink { get; set; } // Direct link to the source
        public string PublishedDate { get; set; } = string.Empty;
        public string Format { get; set; } = string.Empty;
        public int Score { get; set; }
    }

    /// <summary>
    /// Search result from torrent/NZB indexers
    /// </summary>
    public class IndexerSearchResult : BaseSearchResult
    {
        public long Size { get; set; }
        public int? Seeders { get; set; }
        public int? Leechers { get; set; }
        public int Grabs { get; set; }
        public int Files { get; set; }
        public string MagnetLink { get; set; } = string.Empty;
        public string TorrentUrl { get; set; } = string.Empty;
        public string NzbUrl { get; set; } = string.Empty;
        [JsonIgnore]
        public byte[]? TorrentFileContent { get; set; }
        [JsonIgnore]
        public string? TorrentFileName { get; set; }
        public string DownloadType { get; set; } = string.Empty; // "Torrent", "Usenet", or "DDL"
        public string? Quality { get; set; }

        // Indexer metadata used to resolve tracker-specific downloads
        public int? IndexerId { get; set; }
        public string? IndexerImplementation { get; set; }

        // Link to the indexer page for this result
        public string? ResultUrl { get; set; }
        public string? DownloadReference { get; set; }

        // Lightweight metadata occasionally parsed from indexer responses
        public string? Description { get; set; }
        public string? Language { get; set; }
        public string? Publisher { get; set; }
        public string? Narrator { get; set; }
    }

    /// <summary>
    /// Search result from audiobook metadata sources (Audible, Audnexus, etc.)
    /// </summary>
    public class MetadataSearchResult : BaseSearchResult
    {
        // Additional properties for enhanced audiobook metadata
        public string? Description { get; set; }
        public string? Publisher { get; set; }
        // Subtitle provided by metadata sources (e.g., Audible/Audible)
        public string? Subtitle { get; set; }
        // Publish year as provided by metadata (convenience for UI)
        public string? PublishYear { get; set; }
        public string? Language { get; set; }
        public int? Runtime { get; set; }
        public string? Narrator { get; set; }
        public string? ImageUrl { get; set; }
        public string? Asin { get; set; }
        public List<string> Isbn { get; set; } = new();
        public string? Series { get; set; }
        public string? SeriesNumber { get; set; }
        public string? ProductUrl { get; set; } // Direct link to Amazon/Audible product page
        public List<string>? Genres { get; set; } // Genres from metadata sources (e.g., Audible)
        // Indicates this result had a successful full metadata enrichment pass
        public bool IsEnriched { get; set; }
        // Tracks which metadata API was used to enrich this result
        public string? MetadataSource { get; set; }
        public string? Subtitles { get; set; }

        // New indexer-derived properties
        public int Grabs { get; set; }
        public int Files { get; set; }
    }

    /// <summary>
    /// Legacy SearchResult class - kept for backwards compatibility
    /// Combines both indexer and metadata properties
    /// </summary>
    public class SearchResult : BaseSearchResult
    {
        // Indexer-specific properties
        public long Size { get; set; }
        public int? Seeders { get; set; }
        public int? Leechers { get; set; }
        public int Grabs { get; set; }
        public int Files { get; set; }
        public string MagnetLink { get; set; } = string.Empty;
        public string TorrentUrl { get; set; } = string.Empty;
        public string NzbUrl { get; set; } = string.Empty;
        [JsonIgnore]
        public byte[]? TorrentFileContent { get; set; }
        [JsonIgnore]
        public string? TorrentFileName { get; set; }
        public string DownloadType { get; set; } = string.Empty; // "Torrent", "Usenet", or "DDL"
        public string? Quality { get; set; }

        // Indexer metadata used to resolve tracker-specific downloads
        public int? IndexerId { get; set; }
        public string? IndexerImplementation { get; set; }

        // Link to the indexer page for this result
        public string? ResultUrl { get; set; }
        public string? DownloadReference { get; set; }

        // Metadata-specific properties
        public string? Description { get; set; }
        public string? Publisher { get; set; }
        // Subtitle provided by metadata sources (e.g., Audible/Audible)
        public string? Subtitle { get; set; }
        // Publish year as provided by metadata (convenience for UI)
        public string? PublishYear { get; set; }
        public string? Language { get; set; }
        public int? Runtime { get; set; }
        public string? Narrator { get; set; }
        public string? ImageUrl { get; set; }
        public string? Asin { get; set; }
        public List<string> Isbn { get; set; } = new();
        public string? Series { get; set; }
        public string? SeriesNumber { get; set; }
        public string? ProductUrl { get; set; } // Direct link to Amazon/Audible product page
        public List<string>? Genres { get; set; } // Genres from metadata sources (e.g., Audible)
        // Indicates this result had a successful full metadata enrichment pass (Audible product scrape)
        public bool IsEnriched { get; set; }
        // Tracks which metadata API was used to enrich this result (e.g., "Audible", "Audnexus", "Audible (Scraped)")
        public string? MetadataSource { get; set; }
        public string? Subtitles { get; set; }
    }

    public class SearchAndDownloadResult
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? DownloadId { get; set; }
        public string? IndexerUsed { get; set; }
        public string? DownloadClientUsed { get; set; }
        public SearchResult? SearchResult { get; set; }
    }

    /// <summary>
    /// DTO representing indexer results in a Prowlarr-like shape for the public API
    /// </summary>
    public class IndexerResultDto
    {
        public string? Guid { get; set; }
        public int? Age { get; set; }
        public double? AgeHours { get; set; }
        public double? AgeMinutes { get; set; }
        public long Size { get; set; }
        public int Files { get; set; }
        public int Grabs { get; set; }
        public int? IndexerId { get; set; }
        public string? Indexer { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? SortTitle { get; set; }
        public int ImdbId { get; set; }
        public int TmdbId { get; set; }
        public int TvdbId { get; set; }
        public int TvMazeId { get; set; }
        public string? PublishDate { get; set; }
        public string? DownloadUrl { get; set; }
        public string? InfoUrl { get; set; }
        public List<string> IndexerFlags { get; set; } = new();
        public List<object> Categories { get; set; } = new();
        public int? Seeders { get; set; }
        public int? Leechers { get; set; }
        public string? Protocol { get; set; }
        public string? FileName { get; set; }
        public string? DownloadReference { get; set; }
        // Filetype as provided/derived from indexer (e.g., MP3, M4B)
        [System.Text.Json.Serialization.JsonPropertyName("filetype")]
        public string? FileType { get; set; }
        // Language code or parsed language (lang_code / ENG -> English)
        [System.Text.Json.Serialization.JsonPropertyName("lang_code")]
        public string? Language { get; set; }
    }

    /// <summary>
    /// Response wrapper for search operations that can contain different types of results
    /// </summary>
    public class SearchResponse
    {
        public List<IndexerResultDto> IndexerResults { get; set; } = new();
        public List<MetadataSearchResult> MetadataResults { get; set; } = new();
        public int TotalCount => IndexerResults.Count + MetadataResults.Count;
    }

    /// <summary>
    /// Conversion methods for search result types
    /// </summary>
    public static class SearchResultConverters
    {
        // Helper: do a lightweight language detection on a text block when Indexer did not provide language
        private static string? DetectLanguageFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text.ToUpperInvariant();
            if (System.Text.RegularExpressions.Regex.IsMatch(t, "\\b(ENG|EN)\\b")) return "English";
            if (System.Text.RegularExpressions.Regex.IsMatch(t, "\\b(FRE|FR)\\b")) return "French";
            if (System.Text.RegularExpressions.Regex.IsMatch(t, "\\b(GER|DE)\\b")) return "German";
            if (System.Text.RegularExpressions.Regex.IsMatch(t, "\\b(DUT|NL)\\b")) return "Dutch";
            return null;
        }

        // Normalize language tokens that should be treated as absent (e.g., unknown)
        private static string? NormalizeLanguage(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var v = value.Trim();
            if (string.Equals(v, "unknown", StringComparison.OrdinalIgnoreCase)) return null;
            return v;
        }

        // Normalize quality tokens that should be treated as absent (e.g., unknown)
        private static string? NormalizeQuality(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var v = value.Trim();
            if (string.Equals(v, "unknown", StringComparison.OrdinalIgnoreCase)) return null;
            return v;
        }

        public static MetadataSearchResult ToMetadata(SearchResult result)
        {
            return new MetadataSearchResult
            {
                Id = result.Id,
                Title = result.Title,
                Artist = result.Artist,
                Album = result.Album,
                Category = result.Category,
                Source = result.Source,
                SourceLink = result.SourceLink,
                PublishedDate = result.PublishedDate,
                Format = result.Format,
                Score = result.Score,
                Description = result.Description,
                Subtitle = result.Subtitle,
                Publisher = result.Publisher,
                Language = result.Language,
                Runtime = result.Runtime,
                Narrator = result.Narrator,
                ImageUrl = result.ImageUrl,
                Asin = result.Asin,
                Isbn = result.Isbn,
                Series = result.Series,
                SeriesNumber = result.SeriesNumber,
                ProductUrl = result.ProductUrl,
                IsEnriched = result.IsEnriched,
                MetadataSource = result.MetadataSource
            };
        }

        public static SearchResult ToSearchResult(IndexerSearchResult result)
        {
            return new SearchResult
            {
                Id = result.Id,
                Title = result.Title,
                Artist = result.Artist,
                Album = result.Album,
                Category = result.Category,
                Source = result.Source,
                SourceLink = result.SourceLink,
                PublishedDate = result.PublishedDate,
                Format = result.Format,
                Score = result.Score,
                Size = result.Size,
                // Only populate peer counts for torrent results; usenet/ddl should not show peers
                Seeders = string.Equals(result.DownloadType, "Torrent", StringComparison.OrdinalIgnoreCase) ? result.Seeders : null,
                Leechers = string.Equals(result.DownloadType, "Torrent", StringComparison.OrdinalIgnoreCase) ? result.Leechers : null,
                MagnetLink = result.MagnetLink,
                TorrentUrl = result.TorrentUrl,
                NzbUrl = result.NzbUrl,
                DownloadType = result.DownloadType,
                // Only set quality when it was actually parsed / non-empty; treat 'unknown' as absent
                Quality = NormalizeQuality(result.Quality),
                // Preserve parsed language from indexer responses (e.g., MyAnonamouse); normalize empty/unknown values and leave null if not available.
                // Only attempt lightweight detection for Torrent results; do not infer language for Usenet/DDL results.
                Language = NormalizeLanguage(result.Language) ?? (string.Equals(result.DownloadType, "Torrent", System.StringComparison.OrdinalIgnoreCase) ? DetectLanguageFromText(result.Title + " " + (result.Description ?? string.Empty)) : null),
                ResultUrl = result.ResultUrl,
                DownloadReference = result.DownloadReference,
                Grabs = result.Grabs,
                Files = result.Files,
                // Copy indexer metadata for MAM server-side downloads
                IndexerId = result.IndexerId,
                IndexerImplementation = result.IndexerImplementation
            };
        }

        public static IndexerSearchResult ToIndexerSearchResult(SearchResult result)
        {
            return new IndexerSearchResult
            {
                Id = result.Id,
                Title = result.Title,
                Artist = result.Artist,
                Album = result.Album,
                Category = result.Category,
                Source = result.Source,
                SourceLink = result.SourceLink,
                PublishedDate = result.PublishedDate,
                Format = result.Format,
                Score = result.Score,
                Size = result.Size,
                Seeders = result.Seeders,
                Leechers = result.Leechers,
                MagnetLink = result.MagnetLink,
                TorrentUrl = result.TorrentUrl,
                NzbUrl = result.NzbUrl,
                DownloadType = result.DownloadType,
                Quality = result.Quality,
                ResultUrl = result.ResultUrl,
                DownloadReference = result.DownloadReference,
                Grabs = result.Grabs,
                Files = result.Files,
                TorrentFileName = result.TorrentFileName
            };
        }

        // Map an IndexerSearchResult into an API-friendly Prowlarr-like DTO
        public static IndexerResultDto ToIndexerResultDto(IndexerSearchResult result)
        {
            var dto = new IndexerResultDto
            {
                Guid = !string.IsNullOrWhiteSpace(result.ResultUrl) ? result.ResultUrl : (!string.IsNullOrWhiteSpace(result.TorrentUrl) ? result.TorrentUrl : result.Id),
                Size = result.Size,
                Files = result.Files,
                Grabs = result.Grabs,
                IndexerId = result.IndexerId,
                Indexer = result.Source,
                Title = result.Title ?? string.Empty,
                PublishDate = result.PublishedDate,
                DownloadUrl = !string.IsNullOrWhiteSpace(result.TorrentUrl) ? result.TorrentUrl : (!string.IsNullOrWhiteSpace(result.NzbUrl) ? result.NzbUrl : null),
                InfoUrl = result.ResultUrl,
                Seeders = result.Seeders,
                Leechers = result.Leechers,
                Protocol = !string.IsNullOrWhiteSpace(result.DownloadType) ? result.DownloadType.ToLowerInvariant() : null,
                FileName = result.TorrentFileName,
                DownloadReference = result.DownloadReference,
                FileType = string.IsNullOrWhiteSpace(result.Format) ? null : result.Format,
                Language = NormalizeLanguage(result.Language)
            };

            // Derive age fields from publishDate if available
            if (!string.IsNullOrWhiteSpace(dto.PublishDate) && DateTimeOffset.TryParse(dto.PublishDate, out var dtoPub))
            {
                var ageSpan = DateTimeOffset.UtcNow - dtoPub;
                dto.Age = (int)Math.Floor(ageSpan.TotalDays);
                dto.AgeHours = ageSpan.TotalHours;
                dto.AgeMinutes = ageSpan.TotalMinutes;
            }

            // SortTitle: normalized lower-case, remove punctuation
            dto.SortTitle = System.Text.RegularExpressions.Regex.Replace(dto.Title?.ToLowerInvariant() ?? string.Empty, "[^a-z0-9 ]", "").Trim();

            // IndexerFlags and Categories: best-effort mapping (not always present in result); keep empty lists if not available
            dto.IndexerFlags = new List<string>();
            if (!string.IsNullOrWhiteSpace(result.Category))
            {
                // Keep a simple category object with name
                dto.Categories.Add(new { id = 0, name = result.Category, subCategories = new object[0] });
            }

            return dto;
        }

        public static SearchResult ToSearchResult(MetadataSearchResult result)
        {
            return new SearchResult
            {
                Id = result.Id,
                Title = result.Title,
                Artist = result.Artist,
                Album = result.Album,
                Category = result.Category,
                Source = result.Source,
                SourceLink = result.SourceLink,
                PublishedDate = result.PublishedDate,
                Format = result.Format,
                Score = result.Score,
                // Metadata properties
                Description = result.Description,
                Subtitle = result.Subtitle,
                Publisher = result.Publisher,
                // Normalize language tokens (e.g., "unknown") to null so UI doesn't render 'Unknown'
                Language = NormalizeLanguage(result.Language),
                Runtime = result.Runtime,
                Narrator = result.Narrator,
                ImageUrl = result.ImageUrl,
                Asin = result.Asin,
                Isbn = result.Isbn,
                Series = result.Series,
                SeriesNumber = result.SeriesNumber,
                ProductUrl = result.ProductUrl,
                IsEnriched = result.IsEnriched,
                MetadataSource = result.MetadataSource
            };
        }

        public static List<MetadataSearchResult> ToMetadataList(IEnumerable<SearchResult> results)
        {
            return results.Select(ToMetadata).ToList();
        }

        /// <summary>
        /// Convert SearchResult to simplified DTO (removes torrent/NZB fields)
        /// Useful for advanced search responses which focus on metadata
        /// </summary>
        public static object ToSimplified(SearchResult result)
        {
            return new
            {
                result.Id,
                result.Title,
                Artist = result.Artist,
                result.Subtitle,
                result.Description,
                result.Publisher,
                result.Language,
                result.Runtime,
                result.Narrator,
                result.ImageUrl,
                result.Asin,
                result.Isbn,
                result.Series,
                result.SeriesNumber,
                result.ProductUrl,
                result.PublishedDate,
                result.PublishYear,
                result.Genres,
                result.IsEnriched,
                result.MetadataSource,
                result.Source,
                result.SourceLink,
                result.Score
            };
        }

        /// <summary>
        /// Convert list of SearchResults to simplified DTOs
        /// </summary>
        public static List<object> ToSimplifiedList(IEnumerable<SearchResult> results)
        {
            return results.Select(ToSimplified).ToList();
        }
    }
}
