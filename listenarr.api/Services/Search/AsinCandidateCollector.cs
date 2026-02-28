using Listenarr.Api.Services.Search;
using System.Threading;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Services.Search;

/// <summary>
/// Collects ASIN candidates from OpenLibrary and other non-scraping sources.
/// </summary>
public class AsinCandidateCollector
{
    private readonly ILogger<AsinCandidateCollector> _logger;
    private readonly IOpenLibraryService _openLibraryService;
    private readonly MetadataConverters _metadataConverters;
    private readonly SearchProgressReporter _searchProgressReporter;

    public AsinCandidateCollector(
        ILogger<AsinCandidateCollector> logger,
        IOpenLibraryService openLibraryService,
        MetadataConverters metadataConverters,
        SearchProgressReporter searchProgressReporter)
    {
        _logger = logger;
        _openLibraryService = openLibraryService;
        _metadataConverters = metadataConverters;
        _searchProgressReporter = searchProgressReporter;
    }

    /// <summary>
    /// Collects ASIN candidates from non-scraping sources.
    /// </summary>
    public async Task<AsinCandidateCollection> CollectCandidatesAsync(
        string query,
        bool skipOpenLibrary = false,
        CancellationToken ct = default)
    {
        var collection = new AsinCandidateCollection();

        _logger.LogInformation("Collecting candidates from OpenLibrary (query='{Query}')", query);

        // Augment ASIN candidates with OpenLibrary suggestions
        if (!skipOpenLibrary && !string.IsNullOrEmpty(query))
        {
            ct.ThrowIfCancellationRequested();
            await CollectOpenLibraryCandidatesAsync(query, collection, ct);
        }

        return collection;
    }

    private async Task CollectOpenLibraryCandidatesAsync(string query, AsinCandidateCollection collection, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            await _searchProgressReporter.BroadcastAsync($"Searching OpenLibrary for additional titles", null);
            var books = await _openLibraryService.SearchBooksAsync(query, null, 5);
            
            foreach (var book in books.Docs.Take(3))
            {
                ct.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(book.Title) && !string.Equals(book.Title, query, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("OpenLibrary suggested title: {Title}", book.Title);
                    await _searchProgressReporter.BroadcastAsync($"OpenLibrary found: {book.Title}", null);

                    // Convert OpenLibrary work/edition into minimal AudibleBookMetadata and SearchResult
                    try
                    {
                        string? coverUrl = null;
                        if (book.CoverId.HasValue && book.CoverId.Value > 0)
                        {
                            coverUrl = $"https://covers.openlibrary.org/b/id/{book.CoverId}-L.jpg";
                        }

                        var metadata = new AudibleBookMetadata
                        {
                            Asin = null,
                            Source = "OpenLibrary",
                            Title = book.Title,
                            Authors = book.AuthorName?.Where(a => !string.IsNullOrWhiteSpace(a)).ToList(),
                            Publisher = (book.Publisher?.Count > 1) ? "Multiple" : book.Publisher?.FirstOrDefault(),
                            PublishYear = book.FirstPublishYear?.ToString(),
                            Description = null,
                            ImageUrl = coverUrl,
                            OpenLibraryId = book.Key
                        };

                        ct.ThrowIfCancellationRequested();
                        var searchResult = await _metadataConverters.ConvertMetadataToSearchResultAsync(metadata, string.Empty);
                        searchResult.IsEnriched = true;
                        searchResult.MetadataSource = "OpenLibrary";

                        // If OpenLibrary provides a canonical key (work or edition), expose it
                        if (!string.IsNullOrWhiteSpace(book.Key))
                        {
                            // Use OpenLibrary Key as the Id instead of random GUID
                            searchResult.Id = book.Key;
                            
                            if (book.Key.StartsWith("/works", StringComparison.OrdinalIgnoreCase))
                            {
                                searchResult.ProductUrl = $"https://openlibrary.org{book.Key}";
                                searchResult.ResultUrl = $"https://openlibrary.org{book.Key}.json";
                            }
                            else if (book.Key.StartsWith("/books", StringComparison.OrdinalIgnoreCase))
                            {
                                searchResult.ProductUrl = $"https://openlibrary.org{book.Key}";
                                searchResult.ResultUrl = $"https://openlibrary.org{book.Key}.json";
                            }
                        }

                        collection.OpenLibraryDerivedResults.Add(searchResult);
                        
                        // Only store in dictionary if we have a valid OpenLibrary Key
                        // Don't use GUID fallback as it creates invalid openLibraryId values
                        if (!string.IsNullOrWhiteSpace(book.Key))
                        {
                            collection.AsinToOpenLibrary[book.Key] = book;
                        }
                    }
                    catch (Exception exConvert) when (exConvert is not OperationCanceledException && exConvert is not OutOfMemoryException && exConvert is not StackOverflowException) {
                        _logger.LogWarning(exConvert, "Failed to convert OpenLibrary book to SearchResult: {Title}", book.Title);
                    }
                }
            }
        }
        catch (Exception exOL) when (exOL is not OperationCanceledException && exOL is not OutOfMemoryException && exOL is not StackOverflowException) {
            _logger.LogWarning(exOL, "OpenLibrary augmentation failed: {Message}", exOL.Message);
        }
    }
}

/// <summary>
/// Contains collected ASIN candidates and associated metadata.
/// </summary>
public class AsinCandidateCollection
{
    public List<string> AsinCandidates { get; } = new List<string>();
    public Dictionary<string, (string Title, string Author, string? ImageUrl, string? Language)> AsinToRawResult { get; } = new Dictionary<string, (string, string, string?, string?)>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> AsinToSource { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, OpenLibraryBook> AsinToOpenLibrary { get; } = new Dictionary<string, OpenLibraryBook>(StringComparer.OrdinalIgnoreCase);
    public List<SearchResult> OpenLibraryDerivedResults { get; } = new List<SearchResult>();
}

