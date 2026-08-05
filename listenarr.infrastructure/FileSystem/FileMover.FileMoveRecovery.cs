using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private async Task<FileMoveClaimRecoveryOutcome> TryRecoverInterruptedFileMoveClaimsAsync(
        FileMoveGateLease lease,
        Guid? operationId)
    {
        var sourceFile = lease.SourcePath;
        var destinationFile = lease.DestinationPath;
        var sourceIdentity = lease.SourceIdentity;
        var destinationIdentity = lease.DestinationIdentity;
        if (!lease.SourceParent.VisiblePathMatches()
            || !lease.DestinationParent.VisiblePathMatches())
        {
            return FileMoveClaimRecoveryOutcome.Blocked;
        }

        var state = GetFileMoveStatePaths(
            sourceFile,
            destinationFile,
            sourceIdentity,
            destinationIdentity);
        if (!string.Equals(sourceIdentity, destinationIdentity, StringComparison.Ordinal))
        {
            var reverseState = GetFileMoveStatePaths(
                destinationFile,
                sourceFile,
                destinationIdentity,
                sourceIdentity);
            using var reverseSource =
                lease.DestinationParent.TryOpenExistingChildForPublication(
                    Path.GetFileName(reverseState.SourceStateDirectory));
            using var reverseDestination =
                lease.SourceParent.TryOpenExistingChildForPublication(
                    Path.GetFileName(reverseState.DestinationStateDirectory));
            if (reverseSource != null || reverseDestination != null)
            {
                return FileMoveClaimRecoveryOutcome.Blocked;
            }
        }

        using var sourceStatePublication =
            lease.SourceParent.TryOpenExistingChildForPublication(
                Path.GetFileName(state.SourceStateDirectory));
        using var destinationStatePublication =
            lease.DestinationParent.TryOpenExistingChildForPublication(
                Path.GetFileName(state.DestinationStateDirectory));
        if (sourceStatePublication == null && destinationStatePublication == null)
        {
            return FileMoveClaimRecoveryOutcome.Ready;
        }

        try
        {
            using var sourceState =
                sourceStatePublication?.OpenCreatedDirectoryAnchor();
            using var destinationState =
                destinationStatePublication?.OpenCreatedDirectoryAnchor();
            if ((sourceState != null
                    && !AnchoredStateContainsOnly(
                        sourceState,
                        "source.claim",
                        "operation.state",
                        "replacement-generation.fence"))
                || (destinationState != null
                    && !AnchoredStateContainsOnly(
                        destinationState,
                        "destination.stage",
                        "destination.previous",
                        "destination.published.claim")))
            {
                return FileMoveClaimRecoveryOutcome.Blocked;
            }

            using var sourceClaim = sourceState?.TryOpenExistingFile(
                "source.claim",
                requireDeleteAccess: true);
            using var operationState = sourceState?.TryOpenExistingFile(
                "operation.state",
                requireDeleteAccess: true);
            using var destinationStage = destinationState?.TryOpenExistingFile(
                "destination.stage",
                requireDeleteAccess: true);
            using var destinationPrevious = destinationState?.TryOpenExistingFile(
                "destination.previous",
                requireDeleteAccess: true);
            using var destinationPublicationClaim =
                destinationState?.TryOpenExistingFile(
                    "destination.published.claim",
                    requireDeleteAccess: true);
            using var generationFence = sourceState?.TryOpenExistingFile(
                "replacement-generation.fence",
                requireDeleteAccess: true);

            FileMoveFence? persistedFence = null;
            if (operationState != null)
            {
                persistedFence = await ReadFileMoveContentAsync(operationState);
                if (!persistedFence.HasValue
                    || persistedFence.Value.OperationId != operationId
                    || !string.Equals(
                        persistedFence.Value.SourceIdentity,
                        sourceIdentity,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        persistedFence.Value.DestinationIdentity,
                        destinationIdentity,
                        StringComparison.Ordinal))
                {
                    return FileMoveClaimRecoveryOutcome.Blocked;
                }
            }

            if (!RecoveryArtifactsMatchPersistedGeneration(
                    persistedFence,
                    sourceClaim,
                    destinationStage,
                    destinationPrevious,
                    destinationPublicationClaim,
                    sourceRetirementCommitted: generationFence != null))
            {
                return FileMoveClaimRecoveryOutcome.Blocked;
            }

            if (generationFence == null)
            {
                using var publicSource = lease.SourceParent.TryOpenExistingFile(
                    lease.SourceName,
                    requireDeleteAccess: false);
                using var publicDestination =
                    lease.DestinationParent.TryOpenExistingFile(
                        lease.DestinationName,
                        requireDeleteAccess: false);
                if (sourceClaim != null)
                {
                    if (persistedFence is not { Version: >= 2 } rollbackFence
                        || publicSource != null
                        || (destinationPrevious != null && publicDestination != null))
                    {
                        return FileMoveClaimRecoveryOutcome.Blocked;
                    }
                    if (publicDestination != null)
                    {
                        if (string.IsNullOrWhiteSpace(
                                rollbackFence.DestinationPreviousObjectIdentity)
                            || !string.Equals(
                                publicDestination.GetObjectIdentity(),
                                rollbackFence.DestinationPreviousObjectIdentity,
                                StringComparison.Ordinal))
                        {
                            return FileMoveClaimRecoveryOutcome.Blocked;
                        }
                    }
                    else if (destinationPrevious == null
                        && !string.IsNullOrWhiteSpace(
                            rollbackFence.DestinationPreviousObjectIdentity))
                    {
                        return FileMoveClaimRecoveryOutcome.Blocked;
                    }
                    if (!rollbackFence.NativeRename
                        && !string.IsNullOrWhiteSpace(
                            rollbackFence.DestinationStageObjectIdentity)
                        && destinationStage == null)
                    {
                        return FileMoveClaimRecoveryOutcome.Blocked;
                    }

                    sourceClaim.MoveTo(lease.SourceParent, lease.SourceName);
                    sourceClaim.Dispose();
                    FlushFileMoveDirectory(
                        lease.SourceParent,
                        "interrupted source rollback publication");
                    FlushFileMoveDirectory(
                        sourceState!,
                        "interrupted source claim retirement");
                    if (destinationPublicationClaim != null)
                    {
                        destinationPublicationClaim.Delete(immediateWindows: true);
                        destinationPublicationClaim.Dispose();
                        FlushFileMoveDirectory(
                            destinationState!,
                            "interrupted publication claim retirement");
                    }
                    destinationStage?.Delete(immediateWindows: true);
                    destinationStage?.Dispose();
                    if (destinationPrevious != null)
                    {
                        destinationPrevious.MoveTo(
                            lease.DestinationParent,
                            lease.DestinationName);
                        destinationPrevious.Dispose();
                        FlushFileMoveDirectory(
                            lease.DestinationParent,
                            "interrupted destination rollback publication");
                    }
                    if (destinationState != null)
                    {
                        FlushFileMoveDirectory(
                            destinationState,
                            "interrupted destination state rollback");
                    }
                }
                else if (destinationStage != null
                    || destinationPrevious != null
                    || destinationPublicationClaim != null)
                {
                    return FileMoveClaimRecoveryOutcome.Blocked;
                }
                else if (publicSource == null)
                {
                    return FileMoveClaimRecoveryOutcome.Blocked;
                }

                operationState?.Delete(immediateWindows: true);
                operationState?.Dispose();
                if (sourceState != null)
                {
                    FlushFileMoveDirectory(
                        sourceState,
                        "interrupted operation-state retirement");
                }
                sourceState?.Dispose();
                destinationState?.Dispose();
                TryDeleteAnchoredStateDirectory(
                    sourceStatePublication,
                    Path.GetFileName(state.SourceStateDirectory));
                TryDeleteAnchoredStateDirectory(
                    destinationStatePublication,
                    Path.GetFileName(state.DestinationStateDirectory));
                FlushFileMoveDirectory(
                    lease.SourceParent,
                    "interrupted source-state retirement");
                FlushFileMoveDirectory(
                    lease.DestinationParent,
                    "interrupted destination-state retirement");
                return FileMoveClaimRecoveryOutcome.Ready;
            }

            if (persistedFence is not { Version: >= 2 } committedFenceState)
            {
                return FileMoveClaimRecoveryOutcome.Blocked;
            }
            var nativeRename = committedFenceState.NativeRename;
            var committedContent = committedFenceState.Content;
            if (destinationPublicationClaim == null
                && string.IsNullOrWhiteSpace(
                    committedFenceState.PublishedDestinationObjectIdentity))
            {
                return FileMoveClaimRecoveryOutcome.Blocked;
            }

            if (nativeRename && destinationStage != null)
            {
                return FileMoveClaimRecoveryOutcome.Blocked;
            }
            if (sourceClaim != null)
            {
                if (!await FileMatchesMoveContentAsync(
                        sourceClaim,
                        committedContent))
                {
                    return FileMoveClaimRecoveryOutcome.Blocked;
                }

                if (nativeRename)
                {
                    using var publicDestination =
                        lease.DestinationParent.TryOpenExistingFile(
                            lease.DestinationName,
                            requireDeleteAccess: false);
                    if (publicDestination != null)
                    {
                        return FileMoveClaimRecoveryOutcome.Blocked;
                    }
                    sourceClaim.MoveTo(
                        lease.DestinationParent,
                        lease.DestinationName);
                    sourceClaim.Dispose();
                    FlushFileMoveDirectory(
                        lease.DestinationParent,
                        "native destination recovery publication");
                    FlushFileMoveDirectory(
                        sourceState!,
                        "native source-claim recovery retirement");
                }
                else
                {
                    if (destinationStage == null
                        || !await FileMatchesMoveContentAsync(
                            destinationStage,
                            committedContent))
                    {
                        return FileMoveClaimRecoveryOutcome.Blocked;
                    }
                    sourceClaim.Delete(immediateWindows: true);
                    sourceClaim.Dispose();
                    FlushFileMoveDirectory(
                        sourceState!,
                        "copied source-claim recovery retirement");
                }
            }

            if (!nativeRename && destinationStage != null)
            {
                if (!await FileMatchesMoveContentAsync(
                        destinationStage,
                        committedContent))
                {
                    return FileMoveClaimRecoveryOutcome.Blocked;
                }
                using var publicDestination =
                    lease.DestinationParent.TryOpenExistingFile(
                        lease.DestinationName,
                        requireDeleteAccess: false);
                if (publicDestination != null)
                {
                    return FileMoveClaimRecoveryOutcome.Blocked;
                }
                destinationStage.MoveTo(
                    lease.DestinationParent,
                    lease.DestinationName);
                destinationStage.Dispose();
                FlushFileMoveDirectory(
                    lease.DestinationParent,
                    "copied destination recovery publication");
                FlushFileMoveDirectory(
                    destinationState!,
                    "destination-stage recovery retirement");
            }

            using var publishedDestination =
                lease.DestinationParent.TryOpenExistingFile(
                    lease.DestinationName,
                    requireDeleteAccess: false);
            if (publishedDestination == null
                || !publishedDestination.VisiblePathMatches())
            {
                return FileMoveClaimRecoveryOutcome.Blocked;
            }
            if (!await FileMatchesMoveContentAsync(
                    publishedDestination,
                    committedContent))
            {
                return FileMoveClaimRecoveryOutcome.Blocked;
            }

            var publishedObjectIdentity = publishedDestination.GetObjectIdentity();
            if (destinationPublicationClaim != null)
            {
                if (!destinationPublicationClaim.VisiblePathMatches()
                    || !destinationPublicationClaim.IdentifiesSameEntry(
                        publishedDestination))
                {
                    return FileMoveClaimRecoveryOutcome.Blocked;
                }
                if (!string.IsNullOrWhiteSpace(
                        committedFenceState.PublishedDestinationObjectIdentity)
                    && !string.Equals(
                        committedFenceState.PublishedDestinationObjectIdentity,
                        publishedObjectIdentity,
                        StringComparison.Ordinal))
                {
                    return FileMoveClaimRecoveryOutcome.Blocked;
                }
                if (operationState == null)
                {
                    return FileMoveClaimRecoveryOutcome.Blocked;
                }

                await WriteFileMoveContentAsync(
                    operationState,
                    committedContent,
                    committedFenceState.OperationId,
                    committedFenceState.SourceIdentity,
                    committedFenceState.DestinationIdentity,
                    committedFenceState.SourceObjectIdentity!,
                    committedFenceState.DestinationStageObjectIdentity,
                    committedFenceState.DestinationPreviousObjectIdentity,
                    publishedObjectIdentity,
                    nativeRename);
                FlushFileMoveDirectory(
                    sourceState!,
                    "recovered published destination generation evidence");
                committedFenceState = committedFenceState with
                {
                    PublishedDestinationObjectIdentity = publishedObjectIdentity
                };
            }
            else if (string.IsNullOrWhiteSpace(
                    committedFenceState.PublishedDestinationObjectIdentity)
                || !string.Equals(
                    committedFenceState.PublishedDestinationObjectIdentity,
                    publishedObjectIdentity,
                    StringComparison.Ordinal))
            {
                return FileMoveClaimRecoveryOutcome.Blocked;
            }

            destinationPrevious?.Delete(immediateWindows: true);
            destinationPrevious?.Dispose();
            if (destinationState != null)
            {
                FlushFileMoveDirectory(
                    destinationState,
                    "previous destination recovery retirement");
            }

            if (destinationPublicationClaim != null)
            {
                if (!destinationPublicationClaim.VisiblePathMatches()
                    || !publishedDestination.VisiblePathMatches()
                    || !destinationPublicationClaim.IdentifiesSameEntry(
                        publishedDestination))
                {
                    return FileMoveClaimRecoveryOutcome.Blocked;
                }
                destinationPublicationClaim.Delete(immediateWindows: true);
                destinationPublicationClaim.Dispose();
                FlushFileMoveDirectory(
                    destinationState!,
                    "destination publication claim recovery retirement");
            }

            if (!publishedDestination.VisiblePathMatches()
                || !string.Equals(
                    publishedDestination.GetObjectIdentity(),
                    committedFenceState.PublishedDestinationObjectIdentity,
                    StringComparison.Ordinal)
                || !await FileMatchesMoveContentAsync(
                    publishedDestination,
                    committedContent))
            {
                return FileMoveClaimRecoveryOutcome.Blocked;
            }

            using var publishedSource = lease.SourceParent.TryOpenExistingFile(
                lease.SourceName,
                requireDeleteAccess: false);
            var sourceWasRecreated = publishedSource != null;
            if (!sourceWasRecreated)
            {
                generationFence.Delete(immediateWindows: true);
                generationFence.Dispose();
                operationState?.Delete(immediateWindows: true);
                operationState?.Dispose();
                FlushFileMoveDirectory(
                    sourceState!,
                    "committed source-state recovery retirement");
            }

            sourceState?.Dispose();
            destinationState?.Dispose();
            if (!sourceWasRecreated)
            {
                TryDeleteAnchoredStateDirectory(
                    sourceStatePublication,
                    Path.GetFileName(state.SourceStateDirectory));
                FlushFileMoveDirectory(
                    lease.SourceParent,
                    "committed source-state directory retirement");
            }
            TryDeleteAnchoredStateDirectory(
                destinationStatePublication,
                Path.GetFileName(state.DestinationStateDirectory));
            FlushFileMoveDirectory(
                lease.DestinationParent,
                "committed destination-state directory retirement");
            return sourceWasRecreated
                ? FileMoveClaimRecoveryOutcome.SourceRecreated
                : FileMoveClaimRecoveryOutcome.Completed;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning(
                exception,
                "Blocked file move because interrupted state could not be recovered: {Source} -> {Destination}",
                LogRedaction.SanitizeFilePath(sourceFile),
                LogRedaction.SanitizeFilePath(destinationFile));
            return FileMoveClaimRecoveryOutcome.Blocked;
        }
    }
}
