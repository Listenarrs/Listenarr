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

namespace Listenarr.Application.Search.Indexers.Common;

public class IndexerSearchWorkflow
{
    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configurationService;
    private readonly IIndexerRepository _indexerRepository;
    private readonly IEnumerable<IIndexerSearchProvider> _searchProviders;
    private readonly IndexerAdditionalSettingsParser _additionalSettingsParser;
    private readonly TorznabResponseParser _torznabResponseParser;
    private readonly ILogger<IndexerSearchWorkflow> _logger;

    public IndexerSearchWorkflow(
        HttpClient httpClient,
        IConfigurationService configurationService,
        IIndexerRepository indexerRepository,
        IEnumerable<IIndexerSearchProvider> searchProviders,
        IndexerAdditionalSettingsParser additionalSettingsParser,
        ILogger<IndexerSearchWorkflow> logger,
        IHtmlTextExtractor? htmlTextExtractor = null)
    {
        _httpClient = httpClient;
        _configurationService = configurationService;
        _indexerRepository = indexerRepository;
        _searchProviders = searchProviders;
        _additionalSettingsParser = additionalSettingsParser;
        _logger = logger;
        _torznabResponseParser = new TorznabResponseParser(httpClient, logger, htmlTextExtractor);
    }

    public async Task<List<IndexerSearchResult>> SearchIndexersAsync(
        string query,
        string? category = null,
        SearchSortBy sortBy = SearchSortBy.Seeders,
        SearchSortDirection sortDirection = SearchSortDirection.Descending,
        bool isAutomaticSearch = false,
        SearchRequest? request = null)
    {
        var results = new List<IndexerSearchResult>();
        var indexers = await _indexerRepository.GetEnabledAsync(isAutomaticSearch);

        _logger.LogInformation("Searching {Count} enabled indexers for query: {Query}", indexers.Count, query);

        if (!indexers.Any())
        {
            _logger.LogWarning("No indexers configured, returning mock results for query: {Query}", query);
            return GenerateMockIndexerResults(query);
        }

        var searchTasks = indexers.Select(async indexer =>
        {
            try
            {
                _logger.LogInformation("Searching indexer {Name} ({Type}) for query: {Query}", indexer.Name, indexer.Type, query);
                var perIndexerRequest = ApplyIndexerMamOptions(indexer, request);

                var indexerResults = await SearchIndexerAsync(indexer, query, category, perIndexerRequest);
                _logger.LogInformation("Found {Count} results from indexer {Name}", indexerResults.Count, indexer.Name);
                return indexerResults;
            }
            catch (OperationCanceledException ex)
            {
                // No workflow-level cancellation token flows into this search, so an
                // OperationCanceledException here is an HttpClient per-request timeout
                // (TaskCanceledException derives from OperationCanceledException). Contain it
                // to this indexer so a single slow indexer can't abort every other one's results.
                _logger.LogWarning(ex, "Timed out searching indexer {Name} for query: {Query}", indexer.Name, query);
                return new List<IndexerSearchResult>();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error searching indexer {Name} for query: {Query}", indexer.Name, query);
                return new List<IndexerSearchResult>();
            }
        }).ToList();

        var indexerResults = await Task.WhenAll(searchTasks);
        foreach (var indexerResult in indexerResults)
        {
            results.AddRange(indexerResult);
        }

        _logger.LogInformation("Total {Count} results from all indexers for query: {Query}", results.Count, query);

        return results.OrderByDescending(r => r.Seeders ?? 0).ThenByDescending(r => r.PublishedDate).ToList();
    }

    public async Task<List<SearchResult>> SearchByApiAsync(string apiId, string query, string? category = null)
    {
        try
        {
            var indexer = await GetIndexerByApiIdAsync(apiId);

            if (indexer == null)
            {
                _logger.LogWarning("Indexer not found for apiId: {ApiId}", apiId);
                return new List<SearchResult>();
            }

            if (!indexer.IsEnabled)
            {
                _logger.LogWarning("Indexer {IndexerName} (apiId: {ApiId}) is not enabled", indexer.Name, apiId);
                return new List<SearchResult>();
            }

            var req = new SearchRequest();
            var mamOpts = _additionalSettingsParser.ParseMamOptions(indexer.AdditionalSettings);
            if (mamOpts != null) req.MyAnonamouse = mamOpts;

            var idxResults = await SearchIndexerAsync(indexer, query, category, req);
            return idxResults.Select(SearchResultConverters.ToSearchResult).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error searching indexer {ApiId} for query: {Query}", apiId, query);
            return new List<SearchResult>();
        }
    }

    public async Task<List<IndexerSearchResult>> SearchIndexerResultsAsync(
        string apiId,
        string query,
        string? category = null,
        SearchRequest? request = null)
    {
        try
        {
            var indexer = await GetIndexerByApiIdAsync(apiId);

            if (indexer == null || !indexer.IsEnabled)
            {
                _logger.LogWarning("Indexer not found or disabled for apiId: {ApiId}", apiId);
                return new List<IndexerSearchResult>();
            }

            request = ApplyIndexerMamOptions(indexer, request);
            return await SearchIndexerAsync(indexer, query, category, request);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error searching indexer {ApiId} for query: {Query}", apiId, query);
            return new List<IndexerSearchResult>();
        }
    }

    public async Task<bool> TestApiConnectionAsync(string apiId)
    {
        try
        {
            var apiConfig = await _configurationService.GetApiConfigurationAsync(apiId);
            if (apiConfig == null) return false;

            var response = await _httpClient.GetAsync(apiConfig.BaseUrl);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error testing API connection for {ApiId}", apiId);
            return false;
        }
    }

    public async Task<List<IndexerSearchResult>> ParseTorznabResponseAsync(string xmlContent, Indexer indexer)
    {
        return await _torznabResponseParser.ParseAsync(xmlContent, indexer);
    }

    private async Task<Indexer?> GetIndexerByApiIdAsync(string apiId)
    {
        return int.TryParse(apiId, out var indexerId)
            ? await _indexerRepository.GetByIdAsync(indexerId)
            : await _indexerRepository.GetByNameAsync(apiId);
    }

    private SearchRequest? ApplyIndexerMamOptions(Indexer indexer, SearchRequest? request)
    {
        if (request?.MyAnonamouse != null)
            return request;

        var mam = _additionalSettingsParser.ParseMamOptions(indexer.AdditionalSettings);
        if (mam == null)
            return request;

        request ??= new SearchRequest();
        request.MyAnonamouse = mam;
        return request;
    }

    private async Task<List<IndexerSearchResult>> SearchIndexerAsync(
        Indexer indexer,
        string query,
        string? category = null,
        SearchRequest? request = null)
    {
        try
        {
            query = IndexerQuerySanitizer.Sanitize(query);
            _logger.LogInformation("Searching indexer {Name} ({Implementation}) for: {Query}", indexer.Name, indexer.Implementation, query);

            var fallbackName = GetFallbackIndexerName(indexer);
            var provider = _searchProviders.FirstOrDefault(p =>
                p.IndexerType.Equals(indexer.Implementation, StringComparison.OrdinalIgnoreCase) ||
                (p.IndexerType.Equals("Torznab", StringComparison.OrdinalIgnoreCase)
                 && indexer.Implementation.Equals("Newznab", StringComparison.OrdinalIgnoreCase)));

            if (provider == null)
            {
                _logger.LogWarning("No provider found for indexer type: {Implementation}", indexer.Implementation);
                return new List<IndexerSearchResult>();
            }

            var providerResults = await provider.SearchAsync(indexer, query, category, request);
            foreach (var r in providerResults.Where(r => string.IsNullOrWhiteSpace(r.Source)))
            {
                r.Source = fallbackName;
            }

            return providerResults;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error searching indexer {Name}", indexer.Name);
            return new List<IndexerSearchResult>();
        }
    }

    private static string GetFallbackIndexerName(Indexer indexer)
    {
        if (!string.IsNullOrWhiteSpace(indexer.Name))
            return indexer.Name;

        if (!string.IsNullOrWhiteSpace(indexer.Implementation))
            return indexer.Implementation;

        try
        {
            var baseUrl = indexer.Url?.TrimEnd('/') ?? string.Empty;
            var baseUri = new Uri(baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? baseUrl : "https://" + baseUrl);
            return baseUri.Host;
        }
        catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
        {
            return "Indexer";
        }
    }

    private List<IndexerSearchResult> GenerateMockIndexerResults(string query)
    {
        return GenerateMockIndexerResults(query, "Mock Indexer", "Torrent");
    }

    private List<IndexerSearchResult> GenerateMockIndexerResults(string query, string indexerName, string indexerType)
    {
        var random = new Random();
        var results = new List<IndexerSearchResult>();
        var isUsenet = indexerType.Equals("Usenet", StringComparison.OrdinalIgnoreCase);

        _logger.LogInformation("Generating {Count} mock {Type} results for indexer {IndexerName}", 5, indexerType, indexerName);

        for (int i = 0; i < 5; i++)
        {
            var result = new IndexerSearchResult
            {
                Id = Guid.NewGuid().ToString(),
                Title = $"{query} - Quality {i + 1}",
                Artist = "Various Authors",
                Album = $"{query} Series",
                Category = "Audiobook",
                Size = random.Next(200_000_000, 1_500_000_000),
                Seeders = isUsenet ? 0 : random.Next(5, 100),
                Leechers = isUsenet ? 0 : random.Next(0, 20),
                Source = indexerName,
                PublishedDate = DateTime.UtcNow.AddDays(-random.Next(1, 365)).ToString("o"),
                Quality = i switch
                {
                    0 => "MP3 64kbps",
                    1 => "MP3 128kbps",
                    2 => "MP3 192kbps",
                    3 => "M4B 128kbps",
                    _ => "FLAC"
                },
                Format = i >= 3 ? "M4B" : "MP3",
                Language = "English"
            };

            if (isUsenet)
            {
                result.NzbUrl = $"https://{indexerName.ToLowerInvariant()}.example.com/api/nzb/{Guid.NewGuid():N}";
                result.MagnetLink = string.Empty;
                result.TorrentUrl = string.Empty;
            }
            else
            {
                result.MagnetLink = $"magnet:?xt=urn:btih:{Guid.NewGuid():N}";
                result.NzbUrl = string.Empty;
            }

            results.Add(result);
        }

        return results;
    }
}
