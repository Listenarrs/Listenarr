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

namespace Listenarr.Application.Audiobooks.Catalog
{
    public partial class LibraryAddService : ILibraryAddService
    {
        private readonly IAudiobookRepository _repo;
        private readonly ILibraryAddCommitStore _commitStore;
        private readonly IImageCacheService _imageCacheService;
        private readonly ILogger<LibraryAddService> _logger;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly AudibleService _audibleService;
        private readonly IConfigurationService _configurationService;
        private readonly IFileNamingService _fileNamingService;
        private readonly IRootFolderService _rootFolderService;
        private readonly ILibraryDestinationMutationGuard _destinationMutationGuard;
        private readonly IFileSystemSemanticsResolver _semanticsResolver;
        private readonly IFileSystem _fileSystem;
        private readonly IFilesystemMutationCoordinator _mutationCoordinator;
        private readonly INotificationService? _notificationService;

        public LibraryAddService(
            IAudiobookRepository repo,
            ILibraryAddCommitStore commitStore,
            IImageCacheService imageCacheService,
            ILogger<LibraryAddService> logger,
            IQualityProfileService qualityProfileService,
            AudibleService audibleService,
            IConfigurationService configurationService,
            IFileNamingService fileNamingService,
            IRootFolderService rootFolderService,
            ILibraryDestinationMutationGuard destinationMutationGuard,
            IFileSystemSemanticsResolver semanticsResolver,
            IFileSystem fileSystem,
            IFilesystemMutationCoordinator mutationCoordinator,
            INotificationService? notificationService = null)
        {
            _repo = repo;
            _commitStore = commitStore;
            _imageCacheService = imageCacheService;
            _logger = logger;
            _qualityProfileService = qualityProfileService;
            _audibleService = audibleService;
            _configurationService = configurationService;
            _fileNamingService = fileNamingService;
            _rootFolderService = rootFolderService;
            _destinationMutationGuard = destinationMutationGuard
                ?? throw new ArgumentNullException(nameof(destinationMutationGuard));
            _semanticsResolver = semanticsResolver
                ?? throw new ArgumentNullException(nameof(semanticsResolver));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _mutationCoordinator = mutationCoordinator ?? throw new ArgumentNullException(nameof(mutationCoordinator));
            _notificationService = notificationService;
        }

        public async Task<LibraryAddOperationResult> AddToLibraryAsync(
            LibraryAddOperationRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            var metadata = request.Metadata ?? new AudibleBookMetadata();

            _logger.LogInformation(
                "LibraryAddService received metadata: Title={Title}, Asin={Asin}, PublishYear={PublishYear}, Authors={Authors}, Series={Series}",
                metadata.Title,
                metadata.Asin,
                metadata.PublishYear,
                metadata.Authors != null ? string.Join(", ", metadata.Authors) : "null",
                metadata.Series);

            if (string.IsNullOrWhiteSpace(metadata.PublishYear) && request.SearchResult != null)
            {
                try
                {
                    if (DateTime.TryParse(request.SearchResult.PublishedDate, out var publishDate))
                    {
                        metadata.PublishYear = publishDate.Year.ToString();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to extract publish year from search result publishedDate");
                }
            }

            if (!string.IsNullOrWhiteSpace(metadata.Asin))
            {
                var existingByAsin = await _repo.GetByAsinAsync(metadata.Asin);
                cancellationToken.ThrowIfCancellationRequested();
                if (existingByAsin != null)
                {
                    return new LibraryAddOperationResult
                    {
                        AlreadyExists = true,
                        Message = "Audiobook already exists in library",
                        Audiobook = existingByAsin
                    };
                }
            }

            var firstIsbn = (metadata.Isbn ?? Enumerable.Empty<string>())
                .FirstOrDefault(i => !string.IsNullOrWhiteSpace(i));
            if (!string.IsNullOrWhiteSpace(firstIsbn))
            {
                var existingByIsbn = await _repo.GetByIsbnAsync(firstIsbn);
                cancellationToken.ThrowIfCancellationRequested();
                if (existingByIsbn != null)
                {
                    return new LibraryAddOperationResult
                    {
                        AlreadyExists = true,
                        Message = "Audiobook already exists in library",
                        Audiobook = existingByIsbn
                    };
                }
            }

            var audiobook = metadata.ToAudiobook();

            audiobook.Monitored = request.Monitored;

            AudiobookIdentifierMapper.SyncImportedIdentifiersFromLegacyFields(audiobook, metadata.Region);

            if (request.QualityProfileId.HasValue)
            {
                audiobook.QualityProfileId = request.QualityProfileId.Value;
            }
            else
            {
                var defaultProfile = await _qualityProfileService.GetDefaultAsync();
                if (defaultProfile != null)
                {
                    audiobook.QualityProfileId = defaultProfile.Id;
                }
                else
                {
                    _logger.LogWarning(
                        "No default quality profile found. New audiobook '{Title}' will not have a quality profile assigned.",
                        audiobook.Title);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var preflightFailure = await ResolveAndValidateDestinationAsync(
                audiobook,
                metadata,
                request,
                cancellationToken);
            if (preflightFailure != null)
            {
                return preflightFailure;
            }

            var preparedImage = await PrepareLibraryImageAsync(
                metadata,
                request.SearchResult,
                firstIsbn,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var preparedAuthorImages = await EnrichAuthorAsinsAsync(audiobook, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _mutationCoordinator.ExecuteExclusiveAsync(
                token => CommitAsync(
                    audiobook,
                    metadata,
                    request,
                    firstIsbn,
                    preparedImage,
                    preparedAuthorImages,
                    token),
                cancellationToken);
            if (!result.Added)
            {
                return result;
            }

            // The audiobook and its Added history event are now durably committed.
            // Permanent image publication and notifications are post-commit effects and may
            // not turn this success into an API failure.
            await TryPublishPreparedImagesAsync(
                audiobook,
                preparedImage,
                preparedAuthorImages);
            await TrySendAddedNotificationAsync(audiobook);

            _logger.LogInformation(
                "Added audiobook '{Title}' (ASIN: {Asin}) to library with Monitored={Monitored}, QualityProfileId={QualityProfileId}, AutoSearch={AutoSearch}",
                audiobook.Title,
                audiobook.Asin,
                request.Monitored,
                audiobook.QualityProfileId,
                request.AutoSearch);

            return result;
        }

        private async Task<LibraryAddOperationResult> CommitAsync(
            Audiobook audiobook,
            AudibleBookMetadata metadata,
            LibraryAddOperationRequest request,
            string? firstIsbn,
            PreparedLibraryImage preparedImage,
            IReadOnlyList<string> preparedAuthorImages,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(metadata.Asin))
            {
                var existingByAsin = await _repo.GetByAsinAsync(metadata.Asin);
                cancellationToken.ThrowIfCancellationRequested();
                if (existingByAsin != null)
                {
                    return AlreadyExists(existingByAsin);
                }
            }

            if (!string.IsNullOrWhiteSpace(firstIsbn))
            {
                var existingByIsbn = await _repo.GetByIsbnAsync(firstIsbn);
                cancellationToken.ThrowIfCancellationRequested();
                if (existingByIsbn != null)
                {
                    return AlreadyExists(existingByIsbn);
                }
            }

            var destinationFailure = await ResolveAndValidateDestinationAsync(
                audiobook,
                metadata,
                request,
                cancellationToken);
            if (destinationFailure != null)
            {
                return destinationFailure;
            }

            audiobook.ImageUrl = preparedImage.FallbackImageUrl;
            cancellationToken.ThrowIfCancellationRequested();
            await _commitStore.CommitAsync(
                audiobook,
                CreateHistoryEntry(audiobook, request),
                cancellationToken);
            return new LibraryAddOperationResult
            {
                Added = true,
                Message = "Audiobook added to library successfully",
                Audiobook = audiobook
            };
        }

        private static LibraryAddOperationResult AlreadyExists(Audiobook audiobook) => new()
        {
            AlreadyExists = true,
            Message = "Audiobook already exists in library",
            Audiobook = audiobook
        };

        private static LibraryAddOperationResult ValidationFailure(
            string code,
            string message,
            string? resolvedDestination = null) => new()
            {
                ValidationFailed = true,
                Message = message,
                ValidationMessage = message,
                ValidationCode = code,
                ValidationField = "destinationPath",
                ResolvedDestination = resolvedDestination
            };

    }
}
