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

using System.ComponentModel;
using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public sealed class LibraryManualScanWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly INotificationService? _notificationService;
        private readonly LibraryScanPathResolver _scanPathResolver;
        private readonly LibraryScanQueueWorkflow _scanQueueWorkflow;
        private readonly IFileSystem _fileSystem;
        private readonly IFilesystemMutationCoordinator _filesystemMutationCoordinator;
        private readonly IAudiobookOperationCoordinator _audiobookOperationCoordinator;
        private readonly IMoveQueueService _moveQueueService;
        private readonly ILogger<LibraryManualScanWorkflow> _logger;

        public LibraryManualScanWorkflow(
            IAudiobookRepository repo,
            IServiceScopeFactory scopeFactory,
            LibraryScanPathResolver scanPathResolver,
            LibraryScanQueueWorkflow scanQueueWorkflow,
            IFileSystem fileSystem,
            IFilesystemMutationCoordinator filesystemMutationCoordinator,
            IAudiobookOperationCoordinator audiobookOperationCoordinator,
            IMoveQueueService moveQueueService,
            ILogger<LibraryManualScanWorkflow> logger,
            INotificationService? notificationService = null)
        {
            _repo = repo;
            _scopeFactory = scopeFactory;
            _scanPathResolver = scanPathResolver;
            _scanQueueWorkflow = scanQueueWorkflow;
            _fileSystem = fileSystem;
            _filesystemMutationCoordinator = filesystemMutationCoordinator
                ?? throw new ArgumentNullException(nameof(filesystemMutationCoordinator));
            _audiobookOperationCoordinator = audiobookOperationCoordinator
                ?? throw new ArgumentNullException(nameof(audiobookOperationCoordinator));
            _moveQueueService = moveQueueService ?? throw new ArgumentNullException(nameof(moveQueueService));
            _logger = logger;
            _notificationService = notificationService;
        }

        public Task<IActionResult> ScanAsync(
            int id,
            LibraryController.ScanRequest? request,
            CancellationToken cancellationToken = default) =>
            _filesystemMutationCoordinator.ExecuteExclusiveAsync(
                globalToken => _audiobookOperationCoordinator.ExecuteExclusiveAsync(
                    id,
                    token => ScanCoreAsync(id, request, token),
                    globalToken),
                cancellationToken);

        private async Task<IActionResult> ScanCoreAsync(
            int id,
            LibraryController.ScanRequest? request,
            CancellationToken cancellationToken)
        {
            try
            {
                await _moveQueueService.EnsureFilesystemMutationAllowedAsync(
                    id,
                    cancellationToken);
            }
            catch (ApplicationConflictException exception)
            {
                return new ConflictObjectResult(new
                {
                    message = exception.SafeDetail,
                    code = exception.Code
                });
            }

            var audiobook = await _repo.GetForScanSnapshotAsync(
                id,
                cancellationToken);
            if (audiobook == null)
            {
                return new NotFoundObjectResult(new
                {
                    message = "Audiobook not found"
                });
            }

            var pathResolution = await _scanPathResolver.ResolveAsync(
                audiobook,
                request?.Path,
                cancellationToken);
            if (pathResolution.ErrorResult != null)
            {
                return IsMissingBasePathCandidate(audiobook, request, pathResolution)
                    ? await ReconcileMissingBasePathAsync(audiobook, cancellationToken)
                        ?? pathResolution.ErrorResult
                    : pathResolution.ErrorResult;
            }

            var scanRoot = pathResolution.ScanRoot;
            if (string.IsNullOrEmpty(scanRoot)
                || !_fileSystem.DirectoryExists(scanRoot))
            {
                return new BadRequestObjectResult(new
                {
                    message = "Scan path not provided or does not exist",
                    path = scanRoot
                });
            }

            if (!pathResolution.PathIdentity.HasValue)
            {
                return new ObjectResult(new
                {
                    message = "Scan path authority is unavailable"
                })
                {
                    StatusCode = StatusCodes.Status409Conflict
                };
            }

            var isAuthoritative = IsAuthoritativeScope(
                audiobook.BasePath,
                scanRoot,
                pathResolution.PathIdentity.Value.Semantics);
            cancellationToken.ThrowIfCancellationRequested();
            var queuedResult = await _scanQueueWorkflow.TryEnqueueAsync(
                audiobook,
                scanRoot,
                pathResolution.PathIdentity,
                isAuthoritative);
            if (queuedResult != null)
            {
                return queuedResult;
            }

            _logger.LogInformation(
                "Scanning for audiobook files for '{Title}' under: {Path}",
                LogRedaction.SanitizeText(audiobook.Title),
                LogRedaction.SanitizeFilePath(scanRoot));
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var scanService = scope.ServiceProvider
                    .GetRequiredService<IAudiobookScanService>();
                var result = await scanService.ScanAsync(
                    new AudiobookScanCommand(
                        audiobook.Id,
                        scanRoot,
                        pathResolution.PathIdentity.Value,
                        AllowReconciliation: true,
                        IsAuthoritativeScope: isAuthoritative,
                        Source: "Manual Scan"),
                    cancellationToken);

                await SendAvailableNotificationAsync(
                    audiobook,
                    result.CreatedCount,
                    result.Audiobook);
                return ScanComplete(scanRoot, result);
            }
            catch (DirectoryNotFoundException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Manual scan path was unavailable for audiobook {AudiobookId}",
                    audiobook.Id);
                return new BadRequestObjectResult(new
                {
                    message = "The scan path does not exist or is unavailable.",
                    path = scanRoot
                });
            }
            catch (InvalidOperationException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Manual scan rejected for audiobook {AudiobookId}",
                    audiobook.Id);
                return ScanRejected();
            }
        }

        // A default scan of a book whose folder is gone cannot pin that folder.
        // It then only removes the rows of files that were inside it, and only
        // while the folder's root still exists.
        private bool IsMissingBasePathCandidate(
            Audiobook audiobook,
            LibraryController.ScanRequest? request,
            LibraryScanPathResolution pathResolution) =>
            string.IsNullOrWhiteSpace(request?.Path)
            && pathResolution.AuthorizationFailure
                == ScanPathAuthorizationFailure.IdentityUnavailable
            && !string.IsNullOrWhiteSpace(audiobook.BasePath)
            && !_fileSystem.DirectoryExists(audiobook.BasePath);

        private async Task<IActionResult?> ReconcileMissingBasePathAsync(
            Audiobook audiobook,
            CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var scanService = scope.ServiceProvider
                    .GetRequiredService<IAudiobookScanService>();
                var result = await scanService.ReconcileMissingBasePathAsync(
                    audiobook.Id,
                    "Manual Scan",
                    cancellationToken);
                if (result == null)
                {
                    return null;
                }

                _logger.LogInformation(
                    "Removed {Count} tracked file rows for '{Title}' because its BasePath no longer exists",
                    result.RemovedFiles.Count,
                    LogRedaction.SanitizeText(audiobook.Title));
                return ScanComplete(audiobook.BasePath!, result);
            }
            catch (Exception exception) when (exception is
                IOException or Win32Exception or UnauthorizedAccessException)
            {
                // Includes DirectoryNotFoundException. An unreadable or vanished
                // parent is not proof of anything; keep the original refusal.
                _logger.LogWarning(
                    exception,
                    "The parent of a missing BasePath became unavailable for audiobook {AudiobookId}",
                    audiobook.Id);
                return null;
            }
            catch (InvalidOperationException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Missing BasePath reconciliation rejected for audiobook {AudiobookId}",
                    audiobook.Id);
                return ScanRejected();
            }
        }

        private static OkObjectResult ScanComplete(
            string scannedPath,
            AudiobookScanResult result) =>
            new(new
            {
                message = "Scan complete",
                scannedPath,
                found = result.AttributedFiles.Count,
                created = result.CreatedCount,
                complete = result.IsComplete,
                reconciliationPerformed = result.ReconciliationPerformed,
                diagnostics = result.Diagnostics,
                audiobook = result.Audiobook
            });

        private static ObjectResult ScanRejected() =>
            new(new
            {
                message = "The scan could not be completed safely. Review the server logs for details."
            })
            {
                StatusCode = StatusCodes.Status409Conflict
            };

        private static bool IsAuthoritativeScope(
            string? existingBasePath,
            string scanRoot,
            FileSystemPathSemantics semantics)
        {
            if (string.IsNullOrWhiteSpace(existingBasePath)
                || FileSystemPathIdentity.AreEquivalent(
                    existingBasePath,
                    scanRoot,
                    semantics))
            {
                return true;
            }

            return FileSystemPathIdentity.IsSameOrInside(
                existingBasePath,
                scanRoot,
                semantics);
        }

        private async Task SendAvailableNotificationAsync(
            Audiobook audiobook,
            int createdCount,
            Audiobook? updated)
        {
            if (_notificationService == null
                || !audiobook.Monitored
                || createdCount <= 0)
            {
                return;
            }

            try
            {
                var availableData = new
                {
                    id = audiobook.Id,
                    title = audiobook.Title ?? "Unknown Title",
                    authors = audiobook.Authors,
                    asin = audiobook.Asin,
                    imageUrl = audiobook.ImageUrl,
                    description = audiobook.Description,
                    monitored = audiobook.Monitored,
                    qualityProfileId = audiobook.QualityProfileId,
                    filesImported = createdCount,
                    totalFiles = updated?.Files?.Count ?? 0
                };
                await _notificationService.SendNotificationAsync(
                    NotificationTriggers.BookAvailable,
                    availableData);
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                && exception is not OutOfMemoryException
                && exception is not StackOverflowException)
            {
                _logger.LogWarning(
                    exception,
                    "Failed to send book-available notification for audiobook {AudiobookId}",
                    audiobook.Id);
            }
        }
    }
}
