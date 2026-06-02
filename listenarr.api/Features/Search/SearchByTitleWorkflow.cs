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

namespace Listenarr.Api.Features.Search
{
    public sealed class SearchByTitleWorkflow(
        ISearchService searchService,
        AudibleService audibleService,
        IAudiobookMetadataService metadataService,
        Microsoft.Extensions.Logging.ILogger<SearchByTitleWorkflow> logger,
        IConfigurationService? configurationService = null)
    {
        public async Task<SearchByTitleWorkflowResult> ExecuteAsync(
            string query,
            string? region,
            int limit,
            CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    return SearchByTitleWorkflowResult.BadRequest("Query parameter is required");
                }

                var resolvedRegion = await ResolveSearchRegionAsync(region);

                logger.LogInformation("Searching by title: {Query}", query);

                if (IsAsin(query.Trim()))
                {
                    var directResult = await TryLookupAsinAsync(query.Trim(), resolvedRegion);
                    if (directResult != null)
                    {
                        return SearchByTitleWorkflowResult.Ok(directResult);
                    }
                }

                var searchResults = await searchService.IntelligentSearchAsync(
                    query,
                    region: resolvedRegion,
                    language: null,
                    ct: cancellationToken);

                if (searchResults == null || !searchResults.Any())
                {
                    logger.LogWarning("No results found for title search: {Query}", query);
                    return SearchByTitleWorkflowResult.Ok(new List<object>());
                }

                var results = BuildDiscordTitleResults(searchResults.Take(limit), resolvedRegion);

                logger.LogInformation("Successfully fetched {Count} enriched results for title search: {Query}", results.Count, query);
                return SearchByTitleWorkflowResult.Ok(results);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error performing title search for query: {Query}", query);
                return SearchByTitleWorkflowResult.ServerError("Internal server error");
            }
        }

        private async Task<List<object>?> TryLookupAsinAsync(string asin, string region)
        {
            logger.LogInformation("Query appears to be an ASIN; attempting direct metadata lookup for: {Asin}", asin);

            try
            {
                var audible = await audibleService.GetBookMetadataAsync(asin, region, true);
                if (audible != null)
                {
                    var metadataObj = new
                    {
                        metadata = audible,
                        source = "Audible",
                        sourceUrl = GetAudibleBaseUrl(region)
                    };
                    return new List<object> { metadataObj };
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Audible metadata lookup failed for ASIN {Asin}, trying other configured metadata sources", asin);
            }

            try
            {
                var meta = await metadataService.GetMetadataAsync(asin, region, true);
                if (meta != null)
                {
                    return new List<object> { meta };
                }

                logger.LogWarning("Metadata lookup returned null for ASIN {Asin}, falling back to intelligent search", asin);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Metadata lookup failed for ASIN {Asin}, falling back to intelligent search", asin);
            }

            return null;
        }

        private List<object> BuildDiscordTitleResults(IEnumerable<MetadataSearchResult> searchResults, string region)
        {
            var results = new List<object>();

            foreach (var searchResult in searchResults)
            {
                try
                {
                    var metadata = new
                    {
                        Asin = searchResult.Asin,
                        Title = searchResult.Title,
                        Subtitle = searchResult.Series != null ? $"{searchResult.Series} #{searchResult.SeriesNumber}" : null,
                        Authors = !string.IsNullOrEmpty(searchResult.Author) ? new[] { new { Name = searchResult.Author } } : null,
                        Narrators = !string.IsNullOrEmpty(searchResult.Narrator) ? searchResult.Narrator.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries).Select(n => new { Name = n.Trim() }) : null,
                        Publisher = searchResult.Publisher,
                        Description = searchResult.Description,
                        ImageUrl = searchResult.ImageUrl,
                        LengthMinutes = searchResult.Runtime,
                        Language = searchResult.Language,
                        ReleaseDate = !string.IsNullOrWhiteSpace(searchResult.PublishedDate) ? searchResult.PublishedDate : null,
                        Series = !string.IsNullOrEmpty(searchResult.Series) ? new[] { new { Name = searchResult.Series, Position = searchResult.SeriesNumber } } : null
                    };

                    results.Add(new
                    {
                        metadata,
                        source = searchResult.MetadataSource ?? searchResult.Source ?? "Amazon/Audible",
                        sourceUrl = GetMetadataSourceBaseUrl(searchResult.MetadataSource ?? searchResult.Source, region)
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogWarning(ex, "Failed to convert search result for title: {Title}", searchResult.Title);
                }
            }

            return results;
        }

        private static bool IsAsin(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.Length != 10) return false;
            if (!(s.StartsWith("B0") || char.IsDigit(s[0]))) return false;
            return s.All(char.IsLetterOrDigit);
        }

        private async Task<string> ResolveSearchRegionAsync(string? requestedRegion)
        {
            if (!string.IsNullOrWhiteSpace(requestedRegion))
            {
                return requestedRegion.Trim();
            }

            if (configurationService != null)
            {
                try
                {
                    var settings = await configurationService.GetApplicationSettingsAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(settings?.DefaultSearchRegion))
                    {
                        return settings.DefaultSearchRegion.Trim();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogWarning(ex, "Failed to resolve configured default search region; falling back to us");
                }
            }

            return "us";
        }

        private static string GetAudibleBaseUrl(string region)
        {
            return $"https://{MarketDomainResolver.GetAudibleDomain(region)}";
        }

        private static string GetAmazonBaseUrl(string region)
        {
            return $"https://{MarketDomainResolver.GetAmazonDomain(region)}";
        }

        private static string GetMetadataSourceBaseUrl(string? source, string region)
        {
            var isAmazon = source?.Contains("amazon", StringComparison.OrdinalIgnoreCase) == true;
            var isAudible = source?.Contains("audible", StringComparison.OrdinalIgnoreCase) == true;

            return isAmazon && !isAudible
                ? GetAmazonBaseUrl(region)
                : GetAudibleBaseUrl(region);
        }
    }

    public sealed record SearchByTitleWorkflowResult(int StatusCode, object Payload)
    {
        public static SearchByTitleWorkflowResult Ok(object payload) => new(StatusCodes.Status200OK, payload);
        public static SearchByTitleWorkflowResult BadRequest(object payload) => new(StatusCodes.Status400BadRequest, payload);
        public static SearchByTitleWorkflowResult ServerError(object payload) => new(StatusCodes.Status500InternalServerError, payload);
    }
}
