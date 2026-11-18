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
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Metadata.Audible
{
    public class AudibleService
    {
        private readonly ILogger<AudibleService> _logger;
        private readonly AudibleApiClient _apiClient;
        private readonly AudibleProductMetadataWorkflow _metadataWorkflow;
        private readonly AudibleProductSearchWorkflow _productSearchWorkflow;
        private readonly AudibleAuthorLookupWorkflow _authorLookupWorkflow;
        private readonly AudibleAuthorCatalogWorkflow _authorCatalogWorkflow;
        private readonly AudibleSeriesWorkflow _seriesWorkflow;

        public AudibleService(HttpClient httpClient, ILogger<AudibleService> logger)
            : this(httpClient, logger, null)
        {
        }

        [ActivatorUtilitiesConstructor]
        public AudibleService(HttpClient httpClient, ILogger<AudibleService> logger, IAudibleAuthorPageParser? authorPageParser)
        {
            _logger = logger;
            _apiClient = new AudibleApiClient(httpClient, _logger);
            _metadataWorkflow = new AudibleProductMetadataWorkflow(_apiClient, _logger);
            _productSearchWorkflow = new AudibleProductSearchWorkflow(_apiClient, GetBookMetadataAsync, _logger);
            _authorLookupWorkflow = new AudibleAuthorLookupWorkflow(_apiClient, _productSearchWorkflow, _logger);
            _seriesWorkflow = new AudibleSeriesWorkflow(_apiClient, _metadataWorkflow, _productSearchWorkflow, _logger);
            _authorCatalogWorkflow = new AudibleAuthorCatalogWorkflow(
                authorPageParser,
                SearchProductsDirectAsync,
                GetBookMetadataAsync,
                GetWithTimeoutAsync,
                _apiClient,
                _metadataWorkflow,
                _authorLookupWorkflow,
                _logger);
        }

        /// <summary>
        /// Fetches books for a given author ASIN using the /author/books/[ASIN] endpoint.
        /// </summary>
        /// <param name="authorAsin">The ASIN of the author.</param>
        /// <param name="page">Page number (default 1).</param>
        /// <param name="limit">Number of results per page (default 50).</param>
        /// <param name="region">Region (default "us").</param>
        /// <param name="language">Optional language filter.</param>
        /// <returns>AudibleSearchResponse containing books by the author.</returns>
        public virtual async Task<AudibleSearchResponse?> GetBooksByAuthorAsinAsync(string authorAsin, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            return await _authorCatalogWorkflow.GetBooksByAuthorAsinAsync(authorAsin, page, limit, region, language);
        }

        // Series lookup helpers (proxy audible /series endpoints)
        public virtual async Task<object?> SearchSeriesByNameAsync(string name, string region = "us")
        {
            return await _seriesWorkflow.SearchSeriesByNameAsync(name, region);
        }

        public virtual async Task<SeriesLookupItem?> LookupSeriesAsync(string seriesName, string region = "us")
        {
            return await _seriesWorkflow.LookupSeriesAsync(seriesName, region);
        }

        public virtual async Task<SeriesLookupItem?> GetSeriesByAsinAsync(string seriesAsin, string region = "us")
        {
            return await _seriesWorkflow.GetSeriesByAsinAsync(seriesAsin, region);
        }

        public virtual async Task<object?> GetBooksBySeriesAsinAsync(string seriesAsin, string region = "us")
        {
            return await _seriesWorkflow.GetBooksBySeriesAsinAsync(seriesAsin, region);
        }

        public virtual async Task<List<AudibleSearchResult>?> GetTypedBooksBySeriesAsinAsync(string seriesAsin, string region = "us")
        {
            return await _seriesWorkflow.GetTypedBooksBySeriesAsinAsync(seriesAsin, region);
        }

        public virtual async Task<AudibleBookResponse?> GetBookMetadataAsync(string asin, string region = "us", bool useCache = true, string? language = null)
        {
            return await _metadataWorkflow.GetBookMetadataAsync(asin, region, language);
        }

        public virtual async Task<AudibleSearchResponse?> SearchByTitleAsync(string title, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            return await _productSearchWorkflow.SearchByTitleAsync(title, page, limit, region, language);
        }

        public virtual async Task<AudibleSearchResponse?> SearchByTitleAndAuthorAsync(string title, string author, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            // For advanced title+author searches, prefer the author lookup + /author/books/[ASIN] flow
            return await SearchByTitleAndAuthorPagedAsync(title, author, page, limit, region, language);
        }

        public virtual async Task<AudibleSearchResponse?> SearchByTitleAndAuthorPagedAsync(string title, string author, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            // Prefer author-specific endpoint when an author is provided: lookup author ASIN then request their books
            if (string.IsNullOrWhiteSpace(author))
            {
                return await SearchByTitleAsync(title, page, limit, region, language);
            }

            try
            {
                var authorLookupItems = await LookupAuthorItemsAsync(author, region, language);
                var authorAsin = authorLookupItems.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Asin))?.Asin;
                if (string.IsNullOrWhiteSpace(authorAsin))
                {
                    _logger.LogWarning("No author ASIN found for author '{Author}', falling back to direct Audible title/author search", LogRedaction.SanitizeText(author));
                    var response = await SearchProductsDirectAsync(
                        query: null,
                        title: title,
                        author: author,
                        narrator: null,
                        publisher: null,
                        page: page,
                        limit: limit,
                        region: region,
                        language: language,
                        sortBy: "Title");
                    return ToSearchResponse(response);
                }

                var booksResult = await GetBooksByResolvedAuthorAsync(author, authorAsin, page, limit, region, language);
                if (booksResult == null || booksResult.Results == null) return booksResult;

                // 3) Apply server-side filtering using provided title, isbn, asin, language if present
                var filtered = booksResult.Results.AsEnumerable();

                // If the title parameter encodes an ISBN (e.g. "ISBN:1234567890"), extract it
                string? isbnFromTitle = null;
                if (!string.IsNullOrWhiteSpace(title) && title.Trim().StartsWith("ISBN:", StringComparison.OrdinalIgnoreCase))
                {
                    isbnFromTitle = title.Trim().Substring(5).Trim();
                }

                if (!string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(isbnFromTitle))
                {
                    var t = title.Trim();
                    var ci = CultureInfo.InvariantCulture.CompareInfo;
                    filtered = filtered.Where(r => !string.IsNullOrWhiteSpace(r.Title) && ci.IndexOf(r.Title, t, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
                }

                // If title looks like an ASIN, prefer exact ASIN match
                if (!string.IsNullOrWhiteSpace(title) && title.Trim().StartsWith("B0", StringComparison.OrdinalIgnoreCase) && title.Trim().Length >= 10)
                {
                    var possibleAsin = title.Trim();
                    filtered = filtered.Where(r => string.Equals(r.Asin, possibleAsin, StringComparison.OrdinalIgnoreCase));
                }

                // If ISBN was provided via title token, try to resolve by fetching metadata per candidate
                if (!string.IsNullOrWhiteSpace(isbnFromTitle))
                {
                    var candidates = filtered.ToList();
                    var matched = new List<AudibleSearchResult>();
                    foreach (var c in candidates)
                    {
                        try
                        {
                            if (string.IsNullOrWhiteSpace(c.Asin)) continue;
                            var meta = await GetBookMetadataAsync(c.Asin, region, true, language);
                            if (meta != null && !string.IsNullOrWhiteSpace(meta.Isbn) && string.Equals(meta.Isbn.Trim(), isbnFromTitle.Trim(), StringComparison.OrdinalIgnoreCase))
                            {
                                matched.Add(c);
                            }
                        }
                        catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
                        {
                            _logger.LogDebug(caughtEx_1, "Could not fetch metadata for candidate ASIN {Asin} while matching an ISBN; skipping this candidate", c.Asin);
                        }
                    }

                    filtered = matched;
                }

                // Language filter (use explicit language param when provided)
                if (!string.IsNullOrWhiteSpace(language))
                {
                    var lang = language.Trim().ToLowerInvariant();
                    filtered = filtered.Where(r => !string.IsNullOrWhiteSpace(r.Language) && r.Language.Trim().ToLowerInvariant() == lang);
                }

                var finalList = filtered.ToList();
                return new AudibleSearchResponse { Results = finalList, TotalResults = finalList.Count };
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error executing author-based search for: {Title} / {Author}", title, author);
                return null;
            }
        }

        public virtual async Task<AudibleSearchResponse?> SearchByAuthorAsync(string author, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            return await _authorCatalogWorkflow.SearchByAuthorAsync(author, page, limit, region, language);
        }

        public virtual async Task<AudibleSearchResponse?> GetBooksByAuthorAsync(string author, string authorAsin, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            return await _authorCatalogWorkflow.GetBooksByAuthorAsync(author, authorAsin, page, limit, region, language);
        }

        public virtual async Task<AudibleSearchResponse?> GetAllBooksByAuthorAsync(string author, string authorAsin, int limit = 250, string region = "us", string? language = null)
        {
            if (string.IsNullOrWhiteSpace(authorAsin))
            {
                return null;
            }

            return await _authorCatalogWorkflow.GetAllBooksByAuthorAsync(author, authorAsin, limit, region, language);
        }

        /// <summary>
        /// Lookup a single author by name using the Audible /author endpoint and return basic info (ASIN + image if available).
        /// </summary>
        public virtual async Task<AuthorLookupItem?> LookupAuthorAsync(string author, string region = "us")
        {
            return await _authorLookupWorkflow.LookupAuthorAsync(author, region);
        }

        /// <summary>
        /// Lookup a single author by ASIN using the Audible /author/{asin} endpoint.
        /// </summary>
        public virtual async Task<AuthorLookupItem?> GetAuthorByAsinAsync(string authorAsin, string region = "us")
        {
            return await _authorLookupWorkflow.GetAuthorByAsinAsync(authorAsin, region);
        }

        private async Task<List<AuthorLookupItem>> LookupAuthorItemsAsync(string author, string region = "us", string? language = null)
        {
            return await _authorLookupWorkflow.LookupAuthorItemsAsync(author, region, language);
        }

        private async Task<List<SeriesLookupItem>> LookupSeriesItemsAsync(string seriesName, string region = "us")
        {
            return await _seriesWorkflow.LookupSeriesItemsAsync(seriesName, region);
        }

        private async Task<SearchProductsDirectResponse> SearchProductsDirectAsync(
            string? query,
            string? title,
            string? author,
            string? narrator,
            string? publisher,
            int page,
            int limit,
            string region,
            string? language,
            string sortBy,
            bool returnRawProducts = false)
        {
            return await _productSearchWorkflow.SearchProductsDirectAsync(
                query,
                title,
                author,
                narrator,
                publisher,
                page,
                limit,
                region,
                language,
                sortBy,
                returnRawProducts);
        }

        private static AudibleSearchResponse ToSearchResponse(SearchProductsDirectResponse response)
        {
            return new AudibleSearchResponse
            {
                Results = response.Results,
                TotalResults = response.TotalResults,
                ProviderUnavailable = response.ProviderUnavailable
            };
        }

        /// <summary>
        /// Strips diacritical marks (accents) from a string so that characters
        /// like Å → A, ä → a, ö → o, etc.  The Audible API returns poor or no
        /// results when the query contains non-ASCII diacritics, so we normalize
        /// before sending the request.  Result metadata still contains the
        /// correct accented characters from the API response.
        /// </summary>
        internal static string RemoveDiacritics(string text)
        {
            return AudibleRequestHelper.RemoveDiacritics(text);
        }

        private async Task<AudibleSearchResponse?> GetBooksByResolvedAuthorAsync(string author, string authorAsin, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            return await _authorCatalogWorkflow.GetBooksByResolvedAuthorAsync(author, authorAsin, page, limit, region, language);
        }

        public virtual async Task<AudibleSearchResponse?> SearchByIsbnAsync(string isbn, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            return await _productSearchWorkflow.SearchByIsbnAsync(isbn, page, limit, region, language);
        }

        public virtual async Task<AudibleSearchResponse?> SearchBooksAsync(string query, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            return await _productSearchWorkflow.SearchBooksAsync(query, page, limit, region, language);
        }

        private async Task<AudibleSearchResponse?> ExecuteSearchAsync(string url, string searchTerm)
        {
            try
            {
                var response = await GetWithTimeoutAsync(url);
                if (response == null)
                {
                    _logger.LogWarning("Audible search request timed out for: {SearchTerm}", searchTerm);
                    return null;
                }
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Audible search returned status code {StatusCode} for: {SearchTerm}", response.StatusCode, searchTerm);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();

                // Avoid throwing and logging exceptions for expected formats by inspecting JSON first
                var trimmed = json.TrimStart();

                if (!string.IsNullOrEmpty(trimmed) && trimmed[0] == '[')
                {
                    // JSON array -> deserialize as a list
                    try
                    {
                        var list = JsonSerializer.Deserialize<List<AudibleSearchResult>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (list != null)
                        {
                            var dropped = list.Where(r => SearchResultIndicatesPodcast(r)).ToList();
                            var filtered = list.Except(dropped).ToList();

                            if (dropped.Any())
                            {
                                try
                                {
                                    var entries = dropped.Select(r => string.Format("{0} :: {1} :: {2}", r.Asin ?? "<no-asin>", r.Title ?? "<no-title>", GetPodcastFilterReason(r) ?? "podcast_detected")).ToList();
                                    _logger.LogInformation("Audible search removed {Count} items due to podcast heuristics: {Entries}", dropped.Count, string.Join(" | ", entries));
                                }
                                catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException)
                                {
                                    // Nothing is logged here: the call that failed is the logging call itself.
                                }
                            }

                            if (filtered.Any()) return new AudibleSearchResponse { Results = filtered, TotalResults = filtered.Count };
                            else _logger.LogWarning("Audible search returned {Count} results after podcast filtering (list format) for: {SearchTerm}", filtered.Count, searchTerm);
                        }
                        else
                        {
                            _logger.LogWarning("Audible search returned null list for: {SearchTerm}, JSON: {Json}", searchTerm, json.Length > 500 ? json.Substring(0, 500) + "..." : json);
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Failed to deserialize JSON array as List<AudibleSearchResult> for: {SearchTerm}, JSON: {Json}", searchTerm, json.Length > 500 ? json.Substring(0, 500) + "..." : json);
                    }
                }
                else
                {
                    // JSON object -> expected envelope format
                    try
                    {
                        var envelope = JsonSerializer.Deserialize<AudibleSearchResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (envelope != null && envelope.Results != null)
                        {
                            var dropped = envelope.Results.Where(r => SearchResultIndicatesPodcast(r)).ToList();
                            if (dropped.Any())
                            {
                                try
                                {
                                    var entries = dropped.Select(r => string.Format("{0} :: {1} :: {2}", r.Asin ?? "<no-asin>", r.Title ?? "<no-title>", GetPodcastFilterReason(r) ?? "podcast_detected")).ToList();
                                    _logger.LogInformation("Audible search removed {Count} items due to podcast heuristics: {Entries}", dropped.Count, string.Join(" | ", entries));
                                }
                                catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException)
                                {
                                    // Nothing is logged here: the call that failed is the logging call itself.
                                }
                            }

                            envelope.Results = envelope.Results.Where(r => !SearchResultIndicatesPodcast(r)).ToList();
                            if (envelope.Results.Any()) return envelope;
                            else _logger.LogWarning("Audible search returned {Count} results after podcast filtering for: {SearchTerm}", envelope.Results.Count, searchTerm);
                        }
                        else
                        {
                            _logger.LogWarning("Audible search returned null envelope or null results for: {SearchTerm}, JSON: {Json}", searchTerm, json.Length > 500 ? json.Substring(0, 500) + "..." : json);
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Failed to deserialize as AudibleSearchResponse for: {SearchTerm}, JSON: {Json}", searchTerm, json.Length > 500 ? json.Substring(0, 500) + "..." : json);

                        // Last resort: attempt to parse as a list (some endpoints sometimes return a top-level array)
                        try
                        {
                            var list = JsonSerializer.Deserialize<List<AudibleSearchResult>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (list != null)
                            {
                                var filtered = list.Where(r => !SearchResultIndicatesPodcast(r)).ToList();
                                if (filtered.Any()) return new AudibleSearchResponse { Results = filtered, TotalResults = filtered.Count };
                                else _logger.LogWarning("Audible search returned {Count} results after podcast filtering (list format) for: {SearchTerm}", filtered.Count, searchTerm);
                            }
                        }
                        catch (JsonException ex2)
                        {
                            _logger.LogWarning(ex2, "Failed to deserialize as List<AudibleSearchResult> for: {SearchTerm}, JSON: {Json}", searchTerm, json.Length > 500 ? json.Substring(0, 500) + "..." : json);
                        }
                    }
                }

                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error searching the Audible catalog for: {SearchTerm}", searchTerm);
                return null;
            }
        }

        private async Task<HttpResponseMessage?> GetWithTimeoutAsync(string url, int timeoutSeconds = 5)
        {
            return await _apiClient.GetWithTimeoutAsync(url, timeoutSeconds);
        }

        private static bool SearchResultIndicatesPodcast(AudibleSearchResult? r)
        {
            return AudibleSearchResultFilter.IndicatesPodcast(r);
        }

        private static string? GetPodcastFilterReason(AudibleSearchResult? r)
        {
            return AudibleSearchResultFilter.GetPodcastFilterReason(r);
        }
    }
}
