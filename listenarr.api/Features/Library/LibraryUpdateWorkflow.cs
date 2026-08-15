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

using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public sealed partial class LibraryUpdateWorkflow
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IAudiobookDestinationRewriteService _destinationRewriteService;
        private readonly IAudiobookOperationCoordinator _audiobookOperationCoordinator;
        private readonly IFileSystemSemanticsResolver _fileSystemSemanticsResolver;
        private readonly ILogger<LibraryUpdateWorkflow> _logger;

        public LibraryUpdateWorkflow(
            IServiceScopeFactory scopeFactory,
            IAudiobookDestinationRewriteService destinationRewriteService,
            IAudiobookOperationCoordinator audiobookOperationCoordinator,
            IFileSystemSemanticsResolver fileSystemSemanticsResolver,
            ILogger<LibraryUpdateWorkflow> logger)
        {
            _scopeFactory = scopeFactory;
            _destinationRewriteService = destinationRewriteService;
            _audiobookOperationCoordinator = audiobookOperationCoordinator ?? throw new ArgumentNullException(nameof(audiobookOperationCoordinator));
            _fileSystemSemanticsResolver = fileSystemSemanticsResolver ?? throw new ArgumentNullException(nameof(fileSystemSemanticsResolver));
            _logger = logger;
        }

        public async Task<IActionResult> UpdateAsync(
            int id,
            AudiobookUpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existingAudiobook = await GetAudiobookPreflightSnapshotAsync(
                id,
                cancellationToken);
            if (existingAudiobook == null)
            {
                return new NotFoundObjectResult(new { message = "Audiobook not found" });
            }

            var basePathRewritten = false;
            var suppressStaleImageUrl = false;
            var metadataUpdateRequested = HasMetadataUpdates(request);

            if (request.BasePath != null)
            {
                if (!string.Equals(
                        request.BasePath,
                        existingAudiobook.BasePath,
                        StringComparison.Ordinal))
                {
                    suppressStaleImageUrl = await IsPathInsideBasePathAsync(
                        request.ImageUrl,
                        existingAudiobook.BasePath,
                        cancellationToken);
                    _logger.LogWarning(
                        "Deprecated PUT /library/{AudiobookId} BasePath update received. Route destination changes through the move endpoint with moveFiles=false.",
                        id);

                    try
                    {
                        await _destinationRewriteService.RewriteDestinationAsync(
                            id,
                            request.BasePath,
                            existingAudiobook.BasePath,
                            cancellationToken);
                        basePathRewritten = true;
                    }
                    catch (ListenarrApplicationException ex)
                    {
                        return ToApplicationExceptionResult(ex);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogError(ex, "Failed to compatibility-route BasePath update for audiobook {AudiobookId}", id);
                        return new ObjectResult(new
                        {
                            message = "Failed to update BasePath",
                            code = "destination_update_failed"
                        })
                        {
                            StatusCode = StatusCodes.Status500InternalServerError
                        };
                    }

                }
            }

            var completionToken = basePathRewritten
                ? CancellationToken.None
                : cancellationToken;
            return await _audiobookOperationCoordinator.ExecuteExclusiveAsync(
                id,
                token => ApplyMetadataUpdatesAsync(
                    id,
                    request,
                    basePathRewritten,
                    suppressStaleImageUrl,
                    metadataUpdateRequested,
                    token),
                completionToken);
        }

        private async Task<Audiobook?> GetAudiobookPreflightSnapshotAsync(
            int id,
            CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            return await repository.GetForUpdateSnapshotAsync(id, cancellationToken);
        }

        private async Task<bool> IsPathInsideBasePathAsync(
            string? candidatePath,
            string? basePath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(candidatePath)
                || string.IsNullOrWhiteSpace(basePath))
            {
                return false;
            }

            try
            {
                var resolution = await _fileSystemSemanticsResolver.ResolveAsync(
                    basePath,
                    FileSystemCaseSensitivityMode.Auto,
                    cancellationToken);
                return resolution.State == PathIdentityState.Valid
                    && FileSystemPathIdentity.IsSameOrInside(
                        candidatePath,
                        basePath,
                        resolution.Semantics);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or ArgumentException or
                InvalidOperationException or NotSupportedException or PathTooLongException or
                System.Security.SecurityException)
            {
                return false;
            }
        }

        private static bool HasMetadataUpdates(AudiobookUpdateRequest request) =>
            request.Title != null
            || request.Subtitle != null
            || request.Authors != null
            || request.ImageUrl != null
            || request.PublishYear != null
            || request.PublishedDate != null
            || request.Series != null
            || request.SeriesNumber != null
            || request.SeriesMemberships != null
            || request.Description != null
            || request.Genres != null
            || request.Tags != null
            || request.Narrators != null
            || request.Isbn != null
            || request.Asin != null
            || request.OpenLibraryId != null
            || request.Publisher != null
            || request.Language != null
            || request.Runtime.HasValue
            || request.Edition != null
            || request.Version != null
            || request.Explicit.HasValue
            || request.Abridged.HasValue
            || request.Monitored.HasValue
            || request.FilePath != null
            || request.FileSize.HasValue
            || request.Quality != null
            || request.QualityProfileId.HasValue;

        private static IActionResult ToApplicationExceptionResult(ListenarrApplicationException exception) =>
            exception switch
            {
                ApplicationNotFoundException => new NotFoundObjectResult(new { message = exception.SafeDetail, code = exception.Code }),
                ApplicationConflictException => new ConflictObjectResult(new { message = exception.SafeDetail, code = exception.Code }),
                ApplicationValidationException => new BadRequestObjectResult(new { message = exception.SafeDetail, code = exception.Code }),
                _ => new ObjectResult(new { message = exception.SafeDetail, code = exception.Code })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                }
            };

        private static void ApplySeriesMembershipUpdates(Audiobook existingAudiobook, AudiobookUpdateRequest request)
        {
            var seriesMembershipsTouched =
                request.SeriesMemberships != null ||
                request.Series != null ||
                request.SeriesNumber != null;

            if (!seriesMembershipsTouched)
            {
                return;
            }

            var mergedSeries = request.Series ?? existingAudiobook.Series;
            var mergedSeriesNumber = request.SeriesNumber ?? existingAudiobook.SeriesNumber;
            var existingPrimaryMembership = AudiobookSeriesMembershipHelper.GetPrimaryMembership(existingAudiobook.SeriesMemberships);

            var normalizedMemberships = AudiobookSeriesMembershipHelper.Normalize(
                request.SeriesMemberships,
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

        private async Task ApplyQualityProfileAsync(
            Audiobook existingAudiobook,
            AudiobookUpdateRequest request,
            CancellationToken cancellationToken)
        {
            if (!request.QualityProfileId.HasValue)
            {
                return;
            }

            if (request.QualityProfileId.Value == -1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var scope = _scopeFactory.CreateScope();
                var qualityProfileService = scope.ServiceProvider.GetRequiredService<IQualityProfileService>();
                var defaultProfile = await qualityProfileService.GetDefaultAsync();
                cancellationToken.ThrowIfCancellationRequested();
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

            existingAudiobook.QualityProfileId = request.QualityProfileId.Value;
            _logger.LogInformation("Updated quality profile for audiobook '{Title}' to ID {ProfileId}",
                existingAudiobook.Title, request.QualityProfileId.Value);
        }
    }
}
