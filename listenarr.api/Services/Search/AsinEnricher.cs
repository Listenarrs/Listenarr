using AsyncKeyedLock;
using Listenarr.Api.Services.Search.Filters;
using Listenarr.Api.Services.Search.Strategies;
using System.Collections.Concurrent;

namespace Listenarr.Api.Services.Search;

/// <summary>
/// Handles concurrent enrichment of ASINs with metadata from multiple sources.
/// </summary>
public class AsinEnricher
{
    private readonly ILogger<AsinEnricher> _logger;
    private readonly MetadataStrategyCoordinator _metadataStrategyCoordinator;
    private readonly MetadataConverters _metadataConverters;
    private readonly SearchResultFilterPipeline _filterPipeline;
    private readonly SearchProgressReporter _searchProgressReporter;

    public AsinEnricher(
        ILogger<AsinEnricher> logger,
        MetadataStrategyCoordinator metadataStrategyCoordinator,
        MetadataConverters metadataConverters,
        SearchResultFilterPipeline filterPipeline,
        SearchProgressReporter searchProgressReporter)
    {
        _logger = logger;
        _metadataStrategyCoordinator = metadataStrategyCoordinator;
        _metadataConverters = metadataConverters;
        _filterPipeline = filterPipeline;
        _searchProgressReporter = searchProgressReporter;
    }

    /// <summary>
    /// Enriches ASINs concurrently with metadata from configured sources.
    /// </summary>
    public async Task<EnrichmentResult> EnrichAsinsAsync(
        List<string> asinCandidates,
        Dictionary<string, (string Title, string Author, string? ImageUrl, string? Language)> asinToRawResult,
        Dictionary<string, string> asinToSource,
        Dictionary<string, OpenLibraryBook> asinToOpenLibrary,
        List<ApiConfiguration> metadataSources,
        string? query,
        CancellationToken ct = default)
    {
        var semaphore = new AsyncNonKeyedLocker(5); // Increased from 3 to 5 for better throughput
        var enrichmentTasks = new List<Task>();
        var enriched = new ConcurrentBag<SearchResult>();
        var asinsNeedingFallback = new ConcurrentBag<string>();
        var candidateDropReasons = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asin in asinCandidates)
        {
            enrichmentTasks.Add(Task.Run(async () =>
            {
                using var _ = await semaphore.LockAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    _logger.LogDebug("Enriching ASIN {Asin}", asin);
                    await _searchProgressReporter.BroadcastAsync($"Enriching ASIN: {asin}", asin);

                    // Get the original search results
                    asinToRawResult.TryGetValue(asin, out var rawResult);
                    asinToSource.TryGetValue(asin, out var originalSource);

                    AudibleBookMetadata? metadata = null;
                    string? metadataSourceName = null;

                    // If this ASIN was added from OpenLibrary augmentation, prefer OpenLibrary metadata
                    if (asinToOpenLibrary.TryGetValue(asin, out var olBook))
                    {
                        try
                        {
                            _logger.LogInformation("ASIN {Asin} has OpenLibrary augmentation. Considering OL metadata.", asin);

                            // Build a simple haystack from OL fields and check if the original query
                            // appears anywhere in those fields. Only show OL-derived results when
                            // the search value appears in OL metadata (title/author/publisher/subject).
                            var hayOl = string.Join(" ", new[] {
                                olBook.Title ?? string.Empty,
                                olBook.AuthorName != null ? string.Join(" ", olBook.AuthorName) : string.Empty,
                                olBook.Publisher != null ? string.Join(" ", olBook.Publisher) : string.Empty,
                                olBook.Subject != null ? string.Join(" ", olBook.Subject) : string.Empty
                            }.Where(s => !string.IsNullOrWhiteSpace(s)));

                            var queryMatchesOl = false;
                            if (!string.IsNullOrWhiteSpace(hayOl) && !string.IsNullOrWhiteSpace(query))
                            {
                                queryMatchesOl = hayOl.IndexOf(query ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0;
                            }

                            if (queryMatchesOl)
                            {
                                // Convert OpenLibrary book to AudibleBookMetadata-like object
                                metadata = new AudibleBookMetadata
                                {
                                    Asin = asin,
                                    Source = "OpenLibrary",
                                    Title = olBook.Title,
                                    Authors = olBook.AuthorName?.Where(a => !string.IsNullOrWhiteSpace(a)).ToList(),
                                    Publisher = olBook.Publisher != null && olBook.Publisher.Any() ? olBook.Publisher.FirstOrDefault() : null,
                                    PublishYear = olBook.FirstPublishYear?.ToString(),
                                    Description = null,
                                    ImageUrl = olBook.CoverId.HasValue ? $"https://covers.openlibrary.org/b/id/{olBook.CoverId}-L.jpg" : null
                                };
                                metadataSourceName = "OpenLibrary";
                                _logger.LogInformation("Using OpenLibrary metadata for ASIN {Asin} (title: {Title})", asin, olBook.Title);
                            }
                            else
                            {
                                // OL exists for this ASIN but does not contain the query -> queue for fallback
                                _logger.LogInformation("OpenLibrary has entry for ASIN {Asin} but OL metadata did not contain the query; queuing for fallback", asin);
                                asinsNeedingFallback.Add(asin);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                            _logger.LogWarning(ex, "Failed to convert OpenLibrary metadata for ASIN {Asin}", asin);
                        }

                        // Skip calling Audimeta/Audnexus for OpenLibrary-augmented ASINs
                        if (metadata != null)
                        {
                            // proceed to convert metadata below without trying other metadata sources
                        }
                        else
                        {
                            // move on to next ASIN; no remote metadata calls for OL-derived ASINs
                            return;
                        }
                    }

                    // Use the pre-fetched metadata sources (avoid DbContext concurrency issues)
                    if (metadata == null && metadataSources.Count > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        await _searchProgressReporter.BroadcastAsync($"Fetching metadata for ASIN: {asin}", asin);
                        (metadata, metadataSourceName) = await _metadataStrategyCoordinator.FetchMetadataAsync(
                            asin, metadataSources, originalSource);
                    }

                    // If all metadata sources failed, queue for fallback handling
                    if (metadata == null)
                    {
                        asinsNeedingFallback.Add(asin);
                        // Note that this ASIN has no metadata yet
                        try { candidateDropReasons[asin] = "queued_for_fallback_no_metadata"; } catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) { 
                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                        }
                    }

                    if (metadata != null)
                    {
                        // Accept metadata even if Title is missing. ConvertMetadataToSearchResult
                        // will use raw result title as fallback if metadata title is empty.
                        var enrichedResult = await _metadataConverters.ConvertMetadataToSearchResultAsync(metadata, asin, rawResult.Title, rawResult.Author, rawResult.ImageUrl, rawResult.Language);
                        enrichedResult.IsEnriched = true;

                        // Store the metadata source name for the badge
                        if (!string.IsNullOrEmpty(metadataSourceName))
                        {
                            enrichedResult.MetadataSource = metadataSourceName;
                            _logger.LogInformation("✓ Enriched result for ASIN {Asin} - Title: {Title}, MetadataSource: {MetadataSource}",
                                asin, enrichedResult.Title ?? "null", metadataSourceName);
                        }
                        else
                        {
                            _logger.LogWarning("⚠ Metadata obtained for ASIN {Asin} but metadataSourceName is null/empty", asin);
                        }

                        // Filter out Kindle Edition ebooks - these are not audiobooks
                        if (_filterPipeline.WouldFilter(enrichedResult, out string? filterReason))
                        {
                            _logger.LogInformation("Filtering out result: {Title} (ASIN: {Asin}) - Reason: {Reason}", 
                                enrichedResult.Title, asin, filterReason);
                            await _searchProgressReporter.BroadcastAsync($"Filtered out: {enrichedResult.Title}", asin);
                            try { candidateDropReasons[asin] = filterReason ?? "filtered"; } catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException) { 
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }
                        }
                        else
                        {
                            enriched.Add(enrichedResult);
                            try { candidateDropReasons[asin] = "enriched_from_metadata"; } catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException) { 
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }
                        }
                    }
                    else
                    {
                        _logger.LogWarning("✗ No metadata obtained for ASIN {Asin} after trying all sources and scraping", asin);
                        try { candidateDropReasons[asin] = "no_metadata_after_sources"; } catch (Exception caughtEx_4) when (caughtEx_4 is not OperationCanceledException && caughtEx_4 is not OutOfMemoryException && caughtEx_4 is not StackOverflowException) { 
                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Metadata enrichment cancelled for ASIN {Asin}", asin);
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogWarning(ex, "Metadata enrichment failed for ASIN {Asin}", asin);
                }
            }));
        }

        await Task.WhenAll(enrichmentTasks);

        return new EnrichmentResult(
            enriched.ToList(),
            asinsNeedingFallback.ToList(),
            candidateDropReasons);
    }
}

/// <summary>
/// Result of ASIN enrichment process.
/// </summary>
public record EnrichmentResult(
    List<SearchResult> EnrichedResults,
    List<string> AsinsNeedingFallback,
    ConcurrentDictionary<string, string> CandidateDropReasons);

