using System.ComponentModel;
using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private async Task<FileMutationJournal> PublishMarkerlessRegistrationTargetAsync(
        FileAction action,
        FileMoveGateLease gate,
        FileMutationJournal journal,
        Action<PinnedDirectoryCreation.PinnedFileEntry> captureCreatedTarget,
        CancellationToken cancellationToken)
    {
        using var sourceEntry = gate.SourceParent.TryOpenExistingFile(
            gate.SourceName,
            requireDeleteAccess: false);
        using var existingTarget = gate.DestinationParent.TryOpenExistingFile(
            gate.DestinationName,
            requireDeleteAccess: false);

        var sourceSharesDestinationVolume = sourceEntry != null
            && !ForceCrossVolumeForTest
            && sourceEntry.IsOnSameVolume(gate.DestinationParent);
        // A Move no longer requires the destination to share the source's
        // kernel identity. v3 publishes verified bytes, then retires the exact
        // source only while its live pinned entry is still held.
        using var sourceHandle = sourceEntry?.DuplicateHandleForOperation();
        var requiresGenerationPreservingLink = action == FileAction.HardlinkCopy
            && sourceHandle != null
            && !ForceCopyForHardlinkForTest
            && HardlinkIdentityCapability.CanVerify(sourceHandle);

        if (existingTarget != null)
        {
            if (requiresGenerationPreservingLink
                && sourceEntry != null
                && VisiblePathMatchesOrThrowUnavailable(
                    sourceEntry,
                    "The hardlink registration source is temporarily unavailable while interrupted publication is being verified.")
                && VisiblePathMatchesOrThrowUnavailable(
                    existingTarget,
                    "The hardlink registration destination is temporarily unavailable while interrupted publication is being verified.")
                && sourceEntry.IdentifiesSameEntry(existingTarget)
                && await MatchesMarkerlessSourceProofAsync(
                    sourceEntry,
                    journal,
                    cancellationToken)
                && await MatchesMarkerlessTargetContentAsync(
                    existingTarget,
                    journal,
                    cancellationToken))
            {
                return await _fileMutationJournalStore!.AdvanceAsync(
                    journal.OperationId,
                    FileMutationJournalState.TargetIdentityPersisted,
                    targetPhysicalObjectIdentity: null,
                    audiobookId: null,
                    error: null,
                    cancellationToken);
            }

            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                journal,
                "A registration destination appeared before its physical identity was persisted.",
                cancellationToken);
            return await _fileMutationJournalStore!.GetAsync(
                journal.OperationId,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    "The markerless registration journal disappeared.");
        }

        if (sourceEntry == null
            || !await MatchesMarkerlessSourceProofAsync(
                sourceEntry,
                journal,
                cancellationToken))
        {
            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                journal,
                "The registration source changed before destination publication.",
                cancellationToken);
            return await _fileMutationJournalStore!.GetAsync(
                journal.OperationId,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    "The markerless registration journal disappeared.");
        }

        PinnedDirectoryCreation.PinnedFileEntry? publishedHardlink = null;
        if (requiresGenerationPreservingLink
            && sourceSharesDestinationVolume)
        {
            try
            {
                if (BeforePinnedHardlinkCreationForTestAsync != null)
                {
                    await BeforePinnedHardlinkCreationForTestAsync();
                }
                publishedHardlink = sourceEntry.CreateHardLinkTo(
                    gate.DestinationParent,
                    gate.DestinationName);
            }
            catch (Exception exception) when (exception is
                IOException or Win32Exception or PlatformNotSupportedException or InvalidOperationException)
            {
                using var failedTarget = gate.DestinationParent.TryOpenExistingFile(
                    gate.DestinationName, requireDeleteAccess: false);
                if (failedTarget != null)
                {
                    // A link may have been created before verification failed.
                    // Its presence is not authority to overwrite or remove it.
                    await MarkMarkerlessRegistrationNeedsAttentionAsync(journal,
                        "Hardlink publication could not verify the created destination; source and destination were preserved for repair.",
                        cancellationToken);
                    return await _fileMutationJournalStore!.GetAsync(journal.OperationId, cancellationToken)
                        ?? throw new InvalidOperationException("The hardlink publication journal disappeared.");
                }
                if (exception is InvalidOperationException) throw;

                _logger.LogInformation(
                    exception,
                    "Markerless hardlink publication was unavailable; falling back to a direct final-name copy: {Source} -> {Destination}",
                    LogRedaction.SanitizeFilePath(gate.SourcePath),
                    LogRedaction.SanitizeFilePath(gate.DestinationPath));
            }
            if (publishedHardlink != null)
            {
                using (publishedHardlink)
                {
                    captureCreatedTarget(publishedHardlink);
                    if (AfterMarkerlessRegistrationTargetCreatedBeforeStateForTestAsync != null)
                        await AfterMarkerlessRegistrationTargetCreatedBeforeStateForTestAsync();
                    journal = await _fileMutationJournalStore!.AdvanceAsync(
                        journal.OperationId, FileMutationJournalState.TargetIdentityPersisted,
                        targetPhysicalObjectIdentity: null, audiobookId: null, error: null, cancellationToken);
                    if (AfterMarkerlessRegistrationTargetStateForTestAsync != null)
                        await AfterMarkerlessRegistrationTargetStateForTestAsync();
                    return journal;
                }
            }
        }

        journal = await EnsureMarkerlessSourceHashAsync(
            sourceEntry,
            journal,
            cancellationToken);
        using var created = gate.DestinationParent.CreateNewFile(
            gate.DestinationName);
        captureCreatedTarget(created);
        if (AfterMarkerlessRegistrationTargetCreatedBeforeStateForTestAsync != null)
        {
            await AfterMarkerlessRegistrationTargetCreatedBeforeStateForTestAsync();
        }
        journal = await _fileMutationJournalStore!.AdvanceAsync(
            journal.OperationId,
            FileMutationJournalState.TargetIdentityPersisted,
            targetPhysicalObjectIdentity: null,
            audiobookId: null,
            error: null,
            cancellationToken);
        if (AfterMarkerlessRegistrationTargetStateForTestAsync != null)
        {
            await AfterMarkerlessRegistrationTargetStateForTestAsync();
        }
        return journal;
    }

    private async Task<FileMutationJournal> VerifyMarkerlessRegistrationTargetAsync(
        FileMoveGateLease gate,
        FileMutationJournal journal,
        CancellationToken cancellationToken,
        bool requirePhysicalIdentity = true,
        bool allowContentPublication = true,
        PinnedDirectoryCreation.PinnedFileEntry? liveTargetEntry = null)
    {
        using var reopenedTarget = liveTargetEntry == null
            ? gate.DestinationParent.TryOpenExistingFile(
                gate.DestinationName,
                requireDeleteAccess: false)
            : null;
        var targetEntry = liveTargetEntry ?? reopenedTarget;
        if (targetEntry == null
            || !VisiblePathMatchesOrThrowUnavailable(
                targetEntry,
                "The registration destination is temporarily unavailable during verification.")
            || (requirePhysicalIdentity
                && !TargetMatchesMarkerlessJournal(targetEntry, journal)))
        {
            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                journal,
                "The registration destination changed before content verification.",
                cancellationToken);
            return await _fileMutationJournalStore!.GetAsync(
                journal.OperationId,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    "The markerless registration journal disappeared.");
        }

        if (!await MatchesMarkerlessTargetContentAsync(
                targetEntry,
                journal,
                cancellationToken))
        {
            if (!allowContentPublication)
            {
                await MarkMarkerlessRegistrationNeedsAttentionAsync(
                    journal,
                    "The interrupted registration destination has unverified content; it was preserved for review.",
                    cancellationToken);
                return await _fileMutationJournalStore!.GetAsync(
                    journal.OperationId,
                    cancellationToken)
                    ?? throw new InvalidOperationException(
                        "The markerless registration journal disappeared.");
            }

            using var sourceEntry = gate.SourceParent.TryOpenExistingFile(
                gate.SourceName,
                requireDeleteAccess: false);
            if (sourceEntry == null
                || !await MatchesMarkerlessSourceProofAsync(
                    sourceEntry,
                    journal,
                    cancellationToken))
            {
                await MarkMarkerlessRegistrationNeedsAttentionAsync(
                    journal,
                    "The registration source is unavailable before destination content was verified.",
                    cancellationToken);
                return await _fileMutationJournalStore!.GetAsync(
                    journal.OperationId,
                    cancellationToken)
                    ?? throw new InvalidOperationException(
                        "The markerless registration journal disappeared.");
            }

            await CopyMarkerlessFileAsync(
                sourceEntry,
                targetEntry,
                cancellationToken);
            sourceEntry.PreserveMarkerlessMetadataTo(targetEntry);
            if (AfterMarkerlessRegistrationTargetWrittenBeforeVerifiedStateForTestAsync != null)
            {
                await AfterMarkerlessRegistrationTargetWrittenBeforeVerifiedStateForTestAsync();
            }
            if (!VisiblePathMatchesOrThrowUnavailable(
                    targetEntry,
                    "The registration destination is temporarily unavailable after content publication.")
                || (requirePhysicalIdentity
                    && !TargetMatchesMarkerlessJournal(targetEntry, journal))
                || !await MatchesMarkerlessTargetContentAsync(
                    targetEntry,
                    journal,
                    cancellationToken))
            {
                throw new IOException(
                    "The markerless registration destination failed content verification.");
            }
        }

        return await _fileMutationJournalStore!.AdvanceAsync(
            journal.OperationId,
            FileMutationJournalState.TargetVerified,
            journal.TargetPhysicalObjectIdentity,
            audiobookId: null,
            error: null,
            cancellationToken);
    }

    private static async Task<bool> MarkerlessRegistrationTargetMatchesAsync(
        FileMoveGateLease gate,
        FileMutationJournal journal,
        CancellationToken cancellationToken,
        bool requirePhysicalIdentity = true)
    {
        using var targetEntry = gate.DestinationParent.TryOpenExistingFile(
            gate.DestinationName,
            requireDeleteAccess: false);
        return targetEntry != null
            && VisiblePathMatchesOrThrowUnavailable(
                targetEntry,
                "The registration destination is temporarily unavailable while its content is being verified.")
            && (!requirePhysicalIdentity
                || TargetMatchesMarkerlessJournal(targetEntry, journal))
            && await MatchesMarkerlessTargetContentAsync(
                targetEntry,
                journal,
                cancellationToken);
    }
}
