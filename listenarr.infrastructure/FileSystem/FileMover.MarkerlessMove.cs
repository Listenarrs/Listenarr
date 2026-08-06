using System.Security.Cryptography;
using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private async Task<bool?> TryMoveFileMarkerlessAsync(
        string source,
        string destination,
        Guid? operationId)
    {
        if (!operationId.HasValue || _fileMutationJournalStore == null)
        {
            return null;
        }
        if (operationId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "A markerless file move requires a non-empty operation ID.",
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
            operationId.Value,
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
                || !initialSource.VisiblePathMatches())
            {
                return false;
            }

            var proof = await CaptureMarkerlessSourceProofAsync(
                initialSource,
                cancellationToken);
            journal = await _fileMutationJournalStore.GetOrCreateAsync(
                new FileMutationJournalClaim(
                    operationId.Value,
                    FileAction.Move,
                    pathLock.SourcePath,
                    pathLock.DestinationPath,
                    proof.PhysicalObjectIdentity,
                    proof.Length,
                    proof.Sha256),
                cancellationToken);
            if (AfterMarkerlessMoveJournalPlannedForTestAsync != null)
            {
                await AfterMarkerlessMoveJournalPlannedForTestAsync();
            }
        }
        else
        {
            ValidateMarkerlessMoveJournal(journal, pathLock);
        }

        if (journal.State == FileMutationJournalState.NeedsAttention)
        {
            return false;
        }

        using (var observedSource = pathLock.SourceParent.TryOpenExistingFile(
            pathLock.SourceName,
            requireDeleteAccess: false))
        using (var observedTarget =
            pathLock.DestinationParent.TryOpenExistingFile(
                pathLock.DestinationName,
                requireDeleteAccess: false))
        {
            if (journal.State == FileMutationJournalState.Planned)
            {
                if (observedSource != null && observedTarget != null)
                {
                    await MarkMarkerlessMoveNeedsAttentionAsync(
                        journal,
                        "Both source and destination exist before markerless publication proof was persisted.",
                        cancellationToken);
                    return false;
                }
                if (observedSource == null && observedTarget == null)
                {
                    await MarkMarkerlessMoveNeedsAttentionAsync(
                        journal,
                        "Both source and destination are missing for a markerless move.",
                        cancellationToken);
                    return false;
                }
                if (observedSource == null)
                {
                    if (observedTarget == null
                        || !observedTarget.VisiblePathMatches()
                        || !string.Equals(
                            observedTarget.GetObjectIdentity(),
                            journal.SourcePhysicalObjectIdentity,
                            StringComparison.Ordinal)
                        || !await observedTarget.MatchesAsync(
                            journal.SourceLength,
                            journal.SourceSha256,
                            cancellationToken))
                    {
                        await MarkMarkerlessMoveNeedsAttentionAsync(
                            journal,
                            "An unproven markerless destination cannot be attributed to the original source generation.",
                            cancellationToken);
                        return false;
                    }

                    journal = await _fileMutationJournalStore.AdvanceAsync(
                        journal.OperationId,
                        FileMutationJournalState.TargetIdentityPersisted,
                        observedTarget.GetObjectIdentity(),
                        audiobookId: null,
                        error: null,
                        cancellationToken);
                }
            }
        }

        if (journal.State == FileMutationJournalState.Planned)
        {
            using var sourceEntry = pathLock.SourceParent.TryOpenExistingFile(
                pathLock.SourceName,
                requireDeleteAccess: true);
            using var existingTarget =
                pathLock.DestinationParent.TryOpenExistingFile(
                    pathLock.DestinationName,
                    requireDeleteAccess: false);
            if (sourceEntry == null
                || existingTarget != null
                || !await MatchesMarkerlessSourceProofAsync(
                    sourceEntry,
                    journal,
                    cancellationToken))
            {
                await MarkMarkerlessMoveNeedsAttentionAsync(
                    journal,
                    "The markerless move source changed before publication.",
                    cancellationToken);
                return false;
            }

            string targetIdentity;
            if (!DisableNativeFileRenameForTest
                && sourceEntry.IsOnSameVolume(pathLock.DestinationParent))
            {
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
                targetIdentity = sourceEntry.GetObjectIdentity();
                if (!sourceEntry.VisiblePathMatches()
                    || !string.Equals(
                        targetIdentity,
                        journal.SourcePhysicalObjectIdentity,
                        StringComparison.Ordinal))
                {
                    throw new IOException(
                        "The markerless native move target could not be verified.");
                }
                if (AfterMarkerlessMovePublishedBeforeTargetStateForTestAsync != null)
                {
                    await AfterMarkerlessMovePublishedBeforeTargetStateForTestAsync();
                }
            }
            else
            {
                using var created = pathLock.DestinationParent.CreateNewFile(
                    pathLock.DestinationName);
                targetIdentity = created.GetObjectIdentity();
                if (AfterMarkerlessMoveTargetCreatedBeforeStateForTestAsync != null)
                {
                    await AfterMarkerlessMoveTargetCreatedBeforeStateForTestAsync();
                }
            }

            journal = await _fileMutationJournalStore.AdvanceAsync(
                journal.OperationId,
                FileMutationJournalState.TargetIdentityPersisted,
                targetIdentity,
                audiobookId: null,
                error: null,
                cancellationToken);
            if (AfterMarkerlessMoveTargetStateForTestAsync != null)
            {
                await AfterMarkerlessMoveTargetStateForTestAsync();
            }
        }

        if (journal.State == FileMutationJournalState.TargetIdentityPersisted)
        {
            using var targetEntry =
                pathLock.DestinationParent.TryOpenExistingFile(
                    pathLock.DestinationName,
                    requireDeleteAccess: false);
            if (targetEntry == null
                || !TargetMatchesMarkerlessJournal(targetEntry, journal))
            {
                await MarkMarkerlessMoveNeedsAttentionAsync(
                    journal,
                    "The markerless destination changed before content verification.",
                    cancellationToken);
                return false;
            }

            if (!await targetEntry.MatchesAsync(
                    journal.SourceLength,
                    journal.SourceSha256,
                    cancellationToken))
            {
                using var sourceEntry =
                    pathLock.SourceParent.TryOpenExistingFile(
                        pathLock.SourceName,
                        requireDeleteAccess: false);
                if (sourceEntry == null
                    || !await MatchesMarkerlessSourceProofAsync(
                        sourceEntry,
                        journal,
                        cancellationToken))
                {
                    await MarkMarkerlessMoveNeedsAttentionAsync(
                        journal,
                        "The markerless source is unavailable before the destination content was verified.",
                        cancellationToken);
                    return false;
                }

                await CopyMarkerlessFileAsync(
                    sourceEntry,
                    targetEntry,
                    cancellationToken);
                sourceEntry.PreserveMarkerlessMetadataTo(targetEntry);
                if (!TargetMatchesMarkerlessJournal(targetEntry, journal)
                    || !await targetEntry.MatchesAsync(
                        journal.SourceLength,
                        journal.SourceSha256,
                        cancellationToken))
                {
                    throw new IOException(
                        "The markerless destination failed content verification.");
                }
                if (AfterMarkerlessMoveTargetWrittenBeforeVerifiedStateForTestAsync != null)
                {
                    await AfterMarkerlessMoveTargetWrittenBeforeVerifiedStateForTestAsync();
                }
            }

            journal = await _fileMutationJournalStore.AdvanceAsync(
                journal.OperationId,
                FileMutationJournalState.TargetVerified,
                journal.TargetPhysicalObjectIdentity,
                audiobookId: null,
                error: null,
                cancellationToken);
        }
        else if (journal.State >= FileMutationJournalState.TargetVerified)
        {
            using var targetEntry =
                pathLock.DestinationParent.TryOpenExistingFile(
                    pathLock.DestinationName,
                    requireDeleteAccess: false);
            if (targetEntry == null
                || !TargetMatchesMarkerlessJournal(targetEntry, journal)
                || !await targetEntry.MatchesAsync(
                    journal.SourceLength,
                    journal.SourceSha256,
                    cancellationToken))
            {
                await MarkMarkerlessMoveNeedsAttentionAsync(
                    journal,
                    "The verified markerless destination changed.",
                    cancellationToken);
                return false;
            }
        }

        if (journal.State >= FileMutationJournalState.SourceDeleted)
        {
            using var recreatedSource =
                pathLock.SourceParent.TryOpenExistingFile(
                    pathLock.SourceName,
                    requireDeleteAccess: false);
            if (recreatedSource != null)
            {
                await MarkMarkerlessMoveNeedsAttentionAsync(
                    journal,
                    "A source path was recreated after markerless deletion completed.",
                    cancellationToken);
                return false;
            }
        }

        if (journal.State < FileMutationJournalState.SourceDeletionAuthorized)
        {
            journal = await _fileMutationJournalStore.AdvanceAsync(
                journal.OperationId,
                FileMutationJournalState.SourceDeletionAuthorized,
                journal.TargetPhysicalObjectIdentity,
                audiobookId: null,
                error: null,
                cancellationToken);
        }

        if (journal.State == FileMutationJournalState.SourceDeletionAuthorized)
        {
            using var sourceEntry = pathLock.SourceParent.TryOpenExistingFile(
                pathLock.SourceName,
                requireDeleteAccess: true);
            if (sourceEntry != null)
            {
                if (!await MatchesMarkerlessSourceProofAsync(
                        sourceEntry,
                        journal,
                        cancellationToken))
                {
                    await MarkMarkerlessMoveNeedsAttentionAsync(
                        journal,
                        "The markerless source was replaced before authorized deletion.",
                        cancellationToken);
                    return false;
                }

                sourceEntry.Delete(immediateWindows: true);
                pathLock.SourceParent.FlushDirectoryEntry();
                if (AfterMarkerlessMoveSourceDeletedBeforeStateForTestAsync != null)
                {
                    await AfterMarkerlessMoveSourceDeletedBeforeStateForTestAsync();
                }
            }

            journal = await _fileMutationJournalStore.AdvanceAsync(
                journal.OperationId,
                FileMutationJournalState.SourceDeleted,
                journal.TargetPhysicalObjectIdentity,
                audiobookId: null,
                error: null,
                cancellationToken);
        }

        _ = await _fileMutationJournalStore.AdvanceAsync(
            journal.OperationId,
            FileMutationJournalState.Completed,
            journal.TargetPhysicalObjectIdentity,
            audiobookId: null,
            error: null,
            cancellationToken);
        LogMutation(
            FileMutationOutcome.Success,
            FileAction.Move,
            source,
            destination,
            "Markerless database-backed file move");
        return true;
    }

    private static async Task<MarkerlessSourceProof>
        CaptureMarkerlessSourceProofAsync(
            PinnedDirectoryCreation.PinnedFileEntry source,
            CancellationToken cancellationToken)
    {
        var physicalObjectIdentity = source.GetObjectIdentity();
        await using var stream = source.OpenReadStream(
            bufferSize: 128 * 1024,
            asynchronous: false);
        var length = stream.Length;
        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return new MarkerlessSourceProof(
            physicalObjectIdentity,
            length,
            Convert.ToHexString(hash));
    }

    private static async Task<bool> MatchesMarkerlessSourceProofAsync(
        PinnedDirectoryCreation.PinnedFileEntry source,
        FileMutationJournal journal,
        CancellationToken cancellationToken) =>
        source.VisiblePathMatches()
        && string.Equals(
            source.GetObjectIdentity(),
            journal.SourcePhysicalObjectIdentity,
            StringComparison.Ordinal)
        && await source.MatchesAsync(
            journal.SourceLength,
            journal.SourceSha256,
            cancellationToken);

    private static bool TargetMatchesMarkerlessJournal(
        PinnedDirectoryCreation.PinnedFileEntry target,
        FileMutationJournal journal) =>
        target.VisiblePathMatches()
        && !string.IsNullOrWhiteSpace(
            journal.TargetPhysicalObjectIdentity)
        && string.Equals(
            target.GetObjectIdentity(),
            journal.TargetPhysicalObjectIdentity,
            StringComparison.Ordinal);

    private static async Task CopyMarkerlessFileAsync(
        PinnedDirectoryCreation.PinnedFileEntry source,
        PinnedDirectoryCreation.PinnedFileEntry target,
        CancellationToken cancellationToken)
    {
        await using var sourceStream = source.OpenReadStream(
            bufferSize: 128 * 1024,
            asynchronous: false);
        await using var targetStream = target.OpenWriteStream(
            bufferSize: 128 * 1024,
            asynchronous: false);
        targetStream.SetLength(0);
        await sourceStream.CopyToAsync(
            targetStream,
            128 * 1024,
            cancellationToken);
        await targetStream.FlushAsync(cancellationToken);
        targetStream.Flush(flushToDisk: true);
    }

    private static void ValidateMarkerlessMoveJournal(
        FileMutationJournal journal,
        FileMoveGateLease pathLock)
    {
        if (journal.ProtocolVersion
                != FileMutationProtocol.MarkerlessDatabaseState
            || journal.Action != FileAction.Move
            || !string.Equals(
                journal.SourcePath,
                Path.GetFullPath(pathLock.SourcePath),
                StringComparison.Ordinal)
            || !string.Equals(
                journal.DestinationPath,
                Path.GetFullPath(pathLock.DestinationPath),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The durable markerless move identity does not match the requested operation.");
        }
    }

    private async Task MarkMarkerlessMoveNeedsAttentionAsync(
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
            "Markerless file move {OperationId} requires attention: {Reason}",
            journal.OperationId,
            reason);
    }

    private sealed record MarkerlessSourceProof(
        string PhysicalObjectIdentity,
        long Length,
        string Sha256);
}
