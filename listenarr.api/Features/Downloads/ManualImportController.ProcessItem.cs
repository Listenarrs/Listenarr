using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Downloads;

public partial class ManualImportController
{
    private async Task<ManualImportResultDto> ImportFileAsync(
        ManualImportItemDto item,
        FileAction action,
        string sourceDirectory,
        FileSystemPathSemantics sourceSemantics,
        ManualImportDestinationTracker destinationTracker,
        IDictionary<int, string> planningBasePaths,
        List<RootFolder> rootFolders,
        ApplicationSettings settings,
        bool hasMultipleFile,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.FullPath))
            {
                return ManualImportResultDto.FailureResult(
                    "FullPath is required",
                    item.FullPath);
            }

            var audiobook = await _audiobookRepository.GetByIdAsync(
                item.MatchedAudiobookId);
            if (audiobook == null)
            {
                return ManualImportResultDto.FailureResult(
                    $"Audiobook with ID {item.MatchedAudiobookId} not found",
                    item.FullPath);
            }

            if (!_fileSystem.FileExists(item.FullPath))
            {
                return ManualImportResultDto.FailureResult(
                    "Source file not found",
                    item.FullPath);
            }

            var isUnderSourceDirectory = FileSystemPathIdentity.IsSameOrInside(
                item.FullPath,
                sourceDirectory,
                sourceSemantics);
            var isUnderConfiguredRoot = await IsInsideAnyConfiguredRootAsync(
                item.FullPath,
                rootFolders,
                cancellationToken);
            if (!isUnderSourceDirectory && !isUnderConfiguredRoot)
            {
                _logger.LogWarning(
                    "Rejected manual import: {Path} is not within the requested path or a configured root folder",
                    item.FullPath);
                return ManualImportResultDto.FailureResult(
                    "Source file is not within the requested import path or a configured root folder",
                    item.FullPath);
            }

            if (action == FileAction.None)
            {
                return ManualImportResultDto.SkippedResult(
                    "No file action was requested.",
                    item.FullPath,
                    audiobook);
            }

            if (!TryResolveManagedDestinationBasePath(
                    audiobook,
                    rootFolders,
                    settings,
                    out var resolvedManagedBasePath,
                    out var allowedDestinationRoots,
                    out var managedBaseReason))
            {
                _logger.LogWarning(
                    "Blocked manual import because audiobook {AudiobookId} has no managed destination: {Reason}",
                    audiobook.Id,
                    LogRedaction.SanitizeText(managedBaseReason));
                return ManualImportResultDto.FailureResult(
                    "The audiobook destination is outside configured roots.",
                    item.FullPath);
            }

            if (!planningBasePaths.TryGetValue(audiobook.Id, out var managedBasePath))
            {
                managedBasePath = resolvedManagedBasePath;
                planningBasePaths.Add(audiobook.Id, managedBasePath);
            }

            var metadata = await _metadataService.ExtractFileMetadataAsync(
                item.FullPath);
            if (metadata == null)
            {
                return ManualImportResultDto.FailureResult(
                    "Failed to extract metadata from file",
                    item.FullPath);
            }

            var destinationResolution = await ResolveDestinationResolutionAsync(
                managedBasePath,
                cancellationToken);
            var destinationSemantics = destinationResolution.Semantics;
            var pathPlan = await _pathPlanner.GeneratePathAsync(
                audiobook,
                metadata,
                item,
                managedBasePath,
                rootFolders,
                settings,
                destinationSemantics,
                hasMultipleFile);
            var destinationPath = pathPlan.DestinationPath;
            if (!_fileSystem.TryValidateMutationTarget(
                    destinationPath,
                    allowedDestinationRoots,
                    out destinationPath,
                    out var destinationReason))
            {
                _logger.LogWarning(
                    "Blocked manual import destination for audiobook {AudiobookId}: {Reason}",
                    audiobook.Id,
                    LogRedaction.SanitizeText(destinationReason));
                return ManualImportResultDto.FailureResult(
                    "The generated destination is outside configured roots.",
                    item.FullPath);
            }

            var destinationReservation =
                await destinationTracker.PlanIdempotentOrUniqueAsync(
                    item.FullPath,
                    destinationPath,
                    cancellationToken);
            destinationPath = destinationReservation.Path;
            var authoritativeBasePath = pathPlan.AudiobookBasePath;
            if (string.IsNullOrWhiteSpace(authoritativeBasePath))
            {
                return ManualImportResultDto.FailureResult(
                    "The generated destination has no managed parent directory.",
                    item.FullPath);
            }

            var ownership = await _audiobookFileService
                .CheckAudiobookFileOwnershipAsync(
                    audiobook,
                    destinationPath,
                    authoritativeBasePath,
                    cancellationToken);
            if (ownership.Outcome is not (
                    AudiobookFileOwnershipCheckOutcome.Available or
                    AudiobookFileOwnershipCheckOutcome.AlreadyOwnedByAudiobook))
            {
                _logger.LogWarning(
                    "Blocked manual import because destination ownership is unavailable. Audiobook {AudiobookId}, Source {Source}, Destination {Destination}, Outcome {Outcome}, Reason {Reason}",
                    audiobook.Id,
                    item.FullPath,
                    destinationPath,
                    ownership.Outcome,
                    ownership.Reason);
                var publicError = ownership.Outcome switch
                {
                    AudiobookFileOwnershipCheckOutcome.OwnedByOtherAudiobook =>
                        "The destination file is owned by another audiobook.",
                    AudiobookFileOwnershipCheckOutcome.IdentityConflict =>
                        "The destination file conflicts with existing ownership data.",
                    _ => "Destination ownership is unavailable."
                };
                return new ManualImportResultDto
                {
                    Success = false,
                    Error = publicError,
                    SourcePath = item.FullPath,
                    DestinationPath = destinationPath,
                    Audiobook = audiobook
                };
            }

            var operationId = FileMoveOperationIdentity.CreateForPaths(
                "manual-import",
                audiobook.Id,
                action,
                item.FullPath,
                sourceSemantics,
                destinationPath,
                destinationSemantics);
            using (var registrationLease =
                await PrepareOwnedManualImportActionForRegistrationAsync(
                    action,
                    item.FullPath,
                    destinationPath,
                    audiobook,
                    rootFolders,
                    destinationSemantics,
                    destinationResolution.BoundaryPath,
                    operationId,
                    ownership.ExistingFile?.PhysicalObjectIdentity,
                    cancellationToken))
            {
                if (registrationLease == null
                    || !await RegisterPublishedManualImportAsync(
                        audiobook,
                        ownership,
                        registrationLease,
                        authoritativeBasePath,
                        cancellationToken))
                {
                    return new ManualImportResultDto
                    {
                        Success = false,
                        Error = "The file could not be published and registered safely.",
                        SourcePath = item.FullPath,
                        DestinationPath = destinationPath,
                        Audiobook = audiobook
                    };
                }

                if (action == FileAction.Move
                    && !await _fileMover.CompletePreparedMoveAsync(
                        item.FullPath,
                        destinationPath,
                        registrationLease,
                        operationId))
                {
                    await _audiobookFileService
                        .RollbackPublishedGenerationIfStaleAsync(
                            audiobook,
                            registrationLease);
                    return new ManualImportResultDto
                    {
                        Success = false,
                        Error = "The file could not be published and registered safely.",
                        SourcePath = item.FullPath,
                        DestinationPath = destinationPath,
                        Audiobook = audiobook
                    };
                }

                if (!string.IsNullOrWhiteSpace(audiobook.Asin))
                {
                    try
                    {
                        await _metadataService.WriteAsinTagAsync(
                            registrationLease,
                            audiobook.Asin);
                    }
                    catch (Exception exception) when (exception is not (
                        OutOfMemoryException or StackOverflowException))
                    {
                        _logger.LogWarning(
                            exception,
                            "Manual import completed, but generation-bound ASIN tag enrichment failed for audiobook {AudiobookId} at {Path}",
                            audiobook.Id,
                            LogRedaction.SanitizeFilePath(destinationPath));
                    }
                }

                var completion = registrationLease.CompletePublication();
                if (completion
                    == RegistrationPublicationCompletion.CommittedCleanupPending)
                {
                    _logger.LogWarning(
                        "Manual import committed for audiobook {AudiobookId}, but registration-publication cleanup remains pending for {Path}",
                        audiobook.Id,
                        LogRedaction.SanitizeFilePath(destinationPath));
                }
            }

            destinationTracker.Commit(destinationReservation);

            return new ManualImportResultDto
            {
                Success = true,
                SourcePath = item.FullPath,
                DestinationPath = destinationPath,
                Audiobook = audiobook
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            && ex is not OutOfMemoryException
            && ex is not StackOverflowException)
        {
            _logger.LogError(
                ex,
                "Error importing file {FilePath}",
                item.FullPath);
            return ManualImportResultDto.FailureResult(
                "Failed to import file.",
                item.FullPath);
        }
    }
}
