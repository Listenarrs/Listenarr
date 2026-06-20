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
    internal sealed class AudibleAuthorLookupWorkflow
    {
        private readonly AudibleApiClient _apiClient;
        private readonly AudibleProductSearchWorkflow _productSearchWorkflow;
        private readonly ILogger _logger;

        public AudibleAuthorLookupWorkflow(
            AudibleApiClient apiClient,
            AudibleProductSearchWorkflow productSearchWorkflow,
            ILogger logger)
        {
            _apiClient = apiClient;
            _productSearchWorkflow = productSearchWorkflow;
            _logger = logger;
        }

        public async Task<AuthorLookupItem?> LookupAuthorAsync(string author, string region)
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

        public async Task<AuthorLookupItem?> GetAuthorByAsinAsync(string authorAsin, string region)
        {
            if (string.IsNullOrWhiteSpace(authorAsin)) return null;

            try
            {
                var locale = AudibleRequestHelper.GetLocale(region);
                var url =
                    $"{AudibleRequestHelper.BuildApiBaseUrl(region)}/1.0/catalog/contributors/{Uri.EscapeDataString(authorAsin)}" +
                    $"?locale={Uri.EscapeDataString(locale)}";
                using var doc = await _apiClient.GetJsonDocumentAsync(url, region, includeLocaleHeaders: true, timeoutSeconds: 10);
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
                    Region = AudibleRequestHelper.NormalizeRegion(region),
                    Description = GetString(contributor, "bio")
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to lookup Audible author details by ASIN {AuthorAsin}", LogRedaction.SanitizeText(authorAsin));
                return null;
            }
        }

        public async Task<List<AuthorLookupItem>> LookupAuthorItemsAsync(string author, string region, string? language = null)
        {
            var response = await _productSearchWorkflow.SearchProductsDirectAsync(
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
                            Region = AudibleRequestHelper.NormalizeRegion(region)
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
    }
}
