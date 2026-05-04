using System.Text.RegularExpressions;
using Listenarr.Application.Common;
using Listenarr.Application.Metadata;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Search;

/// <summary>
/// Converts external metadata API responses to internal AudibleBookMetadata and SearchResult models.
/// </summary>
public class MetadataConverters
{
    private readonly IImageCacheService? _imageCacheService;
    private readonly ILogger<MetadataConverters> _logger;
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public MetadataConverters(IImageCacheService? imageCacheService, ILogger<MetadataConverters> logger, IHttpContextAccessor? httpContextAccessor = null)
    {
        _imageCacheService = imageCacheService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    private static List<AudiobookSeriesMembership>? BuildSeriesMemberships(IEnumerable<AudibleSeries>? series)
    {
        if (series == null)
        {
            return null;
        }

        var memberships = series
            .Select((entry, index) => new AudiobookSeriesMembership
            {
                SeriesAsin = entry.Asin,
                SeriesName = entry.Name,
                SeriesNumber = entry.Position,
                IsPrimary = index == 0,
                SortOrder = index
            })
            .ToList();

        var normalized = AudiobookSeriesMembershipHelper.Normalize(memberships);
        return normalized.Count == 0 ? null : normalized;
    }

    private static void ApplyPrimarySeriesFields(AudibleBookMetadata metadata)
    {
        var primaryMembership = AudiobookSeriesMembershipHelper.GetPrimaryMembership(metadata.SeriesMemberships);
        metadata.Series = primaryMembership?.SeriesName;
        metadata.SeriesNumber = primaryMembership?.SeriesNumber;
    }

    /// <summary>
    /// Converts Audible API response to AudibleBookMetadata.
    /// </summary>
    public AudibleBookMetadata ConvertAudibleToMetadata(AudibleBookResponse audibleData, string asin, string source = "Audible")
    {
        var metadata = new AudibleBookMetadata
        {
            Asin = audibleData.Asin ?? asin,
            Source = source, // Use the original search source (Amazon or Audible)
            Title = audibleData.Title,
            Subtitle = audibleData.Subtitle,
            Authors = audibleData.Authors?.Where(a => !string.IsNullOrEmpty(a.Name)).Select(a => a.Name!).ToList(),
            Narrators = audibleData.Narrators?.Where(n => !string.IsNullOrEmpty(n.Name)).Select(n => n.Name!).ToList(),
            Publisher = audibleData.Publisher,
            Description = audibleData.Description,
            Genres = audibleData.Genres?.Where(g => !string.IsNullOrEmpty(g.Name)).Select(g => g.Name!).ToList(),
            Language = audibleData.Language,
            Isbn = !string.IsNullOrWhiteSpace(audibleData.Isbn) ? new List<string> { audibleData.Isbn! } : new List<string>(),
            ImageUrl = audibleData.ImageUrl,
            Abridged = audibleData.BookFormat?.Contains("abridged", StringComparison.OrdinalIgnoreCase) ?? false,
            Explicit = audibleData.Explicit ?? false
        };

        metadata.SeriesMemberships = BuildSeriesMemberships(audibleData.Series);
        ApplyPrimarySeriesFields(metadata);

        // Convert runtime from minutes to minutes (audible returns lengthMinutes)
        if (audibleData.LengthMinutes.HasValue && audibleData.LengthMinutes > 0)
        {
            metadata.Runtime = audibleData.LengthMinutes.Value;
        }

        // Extract year from releaseDate (format: "2023-10-24T00:00:00.000+00:00")
        string? dateStr = audibleData.ReleaseDate ?? audibleData.PublishDate;
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

        _logger.LogInformation("Converted audible data for {Asin}: Title={Title}, Runtime={Runtime}min, Year={Year}, Series={Series}, ImageUrl={ImageUrl}",
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
            Authors = audnexusData.Authors?.Where(a => !string.IsNullOrEmpty(a.Name)).Select(a => a.Name!).ToList(),
            Narrators = audnexusData.Narrators?.Where(n => !string.IsNullOrEmpty(n.Name)).Select(n => n.Name!).ToList(),
            Publisher = audnexusData.PublisherName,
            Description = !string.IsNullOrWhiteSpace(audnexusData.Summary) ? audnexusData.Summary : audnexusData.Description,
            Genres = audnexusData.Genres?.Where(g => !string.IsNullOrEmpty(g.Name)).Select(g => g.Name!).ToList(),
            Language = audnexusData.Language,
            Isbn = !string.IsNullOrWhiteSpace(audnexusData.Isbn) ? new List<string> { audnexusData.Isbn! } : new List<string>(),
            ImageUrl = audnexusData.Image,
            Abridged = audnexusData.FormatType?.Contains("abridged", StringComparison.OrdinalIgnoreCase) ?? false,
            Explicit = audnexusData.IsAdult ?? false
        };

        var audnexusSeriesMemberships = new List<AudiobookSeriesMembership>();
        if (audnexusData.SeriesPrimary != null)
        {
            audnexusSeriesMemberships.Add(new AudiobookSeriesMembership
            {
                SeriesName = audnexusData.SeriesPrimary.Name,
                SeriesNumber = audnexusData.SeriesPrimary.Position,
                IsPrimary = true,
                SortOrder = 0
            });
        }

        if (audnexusData.SeriesSecondary != null)
        {
            audnexusSeriesMemberships.Add(new AudiobookSeriesMembership
            {
                SeriesName = audnexusData.SeriesSecondary.Name,
                SeriesNumber = audnexusData.SeriesSecondary.Position,
                IsPrimary = audnexusSeriesMemberships.Count == 0,
                SortOrder = audnexusSeriesMemberships.Count
            });
        }

        metadata.SeriesMemberships = audnexusSeriesMemberships.Count == 0
            ? null
            : AudiobookSeriesMembershipHelper.Normalize(audnexusSeriesMemberships);
        ApplyPrimarySeriesFields(metadata);

        if (metadata.SeriesMemberships != null && metadata.SeriesMemberships.Count > 0)
        {
            _logger.LogInformation("Extracted {Count} series memberships from Audnexus for ASIN {Asin}", metadata.SeriesMemberships.Count, asin);
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
                    imageUrl = ApiVersionPathBuilder.BuildImagePath(asin, _httpContextAccessor?.HttpContext);
                    _logger.LogInformation("Using cached image for ASIN {Asin}: {ImageUrl}", asin, imageUrl);
                }
                else
                {
                    // Even if not cached, map to API endpoint to ensure consistent serving
                    // and avoid external URL failures. Background download will populate cache.
                    imageUrl = ApiVersionPathBuilder.BuildImagePath(asin, _httpContextAccessor?.HttpContext);
                    _logger.LogDebug("Mapping to API endpoint for ASIN {Asin} (not yet cached): {ImageUrl}", asin, imageUrl);
                    _logger.LogDebug("Initiating background image cache for ASIN {Asin} with URL: {OriginalUrl}", asin, metadata.ImageUrl ?? imageUrl);
                    _ = _imageCacheService.DownloadAndCacheImageAsync(metadata.ImageUrl ?? imageUrl, asin);
                    _logger.LogDebug("Started background image cache for ASIN {Asin}", asin);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
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
                    imageUrl = ApiVersionPathBuilder.BuildImagePath(asin, _httpContextAccessor?.HttpContext);
                    _logger.LogInformation("Using cached image for ASIN {Asin}: {ImageUrl}", asin, imageUrl);
                }
                else
                {
                    // Even if not cached, map to API endpoint to ensure consistent serving
                    // and avoid external URL failures. Background download will populate cache.
                    imageUrl = ApiVersionPathBuilder.BuildImagePath(asin, _httpContextAccessor?.HttpContext);
                    _logger.LogDebug("Mapping to API endpoint for ASIN {Asin} (not yet cached): {ImageUrl}", asin, imageUrl);
                    _logger.LogDebug("Initiating background image cache for ASIN {Asin} with URL: {OriginalUrl}", asin, metadata.ImageUrl ?? imageUrl);
                    _ = _imageCacheService.DownloadAndCacheImageAsync(metadata.ImageUrl ?? imageUrl, asin);
                    _logger.LogDebug("Started background image cache for ASIN {Asin}", asin);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
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


