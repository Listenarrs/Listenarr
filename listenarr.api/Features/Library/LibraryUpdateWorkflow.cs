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

using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public sealed class LibraryUpdateWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<LibraryUpdateWorkflow> _logger;

        public LibraryUpdateWorkflow(
            IAudiobookRepository repo,
            IServiceScopeFactory scopeFactory,
            ILogger<LibraryUpdateWorkflow> logger)
        {
            _repo = repo;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task<IActionResult> UpdateAsync(int id, Audiobook updatedAudiobook)
        {
            var existingAudiobook = await _repo.GetByIdAsync(id);
            if (existingAudiobook == null)
            {
                return new NotFoundObjectResult(new { message = "Audiobook not found" });
            }

            var legacyIdentifierFieldsTouched = false;

            if (updatedAudiobook.Title != null) existingAudiobook.Title = updatedAudiobook.Title;
            if (updatedAudiobook.Subtitle != null) existingAudiobook.Subtitle = updatedAudiobook.Subtitle;
            if (updatedAudiobook.Authors != null) existingAudiobook.Authors = updatedAudiobook.Authors;
            if (updatedAudiobook.ImageUrl != null) existingAudiobook.ImageUrl = updatedAudiobook.ImageUrl;
            if (updatedAudiobook.PublishYear != null) existingAudiobook.PublishYear = updatedAudiobook.PublishYear;
            if (updatedAudiobook.PublishedDate != null) existingAudiobook.PublishedDate = updatedAudiobook.PublishedDate;
            if (updatedAudiobook.Description != null) existingAudiobook.Description = updatedAudiobook.Description;
            if (updatedAudiobook.Genres != null) existingAudiobook.Genres = updatedAudiobook.Genres;
            if (updatedAudiobook.Tags != null) existingAudiobook.Tags = updatedAudiobook.Tags;
            if (updatedAudiobook.Narrators != null) existingAudiobook.Narrators = updatedAudiobook.Narrators;
            if (updatedAudiobook.Isbn != null)
            {
                existingAudiobook.Isbn = updatedAudiobook.Isbn;
                legacyIdentifierFieldsTouched = true;
            }

            if (updatedAudiobook.Asin != null)
            {
                existingAudiobook.Asin = updatedAudiobook.Asin;
                legacyIdentifierFieldsTouched = true;
            }

            if (updatedAudiobook.OpenLibraryId != null)
            {
                existingAudiobook.OpenLibraryId = updatedAudiobook.OpenLibraryId;
                legacyIdentifierFieldsTouched = true;
            }

            if (updatedAudiobook.Publisher != null) existingAudiobook.Publisher = updatedAudiobook.Publisher;
            if (updatedAudiobook.Language != null) existingAudiobook.Language = updatedAudiobook.Language;
            if (updatedAudiobook.Runtime != null) existingAudiobook.Runtime = updatedAudiobook.Runtime;
            if (updatedAudiobook.Edition != null) existingAudiobook.Edition = updatedAudiobook.Edition;
            if (updatedAudiobook.Version != null) existingAudiobook.Version = updatedAudiobook.Version;

            ApplySeriesMembershipUpdates(existingAudiobook, updatedAudiobook);

            existingAudiobook.Explicit = updatedAudiobook.Explicit;
            existingAudiobook.Abridged = updatedAudiobook.Abridged;
            existingAudiobook.Monitored = updatedAudiobook.Monitored;

            if (updatedAudiobook.FilePath != null) existingAudiobook.FilePath = updatedAudiobook.FilePath;
            if (updatedAudiobook.FileSize.HasValue) existingAudiobook.FileSize = updatedAudiobook.FileSize;
            if (updatedAudiobook.Quality != null) existingAudiobook.Quality = updatedAudiobook.Quality;

            await ApplyQualityProfileAsync(existingAudiobook, updatedAudiobook);

            if (updatedAudiobook.BasePath != null)
            {
                existingAudiobook.BasePath = FileUtils.NormalizeStoredPath(updatedAudiobook.BasePath);
                _logger.LogInformation("Updated BasePath for audiobook '{Title}' to: {BasePath}", LogRedaction.SanitizeText(existingAudiobook.Title), LogRedaction.SanitizeFilePath(updatedAudiobook.BasePath));
            }

            if (legacyIdentifierFieldsTouched)
            {
                AudiobookIdentifierMapper.SyncImportedIdentifiersFromLegacyFields(existingAudiobook);
            }

            await _repo.UpdateAsync(existingAudiobook);

            _logger.LogInformation("Updated audiobook '{Title}' (ID: {Id})", LogRedaction.SanitizeText(existingAudiobook.Title), id);

            return new OkObjectResult(new { message = "Audiobook updated successfully", audiobook = existingAudiobook });
        }

        private static void ApplySeriesMembershipUpdates(Audiobook existingAudiobook, Audiobook updatedAudiobook)
        {
            var seriesMembershipsTouched =
                updatedAudiobook.SeriesMemberships != null ||
                updatedAudiobook.Series != null ||
                updatedAudiobook.SeriesNumber != null;

            if (!seriesMembershipsTouched)
            {
                return;
            }

            var mergedSeries = updatedAudiobook.Series ?? existingAudiobook.Series;
            var mergedSeriesNumber = updatedAudiobook.SeriesNumber ?? existingAudiobook.SeriesNumber;
            var existingPrimaryMembership = AudiobookSeriesMembershipHelper.GetPrimaryMembership(existingAudiobook.SeriesMemberships);

            var normalizedMemberships = AudiobookSeriesMembershipHelper.Normalize(
                updatedAudiobook.SeriesMemberships,
                mergedSeries,
                mergedSeriesNumber,
                existingPrimaryMembership?.SeriesAsin);

            if (existingAudiobook.SeriesMemberships == null)
            {
                existingAudiobook.SeriesMemberships = new List<AudiobookSeriesMembership>();
            }
            else
            {
                existingAudiobook.SeriesMemberships.Clear();
            }

            foreach (var membership in normalizedMemberships)
            {
                existingAudiobook.SeriesMemberships.Add(membership);
            }

            AudiobookSeriesMembershipHelper.ApplyPrimarySeriesFields(existingAudiobook);
        }

        private async Task ApplyQualityProfileAsync(Audiobook existingAudiobook, Audiobook updatedAudiobook)
        {
            if (!updatedAudiobook.QualityProfileId.HasValue)
            {
                return;
            }

            if (updatedAudiobook.QualityProfileId.Value == -1)
            {
                using var scope = _scopeFactory.CreateScope();
                var qualityProfileService = scope.ServiceProvider.GetRequiredService<IQualityProfileService>();
                var defaultProfile = await qualityProfileService.GetDefaultAsync();
                if (defaultProfile != null)
                {
                    existingAudiobook.QualityProfileId = defaultProfile.Id;
                    _logger.LogInformation("Assigned default quality profile '{ProfileName}' (ID: {ProfileId}) to audiobook '{Title}'",
                        defaultProfile.Name, defaultProfile.Id, existingAudiobook.Title);
                }
                else
                {
                    _logger.LogWarning("No default quality profile found. Audiobook '{Title}' quality profile set to null.", LogRedaction.SanitizeText(existingAudiobook.Title));
                    existingAudiobook.QualityProfileId = null;
                }

                return;
            }

            existingAudiobook.QualityProfileId = updatedAudiobook.QualityProfileId.Value;
            _logger.LogInformation("Updated quality profile for audiobook '{Title}' to ID {ProfileId}",
                existingAudiobook.Title, updatedAudiobook.QualityProfileId.Value);
        }
    }
}
