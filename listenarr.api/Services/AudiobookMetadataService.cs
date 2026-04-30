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
namespace Listenarr.Api.Services
{
    /// <summary>
    /// Routes audiobook metadata lookups across configured providers.
    /// </summary>
    public class AudiobookMetadataService : IAudiobookMetadataService
    {
        private readonly ISearchService _searchService;
        private readonly AudibleService _audibleService;
        private readonly IAudnexusService _audnexusService;
        private readonly ILogger<AudiobookMetadataService> _logger;

        public AudiobookMetadataService(
            ISearchService searchService,
            AudibleService audibleService,
            IAudnexusService audnexusService,
            ILogger<AudiobookMetadataService> logger)
        {
            _searchService = searchService;
            _audibleService = audibleService;
            _audnexusService = audnexusService;
            _logger = logger;
        }

        public async Task<object?> GetMetadataAsync(string asin, string region = "us", bool cache = true)
        {
            if (string.IsNullOrWhiteSpace(asin))
            {
                _logger.LogWarning("GetMetadataAsync called with empty ASIN");
                return null;
            }

            // Get enabled metadata sources ordered by priority
            var metadataSources = await _searchService.GetEnabledMetadataSourcesAsync();

            _logger.LogInformation("Found {Count} enabled metadata sources for ASIN {Asin}: {Sources}",
                metadataSources?.Count ?? 0, LogRedaction.SanitizeText(asin),
                string.Join(", ", metadataSources?.Select(s => $"{s.Name} (Priority: {s.Priority}, Enabled: {s.IsEnabled})") ?? new List<string>()));

            if (metadataSources == null || !metadataSources.Any())
            {
                _logger.LogWarning("No enabled metadata sources found for ASIN {Asin}", asin);
                return null;
            }

            foreach (var source in metadataSources)
            {
                try
                {
                    _logger.LogInformation("Attempting to fetch metadata from {SourceName} (Priority: {Priority}) for ASIN: {Asin}",
                        source.Name, source.Priority, asin);

                    object? result = null;

                    if (IsAudibleMetadataSource(source))
                    {
                        result = await _audibleService.GetBookMetadataAsync(asin, region, cache);
                    }
                    else if (source.BaseUrl.Contains("audnex.us", StringComparison.OrdinalIgnoreCase) || source.BaseUrl.Contains("audnex", StringComparison.OrdinalIgnoreCase) || source.BaseUrl.Contains("audnexus", StringComparison.OrdinalIgnoreCase))
                    {
                        // Use Audnexus service to fetch metadata
                        try
                        {
                            var audnexusResult = await _audnexusService.GetBookMetadataAsync(asin, region, seedAuthors: true, update: false);
                            if (audnexusResult != null)
                            {
                                // Convert AudnexusBookResponse to a general shape to return (reuse AudibleBookResponse for compatibility where possible)
                                var converted = new AudibleBookResponse
                                {
                                    Asin = audnexusResult.Asin,
                                    Title = audnexusResult.Title,
                                    Subtitle = audnexusResult.Subtitle,
                                    ImageUrl = audnexusResult.Image,
                                    Publisher = audnexusResult.PublisherName,
                                    LengthMinutes = audnexusResult.RuntimeLengthMin,
                                    Language = audnexusResult.Language,
                                    Explicit = audnexusResult.IsAdult ?? false,
                                    Isbn = audnexusResult.Isbn,
                                    ReleaseDate = audnexusResult.ReleaseDate,
                                    Description = audnexusResult.Description ?? audnexusResult.Summary,
                                    Authors = audnexusResult.Authors?.Select(a => new AudibleAuthor { Asin = a.Asin, Name = a.Name, Region = audnexusResult.Region }).ToList(),
                                    Narrators = audnexusResult.Narrators?.Select(n => new AudibleNarrator { Name = n.Name }).ToList(),
                                    Genres = audnexusResult.Genres?.Select(g => new AudibleGenre { Asin = g.Asin, Name = g.Name, Type = g.Type }).ToList()
                                };
                                result = converted;
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Audnexus lookup failed, trying next source");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Unknown metadata source: {SourceName} ({BaseUrl})", source.Name, source.BaseUrl);
                        continue;
                    }

                    if (result != null)
                    {
                        _logger.LogInformation("Successfully fetched metadata from {SourceName} for ASIN: {Asin}", source.Name, asin);
                        return new
                        {
                            metadata = result,
                            source = source.Name,
                            sourceUrl = source.BaseUrl
                        };
                    }
                }
                catch (Exception sourceEx) when (sourceEx is not OperationCanceledException && sourceEx is not OutOfMemoryException && sourceEx is not StackOverflowException)
                {
                    _logger.LogWarning(sourceEx, "Failed to fetch metadata from {SourceName}, trying next source", source.Name);
                    continue;
                }
            }

            _logger.LogWarning("No metadata found for ASIN: {Asin} from any configured source", asin);
            return null;
        }

        public async Task<AudibleBookResponse?> GetAudibleMetadataAsync(string asin, string region = "us", bool cache = true)
        {
            if (string.IsNullOrWhiteSpace(asin))
            {
                _logger.LogWarning("GetAudibleMetadataAsync called with empty ASIN");
                return null;
            }

            return await _audibleService.GetBookMetadataAsync(asin, region, cache);
        }

        private static bool IsAudibleMetadataSource(ApiConfiguration source)
        {
            if (source == null) return false;

            var name = source.Name ?? string.Empty;
            var baseUrl = source.BaseUrl ?? string.Empty;

            return name.Contains("Audible", StringComparison.OrdinalIgnoreCase) ||
                baseUrl.Contains("api.audible", StringComparison.OrdinalIgnoreCase);
        }
    }
}

