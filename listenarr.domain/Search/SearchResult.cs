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
    public enum DirectDownloadArtifactPackaging
    {
        File,
        Archive
    }

    public sealed record DirectDownloadArtifactDescriptor(
        string Url,
        string FileName,
        long ExpectedSize,
        DirectDownloadArtifactPackaging Packaging);

    /// <summary>
    /// Base class for all search results with common properties
    /// </summary>
    public abstract class BaseSearchResult
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public CandidateIdentityEvidence? IdentityEvidence { get; set; }
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
        [JsonIgnore]
        public IReadOnlyList<DirectDownloadArtifactDescriptor> DirectDownloadArtifacts { get; set; } = [];
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
        [JsonIgnore]
        public IReadOnlyList<DirectDownloadArtifactDescriptor> DirectDownloadArtifacts { get; set; } = [];
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

}
