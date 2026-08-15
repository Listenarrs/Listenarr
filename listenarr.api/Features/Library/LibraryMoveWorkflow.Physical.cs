using Listenarr.Application.Common;
using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library;

public sealed partial class LibraryMoveWorkflow
{
    private async Task<IActionResult> EnqueuePhysicalAsync(
        int id,
        LibraryController.MoveRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var recovery = await _moveQueueService!.GetRecoveryStateForAudiobookAsync(
            id,
            cancellationToken);
        if (recovery.BlocksFilesystemMutation)
        {
            return MoveRecoveryConflict(recovery);
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var rootFolderService = scope.ServiceProvider.GetRequiredService<IRootFolderService>();
            var settings = await configService.GetApplicationSettingsAsync();
            var rootFolders = await rootFolderService.GetAllAsync();
            var directoryIdentityResolver = scope.ServiceProvider
                .GetRequiredService<IDirectoryObjectIdentityResolver>();
            cancellationToken.ThrowIfCancellationRequested();

            var allowedMoveRoots = new List<MoveRootBoundary>();
            // RootFolders are the authoritative managed-storage boundaries. OutputPath is
            // retained only as a legacy fallback for databases that have not configured
            // any root folders yet; otherwise stale cross-host OutputPath values must not
            // grant independent filesystem mutation authority.
            var normalizedOutputPath = rootFolders.Count == 0
                ? TryNormalizeMoveRoot(settings.OutputPath, "legacy configured output path")
                : null;
            await AddAllowedMoveRootAsync(
                allowedMoveRoots,
                normalizedOutputPath,
                FileSystemCaseSensitivityMode.Auto,
                directoryIdentityResolver,
                cancellationToken);

            string? defaultRootPath = null;
            foreach (var rootFolder in rootFolders)
            {
                var normalizedRootPath = TryNormalizeMoveRoot(
                    rootFolder.Path,
                    $"root folder {rootFolder.Id}");
                if (normalizedRootPath == null)
                {
                    continue;
                }

                await AddAllowedMoveRootAsync(
                    allowedMoveRoots,
                    normalizedRootPath,
                    rootFolder.CaseSensitivityMode,
                    directoryIdentityResolver,
                    cancellationToken,
                    rootFolder.DirectoryObjectIdentityVersion,
                    rootFolder.DirectoryObjectIdentity,
                    rootFolder.DirectoryObjectIdentityUnavailableReason,
                    RootFolderPathSemantics.ResolvePersisted(rootFolder),
                    isManagedRoot: true);
                if (rootFolder.IsDefault && defaultRootPath == null)
                {
                    defaultRootPath = normalizedRootPath;
                }
            }

            if (allowedMoveRoots.Count == 0)
            {
                return DestinationValidationResult(
                    "destination_path_outside_roots",
                    "DestinationPath must be inside a configured root folder or output path");
            }

            var destinationIsRooted = Path.IsPathRooted(request.DestinationPath!);
            var relativeMoveBase = normalizedOutputPath
                ?? defaultRootPath
                ?? allowedMoveRoots.FirstOrDefault()?.Path;
            if (!destinationIsRooted && string.IsNullOrEmpty(relativeMoveBase))
            {
                return DestinationValidationResult(
                    "destination_path_requires_root",
                    "DestinationPath requires a configured root folder or output path");
            }

            var destinationCandidate = destinationIsRooted
                ? request.DestinationPath!
                : FileUtils.CombineWithOptionalBase(relativeMoveBase, request.DestinationPath!);
            if (!FileUtils.TryNormalizeUserProvidedDirectoryPathForCurrentOs(
                    destinationCandidate,
                    out var final,
                    out var validationReason,
                    rejectParentTraversal: true))
            {
                return DestinationValidationResult(
                    "destination_path_invalid",
                    $"DestinationPath is invalid: {validationReason}",
                    destinationCandidate);
            }

            if (!_fileSystem.TryValidateMutationTarget(
                    final,
                    allowedMoveRoots.Select(root => root.Path),
                    out final,
                    out var finalReason))
            {
                _logger.LogWarning(
                    "Blocked move destination for audiobook {AudiobookId}: {Destination}. Reason: {Reason}",
                    id,
                    final,
                    finalReason);
                return DestinationValidationResult(
                    "destination_path_outside_roots",
                    "DestinationPath must be inside a configured root folder or output path",
                    final);
            }

            var targetBoundary = FindAllowedMoveRoot(final, allowedMoveRoots);

            if (targetBoundary == null)
            {
                return DestinationValidationResult(
                    "destination_filesystem_identity_unavailable",
                    "Destination filesystem identity is unavailable.",
                    final);
            }
            if (!targetBoundary.DirectoryIdentity.IsAvailable)
            {
                return DestinationValidationResult(
                    "destination_physical_identity_unavailable",
                    "Destination root physical identity is unavailable or changed.",
                    final);
            }

            var targetParent = Path.GetDirectoryName(final);
            if (string.IsNullOrEmpty(targetParent))
            {
                return DestinationValidationResult(
                    "destination_path_invalid",
                    "DestinationPath must identify a directory below a filesystem root.",
                    final);
            }

            var nearestTargetAncestor = TryFindNearestExistingDirectory(targetParent);
            var ancestorReason = "No existing target ancestor is available.";
            if (string.IsNullOrWhiteSpace(nearestTargetAncestor)
                || !_fileSystem.TryValidateMutationTarget(
                    final,
                    [nearestTargetAncestor],
                    out final,
                    out ancestorReason))
            {
                _logger.LogWarning(
                    "Blocked move destination for audiobook {AudiobookId}: {Destination}. Reason: {Reason}",
                    id,
                    final,
                    ancestorReason);
                return DestinationValidationResult(
                    "destination_parent_unavailable",
                    "Target parent path is unavailable",
                    final);
            }

            var targetIdentity = PathIdentitySnapshot.FromResolution(
                targetBoundary.Semantics,
                targetBoundary.CaseSensitivityMode,
                targetBoundary.Path,
                final);
            var deleteEmptySource = request.DeleteEmptySource ?? true;
            var jobId = await _audiobookOperationCoordinator.ExecuteExclusiveAsync(
                id,
                async lockedToken =>
                {
                    var recovery = await _moveQueueService!.GetRecoveryStateForAudiobookAsync(
                        id,
                        lockedToken);
                    if (recovery.BlocksFilesystemMutation)
                    {
                        throw new MoveRecoveryConflictException(recovery);
                    }

                    using var authoritativeScope = _scopeFactory.CreateScope();
                    var authoritativeRepository = authoritativeScope.ServiceProvider
                        .GetRequiredService<IAudiobookRepository>();
                    var manifestService = authoritativeScope.ServiceProvider
                        .GetRequiredService<IMoveSourcePlanService>();
                    var currentAudiobook = await authoritativeRepository
                        .GetPathReferenceSnapshotAsync(id, lockedToken)
                        ?? throw new ApplicationNotFoundException(
                            "audiobook_not_found",
                            "Audiobook not found");
                    var manifest = await manifestService.BuildPlanAsync(
                        currentAudiobook,
                        lockedToken);

                    if (!string.IsNullOrWhiteSpace(request.SourcePath))
                    {
                        if (!FileUtils.TryNormalizeUserProvidedDirectoryPathForCurrentOs(
                                request.SourcePath,
                                out var expectedSourceFull,
                                out var expectedSourceReason,
                                rejectParentTraversal: true))
                        {
                            throw new ApplicationValidationException(
                                "source_path_invalid",
                                $"SourcePath is invalid: {expectedSourceReason}");
                        }

                        if (string.IsNullOrWhiteSpace(currentAudiobook.BasePath)
                            || !SourceStateMatches(
                                currentAudiobook.BasePath,
                                expectedSourceFull,
                                manifest.SourceIdentity.Semantics))
                        {
                            throw new ApplicationConflictException(
                                "source_path_changed",
                                "The audiobook source path changed. Refresh and try again.");
                        }
                    }

                    if (AreSameMoveEndpoint(
                            manifest.SourceRoot,
                            manifest.SourceIdentity,
                            final,
                            targetIdentity))
                    {
                        throw new ApplicationValidationException(
                            "identical_move_endpoint",
                            "Source and target paths are identical; nothing to move.");
                    }

                    string? sourceCleanupBoundary = null;
                    if (deleteEmptySource)
                    {
                        var cleanupBoundary = await _cleanupBoundaryResolver.ResolveAsync(
                            manifest.SourceRoot,
                            final,
                            rootFolders,
                            cancellationToken: lockedToken);
                        sourceCleanupBoundary = cleanupBoundary.Boundary;
                        if (!cleanupBoundary.IsAvailable)
                        {
                            _logger.LogWarning(
                                "Move for audiobook {AudiobookId} has no safe source cleanup boundary: {Reason}",
                                id,
                                cleanupBoundary.Reason ?? "boundary unavailable");
                        }
                        else
                        {
                            _logger.LogInformation(
                                "Resolved {BoundaryKind} source cleanup boundary {Boundary} for audiobook {AudiobookId}",
                                cleanupBoundary.Kind,
                                LogRedaction.SanitizeFilePath(sourceCleanupBoundary),
                                id);
                        }
                    }

                    return await _moveQueueService!.EnqueueMoveAsync(
                        new MoveEnqueueCommand(
                            id,
                            manifest.SourceRoot,
                            manifest.SourceIdentity,
                            manifest.Entries,
                            final,
                            targetIdentity,
                            targetBoundary.DirectoryIdentity.Version!.Value,
                            targetBoundary.DirectoryIdentity.Value!,
                            deleteEmptySource,
                            sourceCleanupBoundary),
                        lockedToken);
                },
                cancellationToken);

            return new AcceptedResult(
                string.Empty,
                new MoveEnqueuedResponse("Move enqueued", jobId, final));
        }
        catch (MoveRecoveryConflictException exception)
        {
            return MoveRecoveryConflict(exception.Recovery);
        }
        catch (PersistenceException ex)
        {
            _logger.LogError(
                ex,
                "Move queue persistence failed while enqueueing move job for audiobook {AudiobookId}",
                id);
            return MoveQueuePersistenceUnavailableResult();
        }
        catch (MoveRelocationConflictException ex)
        {
            _logger.LogWarning(
                ex,
                "Blocked move for audiobook {AudiobookId} during an active root folder relocation",
                id);
            return new ConflictObjectResult(new
            {
                message = "The move overlaps an active root folder relocation. Retry after the relocation completes.",
                code = "move_relocation_conflict"
            });
        }
        catch (ListenarrApplicationException ex)
        {
            return ToApplicationExceptionResult(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            && ex is not OutOfMemoryException
            && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Failed to enqueue move job for audiobook {AudiobookId}", id);
            return new ObjectResult(new
            {
                message = "Failed to enqueue move job",
                code = "move_enqueue_failed"
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }
}
