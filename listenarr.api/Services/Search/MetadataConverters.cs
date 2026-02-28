using System.Text.RegularExpressions;
using Listenarr.Infrastructure.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Services.Search;

/// <summary>
/// Converts external metadata API responses to internal AudibleBookMetadata and SearchResult models.
/// </summary>
public class MetadataConverters
{
    private readonly IImageCacheService? _imageCacheService;
    private readonly ILogger<MetadataConverters> _logger;

    public MetadataConverters(IImageCacheService? imageCacheService, ILogger<MetadataConverters> logger)
    {
        _imageCacheService = imageCacheService;
        _logger = logger;
    }

    /// <summary>
    /// Converts Audimeta API response to AudibleBookMetadata.
    /// </summary>
    public AudibleBookMetadata ConvertAudimetaToMetadata(AudimetaBookResponse audimetaData, string asin, string source = "Audible")
    {
        var metadata = new AudibleBookMetadata
        {
            Asin = audimetaData.Asin ?? asin,
            Source = source, // Use the original search source (Amazon or Audible)
            Title = audimetaData.Title,
            Subtitle = audimetaData.Subtitle,
            Authors = audimetaData.Authors?.Select(a => a.Name).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList(),
            Narrators = audimetaData.Narrators?.Select(n => n.Name).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList(),
            Publisher = audimetaData.Publisher,
            Description = audimetaData.Description,
            Genres = audimetaData.Genres?.Select(g => g.Name).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList(),
            Language = audimetaData.Language,
            Isbn = !string.IsNullOrWhiteSpace(audimetaData.Isbn) ? new List<string> { audimetaData.Isbn! } : new List<string>(),
            ImageUrl = audimetaData.ImageUrl,
            Abridged = audimetaData.BookFormat?.Contains("abridged", StringComparison.OrdinalIgnoreCase) ?? false,
            Explicit = audimetaData.Explicit ?? false
        };

        // Handle series (audimeta returns array, we just take the first one)
        if (audimetaData.Series != null && audimetaData.Series.Any())
        {
            var firstSeries = audimetaData.Series.First();
            metadata.Series = firstSeries.Name;
            metadata.SeriesNumber = firstSeries.Position;
        }

        // Convert runtime from minutes to minutes (audimeta returns lengthMinutes)
        if (audimetaData.LengthMinutes.HasValue && audimetaData.LengthMinutes > 0)
        {
            metadata.Runtime = audimetaData.LengthMinutes.Value;
        }

        // Extract year from releaseDate (format: "2023-10-24T00:00:00.000+00:00")
        string? dateStr = audimetaData.ReleaseDate ?? audimetaData.PublishDate;
        if (!string.IsNullOrEmpty(dateStr))
        {
            var yearMatch = Regex.Match(dateStr, @"\d{4}");
            if (yearMatch.Success)
            {
                metadata.PublishYear = yearMatch.Value;
            }
            // Also store the full date for calendar/timeline features
            // Try to parse as ISO 8601 datetime
            if (DateTime.TryParse(dateStr, out var parsedDate))
            {
                metadata.PublishedDate = parsedDate.ToString("yyyy-MM-dd");
            }
            else
            {
                // If parsing fails, try just the year
                var datePart = Regex.Match(dateStr, @"^(\d{4})-\d{2}-\d{2}");
                if (datePart.Success)
                {
                    metadata.PublishedDate = datePart.Value;
                }
            }
        }

        _logger.LogInformation("Converted audimeta data for {Asin}: Title={Title}, Runtime={Runtime}min, Year={Year}, Series={Series}, ImageUrl={ImageUrl}",
            asin, metadata.Title, metadata.Runtime, metadata.PublishYear, metadata.Series, metadata.ImageUrl);

        return metadata;
    }

    /// <summary>
    /// Converts Audnexus API response to AudibleBookMetadata.
    /// </summary>
    public AudibleBookMetadata ConvertAudnexusToMetadata(AudnexusBookResponse audnexusData, string asin, string source = "Audible")
    {
        var metadata = new AudibleBookMetadata
        {
            Asin = audnexusData.Asin ?? asin,
            Source = source, // Use the original search source (Amazon or Audible)
            Title = audnexusData.Title,
            Subtitle = audnexusData.Subtitle,
            Authors = audnexusData.Authors?.Select(a => a.Name).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList(),
            Narrators = audnexusData.Narrators?.Select(n => n.Name).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList(),
            Publisher = audnexusData.PublisherName,
            Description = !string.IsNullOrWhiteSpace(audnexusData.Summary) ? audnexusData.Summary : audnexusData.Description,
            Genres = audnexusData.Genres?.Select(g => g.Name).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList(),
            Language = audnexusData.Language,
            Isbn = !string.IsNullOrWhiteSpace(audnexusData.Isbn) ? new List<string> { audnexusData.Isbn! } : new List<string>(),
            ImageUrl = audnexusData.Image,
            Abridged = audnexusData.FormatType?.Contains("abridged", StringComparison.OrdinalIgnoreCase) ?? false,
            Explicit = audnexusData.IsAdult ?? false
        };

        // Handle series (primary series first, then secondary) - Audnexus returns single objects, not arrays
        if (audnexusData.SeriesPrimary != null)
        {
            metadata.Series = audnexusData.SeriesPrimary.Name;
            metadata.SeriesNumber = audnexusData.SeriesPrimary.Position;
            _logger.LogInformation("Extracted primary series from Audnexus: {Series}, Position={Position}", 
                metadata.Series, metadata.SeriesNumber);
        }
        else if (audnexusData.SeriesSecondary != null)
        {
            metadata.Series = audnexusData.SeriesSecondary.Name;
            metadata.SeriesNumber = audnexusData.SeriesSecondary.Position;
            _logger.LogInformation("Extracted secondary series from Audnexus: {Series}, Position={Position}", 
                metadata.Series, metadata.SeriesNumber);
        }
        else
        {
            _logger.LogDebug("No series information from Audnexus for ASIN {Asin}", asin);
        }

        // Convert runtime from minutes
        if (audnexusData.RuntimeLengthMin.HasValue && audnexusData.RuntimeLengthMin > 0)
        {
            metadata.Runtime = audnexusData.RuntimeLengthMin.Value;
        }

        // Extract year from releaseDate (format: "2021-05-04T00:00:00.000Z")
        if (!string.IsNullOrEmpty(audnexusData.ReleaseDate))
        {
            var yearMatch = Regex.Match(audnexusData.ReleaseDate, @"\d{4}");
            if (yearMatch.Success)
            {
                metadata.PublishYear = yearMatch.Value;
            }
        }
        // Fallback to copyright year if no release date
        else if (audnexusData.Copyright.HasValue)
        {
            metadata.PublishYear = audnexusData.Copyright.Value.ToString();
        }

        _logger.LogInformation("Converted Audnexus data for {Asin}: Title={Title}, Runtime={Runtime}min, Year={Year}, Series={Series}, ImageUrl={ImageUrl}",
            asin, metadata.Title, metadata.Runtime, metadata.PublishYear, metadata.Series, metadata.ImageUrl);

        return metadata;
    }

    /// <summary>
    /// Converts AudibleBookMetadata to SearchResult with fallback handling for missing fields.
    /// </summary>
    public async Task<SearchResult> ConvertMetadataToSearchResultAsync(AudibleBookMetadata metadata, string asin, string? fallbackTitle = null, string? fallbackAuthor = null, string? fallbackImageUrl = null, string? fallbackLanguage = null)
    {
        // Use metadata if available, otherwise fallback to raw search result, finally to generic fallback
        var title = metadata.Title;
        if (string.IsNullOrWhiteSpace(title) || title == "Audible" || title.Contains("English - USD"))
        {
            title = fallbackTitle;
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Unknown Title";
        }

        var author = metadata.Authors?.FirstOrDefault();
        if (SearchValidation.IsAuthorNoise(author))
        {
            author = fallbackAuthor;
        }
        if (SearchValidation.IsAuthorNoise(author))
        {
            author = "Unknown Author";
        }

        var imageUrl = metadata.ImageUrl;
        // Use fallback image if metadata has no image OR if it's a grey-pixel placeholder
        if (string.IsNullOrWhiteSpace(imageUrl) || imageUrl.Contains("grey-pixel.gif"))
        {
            if (!string.IsNullOrWhiteSpace(fallbackImageUrl))
            {
                imageUrl = fallbackImageUrl;
                _logger.LogInformation("Using fallback image URL for ASIN {Asin}: {ImageUrl} (replaced {OriginalUrl})", 
                    asin, imageUrl, string.IsNullOrWhiteSpace(metadata.ImageUrl) ? "null" : "grey-pixel");
            }
            else if (string.IsNullOrWhiteSpace(imageUrl))
            {
                _logger.LogWarning("No image URL available for ASIN {Asin} from metadata or fallback. Metadata source: {Source}", asin, metadata.Source);
            }
        }
        else
        {
            _logger.LogDebug("Using metadata image URL for ASIN {Asin}: {ImageUrl}", asin, imageUrl);
        }

        // If we already have a cached image for this ASIN, use the local API endpoint
        // instead of the external URL so search results serve cached images.
        if (!string.IsNullOrEmpty(asin) && _imageCacheService != null)
        {
            try
            {
                var cachedPath = await _imageCacheService.GetCachedImagePathAsync(asin);
                if (!string.IsNullOrWhiteSpace(cachedPath))
                {
                    imageUrl = $"/api/images/{asin}";
                    _logger.LogInformation("Using cached image for ASIN {Asin}: {ImageUrl}", asin, imageUrl);
                }
                else
                {
                    // Even if not cached, map to API endpoint to ensure consistent serving
                    // and avoid external URL failures. Background download will populate cache.
                    imageUrl = $"/api/images/{asin}";
                    _logger.LogDebug("Mapping to API endpoint for ASIN {Asin} (not yet cached): {ImageUrl}", asin, imageUrl);
                    _logger.LogDebug("Initiating background image cache for ASIN {Asin} with URL: {OriginalUrl}", asin, metadata.ImageUrl ?? imageUrl);
                    _ = _imageCacheService.DownloadAndCacheImageAsync(metadata.ImageUrl ?? imageUrl, asin);
                    _logger.LogDebug("Started background image cache for ASIN {Asin}", asin);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Failed to check/initiate image caching for ASIN {Asin}", asin);
            }
        }

        // Generate product URL based on source and ASIN. Ensure only HTTP(S) URLs are used
        string? productUrl = null;
        if (!string.IsNullOrEmpty(asin))
        {
            productUrl = metadata.Source == "Amazon"
                ? $"https://www.amazon.com/dp/{asin}"
                : $"https://www.audible.com/pd/{asin}";
        }

        // If metadata provided a non-http product link, prefer synthesized productUrl and
        // do not leak internal/custom-scheme links into the user-facing ProductUrl field.
        if (!string.IsNullOrEmpty(metadata.Source) && !string.IsNullOrEmpty(metadata.ImageUrl))
        {
            // no-op; placeholder to keep behavior explicit in future
        }

        var categoryText = string.Join(", ", metadata.Genres ?? new List<string> { "Audiobook" });
        var genreList = metadata.Genres;
        if (genreList == null || !genreList.Any())
        {
            genreList = categoryText
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(g => g.Trim())
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (genreList.Count == 1 && string.Equals(genreList[0], "Audiobook", StringComparison.OrdinalIgnoreCase))
            {
                genreList = new List<string>();
            }
        }

        var result = new SearchResult
        {
            Id = Guid.NewGuid().ToString(),
            Title = title,
            Artist = author ?? "Unknown Author",
            Album = metadata.Series ?? metadata.Title ?? "Unknown Album",
            Category = categoryText,
            Size = 0, // We don't have file size from metadata
            Seeders = 0, // Not applicable for direct Amazon results
            Leechers = 0, // Not applicable for direct Amazon results
            MagnetLink = $"amazon://asin/{asin}", // Use a custom scheme to identify Amazon sources
            Source = metadata.Source ?? "Amazon/Audible", // Use the metadata source (Audible or Amazon) if available
            MetadataSource = metadata.Source, // Set the metadata source for display
            SourceLink = productUrl, // Link to the product page
            PublishedDate = !string.IsNullOrEmpty(metadata.PublishedDate) ? metadata.PublishedDate : (!string.IsNullOrEmpty(metadata.PublishYear) && int.TryParse(metadata.PublishYear, out var year) ? $"{year}-01-01" : "1970-01-01"),
            PublishYear = metadata.PublishYear,
            Subtitle = metadata.Subtitle,
            Quality = metadata.Version ?? "Unknown",
            Format = "Audiobook",
            Description = metadata.Description,
            Publisher = metadata.Publisher,
            Language = metadata.Language ?? fallbackLanguage,
            Runtime = metadata.Runtime,
            Narrator = string.Join(", ", metadata.Narrators ?? new List<string>()),
            Series = metadata.Series,
            SeriesNumber = metadata.SeriesNumber,
            ImageUrl = imageUrl,
            Asin = asin,
            Isbn = metadata.Isbn ?? new List<string>(),
            Genres = genreList,
            ProductUrl = productUrl
        };
        
        _logger.LogInformation("SearchResult for ASIN {Asin}: PublishYear='{PublishYear}', PublishedDate={PublishedDate:yyyy-MM-dd}", 
            asin, metadata.PublishYear, result.PublishedDate);
        
        return result;
    }

    /// <summary>
    /// Converts AudibleBookMetadata to MetadataSearchResult with fallback handling for missing fields.
    /// </summary>
    public async Task<MetadataSearchResult> ConvertMetadataToMetadataSearchResultAsync(AudibleBookMetadata metadata, string asin, string? fallbackTitle = null, string? fallbackAuthor = null, string? fallbackImageUrl = null, string? fallbackLanguage = null)
    {
        // Use metadata if available, otherwise fallback to raw search result, finally to generic fallback
        var title = metadata.Title;
        if (string.IsNullOrWhiteSpace(title) || title == "Audible" || title.Contains("English - USD"))
        {
            title = fallbackTitle;
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Unknown Title";
        }

        var author = metadata.Authors?.FirstOrDefault();
        if (SearchValidation.IsAuthorNoise(author))
        {
            author = fallbackAuthor;
        }
        if (SearchValidation.IsAuthorNoise(author))
        {
            author = "Unknown Author";
        }

        var imageUrl = metadata.ImageUrl;
        // Use fallback image if metadata has no image OR if it's a grey-pixel placeholder
        if (string.IsNullOrWhiteSpace(imageUrl) || imageUrl.Contains("grey-pixel.gif"))
        {
            if (!string.IsNullOrWhiteSpace(fallbackImageUrl))
            {
                imageUrl = fallbackImageUrl;
                _logger.LogInformation("Using fallback image URL for ASIN {Asin}: {ImageUrl} (replaced {OriginalUrl})", 
                    asin, imageUrl, string.IsNullOrWhiteSpace(metadata.ImageUrl) ? "null" : "grey-pixel");
            }
            else if (string.IsNullOrWhiteSpace(imageUrl))
            {
                _logger.LogWarning("No image URL available for ASIN {Asin} from metadata or fallback. Metadata source: {Source}", asin, metadata.Source);
            }
        }
        else
        {
            _logger.LogDebug("Using metadata image URL for ASIN {Asin}: {ImageUrl}", asin, imageUrl);
        }

        // If we already have a cached image for this ASIN, use the local API endpoint
        // instead of the external URL so search results serve cached images.
        if (!string.IsNullOrEmpty(asin) && _imageCacheService != null)
        {
            try
            {
                var cachedPath = await _imageCacheService.GetCachedImagePathAsync(asin);
                if (!string.IsNullOrWhiteSpace(cachedPath))
                {
                    imageUrl = $"/api/images/{asin}";
                    _logger.LogInformation("Using cached image for ASIN {Asin}: {ImageUrl}", asin, imageUrl);
                }
                else
                {
                    // Even if not cached, map to API endpoint to ensure consistent serving
                    // and avoid external URL failures. Background download will populate cache.
                    imageUrl = $"/api/images/{asin}";
                    _logger.LogDebug("Mapping to API endpoint for ASIN {Asin} (not yet cached): {ImageUrl}", asin, imageUrl);
                    _logger.LogDebug("Initiating background image cache for ASIN {Asin} with URL: {OriginalUrl}", asin, metadata.ImageUrl ?? imageUrl);
                    _ = _imageCacheService.DownloadAndCacheImageAsync(metadata.ImageUrl ?? imageUrl, asin);
                    _logger.LogDebug("Started background image cache for ASIN {Asin}", asin);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Failed to check/initiate image caching for ASIN {Asin}", asin);
            }
        }

        // Generate product URL based on source and ASIN. Ensure only HTTP(S) URLs are used
        string? productUrl = null;
        if (!string.IsNullOrEmpty(asin))
        {
            productUrl = metadata.Source == "Amazon"
                ? $"https://www.amazon.com/dp/{asin}"
                : $"https://www.audible.com/pd/{asin}";
        }

        var categoryText = string.Join(", ", metadata.Genres ?? new List<string> { "Audiobook" });
        var genreList = metadata.Genres;
        if (genreList == null || !genreList.Any())
        {
            genreList = categoryText
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(g => g.Trim())
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (genreList.Count == 1 && string.Equals(genreList[0], "Audiobook", StringComparison.OrdinalIgnoreCase))
            {
                genreList = new List<string>();
            }
        }

        var result = new MetadataSearchResult
        {
            Id = Guid.NewGuid().ToString(),
            Title = title,
            Artist = author ?? "Unknown Author",
            Album = metadata.Series ?? metadata.Title ?? "Unknown Album",
            Category = categoryText,
            Source = metadata.Source ?? "Amazon/Audible",
            SourceLink = productUrl,
            PublishedDate = !string.IsNullOrEmpty(metadata.PublishedDate) ? metadata.PublishedDate : (!string.IsNullOrEmpty(metadata.PublishYear) && int.TryParse(metadata.PublishYear, out var year) ? $"{year}-01-01" : "1970-01-01"),
            Format = "Audiobook",
            Score = 0,
            Description = metadata.Description,
            Subtitle = metadata.Subtitle,
            Publisher = metadata.Publisher,
            Language = metadata.Language ?? fallbackLanguage,
            Runtime = metadata.Runtime,
            Narrator = string.Join(", ", metadata.Narrators ?? new List<string>()),
            Series = metadata.Series,
            SeriesNumber = metadata.SeriesNumber,
            ImageUrl = imageUrl,
            Asin = asin,
            Isbn = metadata.Isbn ?? new List<string>(),
            Genres = genreList,
            ProductUrl = productUrl,
            IsEnriched = true,
            MetadataSource = metadata.Source
        };
        
        _logger.LogInformation("MetadataSearchResult for ASIN {Asin}: PublishYear='{PublishYear}'", 
            asin, metadata.PublishYear);
        
        return result;
    }
}

