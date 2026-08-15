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
                return pathResolution.ErrorResult;
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

            if (!pathResolution.PathIdentity.HasValue
                || !pathResolution.PhysicalIdentity.HasValue)
            {
                return new ObjectResult(new
                {
                    message = "Scan path identity is unavailable"
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
                pathResolution.PhysicalIdentity,
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
                        pathResolution.PhysicalIdentity.Value,
                        AllowReconciliation: true,
                        IsAuthoritativeScope: isAuthoritative,
                        Source: "Manual Scan"),
                    cancellationToken);

                await SendAvailableNotificationAsync(
                    audiobook,
                    result.CreatedCount,
                    result.Audiobook);
                return new OkObjectResult(new
                {
                    message = "Scan complete",
                    scannedPath = scanRoot,
                    found = result.AttributedFiles.Count,
                    created = result.CreatedCount,
                    complete = result.IsComplete,
                    reconciliationPerformed = result.ReconciliationPerformed,
                    diagnostics = result.Diagnostics,
                    audiobook = result.Audiobook
                });
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
                return new ObjectResult(new
                {
                    message = "The scan could not be completed safely. Review the server logs for details."
                })
                {
                    StatusCode = StatusCodes.Status409Conflict
                };
            }
        }

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
                using var notificationScope = _scopeFactory.CreateScope();
                var configService = notificationScope.ServiceProvider
                    .GetRequiredService<IConfigurationService>();
                var settings = await configService.GetApplicationSettingsAsync();
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
                    "book-available",
                    availableData,
                    settings.WebhookUrl,
                    settings.EnabledNotificationTriggers);
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
