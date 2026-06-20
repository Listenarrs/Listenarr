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

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Metadata.Audible
{
    internal sealed class AudibleProductSearchWorkflow
    {
        private readonly AudibleApiClient _apiClient;
        private readonly Func<string, string, bool, string?, Task<AudibleBookResponse?>> _getBookMetadataAsync;
        private readonly ILogger _logger;

        public AudibleProductSearchWorkflow(
            AudibleApiClient apiClient,
            Func<string, string, bool, string?, Task<AudibleBookResponse?>> getBookMetadataAsync,
            ILogger logger)
        {
            _apiClient = apiClient;
            _getBookMetadataAsync = getBookMetadataAsync;
            _logger = logger;
        }

        public async Task<AudibleSearchResponse?> SearchByTitleAsync(string title, int page, int limit, string region, string? language)
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
                AudibleRequestHelper.NormalizeRegion(region));

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

        public async Task<AudibleSearchResponse?> SearchByIsbnAsync(string isbn, int page, int limit, string region, string? language)
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

        public async Task<AudibleSearchResponse?> SearchBooksAsync(string query, int page, int limit, string region, string? language)
        {
            if (IsAsin(query?.Trim() ?? string.Empty))
            {
                var asin = query?.Trim() ?? string.Empty;
                _logger.LogInformation("Query appears to be an ASIN; performing direct Audible book lookup for {Asin}", LogRedaction.SanitizeText(asin));
                var meta = await _getBookMetadataAsync(asin, region, true, language);
                if (meta == null) return null;

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

        public async Task<SearchProductsDirectResponse> SearchProductsDirectAsync(
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
            var safeRegion = AudibleRequestHelper.NormalizeRegion(region);

            var result = await SearchProductsCoreAsync(
                query, title, author, narrator, publisher,
                page, limit, safeRegion, language, sortBy, returnRawProducts);

            if (result.Results.Count == 0)
            {
                var hasDiacritics =
                    HasDiacritics(query) || HasDiacritics(title) ||
                    HasDiacritics(author) || HasDiacritics(narrator) ||
                    HasDiacritics(publisher);

                if (hasDiacritics)
                {
                    _logger.LogInformation("Retrying Audible search with diacritics stripped (region={Region})", safeRegion);
                    result = await SearchProductsCoreAsync(
                        AudibleRequestHelper.RemoveDiacritics(query ?? string.Empty),
                        AudibleRequestHelper.RemoveDiacritics(title ?? string.Empty),
                        AudibleRequestHelper.RemoveDiacritics(author ?? string.Empty),
                        AudibleRequestHelper.RemoveDiacritics(narrator ?? string.Empty),
                        AudibleRequestHelper.RemoveDiacritics(publisher ?? string.Empty),
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

            var url = $"{AudibleRequestHelper.BuildApiBaseUrl(safeRegion)}/1.0/catalog/products/?{AudibleRequestHelper.BuildQueryString(parameters)}";
            using var doc = await _apiClient.GetJsonDocumentAsync(url, safeRegion, includeLocaleHeaders: false, timeoutSeconds: 10);
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
                .Select(product => AudibleProductMapper.MapProductToBookResponse(product, safeRegion))
                .Where(product => product != null)
                .Select(product => AudibleProductMapper.MapBookResponseToSearchResult(product!))
                .Where(product => product != null)
                .Cast<AudibleSearchResult>()
                .Where(product => !AudibleSearchResultFilter.IndicatesPodcast(product))
                .ToList();

            results = AudibleProductMapper.ApplyLanguageFilter(results, language);

            return new SearchProductsDirectResponse
            {
                Results = results,
                TotalResults = root.TryGetProperty("total_results", out var totalResultsElement) && totalResultsElement.TryGetInt32(out var totalResults)
                    ? totalResults
                    : results.Count,
                RawProducts = returnRawProducts ? rawProducts : null
            };
        }

        private static AudibleSearchResponse ToSearchResponse(SearchProductsDirectResponse response)
        {
            return new AudibleSearchResponse
            {
                Results = response.Results,
                TotalResults = response.TotalResults
            };
        }

        private static bool HasDiacritics(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text != AudibleRequestHelper.RemoveDiacritics(text);
        }

        private static bool IsAsin(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (value.Length != 10) return false;
            if (!(value.StartsWith("B0", StringComparison.OrdinalIgnoreCase) || char.IsDigit(value[0]))) return false;
            return value.All(char.IsLetterOrDigit);
        }

        private static IEnumerable<JsonElement> GetArray(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray()
                : Enumerable.Empty<JsonElement>();
        }
    }
}
