using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private async Task<bool?> TryMoveFilePreservingPhysicalIdentityMarkerlessAsync(
        string source,
        string destination,
        string expectedSourcePhysicalObjectIdentity,
        Guid operationId,
        int? audiobookId = null,
        int? audiobookFileId = null)
    {
        if (_fileMutationJournalStore == null)
        {
            return null;
        }
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A markerless file rename requires a non-empty operation ID.",
                nameof(operationId));
        }

        using var pathLock = await TryAcquireFileMoveGateAsync(
            source,
            destination,
            allowExistingAliasForRecovery: true);
        if (pathLock == null)
        {
            return false;
        }

        var cancellationToken = CancellationToken.None;
        var journal = await _fileMutationJournalStore.GetAsync(
            operationId,
            cancellationToken);
        if (journal == null)
        {
            using var initialSource = pathLock.SourceParent.TryOpenExistingFile(
                pathLock.SourceName,
                requireDeleteAccess: true);
            using var initialDestination =
                pathLock.DestinationParent.TryOpenExistingFile(
                    pathLock.DestinationName,
                    requireDeleteAccess: false);
            if (initialSource == null
                || initialDestination != null
                || !initialSource.VisiblePathMatches()
                || !initialSource.MatchesObjectIdentity(
                    expectedSourcePhysicalObjectIdentity)
                || !initialSource.IsOnSameVolume(
                    pathLock.DestinationParent))
            {
                return false;
            }

            long sourceLength;
            using (var stream = initialSource.OpenReadStream(
                bufferSize: 128 * 1024,
                asynchronous: false))
            {
                sourceLength = stream.Length;
            }
            journal = await _fileMutationJournalStore.GetOrCreateAsync(
                new FileMutationJournalClaim(
                    operationId,
                    FileAction.Move,
                    pathLock.SourcePath,
                    pathLock.DestinationPath,
                    pathLock.SourceParent.GetDirectoryObjectIdentity(),
                    pathLock.DestinationParent.GetDirectoryObjectIdentity(),
                    expectedSourcePhysicalObjectIdentity,
                    sourceLength,
                    SourceSha256: null,
                    audiobookId,
                    audiobookFileId),
                cancellationToken);
            if (AfterMarkerlessRenameJournalPlannedForTestAsync != null)
            {
                await AfterMarkerlessRenameJournalPlannedForTestAsync();
            }
        }
        else
        {
            await ValidateMarkerlessRenameJournalAsync(
                journal,
                pathLock,
                expectedSourcePhysicalObjectIdentity,
                audiobookId,
                audiobookFileId);
            if (!JournalParentGenerationsMatchGate(journal, pathLock))
            {
                await MarkMarkerlessRenameNeedsAttentionAsync(
                    journal,
                    "A markerless rename parent directory changed physical generation while the operation was interrupted.",
                    cancellationToken);
                return false;
            }
        }

        if (journal.State == FileMutationJournalState.NeedsAttention)
        {
            return false;
        }
        if (journal.State == FileMutationJournalState.OwnerMetadataReconciled)
        {
            return OwnerMetadataReconciledTargetMatches(pathLock, journal);
        }

        using var sourceEntry = pathLock.SourceParent.TryOpenExistingFile(
            pathLock.SourceName,
            requireDeleteAccess: true);
        using var destinationEntry =
            pathLock.DestinationParent.TryOpenExistingFile(
                pathLock.DestinationName,
                requireDeleteAccess: false);
        if (sourceEntry != null && destinationEntry != null)
        {
            await MarkMarkerlessRenameNeedsAttentionAsync(
                journal,
                "Both source and destination exist for a markerless rename.",
                cancellationToken);
            return false;
        }
        if (sourceEntry == null && destinationEntry == null)
        {
            await MarkMarkerlessRenameNeedsAttentionAsync(
                journal,
                "Both source and destination are missing for a markerless rename.",
                cancellationToken);
            return false;
        }

        string targetPhysicalObjectIdentity;
        if (sourceEntry != null)
        {
            if (journal.State != FileMutationJournalState.Planned
                || !VisiblePathMatchesOrThrowUnavailable(
                    sourceEntry,
                    "The markerless rename source is temporarily unavailable while its generation is being verified.")
                || !sourceEntry.MatchesObjectIdentity(
                    expectedSourcePhysicalObjectIdentity)
                || !sourceEntry.IsOnSameVolume(
                    pathLock.DestinationParent))
            {
                await MarkMarkerlessRenameNeedsAttentionAsync(
                    journal,
                    "The markerless rename source changed or was recreated.",
                    cancellationToken);
                return false;
            }

            sourceEntry.MoveTo(
                pathLock.DestinationParent,
                pathLock.DestinationName);
            pathLock.SourceParent.FlushDirectoryEntry();
            if (!string.Equals(
                    pathLock.SourceParent.FullPath,
                    pathLock.DestinationParent.FullPath,
                    StringComparison.Ordinal))
            {
                pathLock.DestinationParent.FlushDirectoryEntry();
            }
            targetPhysicalObjectIdentity = sourceEntry.GetObjectIdentity();
            if (!sourceEntry.VisiblePathMatches()
                || !sourceEntry.MatchesObjectIdentity(
                    expectedSourcePhysicalObjectIdentity))
            {
                throw new IOException(
                    "The markerless rename target could not be verified after publication.");
            }
            if (AfterMarkerlessRenamePublishedBeforeTargetStateForTestAsync != null)
            {
                await AfterMarkerlessRenamePublishedBeforeTargetStateForTestAsync();
            }
        }
        else
        {
            if (destinationEntry == null
                || !VisiblePathMatchesOrThrowUnavailable(
                    destinationEntry,
                    "The markerless rename destination is temporarily unavailable while interrupted publication is being verified."))
            {
                await MarkMarkerlessRenameNeedsAttentionAsync(
                    journal,
                    "The markerless rename destination is unavailable.",
                    cancellationToken);
                return false;
            }
            targetPhysicalObjectIdentity = destinationEntry.GetObjectIdentity();
            if (!destinationEntry.MatchesObjectIdentity(
                    expectedSourcePhysicalObjectIdentity))
            {
                await MarkMarkerlessRenameNeedsAttentionAsync(
                    journal,
                    "The markerless rename destination identifies another physical file generation.",
                    cancellationToken);
                return false;
            }
        }

        var publishedTarget = sourceEntry ?? destinationEntry!;
        if (!string.IsNullOrWhiteSpace(
                journal.TargetPhysicalObjectIdentity)
            && !publishedTarget.MatchesObjectIdentity(
                journal.TargetPhysicalObjectIdentity))
        {
            await MarkMarkerlessRenameNeedsAttentionAsync(
                journal,
                "The markerless rename target changed after publication.",
                cancellationToken);
            return false;
        }

        var durableTargetPhysicalObjectIdentity =
            journal.TargetPhysicalObjectIdentity ?? expectedSourcePhysicalObjectIdentity;
        if (journal.State < FileMutationJournalState.TargetIdentityPersisted)
        {
            if (!MarkerlessRenamePublicationStillMatches(
                    pathLock,
                    publishedTarget))
            {
                await MarkMarkerlessRenameNeedsAttentionAsync(
                    journal,
                    "The markerless rename parent or target changed before publication identity could be recorded durably.",
                    cancellationToken);
                return false;
            }

            journal = await _fileMutationJournalStore.AdvanceAsync(
                operationId,
                FileMutationJournalState.TargetIdentityPersisted,
                durableTargetPhysicalObjectIdentity,
                audiobookId: null,
                error: null,
                cancellationToken);
            if (AfterMarkerlessRenameTargetStateForTestAsync != null)
            {
                await AfterMarkerlessRenameTargetStateForTestAsync();
            }
        }
        if (journal.State < FileMutationJournalState.TargetVerified)
        {
            journal = await _fileMutationJournalStore.AdvanceAsync(
                operationId,
                FileMutationJournalState.TargetVerified,
                durableTargetPhysicalObjectIdentity,
                audiobookId: null,
                error: null,
                cancellationToken);
        }
        if (journal.State < FileMutationJournalState.SourceDeletionAuthorized)
        {
            journal = await _fileMutationJournalStore.AdvanceAsync(
                operationId,
                FileMutationJournalState.SourceDeletionAuthorized,
                durableTargetPhysicalObjectIdentity,
                audiobookId: null,
                error: null,
                cancellationToken);
        }
        if (journal.State < FileMutationJournalState.SourceDeleted)
        {
            if (!MarkerlessRenamePublicationStillMatches(
                    pathLock,
                    publishedTarget))
            {
                await MarkMarkerlessRenameNeedsAttentionAsync(
                    journal,
                    "The markerless rename parent or target changed before source retirement could be recorded durably.",
                    cancellationToken);
                return false;
            }

            journal = await _fileMutationJournalStore.AdvanceAsync(
                operationId,
                FileMutationJournalState.SourceDeleted,
                durableTargetPhysicalObjectIdentity,
                audiobookId: null,
                error: null,
                cancellationToken);
            if (AfterMarkerlessRenameSourceDeletedStateForTestAsync != null)
            {
                await AfterMarkerlessRenameSourceDeletedStateForTestAsync();
            }
        }
        if (journal.State < FileMutationJournalState.Completed)
        {
            var completionValidation =
                await _fileMutationJournalStore.AdvanceWithCommitValidationAsync(
                    operationId,
                    FileMutationJournalState.Completed,
                    durableTargetPhysicalObjectIdentity,
                    audiobookId: null,
                    error: null,
                    async validationToken =>
                    {
                        if (BeforeMarkerlessCompletedJournalCommitForTestAsync != null)
                        {
                            await BeforeMarkerlessCompletedJournalCommitForTestAsync();
                        }

                        return await ProbeMarkerlessMoveCompletionAsync(
                            pathLock,
                            journal,
                            validationToken);
                    },
                    cancellationToken);
            if (completionValidation == RegistrationPublicationMatchOutcome.Unavailable)
            {
                return false;
            }
            if (completionValidation != RegistrationPublicationMatchOutcome.Match)
            {
                await MarkMarkerlessRenameNeedsAttentionAsync(
                    journal,
                    "The markerless rename source, destination, or parent generation changed before completion could be committed.",
                    cancellationToken);
                return false;
            }
        }

        LogMutation(
            FileMutationOutcome.Success,
            FileAction.Move,
            source,
            destination,
            "Markerless generation-preserving rename");
        return true;
    }

    private static bool MarkerlessRenamePublicationStillMatches(
        FileMoveGateLease pathLock,
        PinnedDirectoryCreation.PinnedFileEntry publishedTarget) =>
        VisiblePathMatchesOrThrowUnavailable(
            pathLock.SourceParent,
            "The markerless rename source parent is temporarily unavailable before durable state advancement.")
        && VisiblePathMatchesOrThrowUnavailable(
            pathLock.DestinationParent,
            "The markerless rename destination parent is temporarily unavailable before durable state advancement.")
        && VisiblePathMatchesOrThrowUnavailable(
            publishedTarget,
            "The markerless rename target is temporarily unavailable before durable state advancement.");

    private async Task ValidateMarkerlessRenameJournalAsync(
        FileMutationJournal journal,
        FileMoveGateLease pathLock,
        string expectedSourcePhysicalObjectIdentity,
        int? audiobookId,
        int? audiobookFileId)
    {
        if (journal.ProtocolVersion != FileMutationProtocol.Current
            || journal.Action != FileAction.Move
            || journal.AudiobookId != audiobookId
            || journal.AudiobookFileId != audiobookFileId
            || !await JournalPathsMatchGateAsync(journal, pathLock)
            || !string.Equals(
                journal.SourcePhysicalObjectIdentity,
                expectedSourcePhysicalObjectIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The durable markerless rename identity does not match the requested operation.");
        }
    }

    private async Task MarkMarkerlessRenameNeedsAttentionAsync(
        FileMutationJournal journal,
        string reason,
        CancellationToken cancellationToken)
    {
        _ = await _fileMutationJournalStore!.AdvanceAsync(
            journal.OperationId,
            FileMutationJournalState.NeedsAttention,
            journal.TargetPhysicalObjectIdentity,
            journal.AudiobookId,
            reason,
            cancellationToken);
        _logger.LogWarning(
            "Markerless file rename {OperationId} requires attention: {Reason}",
            journal.OperationId,
            reason);
    }
}
