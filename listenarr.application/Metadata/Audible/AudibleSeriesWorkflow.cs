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
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Metadata.Audible
{
    internal sealed class AudibleSeriesWorkflow
    {
        private const string DefaultSeriesResponseGroups =
            "relationships,product_attrs,product_desc,product_extended_attrs";

        private readonly AudibleApiClient _apiClient;
        private readonly AudibleProductMetadataWorkflow _metadataWorkflow;
        private readonly AudibleProductSearchWorkflow _productSearchWorkflow;
        private readonly ILogger _logger;

        public AudibleSeriesWorkflow(
            AudibleApiClient apiClient,
            AudibleProductMetadataWorkflow metadataWorkflow,
            AudibleProductSearchWorkflow productSearchWorkflow,
            ILogger logger)
        {
            _apiClient = apiClient;
            _metadataWorkflow = metadataWorkflow;
            _productSearchWorkflow = productSearchWorkflow;
            _logger = logger;
        }

        public async Task<object?> SearchSeriesByNameAsync(string name, string region)
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

        public async Task<SeriesLookupItem?> LookupSeriesAsync(string seriesName, string region)
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

        public async Task<SeriesLookupItem?> GetSeriesByAsinAsync(string seriesAsin, string region)
        {
            if (string.IsNullOrWhiteSpace(seriesAsin))
            {
                return null;
            }

            try
            {
                using var doc = await _apiClient.GetProductDocumentAsync(seriesAsin, region, DefaultSeriesResponseGroups);
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
                    Region = AudibleRequestHelper.NormalizeRegion(region),
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

        public async Task<object?> GetBooksBySeriesAsinAsync(string seriesAsin, string region)
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

        public async Task<List<AudibleSearchResult>?> GetTypedBooksBySeriesAsinAsync(string seriesAsin, string region)
        {
            if (string.IsNullOrWhiteSpace(seriesAsin))
            {
                return null;
            }

            try
            {
                using var doc = await _apiClient.GetProductDocumentAsync(seriesAsin, region, DefaultSeriesResponseGroups);
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

                var books = await _metadataWorkflow.GetBooksMetadataByAsinsAsync(
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

                    var mapped = AudibleProductMapper.MapBookResponseToSearchResult(book);
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

        public async Task<List<SeriesLookupItem>> LookupSeriesItemsAsync(string seriesName, string region)
        {
            var responses = new List<SearchProductsDirectResponse>();

            responses.Add(await _productSearchWorkflow.SearchProductsDirectAsync(
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

            responses.Add(await _productSearchWorkflow.SearchProductsDirectAsync(
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
                            Region = AudibleRequestHelper.NormalizeRegion(region),
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

        private static decimal ParseSeriesPosition(string? rawPosition)
        {
            return decimal.TryParse(rawPosition, out var parsed) ? parsed : decimal.MaxValue;
        }
    }
}
