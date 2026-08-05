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
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public sealed partial class LibraryBulkEditWorkflow
    {
        private readonly IImageCacheService _imageCacheService;
        private readonly IHistoryRepository _historyRepository;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IFileNamingService _fileNamingService;
        private readonly string _contentRootPath;
        private readonly IFileSystem _fileSystem;
        private readonly IAudiobookDestinationRewriteService _destinationRewriteService;
        private readonly IFilesystemMutationCoordinator _filesystemMutationCoordinator;
        private readonly IAudiobookOperationCoordinator _audiobookOperationCoordinator;
        private readonly IMoveQueueService _moveQueueService;
        private readonly LibraryMoveWorkflow _moveWorkflow;
        private readonly ILogger<LibraryBulkEditWorkflow> _logger;

        public LibraryBulkEditWorkflow(
            IImageCacheService imageCacheService,
            IHistoryRepository historyRepository,
            IServiceScopeFactory scopeFactory,
            IFileNamingService fileNamingService,
            IApplicationPathService applicationPathService,
            IFileSystem fileSystem,
            IAudiobookDestinationRewriteService destinationRewriteService,
            IFilesystemMutationCoordinator filesystemMutationCoordinator,
            IAudiobookOperationCoordinator audiobookOperationCoordinator,
            IMoveQueueService moveQueueService,
            LibraryMoveWorkflow moveWorkflow,
            ILogger<LibraryBulkEditWorkflow> logger)
        {
            _imageCacheService = imageCacheService;
            _historyRepository = historyRepository;
            _scopeFactory = scopeFactory;
            _fileNamingService = fileNamingService;
            _contentRootPath = applicationPathService.ContentRootPath;
            _fileSystem = fileSystem;
            _destinationRewriteService = destinationRewriteService ?? throw new ArgumentNullException(nameof(destinationRewriteService));
            _filesystemMutationCoordinator = filesystemMutationCoordinator ?? throw new ArgumentNullException(nameof(filesystemMutationCoordinator));
            _audiobookOperationCoordinator = audiobookOperationCoordinator ?? throw new ArgumentNullException(nameof(audiobookOperationCoordinator));
            _moveQueueService = moveQueueService ?? throw new ArgumentNullException(nameof(moveQueueService));
            _moveWorkflow = moveWorkflow ?? throw new ArgumentNullException(nameof(moveWorkflow));
            _logger = logger;
        }

        public async Task<IActionResult> BulkUpdateAsync(LibraryController.BulkUpdateRequest request)
        {
            if (request?.Ids == null || !request.Ids.Any())
            {
                return new BadRequestObjectResult(new { message = "No audiobook IDs provided for bulk update" });
            }

            var results = new List<object>();
            var settings = await TryLoadApplicationSettingsAsync();
            var pathChangeMode = request.PathChange?.Mode
                ?? LibraryController.BulkPathChangeMode.None;
            if (!Enum.IsDefined(pathChangeMode))
            {
                return new BadRequestObjectResult(new
                {
                    message = "Invalid path change mode"
                });
            }

            var metadataUpdates = new Dictionary<string, object>(
                request.Updates ?? [],
                StringComparer.OrdinalIgnoreCase);
            metadataUpdates.Remove("moveFiles");
            metadataUpdates.Remove("deleteEmptySource");
            if (pathChangeMode == LibraryController.BulkPathChangeMode.Physical)
            {
                metadataUpdates.Remove("rootFolder");
            }

            foreach (var id in request.Ids.Distinct())
            {
                var physicalPlan = pathChangeMode == LibraryController.BulkPathChangeMode.Physical
                    ? await PlanPhysicalPathChangeAsync(
                        id,
                        request.PathChange?.DestinationRootOrPath,
                        settings)
                    : PhysicalPathChangePlan.NotRequested;
                var rootRewrite = pathChangeMode switch
                {
                    LibraryController.BulkPathChangeMode.Physical =>
                        new RootFolderRewriteOutcome(false, null, null),
                    LibraryController.BulkPathChangeMode.MetadataOnly =>
                        await RewriteRootFolderIfRequestedAsync(
                            id,
                            metadataUpdates,
                            settings,
                            request.PathChange?.DestinationRootOrPath),
                    _ => await RewriteRootFolderIfRequestedAsync(
                        id,
                        metadataUpdates,
                        settings)
                };
                BulkUpdateOutcome outcome;
                try
                {
                    outcome = await _audiobookOperationCoordinator.ExecuteExclusiveAsync(
                        id,
                        async token =>
                        {
                            if (pathChangeMode == LibraryController.BulkPathChangeMode.Physical)
                            {
                                await _moveQueueService.EnsureFilesystemMutationAllowedAsync(id, token);
                            }

                            return await UpdateOneAsync(
                                id,
                                metadataUpdates,
                                rootRewrite.Rewritten,
                                pathChangeMode == LibraryController.BulkPathChangeMode.Physical);
                        });
                }
                catch (ApplicationConflictException exception)
                {
                    outcome = new BulkUpdateOutcome(
                        Success: false,
                        MetadataUpdated: false,
                        Errors: [exception.SafeDetail]);
                }
                var errors = outcome.Errors
                    .Concat(rootRewrite.Error == null ? [] : [rootRewrite.Error])
                    .Concat(physicalPlan.Error == null ? [] : [physicalPlan.Error])
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var success = outcome.Success
                    && (pathChangeMode != LibraryController.BulkPathChangeMode.MetadataOnly
                        || rootRewrite.Error == null);
                Guid? moveJobId = null;
                var resolvedDestination = pathChangeMode == LibraryController.BulkPathChangeMode.MetadataOnly
                    ? rootRewrite.Destination
                    : physicalPlan.Destination;
                var pathChangeOutcome = pathChangeMode switch
                {
                    LibraryController.BulkPathChangeMode.Physical => "not-enqueued",
                    LibraryController.BulkPathChangeMode.MetadataOnly when rootRewrite.Rewritten => "metadata-updated",
                    LibraryController.BulkPathChangeMode.MetadataOnly => "failed",
                    _ => "none"
                };

                if (pathChangeMode == LibraryController.BulkPathChangeMode.Physical)
                {
                    if (physicalPlan.Error != null || string.IsNullOrWhiteSpace(physicalPlan.Destination))
                    {
                        success = false;
                    }
                    else if (outcome.Success)
                    {
                        var enqueueResult = await _moveWorkflow.EnqueueAsync(
                            id,
                            new LibraryController.MoveRequest
                            {
                                DestinationPath = physicalPlan.Destination,
                                MoveFiles = true,
                                DeleteEmptySource = request.PathChange?.DeleteEmptySource ?? true
                            });
                        if (enqueueResult is AcceptedResult
                            {
                                Value: MoveEnqueuedResponse enqueued
                            })
                        {
                            if (enqueued.JobId == Guid.Empty)
                            {
                                success = false;
                                pathChangeOutcome = "failed";
                                errors.Add("The server did not return a durable move job ID.");
                            }
                            else
                            {
                                moveJobId = enqueued.JobId;
                                resolvedDestination = enqueued.Target;
                                pathChangeOutcome = "enqueued";
                            }
                        }
                        else
                        {
                            success = false;
                            pathChangeOutcome = "failed";
                            errors.Add(GetActionResultError(enqueueResult));
                        }
                    }
                }

                results.Add(new
                {
                    id,
                    success,
                    metadataUpdated = outcome.MetadataUpdated,
                    pathChangeOutcome,
                    moveJobId,
                    resolvedDestination,
                    errors = errors.Distinct(StringComparer.Ordinal).ToList()
                });
            }

            return new OkObjectResult(new { message = "Bulk update completed", results });
        }

    }
}
