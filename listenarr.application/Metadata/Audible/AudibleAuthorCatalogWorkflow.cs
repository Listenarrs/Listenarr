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

using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Listenarr.Application.Metadata.Audible
{
    internal sealed class AudibleAuthorCatalogWorkflow
    {
        private readonly IAudibleAuthorPageParser? _authorPageParser;
        private readonly Func<string?, string?, string?, string?, string?, int, int, string, string?, string, bool, Task<SearchProductsDirectResponse>> _searchProductsDirectAsync;
        private readonly Func<string, string, bool, string?, Task<AudibleBookResponse?>> _getBookMetadataAsync;
        private readonly Func<string, int, Task<HttpResponseMessage?>> _getWithTimeoutAsync;
        private readonly AudibleApiClient _apiClient;
        private readonly AudibleProductMetadataWorkflow _metadataWorkflow;
        private readonly AudibleAuthorLookupWorkflow _authorLookupWorkflow;
        private readonly ILogger _logger;

        public AudibleAuthorCatalogWorkflow(
            IAudibleAuthorPageParser? authorPageParser,
            Func<string?, string?, string?, string?, string?, int, int, string, string?, string, bool, Task<SearchProductsDirectResponse>> searchProductsDirectAsync,
            Func<string, string, bool, string?, Task<AudibleBookResponse?>> getBookMetadataAsync,
            Func<string, int, Task<HttpResponseMessage?>> getWithTimeoutAsync,
            AudibleApiClient apiClient,
            AudibleProductMetadataWorkflow metadataWorkflow,
            AudibleAuthorLookupWorkflow authorLookupWorkflow,
            ILogger logger)
        {
            _authorPageParser = authorPageParser;
            _searchProductsDirectAsync = searchProductsDirectAsync;
            _getBookMetadataAsync = getBookMetadataAsync;
            _getWithTimeoutAsync = getWithTimeoutAsync;
            _apiClient = apiClient;
            _metadataWorkflow = metadataWorkflow;
            _authorLookupWorkflow = authorLookupWorkflow;
            _logger = logger;
        }

        public async Task<AudibleSearchResponse?> GetBooksByAuthorAsinAsync(string authorAsin, int page, int limit, string region, string? language)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(authorAsin))
                {
                    return null;
                }

                var requestedPage = Math.Max(1, page);
                var pageSize = Math.Clamp(limit, 1, 500);
                var desiredSkip = (requestedPage - 1) * pageSize;
                var desiredTake = pageSize;
                var collectedAsins = new List<string>();
                string? continuationToken = null;
                var iteration = 0;

                while (iteration < 10 && collectedAsins.Count < desiredSkip + desiredTake)
                {
                    iteration++;
                    var tokenQuery = string.IsNullOrWhiteSpace(continuationToken)
                        ? string.Empty
                        : $"&pageSectionContinuationToken={Uri.EscapeDataString(continuationToken)}";
                    var authorPageUrl =
                        $"{AudibleRequestHelper.BuildApiBaseUrl(region)}/1.0/screens/audible-android-author-detail/{Uri.EscapeDataString(authorAsin)}" +
                        $"?tabId=titles&author_asin={Uri.EscapeDataString(authorAsin)}&title_source=all" +
                        $"&session_id={Uri.EscapeDataString(AudibleRequestHelper.GenerateRandomSessionId())}" +
                        $"&applicationType=Android_App&local_time={Uri.EscapeDataString(DateTime.UtcNow.ToString("O"))}" +
                        $"&response_groups=always-returned&surface=Android{tokenQuery}";

                    using var authorPageDoc = await _apiClient.GetJsonDocumentAsync(
                        authorPageUrl,
                        region,
                        includeLocaleHeaders: true,
                        timeoutSeconds: 10);
                    if (authorPageDoc == null)
                    {
                        break;
                    }

                    var root = authorPageDoc.RootElement;
                    if (!root.TryGetProperty("sections", out var sections) || sections.ValueKind != JsonValueKind.Array)
                    {
                        break;
                    }

                    continuationToken = null;
                    foreach (var section in sections.EnumerateArray())
                    {
                        if (!section.TryGetProperty("model", out var model) ||
                            !model.TryGetProperty("rows", out var rows) ||
                            rows.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        collectedAsins.AddRange(
                            rows.EnumerateArray()
                                .Select(row => GetString(row, "product_metadata", "asin"))
                                .Where(asin => !string.IsNullOrWhiteSpace(asin))!);

                        continuationToken = GetString(section, "pagination");
                        if (rows.GetArrayLength() > 0)
                        {
                            break;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(continuationToken))
                    {
                        break;
                    }
                }

                var pagedAsins = collectedAsins
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Skip(desiredSkip)
                    .Take(desiredTake)
                    .ToList();
                if (pagedAsins.Count == 0)
                {
                    return new AudibleSearchResponse
                    {
                        Results = new List<AudibleSearchResult>(),
                        TotalResults = collectedAsins.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                    };
                }

                var books = await _metadataWorkflow.GetBooksMetadataByAsinsAsync(pagedAsins, region);
                var mapped = books
                    .Where(book => book != null)
                    .Select(AudibleProductMapper.MapBookResponseToSearchResult)
                    .Where(book => book != null)
                    .Cast<AudibleSearchResult>()
                    .ToList();

                mapped = AudibleProductMapper.ApplyLanguageFilter(mapped, language);

                return new AudibleSearchResponse
                {
                    Results = mapped,
                    TotalResults = collectedAsins.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error fetching books for author ASIN {AuthorAsin}", LogRedaction.SanitizeText(authorAsin));
                return null;
            }
        }

        public async Task<AudibleSearchResponse?> SearchByAuthorAsync(string author, int page, int limit, string region, string? language)
        {
            var authorLookupItems = await _authorLookupWorkflow.LookupAuthorItemsAsync(author, region, language);
            var authorAsin = authorLookupItems.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Asin))?.Asin;
            if (string.IsNullOrWhiteSpace(authorAsin))
            {
                _logger.LogWarning("No author ASIN found for author '{Author}'", LogRedaction.SanitizeText(author));
                return null;
            }

            return await GetBooksByResolvedAuthorAsync(author, authorAsin, page, limit, region, language);
        }

        public async Task<AudibleSearchResponse?> GetBooksByAuthorAsync(string author, string authorAsin, int page, int limit, string region, string? language)
        {
            if (string.IsNullOrWhiteSpace(authorAsin)) return null;
            return await GetBooksByResolvedAuthorAsync(author, authorAsin, page, limit, region, language);
        }

        public async Task<AudibleSearchResponse?> GetAllBooksByAuthorAsync(string author, string authorAsin, int limit, string region, string? language)
        {
            if (string.IsNullOrWhiteSpace(authorAsin))
            {
                return null;
            }

            var directResults = await GetDirectAuthorCatalogResultsAsync(author, authorAsin, region, language);
            if (directResults.Count > 0)
            {
                var cappedLimit = Math.Clamp(limit, 1, 500);
                return new AudibleSearchResponse
                {
                    Results = directResults.Take(cappedLimit).ToList(),
                    TotalResults = directResults.Count
                };
            }

            var fallbackLimit = Math.Clamp(limit, 1, 500);
            var authorScreenResult = await GetBooksByAuthorAsinAsync(authorAsin, 1, fallbackLimit, region, language);
            if (authorScreenResult?.Results?.Count > 0)
            {
                return authorScreenResult;
            }

            _logger.LogWarning(
                "Direct Audible author catalog lookup returned no results for author {Author} (ASIN {AuthorAsin}); falling back to Audible author page scraping",
                LogRedaction.SanitizeText(author),
                LogRedaction.SanitizeText(authorAsin));

            return await ScrapeAudibleAuthorPageAsync(author, authorAsin, 1, fallbackLimit, region, language);
        }

        public async Task<AudibleSearchResponse?> GetBooksByResolvedAuthorAsync(string author, string authorAsin, int page, int limit, string region, string? language)
        {
            var fullCatalogResult = await GetAllBooksByAuthorAsync(author, authorAsin, 500, region, language);
            if (fullCatalogResult?.Results?.Count > 0)
            {
                var pageSize = Math.Clamp(limit, 1, 500);
                var skip = Math.Max(0, (page - 1) * pageSize);

                return new AudibleSearchResponse
                {
                    Results = fullCatalogResult.Results.Skip(skip).Take(pageSize).ToList(),
                    TotalResults = fullCatalogResult.TotalResults
                };
            }

            return fullCatalogResult;
        }

        private async Task<List<AudibleSearchResult>> GetDirectAuthorCatalogResultsAsync(string author, string authorAsin, string region, string? language)
        {
            var normalizedAuthor = author?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedAuthor))
            {
                return new List<AudibleSearchResult>();
            }

            var results = new List<AudibleSearchResult>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var maxPages = 10;

            for (var currentPage = 1; currentPage <= maxPages; currentPage++)
            {
                var response = await _searchProductsDirectAsync(
                    null,
                    null,
                    normalizedAuthor,
                    null,
                    null,
                    currentPage,
                    50,
                    region,
                    null,
                    "BestSellers",
                    false);

                if (response.Results.Count == 0)
                {
                    break;
                }

                if (response.TotalResults > 0)
                {
                    maxPages = Math.Min(10, (int)Math.Ceiling(response.TotalResults / 50d));
                }

                foreach (var result in response.Results)
                {
                    if (!AudibleAuthorCatalogMatcher.MatchesTarget(result, normalizedAuthor, authorAsin))
                    {
                        continue;
                    }

                    var key = AudibleAuthorCatalogMatcher.BuildSearchResultKey(result);
                    if (!seenKeys.Add(key))
                    {
                        continue;
                    }

                    results.Add(result);
                }

                if (response.Results.Count < 50)
                {
                    break;
                }
            }

            return AudibleProductMapper.ApplyLanguageFilter(results, language);
        }

        private async Task<AudibleSearchResponse?> ScrapeAudibleAuthorPageAsync(string author, string authorAsin, int page, int limit, string region, string? language)
        {
            try
            {
                var authorPageUrl = AudibleRequestHelper.BuildAuthorPageUrl(author, authorAsin, region);
                _logger.LogInformation("Scraping Audible author page as fallback: {Url}", authorPageUrl);

                var response = await _getWithTimeoutAsync(authorPageUrl, 10);
                if (response == null)
                {
                    _logger.LogWarning("Audible author page request timed out for author {Author}", LogRedaction.SanitizeText(author));
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Audible author page returned status code {StatusCode} for author {Author}", response.StatusCode, author);
                    return null;
                }

                var html = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(html))
                {
                    _logger.LogWarning("Audible author page returned empty HTML for author {Author}", LogRedaction.SanitizeText(author));
                    return null;
                }

                if (_authorPageParser == null)
                {
                    _logger.LogWarning("Audible author page parser is unavailable for author {Author}", LogRedaction.SanitizeText(author));
                    return null;
                }

                var parsedTiles = _authorPageParser.ParseAuthorPage(html, author, authorAsin, region);
                if (parsedTiles.Count == 0)
                {
                    _logger.LogWarning("Audible author page tiles could not be parsed for author {Author}", LogRedaction.SanitizeText(author));
                    return null;
                }

                await EnrichFallbackAuthorResultsAsync(parsedTiles, region);

                var authorMatchedTiles = parsedTiles
                    .Where(result => result.Authors?.Any(authorItem => string.Equals(authorItem.Name, author, StringComparison.OrdinalIgnoreCase)) == true)
                    .ToList();
                var filteredTiles = authorMatchedTiles.Count > 0 ? authorMatchedTiles : parsedTiles;

                if (!string.IsNullOrWhiteSpace(language))
                {
                    filteredTiles = filteredTiles
                        .Where(result => !string.IsNullOrWhiteSpace(result.Language) && string.Equals(result.Language, language, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                var skip = Math.Max(0, (page - 1) * Math.Max(1, limit));
                var pagedTiles = filteredTiles.Skip(skip).Take(Math.Max(1, limit)).ToList();

                _logger.LogInformation(
                    "Audible author page fallback returned {PagedCount} of {TotalCount} parsed title(s) for author {Author}",
                    pagedTiles.Count,
                    filteredTiles.Count,
                    LogRedaction.SanitizeText(author));

                return new AudibleSearchResponse
                {
                    Results = pagedTiles,
                    TotalResults = filteredTiles.Count
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to scrape Audible author page fallback for author {Author}", LogRedaction.SanitizeText(author));
                return null;
            }
        }

        private async Task EnrichFallbackAuthorResultsAsync(List<AudibleSearchResult> books, string region)
        {
            foreach (var book in books)
            {
                if (string.IsNullOrWhiteSpace(book.Asin))
                {
                    continue;
                }

                try
                {
                    var metadata = await _getBookMetadataAsync(book.Asin, region, true, null);
                    if (metadata == null)
                    {
                        continue;
                    }

                    book.Title = string.IsNullOrWhiteSpace(metadata.Title) ? book.Title : metadata.Title;
                    book.Subtitle = string.IsNullOrWhiteSpace(book.Subtitle) ? metadata.Subtitle : book.Subtitle;
                    if (metadata.Authors?.Any() == true)
                    {
                        book.Authors = metadata.Authors;
                    }

                    book.ImageUrl = string.IsNullOrWhiteSpace(book.ImageUrl) ? metadata.ImageUrl : book.ImageUrl;
                    book.LengthMinutes ??= metadata.LengthMinutes;
                    book.RuntimeLengthMin ??= metadata.LengthMinutes;
                    book.Language = string.IsNullOrWhiteSpace(book.Language) ? metadata.Language : book.Language;
                    book.ContentType = string.IsNullOrWhiteSpace(book.ContentType) ? metadata.ContentType : book.ContentType;
                    book.ContentDeliveryType = string.IsNullOrWhiteSpace(book.ContentDeliveryType) ? metadata.ContentDeliveryType : book.ContentDeliveryType;
                    book.BookFormat = string.IsNullOrWhiteSpace(book.BookFormat) ? metadata.BookFormat : book.BookFormat;
                    if (metadata.Genres?.Any() == true)
                    {
                        book.Genres = metadata.Genres;
                    }

                    if (metadata.Series?.Any() == true)
                    {
                        book.Series = metadata.Series;
                    }

                    book.Publisher = string.IsNullOrWhiteSpace(book.Publisher) ? metadata.Publisher : book.Publisher;
                    if (metadata.Narrators?.Any() == true)
                    {
                        book.Narrators = metadata.Narrators;
                    }

                    book.ReleaseDate = string.IsNullOrWhiteSpace(book.ReleaseDate) ? metadata.ReleaseDate : book.ReleaseDate;
                    book.Isbn = string.IsNullOrWhiteSpace(book.Isbn) ? metadata.Isbn : book.Isbn;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Failed to hydrate fallback author page metadata for ASIN {Asin}", book.Asin);
                }
            }
        }

        private static string? GetString(JsonElement element, params string[] path)
        {
            var current = element;
            foreach (var segment in path)
            {
                if (current.ValueKind != JsonValueKind.Object ||
                    !current.TryGetProperty(segment, out current))
                {
                    return null;
                }
            }

            return current.ValueKind switch
            {
                JsonValueKind.String => current.GetString(),
                JsonValueKind.Number => current.ToString(),
                JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
                JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
                _ => null
            };
        }
    }
}
