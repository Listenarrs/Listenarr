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

using System.Text.RegularExpressions;
using Listenarr.Application.Common;
using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public sealed class LibraryDeleteWorkflow
    {
        private readonly IAudiobookDeletionCommitService _deletionCommitService;
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly IAudiobookDeletionIntentStore _deletionIntentStore;
        private readonly IImageCacheService _imageCacheService;
        private readonly IAudiobookFilesystemDeleteService _audiobookFilesystemDeleteService;
        private readonly string _contentRootPath;
        private readonly IFileSystem _fileSystem;
        private readonly IFilesystemMutationCoordinator _filesystemMutationCoordinator;
        private readonly IAudiobookOperationCoordinator _audiobookOperationCoordinator;
        private readonly IMoveQueueService _moveQueueService;
        private readonly ILibraryFilesystemMutationGate _filesystemMutationGate;
        private readonly ILogger<LibraryDeleteWorkflow> _logger;

        public LibraryDeleteWorkflow(
            IAudiobookDeletionCommitService deletionCommitService,
            IAudiobookRepository audiobookRepository,
            IAudiobookDeletionIntentStore deletionIntentStore,
            IImageCacheService imageCacheService,
            IAudiobookFilesystemDeleteService audiobookFilesystemDeleteService,
            IApplicationPathService applicationPathService,
            IFileSystem fileSystem,
            IFilesystemMutationCoordinator filesystemMutationCoordinator,
            IAudiobookOperationCoordinator audiobookOperationCoordinator,
            IMoveQueueService moveQueueService,
            ILibraryFilesystemMutationGate filesystemMutationGate,
            ILogger<LibraryDeleteWorkflow> logger)
        {
            _deletionCommitService = deletionCommitService ?? throw new ArgumentNullException(nameof(deletionCommitService));
            _audiobookRepository = audiobookRepository ?? throw new ArgumentNullException(nameof(audiobookRepository));
            _deletionIntentStore = deletionIntentStore ?? throw new ArgumentNullException(nameof(deletionIntentStore));
            _imageCacheService = imageCacheService;
            _audiobookFilesystemDeleteService = audiobookFilesystemDeleteService;
            _contentRootPath = applicationPathService.ContentRootPath;
            _fileSystem = fileSystem;
            _filesystemMutationCoordinator = filesystemMutationCoordinator ?? throw new ArgumentNullException(nameof(filesystemMutationCoordinator));
            _audiobookOperationCoordinator = audiobookOperationCoordinator ?? throw new ArgumentNullException(nameof(audiobookOperationCoordinator));
            _moveQueueService = moveQueueService ?? throw new ArgumentNullException(nameof(moveQueueService));
            _filesystemMutationGate = filesystemMutationGate
                ?? throw new ArgumentNullException(nameof(filesystemMutationGate));
            _logger = logger;
        }

        public Task<IActionResult> DeleteAsync(
            int id,
            bool deleteFiles,
            bool deleteFolder,
            CancellationToken cancellationToken = default) =>
            _filesystemMutationCoordinator.ExecuteExclusiveAsync(
                globalToken => _audiobookOperationCoordinator.ExecuteExclusiveAsync(
                    id,
                    audiobookToken => DeleteCoreAsync(
                        id,
                        deleteFiles,
                        deleteFolder,
                        audiobookToken),
                    globalToken),
                cancellationToken);

        private async Task<IActionResult> DeleteCoreAsync(
            int id,
            bool deleteFiles,
            bool deleteFolder,
            CancellationToken cancellationToken)
        {
            var deleteFilesystem = deleteFiles || deleteFolder;
            if (deleteFilesystem)
            {
                _filesystemMutationGate.EnsureReady();
            }

            try
            {
                await _moveQueueService.EnsureFilesystemMutationAllowedAsync(
                    id,
                    cancellationToken,
                    allowActiveDeletionIntent: true);
            }
            catch (ApplicationConflictException exception)
            {
                return new ConflictObjectResult(new
                {
                    message = exception.SafeDetail,
                    code = exception.Code
                });
            }

            Audiobook audiobook;
            AudiobookFilesystemDeleteResult? filesystemResult = null;
            if (deleteFilesystem)
            {
                var snapshot = await _audiobookRepository.GetByIdSnapshotAsync(
                    id,
                    cancellationToken);
                if (snapshot == null)
                {
                    return new NotFoundObjectResult(new { message = "Audiobook not found" });
                }

                // Cancellation is authoritative until the durable deletion intent is
                // about to be committed. From this point onward, either this request
                // or startup reconciliation must drive the intent to a terminal state.
                var mutationToken = RequestCancellationBoundary.EnterNonCancelablePhase(
                    cancellationToken);
                var intent = await _deletionIntentStore.GetOrCreateAsync(
                    id,
                    deleteFolder,
                    mutationToken);
                if (intent.State == AudiobookDeletionIntentState.Planned)
                {
                    try
                    {
                        filesystemResult = await _audiobookFilesystemDeleteService.DeleteAsync(
                            snapshot,
                            deleteFolder,
                            mutationToken);
                        if (!filesystemResult.TrackedFileCleanupComplete)
                        {
                            await _deletionIntentStore.RecordErrorAsync(
                                intent.Id,
                                "One or more tracked audiobook file generations remain unresolved after filesystem cleanup.",
                                CancellationToken.None);
                            return new ObjectResult(new
                            {
                                message = "One or more tracked audiobook files could not be deleted safely. The library row was preserved and the deletion can be retried.",
                                code = "delete_recovery_pending",
                                warnings = filesystemResult.Warnings
                            })
                            {
                                StatusCode = StatusCodes.Status500InternalServerError
                            };
                        }
                        await _deletionIntentStore.MarkFilesystemCleanupCompletedAsync(
                            intent.Id,
                            CancellationToken.None);
                    }
                    catch (Exception exception) when (exception is not (
                        OutOfMemoryException or StackOverflowException))
                    {
                        await _deletionIntentStore.RecordErrorAsync(
                            intent.Id,
                            "Filesystem cleanup failed during durable audiobook deletion.",
                            CancellationToken.None);
                        _logger.LogError(
                            exception,
                            "Durable filesystem cleanup failed for audiobook {AudiobookId}; the library row was preserved",
                            id);
                        return new ObjectResult(new
                        {
                            message = "Filesystem cleanup could not be completed safely. The deletion remains pending and can be retried.",
                            code = "delete_recovery_pending"
                        })
                        {
                            StatusCode = StatusCodes.Status500InternalServerError
                        };
                    }
                }
                else if (intent.State != AudiobookDeletionIntentState.FilesystemCleanupCompleted)
                {
                    throw new InvalidOperationException(
                        "The active audiobook deletion intent is not in a retryable state.");
                }

                var commit = await _deletionCommitService.DeleteAsync(
                    id,
                    includeFiles: false,
                    CancellationToken.None);
                if (commit.Outcome == AudiobookDeletionCommitOutcome.Failed)
                {
                    _logger.LogError(
                        "Filesystem cleanup completed for audiobook {AudiobookId}, but the database delete did not commit; startup recovery will retry it",
                        id);
                    return new ObjectResult(new
                    {
                        message = "Filesystem cleanup completed, but the library deletion has not committed yet and will be recovered on restart.",
                        code = "delete_recovery_pending"
                    })
                    {
                        StatusCode = StatusCodes.Status500InternalServerError
                    };
                }

                await _deletionIntentStore.MarkCompletedAsync(
                    intent.Id,
                    CancellationToken.None);
                audiobook = snapshot;
            }
            else
            {
                var commit = await _deletionCommitService.DeleteAsync(
                    id,
                    includeFiles: false,
                    cancellationToken);
                if (commit.Outcome == AudiobookDeletionCommitOutcome.NotFound)
                {
                    return new NotFoundObjectResult(new { message = "Audiobook not found" });
                }

                if (commit.Outcome != AudiobookDeletionCommitOutcome.Deleted
                    || commit.Audiobook == null)
                {
                    return new ObjectResult(new { message = "Failed to delete audiobook" })
                    {
                        StatusCode = StatusCodes.Status500InternalServerError
                    };
                }

                audiobook = commit.Audiobook;
            }

            await DeleteCachedImageAsync(audiobook);
            var message = filesystemResult?.BuildDeleteMessage() ?? "Audiobook deleted successfully.";
            return new OkObjectResult(new
            {
                message,
                id,
                deletedFiles = filesystemResult?.DeletedFiles ?? 0,
                deletedFolder = filesystemResult?.DeletedFolder,
                deletedParentFolder = filesystemResult?.DeletedParentFolder,
                warnings = filesystemResult?.Warnings ?? new List<string>()
            });
        }

        private async Task DeleteCachedImageAsync(Audiobook audiobook)
        {
            try
            {
                if (!string.IsNullOrEmpty(audiobook.Asin))
                {
                    await DeleteCachedImageByIdentifierAsync(audiobook.Asin, "ASIN");
                }
                else if (!string.IsNullOrEmpty(audiobook.ImageUrl))
                {
                    await DeleteCachedImageFromUrlAsync(audiobook);
                }
            }
            catch (Exception ex) when (ex is not (
                OutOfMemoryException or StackOverflowException))
            {
                _logger.LogWarning(ex, "Failed to delete cached image for audiobook id {Id}", audiobook.Id);
            }
        }

        private async Task DeleteCachedImageByIdentifierAsync(string identifier, string source)
        {
            var imagePath = await _imageCacheService.GetCachedImagePathAsync(identifier);
            if (imagePath == null)
            {
                return;
            }

            var fullPath = FileUtils.CombineWithOptionalBase(_contentRootPath, imagePath);
            if (_fileSystem.FileExists(fullPath))
            {
                if (!_fileSystem.TryValidateMutationTarget(fullPath, [_contentRootPath], out var safePath, out var reason))
                {
                    _logger.LogWarning(
                        "Blocked cached image delete for {Source} {Identifier}: {Reason}",
                        source,
                        LogRedaction.SanitizeText(identifier),
                        LogRedaction.SanitizeText(reason));
                    return;
                }

                _fileSystem.DeleteFile(safePath);
                _logger.LogInformation("Deleted cached image for {Source} {Identifier}", source, LogRedaction.SanitizeText(identifier));
            }
        }

        private async Task DeleteCachedImageFromUrlAsync(Audiobook audiobook)
        {
            try
            {
                const string marker = "/config/cache/images/library/";
                var url = audiobook.ImageUrl!;
                var idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    return;
                }

                var filename = url.Substring(idx + marker.Length);
                filename = Path.GetFileName(filename);
                var identifier = Path.GetFileNameWithoutExtension(filename);

                if (!string.IsNullOrEmpty(identifier) && Regex.IsMatch(identifier, "^[A-Za-z0-9_\\-\\.]{1,128}$"))
                {
                    await DeleteCachedImageByIdentifierAsync(identifier, "identifier (from ImageUrl)");
                }
                else
                {
                    _logger.LogWarning("Image identifier from ImageUrl for audiobook id {Id} is invalid: {Identifier}", audiobook.Id, LogRedaction.SanitizeText(identifier));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to delete cached image based on stored ImageUrl for audiobook id {Id}", audiobook.Id);
            }
        }
    }
}
