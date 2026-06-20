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

namespace Listenarr.Application.Search.Strategies;

/// <summary>
/// Coordinates metadata fetching across multiple strategies in priority order.
/// </summary>
public class MetadataStrategyCoordinator
{
    private readonly IEnumerable<IMetadataStrategy> _strategies;
    private readonly ILogger<MetadataStrategyCoordinator> _logger;

    public MetadataStrategyCoordinator(
        IEnumerable<IMetadataStrategy> strategies,
        ILogger<MetadataStrategyCoordinator> logger)
    {
        _strategies = strategies;
        _logger = logger;
    }

    /// <summary>
    /// Attempts to fetch metadata from configured sources using registered strategies.
    /// </summary>
    /// <param name="asin">The ASIN to fetch metadata for</param>
    /// <param name="metadataSources">List of metadata sources to try in order</param>
    /// <param name="originalSource">Original source where ASIN was found (Amazon/Audible)</param>
    /// <returns>Tuple of (metadata, sourceName) if successful, (null, null) otherwise</returns>
    public async Task<(AudibleBookMetadata? metadata, string? sourceName)> FetchMetadataAsync(
        string asin,
        List<ApiConfiguration> metadataSources,
        string? originalSource)
    {
        if (metadataSources.Count > 0)
        {
            _logger.LogInformation("Attempting to fetch metadata for ASIN {Asin} from {Count} configured source(s): {Sources}",
                asin, metadataSources.Count, string.Join(", ", metadataSources.Select(s => s.Name)));
        }

        // Try each metadata source in priority order until one succeeds
        foreach (var source in metadataSources)
        {
            try
            {
                _logger.LogInformation("Attempting to fetch metadata from {SourceName} ({BaseUrl}) for ASIN {Asin}",
                    source.Name, source.BaseUrl, asin);

                // Find a strategy that can handle this source
                var strategy = _strategies.FirstOrDefault(s => s.CanHandle(source));

                if (strategy == null)
                {
                    _logger.LogWarning("No strategy found for metadata source: {BaseUrl}, skipping", source.BaseUrl);
                    continue;
                }

                var metadata = await strategy.FetchMetadataAsync(asin, source, originalSource);

                if (metadata != null)
                {
                    return (metadata, source.Name);
                }
            }
            catch (Exception sourceEx) when (sourceEx is not OperationCanceledException && sourceEx is not OutOfMemoryException && sourceEx is not StackOverflowException)
            {
                _logger.LogWarning(sourceEx, "Failed to fetch metadata from {SourceName} for ASIN {Asin}, trying next source",
                    source.Name, asin);
                continue; // Try next metadata source
            }
        }

        return (null, null);
    }
}

