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


namespace Listenarr.Api.Features.Search;

public sealed class SearchResponseMapper
{
    private readonly IAudiobookMetadataService _metadataService;
    private readonly IImageCacheService? _imageCacheService;
    private readonly ILogger<SearchResponseMapper> _logger;

    public SearchResponseMapper(
        IAudiobookMetadataService metadataService,
        ILogger<SearchResponseMapper> logger,
        IImageCacheService? imageCacheService = null)
    {
        _metadataService = metadataService;
        _logger = logger;
        _imageCacheService = imageCacheService;
    }

    public string BuildApiImagePath(string identifier, HttpContext httpContext, string? sourceUrl = null)
        => HttpApiVersionUtils.BuildImagePath(identifier, httpContext, sourceUrl: sourceUrl);

    public List<object> SimplifySearchResults(List<SearchResult> results)
    {
        return results?.Select(r => new
        {
            r.Id,
            r.Title,
            Artist = r.Artist,
            r.Subtitle,
            r.Description,
            r.Publisher,
            r.Language,
            r.Runtime,
            r.Narrator,
            r.ImageUrl,
            r.Asin,
            Isbn = r.Isbn ?? new List<string>(),
            r.Series,
            r.SeriesNumber,
            r.ProductUrl,
            r.PublishedDate,
            r.PublishYear,
            r.Genres,
            r.IsEnriched,
            r.MetadataSource,
            r.Source,
            r.SourceLink,
            r.Score
        }).Cast<object>().ToList() ?? new List<object>();
    }

    public void SanitizeResultForPublicApi(SearchResult r, string region = "us")
    {
        try
        {
            if (r == null) return;
            if (string.IsNullOrWhiteSpace(r.ProductUrl) && !string.IsNullOrWhiteSpace(r.Asin))
            {
                r.ProductUrl = MarketDomainResolver.BuildAmazonProductUrl(r.Asin, region);
            }

            var sourceText = $"{r.Source} {r.MetadataSource}";
            if (!string.IsNullOrWhiteSpace(r.Asin) &&
                sourceText.Contains("Audible", StringComparison.OrdinalIgnoreCase))
            {
                r.ProductUrl = NormalizeAudibleProductUrl(r.ProductUrl, r.Asin, region);
                r.SourceLink = NormalizeAudibleProductUrl(r.SourceLink, r.Asin, region);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogDebug(ex, "Failed to sanitize public search result for ASIN {Asin}", r.Asin);
        }
    }

    public async Task NormalizeMetadataResultImagesAsync(
        List<MetadataSearchResult>? results,
        HttpContext httpContext,
        string logContext)
    {
        if (_imageCacheService == null || results == null)
            return;

        foreach (var r in results)
        {
            try
            {
                if (r == null) continue;
                if (string.IsNullOrWhiteSpace(r.Asin)) continue;

                var cached = await _imageCacheService.GetCachedImagePathAsync(r.Asin);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    r.ImageUrl = BuildApiImagePath(r.Asin, httpContext);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(r.ImageUrl) &&
                    (r.ImageUrl.StartsWith("http://") || r.ImageUrl.StartsWith("https://")))
                {
                    var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(r.ImageUrl, r.Asin);
                    if (!string.IsNullOrWhiteSpace(downloaded))
                    {
                        r.ImageUrl = BuildApiImagePath(r.Asin, httpContext);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to normalize image for {Context} ASIN {Asin}", logContext, r.Asin);
            }
        }
    }

    public async Task<object> MapAudibleSearchResultToOutputAsync(
        AudibleSearchResult book,
        string region,
        HttpContext httpContext)
    {
        string? imageUrl = book.ImageUrl;
        if (!string.IsNullOrWhiteSpace(book.Asin) && _imageCacheService != null)
        {
            try
            {
                var cached = await _imageCacheService.GetCachedImagePathAsync(book.Asin);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    imageUrl = BuildApiImagePath(book.Asin, httpContext);
                }
                else if (!string.IsNullOrWhiteSpace(imageUrl) && (imageUrl.StartsWith("http://") || imageUrl.StartsWith("https://")))
                {
                    var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(imageUrl, book.Asin);
                    if (!string.IsNullOrWhiteSpace(downloaded)) imageUrl = BuildApiImagePath(book.Asin, httpContext);
                }
                else
                {
                    imageUrl = BuildApiImagePath(book.Asin, httpContext);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to normalize image for series result ASIN {Asin}", book.Asin);
            }
        }

        var authors = (book.Authors ?? new List<AudibleAuthor>()).Where(a => a != null).Select(a => new
        {
            asin = a!.Asin,
            name = a!.Name,
            region = a!.Region ?? region,
            regions = new[] { a!.Region ?? region },
            updatedAt = DateTime.UtcNow.ToString("o")
        }).ToList();
        var narrators = (book.Narrators ?? new List<AudibleNarrator>()).Where(n => n != null).Select(n => new { name = n!.Name, updatedAt = DateTime.UtcNow.ToString("o") }).ToList();
        var genres = (book.Genres ?? new List<AudibleGenre>()).Where(g => g != null).Select(g => new
        {
            asin = g!.Asin,
            name = g!.Name,
            type = g!.Type,
            updatedAt = DateTime.UtcNow.ToString("o")
        }).ToList();
        var series = (book.Series ?? new List<AudibleSeries>()).Where(s => s != null).Select(s => new
        {
            asin = s!.Asin,
            name = s!.Name,
            region = region,
            position = s!.Position,
            updatedAt = DateTime.UtcNow.ToString("o")
        }).ToList();

        return new
        {
            asin = book.Asin,
            title = book.Title,
            subtitle = book.Subtitle,
            region = region,
            regions = new[] { region },
            description = (string?)null,
            summary = (string?)null,
            bookFormat = book.BookFormat,
            imageUrl = imageUrl,
            lengthMinutes = book.RuntimeLengthMin ?? book.LengthMinutes ?? book.RuntimeMinutes,
            whisperSync = false,
            publisher = book.Publisher,
            isbn = book.Isbn,
            language = book.Language,
            releaseDate = book.ReleaseDate,
            @explicit = false,
            hasPdf = false,
            link = !string.IsNullOrWhiteSpace(book.Asin)
                ? MarketDomainResolver.BuildAudibleProductUrl(book.Asin, region)
                : (string?)null,
            sku = book.Sku,
            isListenable = !string.IsNullOrWhiteSpace(book.Asin),
            isAvailable = true,
            isBuyable = true,
            contentType = book.ContentType ?? "Product",
            contentDeliveryType = book.ContentDeliveryType,
            authors,
            narrators,
            genres,
            series,
            seriesList = series.Select(s => $"{s.name}{(s.position != null ? $" #{s.position}" : "")}").ToList(),
            updatedAt = DateTime.UtcNow.ToString("o")
        };
    }

    public async Task<object> MapMetadataResultToAudibleAsync(
        MetadataSearchResult md,
        string region,
        HttpContext httpContext)
    {
        AudibleBookResponse? aud = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(md?.Asin))
            {
                aud = await _metadataService.GetAudibleMetadataAsync(md.Asin, region, true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogDebug(ex, "Failed to retrieve Audible metadata for ASIN {Asin}", md?.Asin);
        }

        if (aud != null)
        {
            return await MapAudibleMetadataToOutputAsync(aud, md, region, httpContext);
        }

        var fallbackAuthors = new List<object>();
        var fallbackNarrators = new List<object>();
        if (!string.IsNullOrWhiteSpace(md?.Narrator)) fallbackNarrators.Add(new { name = md.Narrator, updatedAt = (string?)null });
        if (!string.IsNullOrWhiteSpace(md?.Author)) fallbackAuthors.Add(new { asin = (string?)null, name = md.Author, region = region, regions = new[] { region }, image = (string?)null, updatedAt = (string?)null });

        var fallbackSeries = new List<object>();
        if (!string.IsNullOrWhiteSpace(md?.Series)) fallbackSeries.Add(new { asin = md.Series, name = md.Series, region = region, position = md.SeriesNumber, updatedAt = (string?)null });

        return new
        {
            asin = md?.Asin,
            title = md?.Title,
            subtitle = md?.Subtitle,
            region = region,
            regions = new[] { region },
            description = md?.Description,
            summary = md?.Description,
            copyright = (string?)null,
            bookFormat = (string?)null,
            imageUrl = md?.ImageUrl,
            lengthMinutes = md?.Runtime,
            whisperSync = false,
            publisher = md?.Publisher,
            isbn = md?.Isbn,
            language = md?.Language,
            rating = (double?)null,
            releaseDate = md?.PublishedDate,
            @explicit = false,
            hasPdf = false,
            link = NormalizeAudibleProductUrl(md?.ProductUrl, md?.Asin, region),
            sku = (string?)null,
            skuGroup = (string?)null,
            isListenable = !string.IsNullOrWhiteSpace(md?.Asin),
            isAvailable = true,
            isBuyable = true,
            contentType = "Product",
            contentDeliveryType = (string?)null,
            authors = fallbackAuthors,
            narrators = fallbackNarrators,
            genres = new List<object>(),
            series = fallbackSeries,
            updatedAt = (string?)null
        };
    }

    public async Task EnsureCachedImagesForAudibleResultsAsync(
        List<AudibleSearchResult>? results,
        HttpContext httpContext)
    {
        if (results == null || results.Count == 0) return;
        if (_imageCacheService == null) return;

        foreach (var r in results)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(r.Asin)) continue;

                var cached = await _imageCacheService.GetCachedImagePathAsync(r.Asin);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    r.ImageUrl = BuildApiImagePath(r.Asin, httpContext);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(r.ImageUrl))
                {
                    var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(r.ImageUrl, r.Asin);
                    if (!string.IsNullOrWhiteSpace(downloaded))
                    {
                        r.ImageUrl = BuildApiImagePath(r.Asin, httpContext);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to ensure cached image for {Asin}", r?.Asin);
            }
        }
    }

    private async Task<object> MapAudibleMetadataToOutputAsync(
        AudibleBookResponse aud,
        MetadataSearchResult? md,
        string region,
        HttpContext httpContext)
    {
        string? imageUrl = aud.ImageUrl;
        try
        {
            if (!string.IsNullOrWhiteSpace(aud.Asin) && _imageCacheService != null)
            {
                var cached = await _imageCacheService.GetCachedImagePathAsync(aud.Asin);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    imageUrl = BuildApiImagePath(aud.Asin, httpContext);
                }
                else if (!string.IsNullOrWhiteSpace(imageUrl) && (imageUrl.StartsWith("http://") || imageUrl.StartsWith("https://")))
                {
                    var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(imageUrl, aud.Asin);
                    if (!string.IsNullOrWhiteSpace(downloaded)) imageUrl = BuildApiImagePath(aud.Asin, httpContext);
                }
                else
                {
                    imageUrl = BuildApiImagePath(aud.Asin, httpContext);
                    _ = _imageCacheService.DownloadAndCacheImageAsync(aud.ImageUrl ?? imageUrl, aud.Asin);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogWarning(ex, "Failed to normalize Audible image for {Asin}", aud.Asin);
        }

        var authors = (aud.Authors ?? new List<AudibleAuthor>()).Where(a => a != null).Select(a => new
        {
            asin = a!.Asin,
            name = a!.Name,
            region = a!.Region ?? region,
            regions = new[] { a!.Region ?? region },
            image = (string?)null,
            updatedAt = DateTime.UtcNow.ToString("o")
        }).ToList();

        var narrators = (aud.Narrators ?? new List<AudibleNarrator>()).Where(n => n != null).Select(n => new { name = n!.Name, updatedAt = DateTime.UtcNow.ToString("o") }).ToList();
        var genres = (aud.Genres ?? new List<AudibleGenre>()).Where(g => g != null).Select(g => new
        {
            asin = g!.Asin,
            name = g!.Name,
            type = g!.Type,
            betterType = (string?)null,
            updatedAt = DateTime.UtcNow.ToString("o")
        }).ToList();
        var series = (aud.Series ?? new List<AudibleSeries>()).Where(s => s != null).Select(s => new
        {
            asin = s!.Asin,
            name = s!.Name,
            region = region,
            position = s!.Position,
            updatedAt = DateTime.UtcNow.ToString("o")
        }).ToList();

        return new
        {
            asin = aud.Asin ?? md?.Asin,
            title = aud.Title ?? md?.Title,
            subtitle = aud.Subtitle ?? md?.Subtitle,
            region = aud.Region ?? region,
            regions = new[] { aud.Region ?? region },
            description = aud.Description ?? md?.Description,
            summary = aud.Description ?? md?.Description,
            copyright = (string?)null,
            bookFormat = aud.BookFormat,
            imageUrl = imageUrl,
            lengthMinutes = aud.LengthMinutes ?? md?.Runtime,
            whisperSync = false,
            publisher = aud.Publisher ?? md?.Publisher,
            isbn = aud.Isbn,
            language = aud.Language ?? md?.Language,
            rating = (double?)null,
            releaseDate = aud.ReleaseDate ?? aud.PublishDate ?? md?.PublishedDate,
            @explicit = aud.Explicit ?? false,
            hasPdf = false,
            link = NormalizeAudibleProductUrl(md?.ProductUrl, aud.Asin ?? md?.Asin, aud.Region ?? region),
            sku = aud.Sku,
            skuGroup = (string?)null,
            isListenable = !string.IsNullOrWhiteSpace(aud.Asin ?? md?.Asin),
            isAvailable = true,
            isBuyable = true,
            contentType = aud.ContentType ?? (string?)null,
            contentDeliveryType = aud.ContentDeliveryType,
            authors = authors,
            narrators = narrators,
            genres = genres,
            series = series,
            seriesList = series.Select(s => $"{s.name}{(s.position != null ? $" #{s.position}" : "")}").ToList(),
            updatedAt = DateTime.UtcNow.ToString("o")
        };
    }

    private static string? NormalizeAudibleProductUrl(string? url, string? asin, string? region)
    {
        if (!string.IsNullOrWhiteSpace(asin))
        {
            return MarketDomainResolver.BuildAudibleProductUrl(asin, region);
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!IsAudibleUrl(url))
        {
            return url;
        }

        return RegionalizeAudibleUrl(url, region);
    }

    private static bool IsAudibleUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url) &&
            Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            IsAudibleHost(uri.Host);
    }

    private static bool IsAudibleHost(string host)
    {
        var normalized = host.Trim().ToLowerInvariant();
        return normalized.StartsWith("audible.", StringComparison.Ordinal) ||
               normalized.StartsWith("www.audible.", StringComparison.Ordinal) ||
               normalized.StartsWith("api.audible.", StringComparison.Ordinal);
    }

    private static string RegionalizeAudibleUrl(string url, string? region)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var builder = new UriBuilder(uri)
        {
            Host = MarketDomainResolver.GetAudibleDomain(region)
        };

        return builder.Uri.ToString();
    }
}
