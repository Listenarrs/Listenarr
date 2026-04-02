using Listenarr.Api.Models.Slskd;
using SharpCompress;

namespace Listenarr.Api.Services.Search.Providers;

/// <summary>
/// Search provider for Slskd indexers.
/// Supports only the API defined by https://github.com/slskd/slskd
/// </summary>
public class SlskdSearchProvider : IIndexerSearchProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SlskdSearchProvider> _logger;

    // Interval between search completion polling in seconds
    private readonly int POLL_TIMER = 1;
    // Maximum search completion time allowed before cancelling in seconds
    private readonly int POLL_TIMEOUT = 20;

    public List<Implementation> Implements => [Implementation.Slskd];

    public SlskdSearchProvider(
        HttpClient httpClient,
        ILogger<SlskdSearchProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    private async Task<HttpContent?> Call(Indexer indexer, HttpMethod method, String endpoint, Object? content = null)
    {
        _logger.LogDebug("Call on {endPoint}", endpoint);
        var apiRequest = new HttpRequestMessage(method, endpoint);
        if (!string.IsNullOrWhiteSpace(indexer.ApiKey))
        {
            apiRequest.Headers.Add("X-Api-Key", indexer.ApiKey);
        }
        
        if (content != null)
        {
            apiRequest.Content = JsonContent.Create(content);
        }

        var response = await _httpClient.SendAsync(apiRequest);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Indexer {Name} returned status {Status}", indexer.Name, response.StatusCode);
            return null;
        }
        
        return response.Content;
    }

    private async Task<String?> Search(
        Indexer indexer,
        string query,
        string? category = null,
        Listenarr.Api.Models.SearchRequest? request = null)
    {
        HttpContent? content = await Call(indexer, HttpMethod.Post, $"{indexer.BaseUrl()}/v0/searches", new SearchRequestDto { 
            SearchText = query
        });

        if (content == null)
        {
            return null;
        }

        var responseData = await content.ReadFromJsonAsync<SearchStateDto>();
        if (responseData == null)
        {
            _logger.LogInformation("Unexpected return value for indexer {Name}", indexer.Name);
            return null;
        }

        _logger.LogDebug("Search ID is {Id}", responseData.Id);
        return responseData.Id;
    }

    private async Task<bool> IsSearchCompleted(
        Indexer indexer,
        string searchId)
    {
        HttpContent? content = await Call(indexer, HttpMethod.Get, $"{indexer.BaseUrl()}/v0/searches/{searchId}");
        if (content == null)
        {
            return false;
        }

        var responseData = await content.ReadFromJsonAsync<SearchStateDto>();
        if (responseData == null)
        {
            return false;
        }

        return responseData.EndedAt != "";
    }

    private async Task<List<IndexerSearchResult>> GetSearchResult(
        Indexer indexer,
        string searchId)
    {
        HttpContent? content = await Call(indexer, HttpMethod.Get, $"{indexer.BaseUrl()}/v0/searches/{searchId}/responses");
        if (content == null)
        {
            return [];
        }

        var searchResponses = await content.ReadFromJsonAsync<List<SearchResponseDto>>();
        if (searchResponses == null)
        {
            _logger.LogInformation("Unexpected return value for indexer {Name}", indexer.Name);
            return [];
        }

        return ParseSearchResponses(indexer, searchId, searchResponses);
    }

    private List<IndexerSearchResult> ParseSearchResponses(Indexer indexer, string searchId, List<SearchResponseDto> searchResponses)
    {
        List<IndexerSearchResult> results = [];
        foreach(SearchResponseDto userResponse in searchResponses)
        {
            if (userResponse.FileCount <= 0) continue;
            
            var uploadSpeed = userResponse.UploadSpeed / (1024 * 1024);

            // Process each directory for a given user
            userResponse.Files
                .GroupBy(file => {
                    // Slskd gives "\\" as directory separator, GetDirectoryName does not recognize that on every OS
                    var normalizedPath = file.Filename.Replace('\\', Path.DirectorySeparatorChar);
                    return Path.GetDirectoryName(normalizedPath);
                })
                .ForEach(group => {
                    string? directory = group.Key;
                    if (directory == null) return;

                    FileResult[] files = [.. group.Select(file => new FileResult{
                        Filename = file.Filename,
                        Size = file.Size
                    })];
                    long totalSize = files.Sum(file => file.Size);

                    results.Add(new IndexerSearchResult
                    {
                        Id = $"{userResponse.Token}-{directory.GetHashCode()}",
                        Title = $"[\uD83D\uDC64 {userResponse.Username} ] {directory} [⚡ {uploadSpeed:F2}MB/s ]",
                        Size = totalSize,
                        Source = indexer.Name,
                        Protocol = DownloadProtocol.Soulseek,
                        PublishedDate = DateTime.UtcNow.ToString(),
                        Uploader = userResponse.Username,
                        Files = files,
                        ResultUrl = $"{indexer.Url}/searches/{searchId}"
                    });
                });
        }
        return results;
    }

    public async Task<List<IndexerSearchResult>> SearchAsync(
        Indexer indexer,
        string query,
        string? category = null,
        Listenarr.Api.Models.SearchRequest? request = null)
    {
        try
        {
            // Start a search on Slskd
            var searchId = await Search(indexer, query, category, request);
            if (searchId == null)
            {
                return [];
            }
            
            // Wait for the search to be completed
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(POLL_TIMEOUT));
            bool searchCompleted = false;
            do
            {
                await Task.Delay(TimeSpan.FromSeconds(POLL_TIMER), cts.Token);
                searchCompleted = await IsSearchCompleted(indexer, searchId);
            }
            while(!searchCompleted);
            
            // Collect search results
            var results = await GetSearchResult(indexer, searchId);
            return results;
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("Indexer {Name} failed to completed the search in time", indexer.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Error searching indexer {Name}", indexer.Name);
        }

        return [];
    }
}

