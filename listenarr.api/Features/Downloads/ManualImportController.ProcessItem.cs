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
        IDictionary<int, FileSystemSemanticsResolution> planningDestinationResolutions,
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
                if (string.IsNullOrWhiteSpace(audiobook.BasePath))
                {
                    return ManualImportResultDto.FailureResult(
                        "The audiobook has no existing library folder to register this file in place.",
                        item.FullPath);
                }

                if (!FileSystemPathIdentity.StoredPathMayIdentifySamePath(
                        audiobook.BasePath,
                        sourceDirectory,
                        sourceSemantics))
                {
                    return ManualImportResultDto.FailureResult(
                        "The selected existing-file folder does not match the audiobook library folder.",
                        item.FullPath);
                }

                var registered = await _audiobookScanService.RegisterExistingFileAsync(
                    audiobook.Id,
                    audiobook.BasePath,
                    item.FullPath,
                    "manual-import",
                    cancellationToken);
                return registered
                    ? new ManualImportResultDto
                    {
                        Success = true,
                        SourcePath = item.FullPath,
                        DestinationPath = item.FullPath,
                        Audiobook = audiobook
                    }
                    : new ManualImportResultDto
                    {
                        Success = false,
                        Error = "The existing file could not be registered safely in place.",
                        SourcePath = item.FullPath,
                        DestinationPath = item.FullPath,
                        Audiobook = audiobook
                    };
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

            var sourceCapability = await _filePublicationSourceCapability.CheckAsync(
                item.FullPath,
                cancellationToken);
            if (!sourceCapability.IsSupported
                || !sourceCapability.SourceProof.HasValue)
            {
                _logger.LogWarning(
                    "Blocked manual import before metadata or destination planning because source publication capability is unavailable for {Source}: {Reason}",
                    LogRedaction.SanitizeFilePath(item.FullPath),
                    LogRedaction.SanitizeText(sourceCapability.Reason));
                return ManualImportResultDto.FailureResult(
                    "The file could not be published and registered safely.",
                    item.FullPath);
            }
            var sourceProof = sourceCapability.SourceProof.Value;

            var metadata = await _metadataService.ExtractFileMetadataAsync(
                item.FullPath);
            if (metadata == null)
            {
                return ManualImportResultDto.FailureResult(
                    "Failed to extract metadata from file",
                    item.FullPath);
            }

            if (!planningDestinationResolutions.TryGetValue(
                    audiobook.Id,
                    out var destinationResolution))
            {
                destinationResolution = await ResolveDestinationResolutionAsync(
                    managedBasePath,
                    rootFolders,
                    cancellationToken);
                planningDestinationResolutions.Add(
                    audiobook.Id,
                    destinationResolution);
            }
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

            var requestedDestinationPath = destinationPath;
            ManualImportDestinationReservation destinationReservation;
            AudiobookFileOwnershipCheckResult ownership;
            while (true)
            {
                destinationReservation =
                    await destinationTracker.PlanIdempotentOrUniqueAsync(
                        sourceProof,
                        requestedDestinationPath,
                        destinationResolution,
                        cancellationToken);
                destinationPath = destinationReservation.Path;
                ownership = await _audiobookFileService
                    .CheckAudiobookFileOwnershipAsync(
                        audiobook,
                        destinationPath,
                        pathPlan.AudiobookBasePath,
                        cancellationToken);
                if (!destinationReservation.ReusesExistingFile
                    || sourceProof.HasDurablePhysicalObjectIdentity
                    || ownership.Outcome
                        != AudiobookFileOwnershipCheckOutcome.Available)
                {
                    break;
                }

                // Byte equality is not ownership. Exclude an unowned existing
                // pathname and continue planning a new no-overwrite destination.
                destinationTracker.Commit(destinationReservation);
            }
            var authoritativeBasePath = pathPlan.AudiobookBasePath;
            if (string.IsNullOrWhiteSpace(authoritativeBasePath))
            {
                return ManualImportResultDto.FailureResult(
                    "The generated destination has no managed parent directory.",
                    item.FullPath);
            }

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

            var publicationPlan = _filePublicationCapabilityResolver == null
                ? sourceProof.HasDurablePhysicalObjectIdentity
                    ? FilePublicationPlan.Durable(action)
                    : FilePublicationPlan.Additive(action)
                : await _filePublicationCapabilityResolver.ResolveAsync(
                    action,
                    item.FullPath,
                    destinationPath,
                    sourceProof,
                    cancellationToken);
            if (!publicationPlan.IsAllowed)
            {
                return new ManualImportResultDto
                {
                    Success = false,
                    Error = publicationPlan.Message,
                    SourcePath = item.FullPath,
                    DestinationPath = destinationPath,
                    Audiobook = audiobook,
                    RequestedAction = action.ToString(),
                    EffectiveAction = publicationPlan.EffectiveAction.ToString(),
                    SourceDisposition = publicationPlan.SourceDisposition.ToString(),
                    WarningCode = publicationPlan.ReasonCode
                };
            }

            var operationId = FileMoveOperationIdentity.CreateForPaths(
                "manual-import",
                audiobook.Id,
                action,
                item.FullPath,
                sourceSemantics,
                sourceProof,
                destinationPath,
                destinationSemantics);
            var preparation =
                await PrepareOwnedManualImportActionForRegistrationAsync(
                    publicationPlan,
                    item.FullPath,
                    destinationPath,
                    audiobook,
                    rootFolders,
                    destinationSemantics,
                    destinationResolution.BoundaryPath,
                    operationId,
                    ownership.ExistingFile?.PhysicalObjectIdentity,
                    sourceProof,
                    cancellationToken);
            using (var registrationLease = preparation.RegistrationLease)
            {
                if (registrationLease == null)
                {
                    return new ManualImportResultDto
                    {
                        Success = false,
                        Error = preparation.Message
                            ?? "The file could not be published and registered safely.",
                        SourcePath = item.FullPath,
                        DestinationPath = destinationPath,
                        Audiobook = audiobook
                    };
                }

                var registered = publicationPlan.Mode
                        == FilePublicationExecutionMode.AdditiveCopyRetainSource
                        ? await _audiobookFileService
                            .RegisterCompatibilityPublicationWithBasePathAsync(
                                audiobook,
                                ownership,
                                registrationLease,
                                authoritativeBasePath,
                                "manual-import",
                                cancellationToken)
                        : await RegisterPublishedManualImportAsync(
                            audiobook,
                            ownership,
                            registrationLease,
                            authoritativeBasePath,
                            cancellationToken);
                if (!registered)
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

                if (publicationPlan.EffectiveAction == FileAction.Move
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

                if (registrationLease.HasDurablePhysicalObjectIdentity
                    && !string.IsNullOrWhiteSpace(audiobook.Asin))
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
                Audiobook = audiobook,
                RequestedAction = action.ToString(),
                EffectiveAction = publicationPlan.EffectiveAction.ToString(),
                SourceDisposition = publicationPlan.SourceDisposition.ToString(),
                WarningCode = publicationPlan.ReasonCode,
                Warning = publicationPlan.Message
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
