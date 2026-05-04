using Listenarr.Application.Search;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Configurations;

namespace Listenarr.Application.Interfaces
{
    /// <summary>
    /// Provides search functionality across multiple indexers and metadata sources
    /// </summary>
    public interface ISearchService
    {
        /// <summary>
        /// Searches configured indexers for audiobook content
        /// </summary>
        /// <param name="query">Search query string</param>
        /// <param name="category">Optional category filter</param>
        /// <param name="apiIds">Optional list of specific API IDs to search</param>
        /// <param name="sortBy">Sort results by seeders, peers, size, or age</param>
        /// <param name="sortDirection">Sort direction (ascending or descending)</param>
        /// <param name="isAutomaticSearch">Whether this is an automatic search (affects logging)</param>
        /// <returns>List of search results from all configured indexers</returns>
        Task<List<SearchResult>> SearchAsync(string query, string? category = null, List<string>? apiIds = null, SearchSortBy sortBy = SearchSortBy.Seeders, SearchSortDirection sortDirection = SearchSortDirection.Descending, bool isAutomaticSearch = false);

        /// <summary>
        /// Performs intelligent search with metadata enrichment
        /// </summary>
        /// <param name="query">Search query string</param>
        /// <param name="candidateLimit">Maximum number of ASIN candidates to collect (default 50)</param>
        /// <param name="returnLimit">Maximum number of results to return (default 10)</param>
        /// <param name="containmentMode">Containment mode: Strict|Relaxed|Off</param>
        /// <param name="requireAuthorAndPublisher">Whether both author and publisher are required</param>
        /// <param name="fuzzyThreshold">Similarity threshold used for fuzzy matching (0.0 - 1.0)</param>
        /// <param name="region">Region code to prefer when querying metadata providers (default: us)</param>
        /// <param name="language">Optional language code to filter metadata results (e.g. en, de)</param>
        /// <param name="ct">Cancellation token to cancel the intelligent search operation.</param>
        /// <returns>Search results enriched with metadata from configured sources</returns>
        Task<List<MetadataSearchResult>> IntelligentSearchAsync(string query, int candidateLimit = 50, int returnLimit = 50, string containmentMode = "Relaxed", bool requireAuthorAndPublisher = false, double fuzzyThreshold = 0.7, string region = "us", string? language = null, CancellationToken ct = default);

        /// <summary>
        /// Searches a specific API/indexer by ID
        /// </summary>
        /// <param name="apiId">The API configuration ID to search</param>
        /// <param name="query">Search query string</param>
        /// <param name="category">Optional category filter</param>
        /// <returns>Search results from the specified API</returns>
        Task<List<SearchResult>> SearchByApiAsync(string apiId, string query, string? category = null);
        /// <summary>
        /// Search a specific indexer and return raw indexer search results (IndexerSearchResult) for indexer-specific consumers.
        /// </summary>
        Task<List<IndexerSearchResult>> SearchIndexerResultsAsync(string apiId, string query, string? category = null, SearchRequest? request = null);

        /// <summary>
        /// Tests connectivity and authentication for a specific API
        /// </summary>
        /// <param name="apiId">The API configuration ID to test</param>
        /// <returns>True if connection successful, false otherwise</returns>
        Task<bool> TestApiConnectionAsync(string apiId);

        /// <summary>
        /// Searches configured indexers (excludes metadata sources)
        /// </summary>
        /// <param name="query">Search query string</param>
        /// <param name="category">Optional category filter</param>
        /// <param name="sortBy">Sort results by seeders, peers, size, or age</param>
        /// <param name="sortDirection">Sort direction (ascending or descending)</param>
        /// <param name="isAutomaticSearch">Whether this is an automatic search</param>
        /// <param name="request">Optional request object to pass indexer-specific parameters</param>
        /// <returns>List of search results from indexers only</returns>
        Task<List<IndexerSearchResult>> SearchIndexersAsync(string query, string? category = null, SearchSortBy sortBy = SearchSortBy.Seeders, SearchSortDirection sortDirection = SearchSortDirection.Descending, bool isAutomaticSearch = false, SearchRequest? request = null);

        /// <summary>
        /// Gets all enabled metadata sources (Audible, Audnexus, OpenLibrary, etc.)
        /// </summary>
        /// <returns>List of enabled metadata source configurations</returns>
        Task<List<ApiConfiguration>> GetEnabledMetadataSourcesAsync();
    }
}
