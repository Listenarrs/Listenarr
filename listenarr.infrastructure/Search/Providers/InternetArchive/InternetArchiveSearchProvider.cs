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

namespace Listenarr.Infrastructure.Search.Providers.InternetArchive;

/// <summary>
/// Search provider for Internet Archive (archive.org)
/// Searches public domain audiobooks from collections like LibriVox
/// </summary>
public class InternetArchiveSearchProvider : IIndexerSearchProvider
{
    private const int MaxItemsToProcess = 20;

    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configurationService;
    private readonly ILogger<InternetArchiveSearchProvider> _logger;

    public string IndexerType => "InternetArchive";

    public InternetArchiveSearchProvider(
        HttpClient httpClient,
        IConfigurationService configurationService,
        ILogger<InternetArchiveSearchProvider> logger)
    {
        _httpClient = httpClient;
        _configurationService = configurationService;
        _logger = logger;
    }

    public async Task<List<IndexerSearchResult>> SearchAsync(
        Indexer indexer,
        string query,
        string? category = null,
        SearchRequest? request = null)
    {
        try
        {
            _logger.LogInformation("Searching Internet Archive for: {Query}", LogRedaction.SanitizeText(query));

            // Parse collection from AdditionalSettings (default: librivoxaudio)
            var collection = "librivoxaudio";

            if (!string.IsNullOrEmpty(indexer.AdditionalSettings))
            {
                try
                {
                    using var settings = JsonDocument.Parse(indexer.AdditionalSettings);
                    if (settings.RootElement.TryGetProperty("collection", out var collectionElem))
                    {
                        var parsedCollection = collectionElem.GetString();
                        if (!string.IsNullOrEmpty(parsedCollection))
                            collection = parsedCollection;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to parse Internet Archive settings, using default collection");
                }
            }

            var queryPlan = InternetArchiveSearchQueryPlanner.Create(collection, query, request);
            if (queryPlan.UsedDefaultCollection && !string.IsNullOrWhiteSpace(collection))
            {
                _logger.LogWarning(
                    "Invalid Internet Archive collection setting '{Collection}', using default collection {DefaultCollection}",
                    LogRedaction.SanitizeText(collection),
                    InternetArchiveSearchQueryPlanner.DefaultCollection);
            }

            _logger.LogDebug(
                "Using Internet Archive collection {Collection} with {QueryCount} query variants",
                queryPlan.Collection,
                queryPlan.Queries.Count);

            if (queryPlan.Queries.Count == 0)
            {
                return new List<IndexerSearchResult>();
            }

            var applicationSettings = await _configurationService.GetApplicationSettingsAsync();
            var processedIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var searchResults = new List<IndexerSearchResult>();

            var queryIndex = 0;
            foreach (var searchQuery in queryPlan.Queries)
            {
                queryIndex++;
                if (processedIdentifiers.Count >= MaxItemsToProcess)
                {
                    break;
                }

                var searchUrl = $"https://archive.org/advancedsearch.php?q={Uri.EscapeDataString(searchQuery)}&fl=identifier,title,creator,date,downloads,item_size,description&rows=100&output=json";

                _logger.LogDebug(
                    "Executing Internet Archive search query variant {QueryIndex} of {QueryCount}",
                    queryIndex,
                    queryPlan.Queries.Count);

                using var response = await _httpClient.GetAsync(searchUrl);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Internet Archive returned status {Status}", response.StatusCode);
                    continue;
                }

                var jsonResponse = await response.Content.ReadAsStringAsync();
                _logger.LogDebug("Internet Archive response length: {Length}", jsonResponse.Length);

                var queryResults = await ParseInternetArchiveSearchResponse(
                    jsonResponse,
                    indexer,
                    applicationSettings.ExtractArchives,
                    processedIdentifiers,
                    MaxItemsToProcess - processedIdentifiers.Count);
                searchResults.AddRange(queryResults);
            }

            _logger.LogInformation("Internet Archive returned {Count} results", searchResults.Count);
            return searchResults;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error searching Internet Archive indexer {Name}", indexer.Name);
            return new List<IndexerSearchResult>();
        }
    }

    private async Task<List<IndexerSearchResult>> ParseInternetArchiveSearchResponse(
        string jsonResponse,
        Indexer indexer,
        bool allowArchives,
        ISet<string> processedIdentifiers,
        int remainingItemsToProcess)
    {
        var results = new List<IndexerSearchResult>();

        try
        {
            _logger.LogDebug("Parsing Internet Archive response, length: {Length}", jsonResponse.Length);

            using var doc = JsonDocument.Parse(jsonResponse);

            if (!doc.RootElement.TryGetProperty("response", out var responseObj))
            {
                _logger.LogWarning("Internet Archive response missing 'response' object");
                return results;
            }

            if (!responseObj.TryGetProperty("docs", out var docsArray))
            {
                _logger.LogWarning("Internet Archive response missing 'docs' array");
                return results;
            }

            _logger.LogDebug("Found {Count} Internet Archive items in response", docsArray.GetArrayLength());

            // Limit metadata requests across all query variants to avoid timeout and IA rate pressure.
            var itemsToProcess = Math.Min(remainingItemsToProcess, docsArray.GetArrayLength());
            _logger.LogDebug("Processing first {Count} of {Total} Internet Archive items", itemsToProcess, docsArray.GetArrayLength());

            var metadataAttempts = 0;
            foreach (var item in docsArray.EnumerateArray())
            {
                try
                {
                    var identifier = item.TryGetProperty("identifier", out var idElem) ? ReadInternetArchiveField(idElem) : string.Empty;
                    var title = item.TryGetProperty("title", out var titleElem) ? ReadInternetArchiveField(titleElem) : string.Empty;
                    var creator = item.TryGetProperty("creator", out var creatorElem) ? ReadInternetArchiveField(creatorElem) : string.Empty;
                    var publishedDate = item.TryGetProperty("date", out var dateElem) ? ReadInternetArchiveField(dateElem) : string.Empty;

                    if (string.IsNullOrEmpty(identifier) || string.IsNullOrEmpty(title))
                    {
                        _logger.LogDebug("Skipping item with missing identifier or title");
                        continue;
                    }

                    if (processedIdentifiers.Contains(identifier))
                    {
                        _logger.LogDebug("Skipping duplicate Internet Archive item {Identifier}", identifier);
                        continue;
                    }

                    if (metadataAttempts >= itemsToProcess)
                    {
                        break;
                    }

                    processedIdentifiers.Add(identifier);
                    metadataAttempts++;

                    _logger.LogDebug("Fetching metadata for {Identifier}", identifier);

                    // Fetch detailed metadata to get file information
                    var metadataUrl = $"https://archive.org/metadata/{identifier}";
                    using var metadataResponse = await _httpClient.GetAsync(metadataUrl);

                    if (!metadataResponse.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Failed to fetch metadata for {Identifier}", identifier);
                        continue;
                    }

                    var metadataJson = await metadataResponse.Content.ReadAsStringAsync();
                    var plan = InternetArchiveRepresentationPlanner.Create(
                        metadataJson,
                        identifier,
                        title,
                        allowArchives);

                    foreach (var issue in plan.Issues)
                    {
                        _logger.LogWarning(
                            "Skipping Internet Archive representation {Format} for {Identifier}: {Reason}",
                            issue.Format,
                            identifier,
                            issue.Reason);
                    }

                    if (plan.Representations.Count == 0)
                    {
                        _logger.LogDebug("No complete audio representation found for {Identifier}", identifier);
                        continue;
                    }

                    foreach (var representation in plan.Representations)
                    {
                        var primaryArtifact = representation.Artifacts[0];
                        _logger.LogDebug(
                            "Found complete Internet Archive representation for {Title}: {Format}, {FileCount} files, {Size} bytes",
                            title,
                            representation.Format,
                            representation.FileCount,
                            representation.Size);

                        results.Add(new IndexerSearchResult
                        {
                            Id = Guid.NewGuid().ToString(),
                            Title = title,
                            Artist = string.IsNullOrWhiteSpace(creator) ? "Unknown" : creator,
                            Album = title,
                            Category = "Audiobook",
                            Size = representation.Size,
                            Files = representation.FileCount,
                            Seeders = 0,
                            Leechers = 0,
                            TorrentUrl = primaryArtifact.Url,
                            ResultUrl = $"https://archive.org/details/{identifier}",
                            SourceLink = $"https://archive.org/details/{identifier}",
                            DownloadType = DirectDownloadMetadataKeys.ClientId,
                            Format = representation.Format,
                            Quality = representation.Quality,
                            Language = plan.Language,
                            Source = $"{indexer.Name} (Internet Archive)",
                            PublishedDate = publishedDate,
                            IndexerId = indexer.Id,
                            IndexerImplementation = indexer.Implementation,
                            DirectDownloadArtifacts = representation.Artifacts
                        });
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Error processing Internet Archive item");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error parsing Internet Archive response");
        }

        return results;
    }

    private static string ReadInternetArchiveField(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.Array => value.EnumerateArray()
                .Select(ReadInternetArchiveField)
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ?? string.Empty,
            _ => string.Empty
        };
    }

}
