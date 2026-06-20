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

namespace Listenarr.Application.Metadata.Audible
{
    internal sealed class AudibleProductMetadataWorkflow
    {
        private const string DefaultBookResponseGroups =
            "media,product_attrs,product_desc,product_details,product_extended_attrs,product_plans,rating,series,relationships,review_attrs,category_ladders,customer_rights";

        private readonly AudibleApiClient _apiClient;
        private readonly ILogger _logger;

        public AudibleProductMetadataWorkflow(AudibleApiClient apiClient, ILogger logger)
        {
            _apiClient = apiClient;
            _logger = logger;
        }

        public async Task<AudibleBookResponse?> GetBookMetadataAsync(string asin, string region, string? language)
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

        public async Task<List<AudibleBookResponse>> GetBooksMetadataByAsinsAsync(IEnumerable<string> asins, string region)
        {
            var normalizedRegion = AudibleRequestHelper.NormalizeRegion(region);
            var orderedAsins = asins
                .Where(asin => !string.IsNullOrWhiteSpace(asin))
                .Select(asin => asin.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var results = new Dictionary<string, AudibleBookResponse>(StringComparer.OrdinalIgnoreCase);

            foreach (var chunk in Chunk(orderedAsins, 50))
            {
                var doc = chunk.Count == 1
                    ? await _apiClient.GetProductDocumentAsync(chunk[0], normalizedRegion, DefaultBookResponseGroups)
                    : await _apiClient.GetJsonDocumentAsync(
                        $"{AudibleRequestHelper.BuildApiBaseUrl(normalizedRegion)}/1.0/catalog/products/?" +
                        $"{AudibleRequestHelper.BuildQueryString(new Dictionary<string, string?>
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
                    if (root.TryGetProperty("products", out var products) && products.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var mapped in products.EnumerateArray()
                                     .Select(product => AudibleProductMapper.MapProductToBookResponse(product, normalizedRegion))
                                     .Where(mapped => !string.IsNullOrWhiteSpace(mapped?.Asin)))
                        {
                            results[mapped!.Asin!] = mapped;
                        }
                    }
                    else if (root.TryGetProperty("product", out var product) && product.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        var mapped = AudibleProductMapper.MapProductToBookResponse(product, normalizedRegion);
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

        private static List<List<string>> Chunk(List<string> values, int size)
        {
            var chunks = new List<List<string>>();
            for (var i = 0; i < values.Count; i += size)
            {
                chunks.Add(values.Skip(i).Take(size).ToList());
            }

            return chunks;
        }
    }
}
