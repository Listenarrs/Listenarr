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
        CancellationToken cancellationToken)
    {
        using var sourceEntry = gate.SourceParent.TryOpenExistingFile(
            gate.SourceName,
            requireDeleteAccess: false);
        using var existingTarget = gate.DestinationParent.TryOpenExistingFile(
            gate.DestinationName,
            requireDeleteAccess: false);

        if (existingTarget != null)
        {
            if (action == FileAction.HardlinkCopy
                && sourceEntry != null
                && sourceEntry.VisiblePathMatches()
                && existingTarget.VisiblePathMatches()
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
                    existingTarget.GetObjectIdentity(),
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

        string targetIdentity;
        PinnedDirectoryCreation.PinnedFileEntry? publishedHardlink = null;
        if (action == FileAction.HardlinkCopy
            && sourceEntry.IsOnSameVolume(gate.DestinationParent))
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
                targetIdentity = publishedHardlink.GetObjectIdentity();
                if (AfterMarkerlessRegistrationTargetCreatedBeforeStateForTestAsync != null)
                {
                    await AfterMarkerlessRegistrationTargetCreatedBeforeStateForTestAsync();
                }
                journal = await _fileMutationJournalStore!.AdvanceAsync(
                    journal.OperationId,
                    FileMutationJournalState.TargetIdentityPersisted,
                    targetIdentity,
                    audiobookId: null,
                    error: null,
                    cancellationToken);
                if (AfterMarkerlessRegistrationTargetStateForTestAsync != null)
                {
                    await AfterMarkerlessRegistrationTargetStateForTestAsync();
                }
                return journal;
            }
            catch (Exception exception) when (exception is
                IOException or Win32Exception or PlatformNotSupportedException)
            {
                _logger.LogInformation(
                    exception,
                    "Markerless hardlink publication was unavailable; falling back to a direct final-name copy: {Source} -> {Destination}",
                    LogRedaction.SanitizeFilePath(gate.SourcePath),
                    LogRedaction.SanitizeFilePath(gate.DestinationPath));
            }
            finally
            {
                publishedHardlink?.Dispose();
            }
        }

        journal = await EnsureMarkerlessSourceHashAsync(
            sourceEntry,
            journal,
            cancellationToken);
        using var created = gate.DestinationParent.CreateNewFile(
            gate.DestinationName);
        targetIdentity = created.GetObjectIdentity();
        if (AfterMarkerlessRegistrationTargetCreatedBeforeStateForTestAsync != null)
        {
            await AfterMarkerlessRegistrationTargetCreatedBeforeStateForTestAsync();
        }
        journal = await _fileMutationJournalStore!.AdvanceAsync(
            journal.OperationId,
            FileMutationJournalState.TargetIdentityPersisted,
            targetIdentity,
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
        CancellationToken cancellationToken)
    {
        using var targetEntry = gate.DestinationParent.TryOpenExistingFile(
            gate.DestinationName,
            requireDeleteAccess: false);
        if (targetEntry == null
            || !TargetMatchesMarkerlessJournal(targetEntry, journal))
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
            if (!TargetMatchesMarkerlessJournal(targetEntry, journal)
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
        CancellationToken cancellationToken)
    {
        using var targetEntry = gate.DestinationParent.TryOpenExistingFile(
            gate.DestinationName,
            requireDeleteAccess: false);
        return targetEntry != null
            && TargetMatchesMarkerlessJournal(targetEntry, journal)
            && await MatchesMarkerlessTargetContentAsync(
                targetEntry,
                journal,
                cancellationToken);
    }
}
