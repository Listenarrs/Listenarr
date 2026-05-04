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
using Listenarr.Application.Interfaces;
using Listenarr.Application.Metadata;
using Listenarr.Application.Notification;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Configurations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Search;

/// <summary>
/// Handles direct ASIN queries with metadata-first approach.
/// </summary>
public class AsinSearchHandler
{
    private readonly ILogger<AsinSearchHandler> _logger;
    private readonly IConfigurationService _configurationService;
    private readonly AudibleService _audibleService;
    private readonly IAudnexusService _audnexusService;
    private readonly MetadataConverters _metadataConverters;
    private readonly SearchProgressReporter _searchProgressReporter;

    public AsinSearchHandler(
        ILogger<AsinSearchHandler> logger,
        IConfigurationService configurationService,
        AudibleService audibleService,
        IAudnexusService audnexusService,
        MetadataConverters metadataConverters,
        SearchProgressReporter searchProgressReporter)
    {
        _logger = logger;
        _configurationService = configurationService;
        _audibleService = audibleService;
        _audnexusService = audnexusService;
        _metadataConverters = metadataConverters;
        _searchProgressReporter = searchProgressReporter;
    }

    /// <summary>
    /// Searches for a specific ASIN using the following workflow:
    /// 1. Audible catalog metadata (primary)
    /// 2. Audnexus fallback (if configured)
    /// </summary>
    public async Task<List<SearchResult>> SearchByAsinAsync(
        string asin,
        List<ApiConfiguration> metadataSources,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Processing direct ASIN query: {Asin}", asin);
        await _searchProgressReporter.BroadcastAsync($"Extracting ASIN: {asin}", null);


        // Initialize metadata variables
        AudibleBookMetadata? metadata = null;
        string? metadataSourceName = null;

        // Step 1: Try to get metadata from the Audible-backed provider first.
        _logger.LogInformation("Attempting Audible catalog metadata for ASIN {Asin}", asin);
        await _searchProgressReporter.BroadcastAsync($"Searching Audible for {asin}", null);

        try
        {
            var audibleData = await _audibleService.GetBookMetadataAsync(asin, "us", true);
            if (audibleData != null)
            {
                metadata = _metadataConverters.ConvertAudibleToMetadata(audibleData, asin, "Audible");
                metadataSourceName = "Audible";
                _logger.LogInformation("Successfully got metadata from Audible for ASIN {Asin}", asin);
            }
            else
            {
                _logger.LogInformation("Audible metadata returned no data for ASIN {Asin}", asin);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogDebug(ex, "Failed to get metadata from Audible for ASIN {Asin}", asin);
        }

        // Step 2: If Audible failed, try other configured metadata sources (Audnexus)
        if (metadata == null && metadataSources != null && metadataSources.Any())
        {
            _logger.LogInformation("Trying {Count} fallback metadata source(s) for ASIN {Asin}", metadataSources.Count, asin);
            await _searchProgressReporter.BroadcastAsync($"Checking fallback metadata sources for {asin}", null);

            foreach (var source in metadataSources.OrderBy(s => s.Priority))
            {
                try
                {
                    if (source.Name.Contains("Audnexus", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Attempting Audnexus for ASIN {Asin}", asin);
                        await _searchProgressReporter.BroadcastAsync($"Searching Audnexus for {asin}", null);
                        var audnexusData = await _audnexusService.GetBookMetadataAsync(asin, "us", true, false);
                        if (audnexusData != null)
                        {
                            metadata = _metadataConverters.ConvertAudnexusToMetadata(audnexusData, asin, "Audible");
                            metadataSourceName = source.Name;
                            _logger.LogInformation("Successfully got metadata from {Source} for ASIN {Asin}", source.Name, asin);
                            break;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Failed to get metadata from {Source} for ASIN {Asin}", source.Name, asin);
                }
            }
        }


        // Step 3: Convert metadata to SearchResult
        if (metadata != null)
        {
            await _searchProgressReporter.BroadcastAsync($"Found audiobook: {metadata.Title}", null);
            var result = await _metadataConverters.ConvertMetadataToSearchResultAsync(metadata, asin, null, null, null);
            _logger.LogInformation("Converted metadata to SearchResult: Title={Title}, Series={Series}, SeriesNumber={SeriesNumber}",
                result.Title, result.Series, result.SeriesNumber);
            result.IsEnriched = true;
            result.MetadataSource = metadataSourceName;

            // Set source and source link based on where metadata came from
            if (metadataSourceName == "Amazon")
            {
                result.Source = "Amazon";
                result.SourceLink = $"https://www.amazon.com/dp/{asin}";
            }
            else if (metadataSourceName == "Audible")
            {
                result.Source = "Audible";
                result.SourceLink = $"https://www.audible.com/pd/{asin}";
            }
            else
            {
                // Metadata API source - default to Audible for product link
                result.Source = "Audible";
                result.SourceLink = $"https://www.audible.com/pd/{asin}";
            }

            // Validate result before returning
            if (!string.IsNullOrWhiteSpace(result.Title) &&
                !result.Title.Equals("Amazon.com", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(result.Artist))
            {
                _logger.LogInformation("ASIN query succeeded for {Asin}, returning enriched result from {Source}", asin, metadataSourceName);
                return new List<SearchResult> { result };
            }
            else
            {
                _logger.LogWarning("ASIN query got invalid data for {Asin} (Title={Title}, Artist={Artist})", asin, result.Title, result.Artist);
            }
        }
        else
        {
            _logger.LogWarning("ASIN query failed for {Asin} - no metadata from APIs or scraping", asin);
        }

        // If we reach here, ASIN query failed - return empty list
        return new List<SearchResult>();
    }
}

