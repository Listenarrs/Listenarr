namespace Listenarr.Domain.Models.Converters
{
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
            var value = new SearchResult
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
                Protocol = result.Protocol,
                // Only set quality when it was actually parsed / non-empty; treat 'unknown' as absent
                Quality = NormalizeQuality(result.Quality),
                // Preserve parsed language from indexer responses (e.g., MyAnonamouse); normalize empty/unknown values and leave null if not available.
                // Only attempt lightweight detection for Torrent results
                Language = NormalizeLanguage(result.Language) ?? (result.Protocol == DownloadProtocol.Torrent ? DetectLanguageFromText(result.Title + " " + (result.Description ?? string.Empty)) : null),
                ResultUrl = result.ResultUrl,
                Grabs = result.Grabs,
                // Copy indexer metadata for MAM server-side downloads
                IndexerId = result.IndexerId,
                IndexerImplementation = result.IndexerImplementation,
                Uploader = result.Uploader,
                Files = result.Files
            };
            
            // Legacy: Redact Usenet/DDL values
            if (value.Protocol == DownloadProtocol.Usenet || value.Protocol == DownloadProtocol.DirectDownload)
            {
                value.Seeders = null;
                value.Leechers = null;
                value.Grabs = null;
            }

            return value;
        }

        public static IndexerSearchResult ToIndexerSearchResult(SearchResult result)
        {
            var value = new IndexerSearchResult
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
                Protocol = result.Protocol,
                Quality = result.Quality,
                ResultUrl = result.ResultUrl,
                Grabs = result.Grabs,
                TorrentFileName = result.TorrentFileName,
                Uploader = result.Uploader,
                Files = result.Files
            };

            if (value.FileCount == 0)
            {
                value.FileCount = result.FileCount;
                value.Size = result.Size;
            }

            return value;
        }

        // Map an IndexerSearchResult into an API-friendly Prowlarr-like DTO
        public static IndexerResultDto ToIndexerResultDto(IndexerSearchResult result)
        {
            var dto = new IndexerResultDto
            {
                Guid = !string.IsNullOrWhiteSpace(result.ResultUrl) ? result.ResultUrl : (!string.IsNullOrWhiteSpace(result.TorrentUrl) ? result.TorrentUrl : result.Id),
                Size = result.Size,
                Files = result.FileCount,
                Grabs = result.Grabs,
                IndexerId = result.IndexerId,
                Indexer = result.Source,
                Title = result.Title ?? string.Empty,
                PublishDate = result.PublishedDate,
                DownloadUrl = !string.IsNullOrWhiteSpace(result.TorrentUrl) ? result.TorrentUrl : (!string.IsNullOrWhiteSpace(result.NzbUrl) ? result.NzbUrl : null),
                InfoUrl = result.ResultUrl,
                Seeders = result.Seeders,
                Leechers = result.Leechers,
                Protocol = result.Protocol,
                FileName = result.TorrentFileName,
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