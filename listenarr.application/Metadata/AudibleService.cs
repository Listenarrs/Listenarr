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
using System.Text;
using System.Text.Json;
using HtmlAgilityPack;
using Listenarr.Application.Security;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Metadata
{
    public class AudibleService
    {
        private const string BrowserAcceptHeader = "application/json, text/plain, */*";
        private const string BrowserAcceptLanguageHeader = "en-US,en;q=0.9";
        private const string BrowserUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";
        private const string AudibleApiAcceptHeader = "application/json";
        private const string AudibleApiUserAgent =
            "Dalvik/2.1.0 (Linux; U; Android 15); com.audible.application";
        private const string AudibleApiVerboseUserAgent =
            "Dalvik/2.1.0 (Linux; U; Android 15; good_phone Build/AAAA.240000.005); com.audible.application";
        private const string DefaultBookResponseGroups =
            "media,product_attrs,product_desc,product_details,product_extended_attrs,product_plans,rating,series,relationships,review_attrs,category_ladders,customer_rights";
        private const string DefaultSeriesResponseGroups =
            "relationships,product_attrs,product_desc,product_extended_attrs";
        private static readonly IReadOnlyDictionary<string, string> AudibleApiDomainMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["us"] = "api.audible.com",
                ["ca"] = "api.audible.ca",
                ["uk"] = "api.audible.co.uk",
                ["au"] = "api.audible.com.au",
                ["fr"] = "api.audible.fr",
                ["de"] = "api.audible.de",
                ["jp"] = "api.audible.co.jp",
                ["it"] = "api.audible.it",
                ["in"] = "api.audible.in",
                ["es"] = "api.audible.es",
                ["br"] = "api.audible.com.br",
            };
        private static readonly IReadOnlyDictionary<string, string> AudibleLocaleMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["us"] = "en-US",
                ["ca"] = "en-CA",
                ["uk"] = "en-GB",
                ["au"] = "en-AU",
                ["fr"] = "fr-FR",
                ["de"] = "de-DE",
                ["jp"] = "ja-JP",
                ["it"] = "it-IT",
                ["in"] = "en-IN",
                ["es"] = "es-ES",
                ["br"] = "pt-BR",
            };
        private readonly HttpClient _httpClient;
        private readonly ILogger<AudibleService> _logger;

        public AudibleService(HttpClient httpClient, ILogger<AudibleService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _httpClient.DefaultRequestHeaders.Accept.Clear();
            _httpClient.DefaultRequestHeaders.Accept.ParseAdd(BrowserAcceptHeader);
            _httpClient.DefaultRequestHeaders.AcceptLanguage.Clear();
            _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd(BrowserAcceptLanguageHeader);
            _httpClient.DefaultRequestHeaders.UserAgent.Clear();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
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
                        $"{BuildAudibleApiBaseUrl(region)}/1.0/screens/audible-android-author-detail/{Uri.EscapeDataString(authorAsin)}" +
                        $"?tabId=titles&author_asin={Uri.EscapeDataString(authorAsin)}&title_source=all" +
                        $"&session_id={Uri.EscapeDataString(GenerateRandomSessionId())}" +
                        $"&applicationType=Android_App&local_time={Uri.EscapeDataString(DateTime.UtcNow.ToString("O"))}" +
                        $"&response_groups=always-returned&surface=Android{tokenQuery}";

                    using var authorPageDoc = await GetAudibleJsonDocumentAsync(
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

                var books = await GetBooksMetadataByAsinsAsync(pagedAsins, region);
                var mapped = books
                    .Where(book => book != null)
                    .Select(MapBookResponseToSearchResult)
                    .Where(book => book != null)
                    .Cast<AudibleSearchResult>()
                    .ToList();

                mapped = ApplyLanguageFilter(mapped, language);

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

        // Series lookup helpers (proxy audible /series endpoints)
        public virtual async Task<object?> SearchSeriesByNameAsync(string name, string region = "us")
        {
            try
            {
                return await LookupSeriesItemsAsync(name, region);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error searching Audible series for name {Name}", LogRedaction.SanitizeText(name));
                return null;
            }
        }

        public virtual async Task<SeriesLookupItem?> LookupSeriesAsync(string seriesName, string region = "us")
        {
            if (string.IsNullOrWhiteSpace(seriesName))
            {
                return null;
            }

            try
            {
                var items = await LookupSeriesItemsAsync(seriesName, region);
                return items.FirstOrDefault(item =>
                           !string.IsNullOrWhiteSpace(item.Asin) &&
                           string.Equals(item.Region, region, StringComparison.OrdinalIgnoreCase))
                       ?? items.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Asin))
                       ?? items.FirstOrDefault();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to lookup series {Series}", LogRedaction.SanitizeText(seriesName));
                return null;
            }
        }

        public virtual async Task<SeriesLookupItem?> GetSeriesByAsinAsync(string seriesAsin, string region = "us")
        {
            if (string.IsNullOrWhiteSpace(seriesAsin))
            {
                return null;
            }

            try
            {
                using var doc = await GetAudibleProductDocumentAsync(seriesAsin, region, DefaultSeriesResponseGroups);
                if (doc == null ||
                    !doc.RootElement.TryGetProperty("product", out var product) ||
                    product.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                return new SeriesLookupItem
                {
                    Asin = GetString(product, "asin") ?? seriesAsin,
                    Name = GetString(product, "title"),
                    Region = NormalizeRegion(region),
                    Description = GetString(product, "publisher_summary") ?? GetString(product, "extended_product_description"),
                    Image = GetHighestResolutionImage(product)
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to lookup Audible series details by ASIN {SeriesAsin}", LogRedaction.SanitizeText(seriesAsin));
                return null;
            }
        }

        public virtual async Task<object?> GetBooksBySeriesAsinAsync(string seriesAsin, string region = "us")
        {
            try
            {
                return await GetTypedBooksBySeriesAsinAsync(seriesAsin, region);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error fetching Audible series books for ASIN {Asin}", LogRedaction.SanitizeText(seriesAsin));
                return null;
            }
        }

        public virtual async Task<List<AudibleSearchResult>?> GetTypedBooksBySeriesAsinAsync(string seriesAsin, string region = "us")
        {
            if (string.IsNullOrWhiteSpace(seriesAsin))
            {
                return null;
            }

            try
            {
                using var doc = await GetAudibleProductDocumentAsync(seriesAsin, region, DefaultSeriesResponseGroups);
                if (doc == null ||
                    !doc.RootElement.TryGetProperty("product", out var product) ||
                    product.ValueKind != JsonValueKind.Object)
                {
                    _logger.LogWarning("GetTypedBooksBySeriesAsinAsync: No product document for series ASIN {Asin} (doc={DocNull})", LogRedaction.SanitizeText(seriesAsin), doc == null);
                    return null;
                }

                if (!product.TryGetProperty("relationships", out var relationships) ||
                    relationships.ValueKind != JsonValueKind.Array)
                {
                    _logger.LogWarning("GetTypedBooksBySeriesAsinAsync: No relationships array for series ASIN {Asin}. Product has properties: {Props}",
                        LogRedaction.SanitizeText(seriesAsin),
                        string.Join(", ", product.EnumerateObject().Select(p => p.Name).Take(15)));
                    return new List<AudibleSearchResult>();
                }

                var relationshipEntries = relationships.EnumerateArray()
                    .Select(item => new
                    {
                        Asin = GetString(item, "asin"),
                        Position = GetString(item, "sequence") ?? GetString(item, "sort")
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.Asin))
                    .GroupBy(item => item.Asin!, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(item => ParseSeriesPosition(item.Position))
                    .ToList();

                _logger.LogInformation("GetTypedBooksBySeriesAsinAsync: Series ASIN {Asin} has {Count} relationship entries", LogRedaction.SanitizeText(seriesAsin), relationshipEntries.Count);

                var books = await GetBooksMetadataByAsinsAsync(
                    relationshipEntries.Select(item => item.Asin!),
                    region);

                _logger.LogInformation("GetTypedBooksBySeriesAsinAsync: Fetched metadata for {FetchedCount}/{TotalCount} books from series {Asin}",
                    books.Count, relationshipEntries.Count, LogRedaction.SanitizeText(seriesAsin));

                var booksByAsin = books
                    .Where(book => !string.IsNullOrWhiteSpace(book.Asin))
                    .ToDictionary(book => book.Asin!, StringComparer.OrdinalIgnoreCase);

                var results = new List<AudibleSearchResult>();
                foreach (var relationship in relationshipEntries)
                {
                    if (!booksByAsin.TryGetValue(relationship.Asin!, out var book))
                    {
                        continue;
                    }

                    var mapped = MapBookResponseToSearchResult(book);
                    if (mapped == null)
                    {
                        continue;
                    }

                    if (mapped.Series?.Any() == true)
                    {
                        foreach (var series in mapped.Series.Where(series =>
                                     string.Equals(series.Asin, seriesAsin, StringComparison.OrdinalIgnoreCase) &&
                                     string.IsNullOrWhiteSpace(series.Position)))
                        {
                            series.Position = relationship.Position;
                        }
                    }

                    results.Add(mapped);
                }

                return results;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error fetching Audible typed series books for ASIN {Asin}", LogRedaction.SanitizeText(seriesAsin));
                return null;
            }
        }

        public virtual async Task<AudibleBookResponse?> GetBookMetadataAsync(string asin, string region = "us", bool useCache = true, string? language = null)
        {
            try
            {
                var result = (await GetBooksMetadataByAsinsAsync(new[] { asin }, region)).FirstOrDefault();
                if (result != null &&
                    !string.IsNullOrWhiteSpace(language) &&
                    !string.Equals(result.Language, language, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error fetching metadata from Audible for ASIN {Asin}", LogRedaction.SanitizeText(asin));
                return null;
            }
        }

        public virtual async Task<AudibleSearchResponse?> SearchByTitleAsync(string title, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            var normalizedTitle = title?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedTitle))
            {
                return new AudibleSearchResponse
                {
                    Results = new List<AudibleSearchResult>(),
                    TotalResults = 0
                };
            }

            var response = await SearchProductsDirectAsync(
                query: normalizedTitle,
                title: null,
                author: null,
                narrator: null,
                publisher: null,
                page: page,
                limit: limit,
                region: region,
                language: language,
                sortBy: "Relevance");

            if (response.Results.Count > 0)
            {
                return ToSearchResponse(response);
            }

            _logger.LogInformation(
                "Audible keyword title search returned no results for '{Title}' in region {Region}; retrying title-field search",
                LogRedaction.SanitizeText(normalizedTitle),
                NormalizeRegion(region));

            var titleFieldResponse = await SearchProductsDirectAsync(
                query: null,
                title: normalizedTitle,
                author: null,
                narrator: null,
                publisher: null,
                page: page,
                limit: limit,
                region: region,
                language: language,
                sortBy: "Title");
            return ToSearchResponse(titleFieldResponse);
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
                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
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
            var authorLookupItems = await LookupAuthorItemsAsync(author, region, language);
            var authorAsin = authorLookupItems.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Asin))?.Asin;
            if (string.IsNullOrWhiteSpace(authorAsin))
            {
                _logger.LogWarning("No author ASIN found for author '{Author}'", LogRedaction.SanitizeText(author));
                return null;
            }

            return await GetBooksByResolvedAuthorAsync(author, authorAsin, page, limit, region, language);
        }

        public virtual async Task<AudibleSearchResponse?> GetBooksByAuthorAsync(string author, string authorAsin, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            if (string.IsNullOrWhiteSpace(authorAsin)) return null;
            return await GetBooksByResolvedAuthorAsync(author, authorAsin, page, limit, region, language);
        }

        public virtual async Task<AudibleSearchResponse?> GetAllBooksByAuthorAsync(string author, string authorAsin, int limit = 250, string region = "us", string? language = null)
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

        /// <summary>
        /// Lookup a single author by name using the Audible /author endpoint and return basic info (ASIN + image if available).
        /// </summary>
        public virtual async Task<AuthorLookupItem?> LookupAuthorAsync(string author, string region = "us")
        {
            if (string.IsNullOrWhiteSpace(author)) return null;

            try
            {
                var authorLookupItems = await LookupAuthorItemsAsync(author, region);
                var candidate = authorLookupItems.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Asin)) ?? authorLookupItems.FirstOrDefault();
                if (candidate == null)
                {
                    return null;
                }

                if (!string.IsNullOrWhiteSpace(candidate.Asin) &&
                    (string.IsNullOrWhiteSpace(candidate.Image) || string.IsNullOrWhiteSpace(candidate.Description)))
                {
                    var detailed = await GetAuthorByAsinAsync(candidate.Asin, region);
                    if (detailed != null)
                    {
                        return detailed;
                    }
                }

                return candidate;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to lookup author {Author}", LogRedaction.SanitizeText(author));
                return null;
            }
        }

        /// <summary>
        /// Lookup a single author by ASIN using the Audible /author/{asin} endpoint.
        /// </summary>
        public virtual async Task<AuthorLookupItem?> GetAuthorByAsinAsync(string authorAsin, string region = "us")
        {
            if (string.IsNullOrWhiteSpace(authorAsin)) return null;

            try
            {
                var locale = GetAudibleLocale(region);
                var url =
                    $"{BuildAudibleApiBaseUrl(region)}/1.0/catalog/contributors/{Uri.EscapeDataString(authorAsin)}" +
                    $"?locale={Uri.EscapeDataString(locale)}";
                using var doc = await GetAudibleJsonDocumentAsync(url, region, includeLocaleHeaders: true, timeoutSeconds: 10);
                if (doc == null ||
                    !doc.RootElement.TryGetProperty("contributor", out var contributor) ||
                    contributor.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                return new AuthorLookupItem
                {
                    Asin = GetString(contributor, "contributor_id") ?? authorAsin,
                    Name = GetString(contributor, "name"),
                    Image = GetString(contributor, "profile_image_url"),
                    Region = NormalizeRegion(region),
                    Description = GetString(contributor, "bio")
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to lookup Audible author details by ASIN {AuthorAsin}", LogRedaction.SanitizeText(authorAsin));
                return null;
            }
        }

        private async Task<List<AuthorLookupItem>> LookupAuthorItemsAsync(string author, string region = "us", string? language = null)
        {
            var response = await SearchProductsDirectAsync(
                query: null,
                title: null,
                author: author,
                narrator: null,
                publisher: null,
                page: 1,
                limit: 10,
                region: region,
                language: language,
                sortBy: "Relevance",
                returnRawProducts: true);

            if (response.RawProducts == null || response.RawProducts.Count == 0)
            {
                return new List<AuthorLookupItem>();
            }

            var normalizedAuthor = author.Trim();
            var compareInfo = CultureInfo.InvariantCulture.CompareInfo;
            const CompareOptions diacriticIgnore = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
            return response.RawProducts
                .SelectMany(product =>
                    GetArray(product, "authors")
                        .Select(authorItem => new AuthorLookupItem
                        {
                            Asin = GetString(authorItem, "asin"),
                            Name = GetString(authorItem, "name"),
                            Region = NormalizeRegion(region)
                        }))
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .Where(item =>
                    compareInfo.Compare(item.Name, normalizedAuthor, diacriticIgnore) == 0 ||
                    compareInfo.IndexOf(item.Name!, normalizedAuthor, diacriticIgnore) >= 0 ||
                    compareInfo.IndexOf(normalizedAuthor, item.Name!, diacriticIgnore) >= 0)
                .GroupBy(item => $"{item.Asin}|{item.Name}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        private async Task<List<SeriesLookupItem>> LookupSeriesItemsAsync(string seriesName, string region = "us")
        {
            var responses = new List<SearchProductsDirectResponse>();

            // First try title search — finds products whose title matches the series name
            responses.Add(await SearchProductsDirectAsync(
                query: null,
                title: seriesName,
                author: null,
                narrator: null,
                publisher: null,
                page: 1,
                limit: 25,
                region: region,
                language: null,
                sortBy: "Title",
                returnRawProducts: true));

            // Always also run a keyword query search — this finds products that *belong* to
            // the series even when no product title contains the series name (e.g. searching
            // "Fjällbacka Mysteries" finds "The Hidden Child" which has the series in its metadata)
            responses.Add(await SearchProductsDirectAsync(
                query: seriesName,
                title: null,
                author: null,
                narrator: null,
                publisher: null,
                page: 1,
                limit: 25,
                region: region,
                language: null,
                sortBy: "Relevance",
                returnRawProducts: true));

            _logger.LogInformation("LookupSeriesItemsAsync '{SeriesName}' region={Region}: title search returned {TitleCount} raw products, query search returned {QueryCount} raw products",
                LogRedaction.SanitizeText(seriesName), LogRedaction.SanitizeText(region),
                responses.ElementAtOrDefault(0)?.RawProducts?.Count ?? 0,
                responses.ElementAtOrDefault(1)?.RawProducts?.Count ?? 0);

            var normalizedSeries = seriesName.Trim();
            var compareInfo = CultureInfo.InvariantCulture.CompareInfo;
            const CompareOptions diacriticIgnore = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

            var allSeriesItems = responses
                .SelectMany(response => response.RawProducts ?? new List<JsonElement>())
                .SelectMany(product =>
                {
                    var productImage = GetHighestResolutionImage(product);
                    return GetArray(product, "series")
                        .Select(series => new SeriesLookupItem
                        {
                            Asin = GetString(series, "asin"),
                            Name = GetString(series, "title"),
                            Position = GetString(series, "sequence"),
                            Region = NormalizeRegion(region),
                            Image = productImage
                        });
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .ToList();

            _logger.LogInformation("LookupSeriesItemsAsync '{SeriesName}': extracted {Count} series items from raw products. Unique names: {Names}",
                LogRedaction.SanitizeText(seriesName), allSeriesItems.Count,
                string.Join(", ", allSeriesItems.Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(10)));

            var matched = allSeriesItems
                .Where(item =>
                    compareInfo.Compare(item.Name, normalizedSeries, diacriticIgnore) == 0 ||
                    compareInfo.IndexOf(item.Name!, normalizedSeries, diacriticIgnore) >= 0 ||
                    compareInfo.IndexOf(normalizedSeries, item.Name!, diacriticIgnore) >= 0)
                .GroupBy(item => $"{item.Asin}|{item.Name}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => compareInfo.Compare(item.Name, normalizedSeries, diacriticIgnore) == 0 ? 0 : 1)
                .ToList();

            _logger.LogInformation("LookupSeriesItemsAsync '{SeriesName}': {MatchCount} series items matched after name filter",
                LogRedaction.SanitizeText(seriesName), matched.Count);

            return matched;
        }

        private sealed class SearchProductsDirectResponse
        {
            public List<AudibleSearchResult> Results { get; set; } = new();
            public int TotalResults { get; set; }
            public List<JsonElement>? RawProducts { get; set; }
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
            var safeRegion = NormalizeRegion(region);

            // Try with original text first (preserves diacritics for APIs that
            // handle them natively, e.g. audible.de for German/Swedish).
            var result = await SearchProductsCoreAsync(
                query, title, author, narrator, publisher,
                page, limit, safeRegion, language, sortBy, returnRawProducts);

            // If no results and any parameter contained diacritics, retry with
            // diacritics stripped (helps US/UK APIs that don't match accented text).
            if (result.Results.Count == 0)
            {
                bool hasDiacritics =
                    HasDiacritics(query) || HasDiacritics(title) ||
                    HasDiacritics(author) || HasDiacritics(narrator) ||
                    HasDiacritics(publisher);

                if (hasDiacritics)
                {
                    _logger.LogInformation("Retrying Audible search with diacritics stripped (region={Region})", safeRegion);
                    result = await SearchProductsCoreAsync(
                        RemoveDiacritics(query ?? string.Empty),
                        RemoveDiacritics(title ?? string.Empty),
                        RemoveDiacritics(author ?? string.Empty),
                        RemoveDiacritics(narrator ?? string.Empty),
                        RemoveDiacritics(publisher ?? string.Empty),
                        page, limit, safeRegion, language, sortBy, returnRawProducts);
                }
            }

            return result;
        }

        private async Task<SearchProductsDirectResponse> SearchProductsCoreAsync(
            string? query, string? title, string? author,
            string? narrator, string? publisher,
            int page, int limit, string safeRegion,
            string? language, string sortBy, bool returnRawProducts)
        {
            var parameters = new Dictionary<string, string?>
            {
                ["num_results"] = Math.Clamp(limit, 1, 50).ToString(),
                ["page"] = Math.Max(0, page - 1).ToString(),
                ["products_sort_by"] = string.IsNullOrWhiteSpace(sortBy) ? "Relevance" : sortBy,
                ["response_groups"] = "media,contributors,series,product_attrs,product_desc,product_extended_attrs,category_ladders"
            };

            if (!string.IsNullOrWhiteSpace(query)) parameters["keywords"] = query;
            if (!string.IsNullOrWhiteSpace(title)) parameters["title"] = title;
            if (!string.IsNullOrWhiteSpace(author)) parameters["author"] = author;
            if (!string.IsNullOrWhiteSpace(narrator)) parameters["narrator"] = narrator;
            if (!string.IsNullOrWhiteSpace(publisher)) parameters["publisher"] = publisher;

            var url = $"{BuildAudibleApiBaseUrl(safeRegion)}/1.0/catalog/products/?{BuildQueryString(parameters)}";
            using var doc = await GetAudibleJsonDocumentAsync(url, safeRegion, includeLocaleHeaders: false, timeoutSeconds: 10);
            if (doc == null)
            {
                return new SearchProductsDirectResponse();
            }

            var root = doc.RootElement;
            var rawProducts = GetArray(root, "products")
                .Where(product => product.ValueKind == JsonValueKind.Object)
                .Select(product => product.Clone())
                .ToList();
            var results = rawProducts
                .Select(product => MapProductToBookResponse(product, safeRegion))
                .Where(product => product != null)
                .Select(product => MapBookResponseToSearchResult(product!))
                .Where(product => product != null)
                .Cast<AudibleSearchResult>()
                .Where(product => !SearchResultIndicatesPodcast(product))
                .ToList();

            results = ApplyLanguageFilter(results, language);

            return new SearchProductsDirectResponse
            {
                Results = results,
                TotalResults = root.TryGetProperty("total_results", out var totalResultsElement) && totalResultsElement.TryGetInt32(out var totalResults)
                    ? totalResults
                    : results.Count,
                RawProducts = returnRawProducts ? rawProducts : null
            };
        }

        /// <summary>
        /// Returns true if the string contains characters with diacritical marks
        /// that would be altered by <see cref="RemoveDiacritics"/>.
        /// </summary>
        private static bool HasDiacritics(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text != RemoveDiacritics(text);
        }

        private async Task<JsonDocument?> GetAudibleProductDocumentAsync(string asin, string region, string responseGroups)
        {
            var safeRegion = NormalizeRegion(region);
            var url =
                $"{BuildAudibleApiBaseUrl(safeRegion)}/1.0/catalog/products/{Uri.EscapeDataString(asin)}?" +
                $"{BuildQueryString(new Dictionary<string, string?>
                {
                    ["response_groups"] = responseGroups,
                    ["image_sizes"] = "500,1000,2400,3200"
                })}";

            return await GetAudibleJsonDocumentAsync(url, safeRegion, includeLocaleHeaders: false, timeoutSeconds: 10);
        }

        private async Task<List<AudibleBookResponse>> GetBooksMetadataByAsinsAsync(IEnumerable<string> asins, string region)
        {
            var normalizedRegion = NormalizeRegion(region);
            var orderedAsins = asins
                .Where(asin => !string.IsNullOrWhiteSpace(asin))
                .Select(asin => asin.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var results = new Dictionary<string, AudibleBookResponse>(StringComparer.OrdinalIgnoreCase);

            foreach (var chunk in Chunk(orderedAsins, 50))
            {
                var doc = chunk.Count == 1
                    ? await GetAudibleProductDocumentAsync(chunk[0], normalizedRegion, DefaultBookResponseGroups)
                    : await GetAudibleJsonDocumentAsync(
                        $"{BuildAudibleApiBaseUrl(normalizedRegion)}/1.0/catalog/products/?" +
                        $"{BuildQueryString(new Dictionary<string, string?>
                        {
                            ["asins"] = string.Join(",", chunk),
                            ["response_groups"] = DefaultBookResponseGroups,
                            ["image_sizes"] = "500,1000,2400,3200"
                        })}",
                        normalizedRegion,
                        includeLocaleHeaders: false,
                        timeoutSeconds: 15);

                if (doc == null)
                {
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var mapped in products.EnumerateArray()
                                     .Select(product => MapProductToBookResponse(product, normalizedRegion))
                                     .Where(mapped => !string.IsNullOrWhiteSpace(mapped?.Asin)))
                        {
                            results[mapped!.Asin!] = mapped;
                        }
                    }
                    else if (root.TryGetProperty("product", out var product) && product.ValueKind == JsonValueKind.Object)
                    {
                        var mapped = MapProductToBookResponse(product, normalizedRegion);
                        if (!string.IsNullOrWhiteSpace(mapped?.Asin))
                        {
                            results[mapped.Asin!] = mapped;
                        }
                    }
                }
            }

            return orderedAsins
                .Where(results.ContainsKey)
                .Select(asin => results[asin])
                .ToList();
        }

        private static AudibleBookResponse? MapProductToBookResponse(JsonElement product, string region)
        {
            if (product.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var asin = GetString(product, "asin");
            if (string.IsNullOrWhiteSpace(asin))
            {
                return null;
            }

            return new AudibleBookResponse
            {
                Asin = asin,
                Title = GetString(product, "title"),
                Subtitle = GetString(product, "subtitle"),
                Authors = GetArray(product, "authors")
                    .Select(author => new AudibleAuthor
                    {
                        Asin = GetString(author, "asin"),
                        Name = GetString(author, "name"),
                        Region = NormalizeRegion(region)
                    })
                    .Where(author => !string.IsNullOrWhiteSpace(author.Name))
                    .ToList(),
                Narrators = GetArray(product, "narrators")
                    .Select(narrator => new AudibleNarrator
                    {
                        Name = GetString(narrator, "name")
                    })
                    .Where(narrator => !string.IsNullOrWhiteSpace(narrator.Name))
                    .ToList(),
                Publisher = GetString(product, "publisher_name"),
                PublishDate = GetString(product, "publication_datetime"),
                Description = GetString(product, "publisher_summary")
                    ?? GetString(product, "merchandising_summary")
                    ?? GetString(product, "extended_product_description")
                    ?? GetString(product, "merchandising_description"),
                ImageUrl = GetHighestResolutionImage(product),
                LengthMinutes = GetInt32(product, "runtime_length_min"),
                Language = GetString(product, "language"),
                Genres = MapGenres(product),
                Series = GetArray(product, "series")
                    .Select(series => new AudibleSeries
                    {
                        Asin = GetString(series, "asin"),
                        Name = GetString(series, "title"),
                        Position = GetString(series, "sequence")
                    })
                    .Where(series => !string.IsNullOrWhiteSpace(series.Name))
                    .ToList(),
                Explicit = GetBoolean(product, "is_adult_product"),
                ReleaseDate = GetString(product, "release_date"),
                Isbn = GetString(product, "isbn"),
                Region = NormalizeRegion(region),
                BookFormat = GetString(product, "format_type"),
                ContentType = GetString(product, "content_type"),
                ContentDeliveryType = GetString(product, "content_delivery_type"),
                EpisodeType = GetString(product, "episode_type"),
                Sku = GetString(product, "sku")
            };
        }

        private static AudibleSearchResult? MapBookResponseToSearchResult(AudibleBookResponse book)
        {
            if (string.IsNullOrWhiteSpace(book.Asin))
            {
                return null;
            }

            return new AudibleSearchResult
            {
                Asin = book.Asin,
                Title = book.Title,
                Subtitle = book.Subtitle,
                Authors = book.Authors,
                ImageUrl = book.ImageUrl,
                RuntimeLengthMin = book.LengthMinutes,
                LengthMinutes = book.LengthMinutes,
                RuntimeMinutes = book.LengthMinutes,
                Language = book.Language,
                ContentType = book.ContentType,
                ContentDeliveryType = book.ContentDeliveryType,
                EpisodeType = book.EpisodeType,
                Sku = book.Sku,
                BookFormat = book.BookFormat,
                Genres = book.Genres,
                Series = book.Series,
                Publisher = book.Publisher,
                Narrators = book.Narrators,
                ReleaseDate = book.ReleaseDate,
                Link = string.IsNullOrWhiteSpace(book.Asin) ? null : $"{GetAudibleBaseUrl(book.Region ?? "us")}/pd/{book.Asin}",
                Isbn = book.Isbn
            };
        }

        private static List<AudibleSearchResult> ApplyLanguageFilter(List<AudibleSearchResult> results, string? language)
        {
            if (string.IsNullOrWhiteSpace(language) ||
                string.Equals(language, "all", StringComparison.OrdinalIgnoreCase))
            {
                return results;
            }

            return results
                .Where(result => string.IsNullOrWhiteSpace(result.Language) ||
                                 string.Equals(result.Language, language, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private static AudibleSearchResponse ToSearchResponse(SearchProductsDirectResponse response)
        {
            return new AudibleSearchResponse
            {
                Results = response.Results,
                TotalResults = response.TotalResults
            };
        }

        private async Task<JsonDocument?> GetAudibleJsonDocumentAsync(
            string url,
            string region,
            bool includeLocaleHeaders,
            int timeoutSeconds)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", includeLocaleHeaders ? AudibleApiVerboseUserAgent : AudibleApiUserAgent);
                request.Headers.TryAddWithoutValidation("Accept", AudibleApiAcceptHeader);
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
                request.Headers.TryAddWithoutValidation("Accept-Charset", "utf-8");
                if (includeLocaleHeaders)
                {
                    var locale = GetAudibleLocale(region);
                    request.Headers.TryAddWithoutValidation("ACCEPTED-LANGUAGE", locale);
                    request.Headers.TryAddWithoutValidation("accept-language", locale);
                    request.Headers.TryAddWithoutValidation("X-ADP-SW", Random.Shared.Next(10_000_000, 99_999_999).ToString());
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                var response = await _httpClient.SendAsync(request, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Audible API returned status code {StatusCode} for URL {Url}", response.StatusCode, url);
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "Audible API request timed out for URL: {Url}", url);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error performing Audible API request for URL: {Url}", url);
                return null;
            }
        }

        private static string BuildAudibleApiBaseUrl(string region)
        {
            var normalizedRegion = NormalizeRegion(region);
            return $"https://{(AudibleApiDomainMap.TryGetValue(normalizedRegion, out var domain) ? domain : AudibleApiDomainMap["us"])}";
        }

        private static string GetAudibleLocale(string region)
        {
            var normalizedRegion = NormalizeRegion(region);
            return AudibleLocaleMap.TryGetValue(normalizedRegion, out var locale)
                ? locale
                : AudibleLocaleMap["us"];
        }

        private static string NormalizeRegion(string region)
        {
            return string.IsNullOrWhiteSpace(region) ? "us" : region.Trim().ToLowerInvariant();
        }

        private static string BuildQueryString(IEnumerable<KeyValuePair<string, string?>> parameters)
        {
            return string.Join(
                "&",
                parameters
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                    .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}"));
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
            if (string.IsNullOrEmpty(text)) return text;
            var normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (var ch in normalized.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark))
                sb.Append(ch);
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private static IEnumerable<JsonElement> GetArray(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray()
                : Enumerable.Empty<JsonElement>();
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

        private static int? GetInt32(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
        }

        private static bool? GetBoolean(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
                _ => null
            };
        }

        private static string? GetHighestResolutionImage(JsonElement product)
        {
            if (product.TryGetProperty("product_images", out var images) && images.ValueKind == JsonValueKind.Object)
            {
                var bestKey = images.EnumerateObject()
                    .Select(property => new { property.Name, Numeric = int.TryParse(property.Name, out var size) ? size : 0 })
                    .OrderByDescending(property => property.Numeric)
                    .FirstOrDefault();
                if (bestKey != null && images.TryGetProperty(bestKey.Name, out var imageValue))
                {
                    return imageValue.GetString();
                }
            }

            return GetString(product, "cover_art_url");
        }

        private static List<AudibleGenre> MapGenres(JsonElement product)
        {
            var genres = new List<AudibleGenre>();
            foreach (var ladderEntry in GetArray(product, "category_ladders"))
            {
                if (!ladderEntry.TryGetProperty("ladder", out var ladder) || ladder.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var index = 0;
                foreach (var genre in ladder.EnumerateArray())
                {
                    var name = GetString(genre, "name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        index++;
                        continue;
                    }

                    genres.Add(new AudibleGenre
                    {
                        Asin = GetString(genre, "id"),
                        Name = name,
                        Type = index == 0 ? "Genres" : "Tags"
                    });
                    index++;
                }
            }

            return genres
                .GroupBy(genre => $"{genre.Asin}|{genre.Name}|{genre.Type}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        private static decimal ParseSeriesPosition(string? rawPosition)
        {
            return decimal.TryParse(rawPosition, out var parsed) ? parsed : decimal.MaxValue;
        }

        private static List<List<string>> Chunk(List<string> values, int size)
        {
            var chunks = new List<List<string>>();
            for (var i = 0; i < values.Count; i += size)
            {
                chunks.Add(values.Skip(i).Take(size).ToList());
            }

            return chunks;
        }

        private static string GenerateRandomSessionId()
        {
            static string RandomDigits()
            {
                return Random.Shared.Next(0, 10_000_000).ToString().PadLeft(7, '0');
            }

            return $"000-{RandomDigits()}-{RandomDigits()}";
        }

        private static AuthorLookupItem? ParseSingleAuthorLookupItem(string lookupJson)
        {
            var items = ParseAuthorLookupItems(lookupJson);
            return items.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Asin)) ?? items.FirstOrDefault();
        }

        private static SeriesLookupItem? ParseSeriesLookupItem(string lookupJson)
        {
            var items = ParseSeriesLookupItems(lookupJson);
            return items.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Asin)) ?? items.FirstOrDefault();
        }

        private async Task<AudibleSearchResponse?> GetBooksByResolvedAuthorAsync(string author, string authorAsin, int page = 1, int limit = 50, string region = "us", string? language = null)
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
                var response = await SearchProductsDirectAsync(
                    query: null,
                    title: null,
                    author: normalizedAuthor,
                    narrator: null,
                    publisher: null,
                    page: currentPage,
                    limit: 50,
                    region: region,
                    language: null,
                    sortBy: "BestSellers");

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
                    if (!AuthorSearchResultMatchesTarget(result, normalizedAuthor, authorAsin))
                    {
                        continue;
                    }

                    var key = BuildSearchResultKey(result);
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

            return ApplyLanguageFilter(results, language);
        }

        private static bool AuthorSearchResultMatchesTarget(AudibleSearchResult result, string author, string? authorAsin)
        {
            if (result.Authors == null || result.Authors.Count == 0)
            {
                return false;
            }

            var normalizedTargetName = NormalizeComparableText(author);
            if (string.IsNullOrWhiteSpace(normalizedTargetName))
            {
                return false;
            }

            foreach (var candidate in result.Authors)
            {
                if (!string.IsNullOrWhiteSpace(authorAsin) &&
                    string.Equals(candidate.Asin, authorAsin, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(NormalizeComparableText(candidate.Name), normalizedTargetName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string BuildSearchResultKey(AudibleSearchResult result)
        {
            return string.IsNullOrWhiteSpace(result.Asin)
                ? $"{result.Title}|{result.Link}"
                : result.Asin;
        }

        private static string NormalizeComparableText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var joined = string.Join(
                ' ',
                value.Trim()
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .ToLowerInvariant();
            return RemoveDiacritics(joined);
        }

        private async Task<AudibleSearchResponse?> ScrapeAudibleAuthorPageAsync(string author, string authorAsin, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            try
            {
                var authorPageUrl = BuildAudibleAuthorPageUrl(author, authorAsin, region);
                _logger.LogInformation("Scraping Audible author page as fallback: {Url}", authorPageUrl);

                var response = await GetWithTimeoutAsync(authorPageUrl, timeoutSeconds: 10);
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

                var htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(html);

                var tiles = htmlDoc.DocumentNode.SelectNodes("//adbl-full-width-product-tile");
                var legacyProductListItems = htmlDoc.DocumentNode.SelectNodes("//li[contains(@class, 'productListItem')]");
                if ((tiles == null || tiles.Count == 0) &&
                    (legacyProductListItems == null || legacyProductListItems.Count == 0))
                {
                    _logger.LogWarning("Audible author page contained no recognizable product tiles for author {Author}", LogRedaction.SanitizeText(author));
                    return null;
                }

                var parsedTiles = new List<AudibleSearchResult>();
                var seenAsins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (tiles != null)
                {
                    foreach (var tile in tiles)
                    {
                        var parsed = ParseAudibleAuthorTile(tile, author, authorAsin, region);
                        if (parsed == null) continue;

                        var key = string.IsNullOrWhiteSpace(parsed.Asin)
                            ? $"{parsed.Title}|{parsed.Link}"
                            : parsed.Asin;
                        if (seenAsins.Add(key))
                        {
                            parsedTiles.Add(parsed);
                        }
                    }
                }

                if (legacyProductListItems != null)
                {
                    foreach (var item in legacyProductListItems)
                    {
                        var parsed = ParseAudibleAuthorListItem(item, author, authorAsin, region);
                        if (parsed == null) continue;

                        var key = string.IsNullOrWhiteSpace(parsed.Asin)
                            ? $"{parsed.Title}|{parsed.Link}"
                            : parsed.Asin;
                        if (seenAsins.Add(key))
                        {
                            parsedTiles.Add(parsed);
                        }
                    }
                }

                if (parsedTiles.Count == 0)
                {
                    _logger.LogWarning("Audible author page tiles could not be parsed for author {Author}", LogRedaction.SanitizeText(author));
                    return null;
                }

                await EnrichFallbackAuthorResultsAsync(parsedTiles, region);

                var authorMatchedTiles = parsedTiles
                    .Where(r => r.Authors?.Any(a => string.Equals(a.Name, author, StringComparison.OrdinalIgnoreCase)) == true)
                    .ToList();
                var filteredTiles = authorMatchedTiles.Count > 0 ? authorMatchedTiles : parsedTiles;

                if (!string.IsNullOrWhiteSpace(language))
                {
                    filteredTiles = filteredTiles
                        .Where(r => !string.IsNullOrWhiteSpace(r.Language) && string.Equals(r.Language, language, StringComparison.OrdinalIgnoreCase))
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
                    var metadata = await GetBookMetadataAsync(book.Asin, region, true, language: null);
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

        private static List<AuthorLookupItem> ParseAuthorLookupItems(string lookupJson)
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            if (string.IsNullOrWhiteSpace(lookupJson)) return new List<AuthorLookupItem>();

            var trimmed = lookupJson.TrimStart();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                return JsonSerializer.Deserialize<List<AuthorLookupItem>>(lookupJson, opts) ?? new List<AuthorLookupItem>();
            }

            var single = JsonSerializer.Deserialize<AuthorLookupItem>(lookupJson, opts);
            if (single != null && (!string.IsNullOrWhiteSpace(single.Asin) || !string.IsNullOrWhiteSpace(single.Name)))
            {
                return new List<AuthorLookupItem> { single };
            }

            var doc = JsonSerializer.Deserialize<AuthorLookupEnvelope>(lookupJson, opts);
            if (doc == null) return new List<AuthorLookupItem>();
            if (doc.Results?.Any() == true) return doc.Results;
            if (!string.IsNullOrWhiteSpace(doc.Asin))
            {
                return new List<AuthorLookupItem>
                {
                    new AuthorLookupItem
                    {
                        Asin = doc.Asin,
                        Name = doc.Name,
                        Image = doc.Image,
                        Region = doc.Region,
                        Description = doc.Description
                    }
                };
            }

            return new List<AuthorLookupItem>();
        }

        private static List<SeriesLookupItem> ParseSeriesLookupItems(string lookupJson)
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            if (string.IsNullOrWhiteSpace(lookupJson)) return new List<SeriesLookupItem>();

            var trimmed = lookupJson.TrimStart();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                return JsonSerializer.Deserialize<List<SeriesLookupItem>>(lookupJson, opts) ?? new List<SeriesLookupItem>();
            }

            var single = JsonSerializer.Deserialize<SeriesLookupItem>(lookupJson, opts);
            if (single != null && (!string.IsNullOrWhiteSpace(single.Asin) || !string.IsNullOrWhiteSpace(single.Name)))
            {
                return new List<SeriesLookupItem> { single };
            }

            var doc = JsonSerializer.Deserialize<SeriesLookupEnvelope>(lookupJson, opts);
            if (doc == null) return new List<SeriesLookupItem>();
            if (doc.Results?.Any() == true) return doc.Results;
            if (!string.IsNullOrWhiteSpace(doc.Asin))
            {
                return new List<SeriesLookupItem>
                {
                    new SeriesLookupItem
                    {
                        Asin = doc.Asin,
                        Name = doc.Name,
                        Region = doc.Region,
                        Description = doc.Description,
                        Position = doc.Position
                    }
                };
            }

            return new List<SeriesLookupItem>();
        }

        private static AudibleSearchResult? ParseAudibleAuthorTile(HtmlNode tile, string author, string authorAsin, string region)
        {
            var productImageNode = tile.SelectSingleNode(".//adbl-product-image")
                ?? tile.SelectSingleNode(".//adbl-full-bleed-image");
            var asin = productImageNode?.GetAttributeValue("data-asin", string.Empty);
            if (string.IsNullOrWhiteSpace(asin))
            {
                asin = tile.SelectSingleNode(".//*[@data-asin]")?.GetAttributeValue("data-asin", string.Empty);
            }
            if (string.IsNullOrWhiteSpace(asin)) return null;

            var title = HtmlEntity.DeEntitize(tile.SelectSingleNode(".//*[@slot='title']")?.InnerText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(title)) return null;

            var subtitle = HtmlEntity.DeEntitize(tile.SelectSingleNode(".//*[@slot='subtitle']")?.InnerText ?? string.Empty).Trim();
            var imageUrl = productImageNode?.SelectSingleNode(".//img")?.GetAttributeValue("src", string.Empty);
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                imageUrl = productImageNode?.GetAttributeValue("portrait-src", string.Empty);
            }
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                imageUrl = productImageNode?.GetAttributeValue("landscape-src", string.Empty);
            }
            var relativeUrl = productImageNode?.GetAttributeValue("data-url", string.Empty);
            if (string.IsNullOrWhiteSpace(relativeUrl))
            {
                relativeUrl = tile.SelectSingleNode(".//adbl-button[@href]")?.GetAttributeValue("href", string.Empty)
                    ?? tile.SelectSingleNode(".//a[@href]")?.GetAttributeValue("href", string.Empty);
            }

            var authors = ParseAudibleAuthorTileAuthors(tile, author, authorAsin, region);
            if (authors.Count == 0 && !string.IsNullOrWhiteSpace(author))
            {
                authors.Add(new AudibleAuthor { Asin = authorAsin, Name = author, Region = region });
            }

            return new AudibleSearchResult
            {
                Asin = asin,
                Title = title,
                Subtitle = string.IsNullOrWhiteSpace(subtitle) ? null : subtitle,
                Authors = authors,
                ImageUrl = string.IsNullOrWhiteSpace(imageUrl) ? null : imageUrl,
                Link = NormalizeAudibleUrl(relativeUrl, region)
            };
        }

        private static AudibleSearchResult? ParseAudibleAuthorListItem(HtmlNode listItem, string author, string authorAsin, string region)
        {
            var asin = listItem.SelectSingleNode(".//*[@data-asin]")?.GetAttributeValue("data-asin", string.Empty);
            if (string.IsNullOrWhiteSpace(asin))
            {
                return null;
            }

            var title = HtmlEntity.DeEntitize(listItem.GetAttributeValue("aria-label", string.Empty)).Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                title = HtmlEntity.DeEntitize(
                    listItem.SelectSingleNode(".//h2")?.InnerText ?? string.Empty).Trim();
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var imageUrl = listItem.SelectSingleNode(".//img[@src]")?.GetAttributeValue("src", string.Empty);
            var relativeUrl = listItem.SelectSingleNode(".//a[@href]")?.GetAttributeValue("href", string.Empty);

            return new AudibleSearchResult
            {
                Asin = asin,
                Title = title,
                Authors = new List<AudibleAuthor>
                {
                    new()
                    {
                        Asin = authorAsin,
                        Name = author,
                        Region = region
                    }
                },
                ImageUrl = string.IsNullOrWhiteSpace(imageUrl) ? null : imageUrl,
                Link = NormalizeAudibleUrl(relativeUrl, region)
            };
        }

        private static List<AudibleAuthor> ParseAudibleAuthorTileAuthors(HtmlNode tile, string author, string authorAsin, string region)
        {
            var authors = new List<AudibleAuthor>();
            var metadataJson = tile.SelectSingleNode(".//adbl-product-metadata/script[@type='application/json']")?.InnerText;
            if (string.IsNullOrWhiteSpace(metadataJson)) return authors;

            try
            {
                var metadata = JsonSerializer.Deserialize<AudibleAuthorTileMetadata>(metadataJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (metadata?.Authors == null) return authors;

                foreach (var metadataAuthor in metadata.Authors.Where(metadataAuthor => !string.IsNullOrWhiteSpace(metadataAuthor.Name)))
                {
                    authors.Add(new AudibleAuthor
                    {
                        Asin = string.Equals(metadataAuthor.Name, author, StringComparison.OrdinalIgnoreCase) ? authorAsin : null,
                        Name = metadataAuthor.Name,
                        Region = region
                    });
                }
            }
            catch (JsonException)
            {
                // Ignore malformed metadata blobs and fall back to the requested author name.
            }

            return authors;
        }

        private static string BuildAudibleAuthorPageUrl(string author, string authorAsin, string region)
        {
            var authorSlug = string.IsNullOrWhiteSpace(author)
                ? authorAsin
                : Uri.EscapeDataString(author.Trim().Replace(' ', '-'));
            return $"{GetAudibleBaseUrl(region)}/author/{authorSlug}/{Uri.EscapeDataString(authorAsin)}";
        }

        private static string? NormalizeAudibleUrl(string? url, string region)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (Uri.TryCreate(url, UriKind.Absolute, out var absoluteUri)
                && !string.Equals(absoluteUri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            {
                return absoluteUri.ToString();
            }
            return $"{GetAudibleBaseUrl(region)}{url}";
        }

        private static string GetAudibleBaseUrl(string region)
        {
            return region?.Trim().ToLowerInvariant() switch
            {
                "au" => "https://www.audible.com.au",
                "ca" => "https://www.audible.ca",
                "de" => "https://www.audible.de",
                "es" => "https://www.audible.es",
                "fr" => "https://www.audible.fr",
                "in" => "https://www.audible.in",
                "it" => "https://www.audible.it",
                "jp" => "https://www.audible.co.jp",
                "uk" => "https://www.audible.co.uk",
                _ => "https://www.audible.com"
            };
        }

        public virtual async Task<AudibleSearchResponse?> SearchByIsbnAsync(string isbn, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            var response = await SearchProductsDirectAsync(
                query: isbn,
                title: null,
                author: null,
                narrator: null,
                publisher: null,
                page: page,
                limit: limit,
                region: region,
                language: language,
                sortBy: "BestSellers");
            var filtered = response.Results
                .Where(result => string.Equals(result.Isbn?.Trim(), isbn.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            return new AudibleSearchResponse
            {
                Results = filtered,
                TotalResults = filtered.Count
            };
        }

        public virtual async Task<AudibleSearchResponse?> SearchBooksAsync(string query, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            // If query looks like an ASIN, perform a direct metadata lookup which returns a single result
            bool IsAsin(string s)
            {
                if (string.IsNullOrEmpty(s)) return false;
                if (s.Length != 10) return false;
                if (!(s.StartsWith("B0", StringComparison.OrdinalIgnoreCase) || char.IsDigit(s[0]))) return false;
                return s.All(char.IsLetterOrDigit);
            }

            if (IsAsin(query?.Trim() ?? string.Empty))
            {
                var asin = query?.Trim() ?? string.Empty;
                _logger.LogInformation("Query appears to be an ASIN; performing direct Audible book lookup for {Asin}", LogRedaction.SanitizeText(asin));
                var meta = await GetBookMetadataAsync(asin, region, true, language);
                if (meta == null) return null;

                // Convert AudibleBookResponse to AudibleSearchResult for compatibility with callers
                var single = new AudibleSearchResult
                {
                    Asin = meta.Asin,
                    Title = meta.Title,
                    Subtitle = meta.Subtitle,
                    Authors = meta.Authors,
                    ImageUrl = meta.ImageUrl,
                    LengthMinutes = meta.LengthMinutes,
                    Language = meta.Language,
                    ContentType = meta.ContentType,
                    ContentDeliveryType = meta.ContentDeliveryType,
                    BookFormat = meta.BookFormat,
                    Genres = meta.Genres,
                    Series = meta.Series,
                    Publisher = meta.Publisher,
                    Narrators = meta.Narrators,
                    ReleaseDate = meta.ReleaseDate,
                    Link = $"https://www.amazon.com/dp/{meta.Asin}"
                };

                return new AudibleSearchResponse { Results = new List<AudibleSearchResult> { single }, TotalResults = 1 };
            }

            var response = await SearchProductsDirectAsync(
                query: query,
                title: null,
                author: null,
                narrator: null,
                publisher: null,
                page: page,
                limit: limit,
                region: region,
                language: language,
                sortBy: "Relevance");
            return ToSearchResponse(response);
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
                                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
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
                                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
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
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                var resp = await _httpClient.GetAsync(url, cts.Token);
                return resp;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "Audible request timed out for URL: {Url}", url);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error performing Audible HTTP request for URL: {Url}", url);
                return null;
            }
        }

        private static bool SearchResultIndicatesPodcast(AudibleSearchResult? r)
        {
            if (r == null) return false;
            // If result explicitly indicates it's a book/product by content type or delivery type,
            // prefer that signal and do not treat it as a podcast even if other fields mention 'podcast'.
            var ct = r.ContentType?.Trim();
            var cdt = r.ContentDeliveryType?.Trim();
            var ctIsBookOrProduct = !string.IsNullOrWhiteSpace(ct) && (string.Equals(ct, "Book", StringComparison.OrdinalIgnoreCase) || string.Equals(ct, "Product", StringComparison.OrdinalIgnoreCase));
            var allowedBookDelivery = new[] { "SinglePartBook", "MultiPartBook", "BookSeries" };
            var cdtIsBook = !string.IsNullOrWhiteSpace(cdt) && allowedBookDelivery.Any(a => string.Equals(a, cdt, StringComparison.OrdinalIgnoreCase));
            if (ctIsBookOrProduct || cdtIsBook) return false;

            if (!string.IsNullOrWhiteSpace(r.ContentType) && r.ContentType.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!string.IsNullOrWhiteSpace(r.ContentDeliveryType) && r.ContentDeliveryType.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!string.IsNullOrWhiteSpace(r.EpisodeType)) return true;
            if (!string.IsNullOrWhiteSpace(r.Sku) && r.Sku.StartsWith("PC_", StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrWhiteSpace(r.BookFormat) && r.BookFormat.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (r.Genres?.Any(g => (!string.IsNullOrWhiteSpace(g?.Name) && g.Name.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) || (!string.IsNullOrWhiteSpace(g?.Type) && g.Type.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0)) == true) return true;
            return false;
        }

        private static string? GetPodcastFilterReason(AudibleSearchResult? r)
        {
            if (r == null) return null;
            if (!string.IsNullOrWhiteSpace(r.ContentType) && r.ContentType.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) return "ContentType contains 'podcast'";
            if (!string.IsNullOrWhiteSpace(r.ContentDeliveryType) && r.ContentDeliveryType.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) return "ContentDeliveryType contains 'podcast'";
            if (!string.IsNullOrWhiteSpace(r.EpisodeType)) return "EpisodeType present";
            if (!string.IsNullOrWhiteSpace(r.Sku) && r.Sku.StartsWith("PC_", StringComparison.OrdinalIgnoreCase)) return "SKU starts with PC_";
            if (!string.IsNullOrWhiteSpace(r.BookFormat) && r.BookFormat.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) return "BookFormat contains 'podcast'";
            if (r.Genres?.Any(g => (!string.IsNullOrWhiteSpace(g?.Name) && g.Name.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) || (!string.IsNullOrWhiteSpace(g?.Type) && g.Type.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0)) == true) return "Genre contains 'podcast'";
            return null;
        }

        private static bool IsAllowedContentTypeOrDelivery(AudibleSearchResult? r)
        {
            if (r == null) return false;
            // Require BOTH: ContentType must be Book|Product AND ContentDeliveryType must be one of allowed book delivery types.
            var ct = r.ContentType?.Trim();
            var cdt = r.ContentDeliveryType?.Trim();

            var ctOk = !string.IsNullOrWhiteSpace(ct) && (string.Equals(ct, "Book", StringComparison.OrdinalIgnoreCase) || string.Equals(ct, "Product", StringComparison.OrdinalIgnoreCase));

            var allowed = new[] { "SinglePartBook", "MultiPartBook", "BookSeries" };
            var cdtOk = !string.IsNullOrWhiteSpace(cdt) && allowed.Any(a => string.Equals(a, cdt, StringComparison.OrdinalIgnoreCase));

            return ctOk && cdtOk;
        }

        private static string? GetTypeFilterReason(AudibleSearchResult? r)
        {
            if (r == null) return null;
            var ct = r.ContentType?.Trim();
            var cdt = r.ContentDeliveryType?.Trim();

            var ctOk = !string.IsNullOrWhiteSpace(ct) && (string.Equals(ct, "Book", StringComparison.OrdinalIgnoreCase) || string.Equals(ct, "Product", StringComparison.OrdinalIgnoreCase));
            var allowed = new[] { "SinglePartBook", "MultiPartBook", "BookSeries" };
            var cdtOk = !string.IsNullOrWhiteSpace(cdt) && allowed.Any(a => string.Equals(a, cdt, StringComparison.OrdinalIgnoreCase));

            if (ctOk && cdtOk) return $"ContentType='{ct}' AND ContentDeliveryType='{cdt}'";
            if (!ctOk && !cdtOk) return "ContentType not allowed; ContentDeliveryType not allowed";
            if (!ctOk) return $"ContentType='{ct ?? "<null>"}' not allowed";
            return $"ContentDeliveryType='{cdt ?? "<null>"}' not allowed";
        }
    }
}
