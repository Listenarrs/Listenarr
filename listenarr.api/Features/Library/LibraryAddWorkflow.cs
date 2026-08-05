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

using System.Security.Cryptography;
using System.Text;
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public sealed class LibraryAddWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IImageCacheService _imageCacheService;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IHistoryRepository _historyRepository;
        private readonly INotificationService? _notificationService;
        private readonly ILibraryAddService? _libraryAddService;
        private readonly ILibraryDestinationMutationGuard _destinationMutationGuard;
        private readonly IFilesystemMutationCoordinator _mutationCoordinator;
        private readonly ILogger<LibraryAddWorkflow> _logger;

        public LibraryAddWorkflow(
            IAudiobookRepository repo,
            IImageCacheService imageCacheService,
            IServiceScopeFactory scopeFactory,
            IHistoryRepository historyRepository,
            ILibraryDestinationMutationGuard destinationMutationGuard,
            IFilesystemMutationCoordinator mutationCoordinator,
            ILogger<LibraryAddWorkflow> logger,
            INotificationService? notificationService = null,
            ILibraryAddService? libraryAddService = null)
        {
            _repo = repo;
            _imageCacheService = imageCacheService;
            _scopeFactory = scopeFactory;
            _historyRepository = historyRepository;
            _destinationMutationGuard = destinationMutationGuard
                ?? throw new ArgumentNullException(nameof(destinationMutationGuard));
            _mutationCoordinator = mutationCoordinator ?? throw new ArgumentNullException(nameof(mutationCoordinator));
            _logger = logger;
            _notificationService = notificationService;
            _libraryAddService = libraryAddService;
        }

        public Task<IActionResult> AddAsync(
            LibraryController.AddToLibraryRequest request,
            CancellationToken cancellationToken = default) =>
            _libraryAddService != null
                ? AddWithServiceAsync(request, cancellationToken)
                : _mutationCoordinator.ExecuteExclusiveAsync(
                    _ => AddCoreAsync(request),
                    cancellationToken);

        private async Task<IActionResult> AddWithServiceAsync(
            LibraryController.AddToLibraryRequest request,
            CancellationToken cancellationToken)
        {
            var result = await _libraryAddService!.AddToLibraryAsync(new LibraryAddOperationRequest
            {
                Metadata = request.Metadata,
                Monitored = request.Monitored,
                QualityProfileId = request.QualityProfileId,
                AutoSearch = request.AutoSearch,
                DestinationPath = request.DestinationPath,
                SearchResult = request.SearchResult,
                HistorySource = "AddNew",
                HistoryMessage = $"Audiobook '{request.Metadata.Title}' added to library from Add New page"
            }, cancellationToken);

            if (result.ValidationFailed)
            {
                return DestinationValidationResult(
                    result.ValidationCode ?? "destination_path_invalid",
                    result.ValidationMessage ?? result.Message,
                    result.ResolvedDestination,
                    result.ValidationField ?? "destinationPath");
            }

            if (result.AlreadyExists)
            {
                return new ConflictObjectResult(new { message = result.Message, audiobook = result.Audiobook });
            }

            return new OkObjectResult(new { message = result.Message, audiobook = result.Audiobook });
        }

        private async Task<IActionResult> AddCoreAsync(LibraryController.AddToLibraryRequest request)
        {
            var metadata = request.Metadata;

            _logger.LogInformation("AddToLibrary received metadata: Title={Title}, Asin={Asin}, PublishYear={PublishYear}, Authors={Authors}, Series={Series}",
                LogRedaction.SanitizeText(metadata.Title), LogRedaction.SanitizeText(metadata.Asin), LogRedaction.SanitizeText(metadata.PublishYear),
                LogRedaction.SanitizeText(metadata.Authors != null ? string.Join(", ", metadata.Authors) : "null"),
                LogRedaction.SanitizeText(metadata.Series));

            TryExtractPublishYear(request);

            if (!string.IsNullOrEmpty(metadata.Asin))
            {
                var existingByAsin = await _repo.GetByAsinAsync(metadata.Asin);
                if (existingByAsin != null)
                {
                    return new ConflictObjectResult(new { message = "Audiobook already exists in library", audiobook = existingByAsin });
                }
            }

            var firstIsbn = (metadata.Isbn != null && metadata.Isbn.Any()) ? metadata.Isbn.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i)) : null;
            if (!string.IsNullOrWhiteSpace(firstIsbn))
            {
                var existingByIsbn = await _repo.GetByIsbnAsync(firstIsbn);
                if (existingByIsbn != null)
                {
                    return new ConflictObjectResult(new { message = "Audiobook already exists in library", audiobook = existingByIsbn });
                }
            }

            var audiobook = metadata.ToAudiobook();

            audiobook.Monitored = request.Monitored;

            AudiobookSeriesMembershipHelper.ApplyToAudiobook(
                audiobook,
                metadata.SeriesMemberships,
                metadata.Series,
                AudibleBookMetadata.ToStringOrFirst(metadata.SeriesNumber));

            AudiobookIdentifierMapper.SyncImportedIdentifiersFromLegacyFields(audiobook);

            _logger.LogInformation("Created Audiobook entity: Title={Title}, Asin={Asin}, PublishYear={PublishYear}",
                LogRedaction.SanitizeText(audiobook.Title), LogRedaction.SanitizeText(audiobook.Asin), LogRedaction.SanitizeText(audiobook.PublishYear));

            await AssignQualityProfileAsync(audiobook, request);

            if (!string.IsNullOrWhiteSpace(request.DestinationPath))
            {
                // Preserve valid Unix path-segment whitespace, but reject values that only become
                // absolute after trimming accidental leading whitespace.
                if (FileUtils.HasLeadingWhitespaceBeforeRootedPath(request.DestinationPath))
                {
                    return DestinationValidationResult(
                        "destination_path_invalid",
                        "DestinationPath is invalid: leading whitespace before an absolute path is not allowed.",
                        request.DestinationPath);
                }

                if (!FileUtils.TryNormalizeUserProvidedDirectoryPathForCurrentOs(
                    request.DestinationPath,
                    out var normalizedDestinationPath,
                    out var validationReason,
                    rejectParentTraversal: true))
                {
                    return DestinationValidationResult(
                        "destination_path_invalid",
                        $"DestinationPath is invalid: {validationReason}",
                        request.DestinationPath);
                }

                using var destinationScope = _scopeFactory.CreateScope();
                var configurationService = destinationScope.ServiceProvider
                    .GetRequiredService<IConfigurationService>();
                var rootFolderService = destinationScope.ServiceProvider
                    .GetRequiredService<IRootFolderService>();
                var fileSystem = destinationScope.ServiceProvider.GetRequiredService<IFileSystem>();
                var settings = await configurationService.GetApplicationSettingsAsync();
                var rootFolders = await rootFolderService.GetAllAsync();
                var allowedDestinationRoots = FileUtils.GetValidMutationRootsForCurrentOs(
                    rootFolders
                        .Select(root => root.Path)
                        .Append(settings.OutputPath));
                if (allowedDestinationRoots.Count == 0
                    || !fileSystem.TryValidateMutationTarget(
                        normalizedDestinationPath,
                        allowedDestinationRoots,
                        out normalizedDestinationPath,
                        out _))
                {
                    return DestinationValidationResult(
                        "destination_path_outside_roots",
                        "DestinationPath must be inside a configured root folder or output path",
                        normalizedDestinationPath);
                }

                audiobook.BasePath = normalizedDestinationPath;
                _logger.LogInformation("Using requested destination path for audiobook '{Title}': {BasePath}",
                    audiobook.Title, audiobook.BasePath);
            }

            if (!string.IsNullOrWhiteSpace(audiobook.BasePath))
            {
                var destinationBlockingReason = await _destinationMutationGuard.GetBlockingReasonAsync(
                    audiobook.BasePath);
                if (destinationBlockingReason != null)
                {
                    return DestinationValidationResult(
                        "destination_path_blocked",
                        destinationBlockingReason,
                        audiobook.BasePath);
                }
            }

            try
            {
                audiobook.ImageUrl = await ResolveLibraryImageUrlAsync(request, firstIsbn);
            }
            catch (LibraryAddConflictException ex)
            {
                return new ConflictObjectResult(new { message = "Audiobook already exists in library", audiobook = ex.Audiobook });
            }

            await _repo.AddAsync(audiobook);
            await ResolveAuthorAsinsAsync(audiobook);
            await SendAddedNotificationAsync(audiobook);
            await AddHistoryAsync(audiobook);

            _logger.LogInformation("Added audiobook '{Title}' (ASIN: {Asin}) to library with Monitored={Monitored}, QualityProfileId={QualityProfileId}, AutoSearch={AutoSearch}",
                audiobook.Title, audiobook.Asin, request.Monitored, audiobook.QualityProfileId, request.AutoSearch);

            return new OkObjectResult(new { message = "Audiobook added to library successfully", audiobook });
        }

        private static BadRequestObjectResult DestinationValidationResult(
            string code,
            string message,
            string? resolvedDestination = null,
            string field = "destinationPath") =>
            new(new
            {
                code,
                field,
                message,
                resolvedDestination
            });

        private void TryExtractPublishYear(LibraryController.AddToLibraryRequest request)
        {
            var metadata = request.Metadata;
            if (!string.IsNullOrWhiteSpace(metadata.PublishYear) || request.SearchResult == null)
            {
                return;
            }

            try
            {
                if (DateTime.TryParse(request.SearchResult.PublishedDate, out var publishDate))
                {
                    metadata.PublishYear = publishDate.Year.ToString();
                    _logger.LogInformation("Extracted publish year from search result publishedDate: {Year}", metadata.PublishYear);
                }
                else
                {
                    _logger.LogWarning("Could not parse PublishedDate as DateTime: {PublishedDate}", LogRedaction.SanitizeText(request.SearchResult.PublishedDate));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to extract publish year from search result publishedDate");
            }
        }

        private async Task<string?> ResolveLibraryImageUrlAsync(LibraryController.AddToLibraryRequest request, string? firstIsbn)
        {
            var metadata = request.Metadata;
            string? imageUrl = metadata.ImageUrl;
            if (!string.IsNullOrEmpty(metadata.Asin))
            {
                return await TryMoveLibraryImageAsync(metadata.Asin, metadata.ImageUrl, imageUrl, "ASIN", metadata.Asin);
            }

            if (metadata.Isbn != null && metadata.Isbn.Any(i => !string.IsNullOrWhiteSpace(i)))
            {
                firstIsbn = metadata.Isbn.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i));
                if (!string.IsNullOrWhiteSpace(firstIsbn))
                {
                    var existingByIsbn = await _repo.GetByIsbnAsync(firstIsbn);
                    if (existingByIsbn != null)
                    {
                        throw new LibraryAddConflictException(existingByIsbn);
                    }
                }

                var derivedKey = "img-" + ComputeShortHash(firstIsbn ?? metadata.ImageUrl ?? string.Empty);
                return await TryMoveLibraryImageAsync(derivedKey, metadata.ImageUrl, imageUrl, "derived ISBN", derivedKey);
            }

            if (!string.IsNullOrEmpty(metadata.ImageUrl))
            {
                var rawKey = request.SearchResult?.Id ?? request.SearchResult?.ResultUrl ?? request.SearchResult?.ProductUrl ?? metadata.ImageUrl;
                var derivedKey = "img-" + ComputeShortHash(rawKey);
                return await TryMoveLibraryImageAsync(derivedKey, metadata.ImageUrl, imageUrl, "derived key", derivedKey);
            }

            return imageUrl;
        }

        private async Task<string?> TryMoveLibraryImageAsync(string key, string? sourceImageUrl, string? fallbackImageUrl, string label, string logValue)
        {
            try
            {
                var libraryImagePath = await _imageCacheService.MoveToLibraryStorageAsync(key, sourceImageUrl);
                if (!string.IsNullOrWhiteSpace(libraryImagePath))
                {
                    _logger.LogInformation("Moved image for {Label} {Value} to permanent library storage", label, LogRedaction.SanitizeText(logValue));
                    return $"/{libraryImagePath}";
                }

                _logger.LogWarning("Failed to move image for {Label} {Value}, image may not be reachable", label, LogRedaction.SanitizeText(logValue));
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Error moving image for {Label} to library storage", label);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Error moving image for {Label} to library storage", label);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Error moving image for {Label} to library storage", label);
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "Error moving image for {Label} to library storage", label);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Error moving image for {Label} to library storage", label);
            }
            catch (UriFormatException ex)
            {
                _logger.LogWarning(ex, "Error moving image for {Label} to library storage", label);
            }

            return fallbackImageUrl;
        }

        private async Task AssignQualityProfileAsync(Audiobook audiobook, LibraryController.AddToLibraryRequest request)
        {
            if (request.QualityProfileId.HasValue)
            {
                audiobook.QualityProfileId = request.QualityProfileId.Value;
                _logger.LogInformation("Assigned custom quality profile ID {ProfileId} to new audiobook '{Title}'",
                    request.QualityProfileId.Value, LogRedaction.SanitizeText(audiobook.Title));
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var qualityProfileService = scope.ServiceProvider.GetRequiredService<IQualityProfileService>();
            var defaultProfile = await qualityProfileService.GetDefaultAsync();
            if (defaultProfile != null)
            {
                audiobook.QualityProfileId = defaultProfile.Id;
                _logger.LogInformation("Assigned default quality profile '{ProfileName}' (ID: {ProfileId}) to new audiobook '{Title}'",
                    defaultProfile.Name, defaultProfile.Id, audiobook.Title);
            }
            else
            {
                _logger.LogWarning("No default quality profile found. New audiobook '{Title}' will not have a quality profile assigned.", LogRedaction.SanitizeText(audiobook.Title));
            }
        }

        private async Task ResolveAuthorAsinsAsync(Audiobook audiobook)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var audible = scope.ServiceProvider.GetRequiredService<AudibleService>();

                if (audiobook.Authors == null || !audiobook.Authors.Any())
                {
                    return;
                }

                audiobook.AuthorAsins ??= new List<string>();
                foreach (var authorName in audiobook.Authors)
                {
                    try
                    {
                        var info = await audible.LookupAuthorAsync(authorName);
                        if (info == null || string.IsNullOrWhiteSpace(info.Asin))
                        {
                            continue;
                        }

                        if (!audiobook.AuthorAsins.Contains(info.Asin))
                        {
                            audiobook.AuthorAsins.Add(info.Asin);
                        }

                        try
                        {
                            var moved = await _imageCacheService.MoveToAuthorLibraryStorageAsync(info.Asin, info.Image);
                            if (moved != null)
                            {
                                _logger.LogInformation("Cached author image for {Author} (ASIN: {Asin})", authorName, info.Asin);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Failed to cache author image for {Author}", authorName);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Author lookup failed for {Author}", authorName);
                    }
                }

                try
                {
                    await _repo.UpdateAsync(audiobook);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to persist author ASINs for audiobook '{Title}'", LogRedaction.SanitizeText(audiobook.Title));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error resolving author ASINs for audiobook '{Title}'", LogRedaction.SanitizeText(audiobook.Title));
            }
        }

        private async Task SendAddedNotificationAsync(Audiobook audiobook)
        {
            if (_notificationService == null)
            {
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var settings = await configService.GetApplicationSettingsAsync();
            var data = new
            {
                id = audiobook.Id,
                title = audiobook.Title ?? "Unknown Title",
                authors = audiobook.Authors,
                narrators = audiobook.Narrators,
                description = audiobook.Description,
                asin = audiobook.Asin,
                publisher = audiobook.Publisher,
                year = audiobook.PublishYear,
                imageUrl = audiobook.ImageUrl
            };
            await _notificationService.SendNotificationAsync("book-added", data, settings.WebhookUrl, settings.EnabledNotificationTriggers);
        }

        private async Task AddHistoryAsync(Audiobook audiobook)
        {
            await _historyRepository.AddAsync(new History
            {
                AudiobookId = audiobook.Id,
                AudiobookTitle = audiobook.Title ?? "Unknown Title",
                EventType = "Added",
                Message = $"Audiobook '{audiobook.Title}' added to library from Add New page",
                Source = "AddNew",
                Timestamp = DateTime.UtcNow
            });
        }

        private static string ComputeShortHash(string? input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return Guid.NewGuid().ToString("N").Substring(0, 12);
            }

            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = SHA1.HashData(bytes);
            return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }

        private sealed class LibraryAddConflictException : Exception
        {
            public LibraryAddConflictException(Audiobook audiobook)
            {
                Audiobook = audiobook;
            }

            public Audiobook Audiobook { get; }
        }
    }
}
