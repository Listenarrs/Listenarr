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

namespace Listenarr.Application.Metadata.Core
{
    /// <inheritdoc />
    public class AudiobookMetadataRefreshService : IAudiobookMetadataRefreshService
    {
        private readonly IAudiobookMetadataService _metadataService;
        private readonly MetadataConverters _metadataConverters;
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly ILogger<AudiobookMetadataRefreshService> _logger;

        public AudiobookMetadataRefreshService(
            IAudiobookMetadataService metadataService,
            MetadataConverters metadataConverters,
            IAudiobookRepository audiobookRepository,
            ILogger<AudiobookMetadataRefreshService> logger)
        {
            _metadataService = metadataService;
            _metadataConverters = metadataConverters;
            _audiobookRepository = audiobookRepository;
            _logger = logger;
        }

        public async Task<bool> TryPopulateMissingMetadataAsync(Audiobook audiobook, string? region = null, CancellationToken cancellationToken = default)
        {
            if (audiobook == null || string.IsNullOrWhiteSpace(audiobook.Asin))
            {
                return false;
            }

            var resolvedRegion = string.IsNullOrWhiteSpace(region) ? "us" : region.Trim();

            AudibleBookResponse? response;
            try
            {
                response = await _metadataService.GetAudibleMetadataAsync(audiobook.Asin, resolvedRegion, cache: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Auto metadata refresh lookup failed for audiobook {AudiobookId} ASIN {Asin}", audiobook.Id, audiobook.Asin);
                return false;
            }

            if (response == null)
            {
                _logger.LogInformation("Auto metadata refresh found no upstream data for audiobook {AudiobookId} ASIN {Asin}", audiobook.Id, audiobook.Asin);
                return false;
            }

            var converted = _metadataConverters.ConvertAudibleToMetadata(response, audiobook.Asin, "Audible");
            if (!FillMissingFields(audiobook, converted))
            {
                return false;
            }

            try
            {
                await _audiobookRepository.UpdateAsync(audiobook);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to save auto-populated metadata for audiobook {AudiobookId}", audiobook.Id);
                return false;
            }

            _logger.LogInformation("Auto-populated missing metadata for audiobook {AudiobookId} ({Title}) from ASIN {Asin}", audiobook.Id, audiobook.Title, audiobook.Asin);
            return true;
        }

        /// <summary>
        /// Fills only fields that are currently empty on the audiobook. Never overwrites values the
        /// user (or a prior metadata fetch) already set. Returns true if anything changed.
        /// </summary>
        internal static bool FillMissingFields(Audiobook audiobook, AudibleBookMetadata metadata)
        {
            var changed = false;

            if (string.IsNullOrWhiteSpace(audiobook.Title) && !string.IsNullOrWhiteSpace(metadata.Title)) { audiobook.Title = metadata.Title; changed = true; }
            if (string.IsNullOrWhiteSpace(audiobook.Subtitle) && !string.IsNullOrWhiteSpace(metadata.Subtitle)) { audiobook.Subtitle = metadata.Subtitle; changed = true; }
            if (string.IsNullOrWhiteSpace(audiobook.Publisher) && !string.IsNullOrWhiteSpace(metadata.Publisher)) { audiobook.Publisher = metadata.Publisher; changed = true; }
            if (string.IsNullOrWhiteSpace(audiobook.PublishYear) && !string.IsNullOrWhiteSpace(metadata.PublishYear)) { audiobook.PublishYear = metadata.PublishYear; changed = true; }
            if (string.IsNullOrWhiteSpace(audiobook.PublishedDate) && !string.IsNullOrWhiteSpace(metadata.PublishedDate)) { audiobook.PublishedDate = metadata.PublishedDate; changed = true; }
            if (string.IsNullOrWhiteSpace(audiobook.Description) && !string.IsNullOrWhiteSpace(metadata.Description)) { audiobook.Description = metadata.Description; changed = true; }
            if (string.IsNullOrWhiteSpace(audiobook.Language) && !string.IsNullOrWhiteSpace(metadata.Language)) { audiobook.Language = metadata.Language; changed = true; }
            if (string.IsNullOrWhiteSpace(audiobook.ImageUrl) && !string.IsNullOrWhiteSpace(metadata.ImageUrl)) { audiobook.ImageUrl = metadata.ImageUrl; changed = true; }

            if ((audiobook.Runtime == null || audiobook.Runtime == 0) && metadata.Runtime.HasValue && metadata.Runtime.Value > 0)
            {
                audiobook.Runtime = metadata.Runtime;
                changed = true;
            }

            if (IsEmpty(audiobook.Authors) && metadata.Authors is { Count: > 0 })
            {
                audiobook.Authors = metadata.Authors.ToList();
                changed = true;
            }

            if (IsEmpty(audiobook.Narrators) && metadata.Narrators is { Count: > 0 })
            {
                audiobook.Narrators = metadata.Narrators.ToList();
                changed = true;
            }

            if (IsEmpty(audiobook.Genres) && metadata.Genres is { Count: > 0 })
            {
                audiobook.Genres = metadata.Genres.ToList();
                changed = true;
            }

            return changed;
        }

        private static bool IsEmpty(List<string>? values) => values == null || values.Count == 0;
    }
}
