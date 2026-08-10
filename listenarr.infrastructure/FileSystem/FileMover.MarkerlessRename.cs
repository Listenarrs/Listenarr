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
                || !string.Equals(
                    initialSource.GetObjectIdentity(),
                    expectedSourcePhysicalObjectIdentity,
                    StringComparison.Ordinal)
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
                || !sourceEntry.VisiblePathMatches()
                || !string.Equals(
                    sourceEntry.GetObjectIdentity(),
                    expectedSourcePhysicalObjectIdentity,
                    StringComparison.Ordinal)
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
                || !string.Equals(
                    targetPhysicalObjectIdentity,
                    expectedSourcePhysicalObjectIdentity,
                    StringComparison.Ordinal))
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
                || !destinationEntry.VisiblePathMatches())
            {
                await MarkMarkerlessRenameNeedsAttentionAsync(
                    journal,
                    "The markerless rename destination is unavailable.",
                    cancellationToken);
                return false;
            }
            targetPhysicalObjectIdentity = destinationEntry.GetObjectIdentity();
            if (!string.Equals(
                    targetPhysicalObjectIdentity,
                    expectedSourcePhysicalObjectIdentity,
                    StringComparison.Ordinal))
            {
                await MarkMarkerlessRenameNeedsAttentionAsync(
                    journal,
                    "The markerless rename destination identifies another physical file generation.",
                    cancellationToken);
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(
                journal.TargetPhysicalObjectIdentity)
            && !string.Equals(
                journal.TargetPhysicalObjectIdentity,
                targetPhysicalObjectIdentity,
                StringComparison.Ordinal))
        {
            await MarkMarkerlessRenameNeedsAttentionAsync(
                journal,
                "The markerless rename target changed after publication.",
                cancellationToken);
            return false;
        }

        if (journal.State < FileMutationJournalState.TargetIdentityPersisted)
        {
            journal = await _fileMutationJournalStore.AdvanceAsync(
                operationId,
                FileMutationJournalState.TargetIdentityPersisted,
                targetPhysicalObjectIdentity,
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
                targetPhysicalObjectIdentity,
                audiobookId: null,
                error: null,
                cancellationToken);
        }
        if (journal.State < FileMutationJournalState.SourceDeletionAuthorized)
        {
            journal = await _fileMutationJournalStore.AdvanceAsync(
                operationId,
                FileMutationJournalState.SourceDeletionAuthorized,
                targetPhysicalObjectIdentity,
                audiobookId: null,
                error: null,
                cancellationToken);
        }
        if (journal.State < FileMutationJournalState.SourceDeleted)
        {
            journal = await _fileMutationJournalStore.AdvanceAsync(
                operationId,
                FileMutationJournalState.SourceDeleted,
                targetPhysicalObjectIdentity,
                audiobookId: null,
                error: null,
                cancellationToken);
        }
        if (journal.State < FileMutationJournalState.Completed)
        {
            _ = await _fileMutationJournalStore.AdvanceAsync(
                operationId,
                FileMutationJournalState.Completed,
                targetPhysicalObjectIdentity,
                audiobookId: null,
                error: null,
                cancellationToken);
        }

        LogMutation(
            FileMutationOutcome.Success,
            FileAction.Move,
            source,
            destination,
            "Markerless generation-preserving rename");
        return true;
    }

    private async Task ValidateMarkerlessRenameJournalAsync(
        FileMutationJournal journal,
        FileMoveGateLease pathLock,
        string expectedSourcePhysicalObjectIdentity,
        int? audiobookId,
        int? audiobookFileId)
    {
        if (journal.ProtocolVersion
                != FileMutationProtocol.MarkerlessDatabaseState
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
