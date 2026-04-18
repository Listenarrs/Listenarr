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

using System.Text.Json.Serialization;

namespace Listenarr.Domain.Models
{
    public class IndexerSearchResult : FileSearchResult
    {
        public int? Seeders { get; set; }
        public int? Leechers { get; set; }
        public int? Grabs { get; set; } = null;
        public string MagnetLink { get; set; } = string.Empty;
        public string TorrentUrl { get; set; } = string.Empty;
        public string NzbUrl { get; set; } = string.Empty;
        public DownloadProtocol Protocol { get; set; }
        [JsonIgnore]
        public byte[]? TorrentFileContent { get; set; }
        [JsonIgnore]
        public string? TorrentFileName { get; set; }
        public string? Quality { get; set; }

        // Indexer metadata used to resolve tracker-specific downloads
        public int? IndexerId { get; set; }
        public string? IndexerImplementation { get; set; }

        // Link to the indexer page for this result
        public string? ResultUrl { get; set; }

        // Metadata-specific properties
        public string? Description { get; set; }
        public string? Language { get; set; }
        public string? Publisher { get; set; }
        public string? Narrator { get; set; }

        public double CalculateSeedScore()
        {
            switch(Protocol)
            {
                case DownloadProtocol.Usenet:
                case DownloadProtocol.DirectDownload:
                    var grabs = Grabs;
                    if (grabs != null && grabs > 0)
                    {
                        return Math.Min(100.0, 20.0 + (Math.Log10((double)grabs) * 20.0));
                    }
                    return 0.0;
                case DownloadProtocol.Torrent:
                    var seeders = Seeders ?? 0;
                    if (seeders <= 0) return 0.0;

                    var seederScore = Math.Min(100.0, 20.0 + (Math.Log10(seeders) * 20.0));
                    var leechers = Leechers ?? 0;
                    if (leechers > 0)
                    {
                        var ratio = (double)seeders / Math.Max(1, leechers);
                        if (ratio > 2.0) seederScore += 10.0;
                        else if (ratio > 1.0) seederScore += 5.0;
                    }

                    return Math.Min(100.0, seederScore);
            }

            // Fallback
            return 0.0;
        }
    }

    /// <summary>
    /// Legacy SearchResult class - kept for backwards compatibility
    /// Combines both indexer and metadata properties
    /// </summary>
    public class SearchResult : IndexerSearchResult
    {
        // Subtitle provided by metadata sources (e.g., Audible/Audible)
        public string? Subtitle { get; set; }
        // Publish year as provided by metadata (convenience for UI)
        public string? PublishYear { get; set; }
        public int? Runtime { get; set; }
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

    /// <summary>
    /// Search result from audiobook metadata sources (Audible, Audnexus, etc.)
    /// </summary>
    public class MetadataSearchResult : FileSearchResult
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
        public int? Grabs { get; set; }
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
        public DownloadProtocol? Protocol { get; set; }
        public string? FileName { get; set; }
        // Filetype as provided/derived from indexer (e.g., MP3, M4B)
        [System.Text.Json.Serialization.JsonPropertyName("filetype")]
        public string? FileType { get; set; }
        // Language code or parsed language (lang_code / ENG -> English)
        [System.Text.Json.Serialization.JsonPropertyName("lang_code")]
        public string? Language { get; set; }
    }

    /// <summary>
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
