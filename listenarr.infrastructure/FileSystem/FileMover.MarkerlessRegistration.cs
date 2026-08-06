using System.ComponentModel;
using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private readonly record struct MarkerlessRegistrationPreparation(
        bool Handled,
        IAudiobookFileRegistrationLease? Lease);

    private async Task<MarkerlessRegistrationPreparation>
        TryPrepareActionForRegistrationMarkerlessAsync(
            FileAction action,
            string source,
            string destination,
            Guid? operationId,
            string? expectedRegisteredPhysicalObjectIdentity)
    {
        if (!operationId.HasValue || _fileMutationJournalStore == null)
        {
            return new MarkerlessRegistrationPreparation(false, null);
        }
        if (operationId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "A markerless registration publication requires a non-empty operation ID.",
                nameof(operationId));
        }

        using var gate = await TryAcquireFileMoveGateAsync(
            source,
            destination,
            allowExistingAliasForRecovery: true);
        if (gate == null)
        {
            return new MarkerlessRegistrationPreparation(true, null);
        }

        var cancellationToken = CancellationToken.None;
        var journal = await _fileMutationJournalStore.GetAsync(
            operationId.Value,
            cancellationToken);
        if (journal == null)
        {
            using var initialSource = gate.SourceParent.TryOpenExistingFile(
                gate.SourceName,
                requireDeleteAccess: false);
            using var initialDestination = gate.DestinationParent.TryOpenExistingFile(
                gate.DestinationName,
                requireDeleteAccess: false);
            if (initialSource == null || !initialSource.VisiblePathMatches())
            {
                return new MarkerlessRegistrationPreparation(true, null);
            }

            var proof = await CaptureMarkerlessSourceProofAsync(
                initialSource,
                cancellationToken);
            if (initialDestination != null
                && (!initialDestination.VisiblePathMatches()
                    || !await initialDestination.MatchesAsync(
                        proof.Length,
                        proof.Sha256,
                        cancellationToken)))
            {
                return new MarkerlessRegistrationPreparation(true, null);
            }

            journal = await _fileMutationJournalStore.GetOrCreateAsync(
                new FileMutationJournalClaim(
                    operationId.Value,
                    action,
                    gate.SourcePath,
                    gate.DestinationPath,
                    proof.PhysicalObjectIdentity,
                    proof.Length,
                    proof.Sha256),
                cancellationToken);
            if (initialDestination != null)
            {
                journal = await _fileMutationJournalStore.AdvanceAsync(
                    journal.OperationId,
                    FileMutationJournalState.TargetIdentityPersisted,
                    initialDestination.GetObjectIdentity(),
                    audiobookId: null,
                    error: null,
                    cancellationToken);
            }
        }
        else
        {
            ValidateMarkerlessRegistrationJournal(journal, action, gate);
        }

        if (journal.State == FileMutationJournalState.NeedsAttention)
        {
            return new MarkerlessRegistrationPreparation(true, null);
        }

        if (journal.State == FileMutationJournalState.Planned)
        {
            journal = await PublishMarkerlessRegistrationTargetAsync(
                action,
                gate,
                journal,
                cancellationToken);
            if (journal.State == FileMutationJournalState.NeedsAttention)
            {
                return new MarkerlessRegistrationPreparation(true, null);
            }
        }

        if (journal.State == FileMutationJournalState.TargetIdentityPersisted)
        {
            journal = await VerifyMarkerlessRegistrationTargetAsync(
                gate,
                journal,
                cancellationToken);
            if (journal.State == FileMutationJournalState.NeedsAttention)
            {
                return new MarkerlessRegistrationPreparation(true, null);
            }
        }
        else if (journal.State >= FileMutationJournalState.TargetVerified)
        {
            if (!await MarkerlessRegistrationTargetMatchesAsync(
                    gate,
                    journal,
                    cancellationToken))
            {
                await MarkMarkerlessRegistrationNeedsAttentionAsync(
                    journal,
                    "The verified registration destination changed physical generation or content.",
                    cancellationToken);
                return new MarkerlessRegistrationPreparation(true, null);
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedRegisteredPhysicalObjectIdentity)
            && !string.Equals(
                journal.TargetPhysicalObjectIdentity,
                expectedRegisteredPhysicalObjectIdentity,
                StringComparison.Ordinal))
        {
            await MarkMarkerlessRegistrationNeedsAttentionAsync(
                journal,
                "Durable audiobook ownership identifies a different destination generation.",
                cancellationToken);
            return new MarkerlessRegistrationPreparation(true, null);
        }

        var targetEntry = gate.DestinationParent.OpenExistingFileForStableRead(
            gate.DestinationName);
        try
        {
            if (!TargetMatchesMarkerlessJournal(targetEntry, journal)
                || !await targetEntry.MatchesAsync(
                    journal.SourceLength,
                    journal.SourceSha256,
                    cancellationToken))
            {
                targetEntry.Dispose();
                await MarkMarkerlessRegistrationNeedsAttentionAsync(
                    journal,
                    "The registration destination changed while its lease was opened.",
                    cancellationToken);
                return new MarkerlessRegistrationPreparation(true, null);
            }

            var lease = PinnedAudiobookFileRegistrationLease.Create(
                targetEntry,
                gate.DestinationPath,
                journal.TargetPhysicalObjectIdentity,
                journal.SourcePhysicalObjectIdentity,
                commitRegistration: audiobookId => CommitMarkerlessRegistration(
                    journal.OperationId,
                    action,
                    journal.TargetPhysicalObjectIdentity!,
                    audiobookId));
            targetEntry = null!;
            return new MarkerlessRegistrationPreparation(true, lease);
        }
        finally
        {
            targetEntry?.Dispose();
        }
    }

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
                && existingTarget.VisiblePathMatches()
                && string.Equals(
                    existingTarget.GetObjectIdentity(),
                    journal.SourcePhysicalObjectIdentity,
                    StringComparison.Ordinal)
                && await existingTarget.MatchesAsync(
                    journal.SourceLength,
                    journal.SourceSha256,
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
                publishedHardlink = sourceEntry.CreateHardLinkTo(
                    gate.DestinationParent,
                    gate.DestinationName);
                targetIdentity = publishedHardlink.GetObjectIdentity();
                return await _fileMutationJournalStore!.AdvanceAsync(
                    journal.OperationId,
                    FileMutationJournalState.TargetIdentityPersisted,
                    targetIdentity,
                    audiobookId: null,
                    error: null,
                    cancellationToken);
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

        using var created = gate.DestinationParent.CreateNewFile(
            gate.DestinationName);
        targetIdentity = created.GetObjectIdentity();
        return await _fileMutationJournalStore!.AdvanceAsync(
            journal.OperationId,
            FileMutationJournalState.TargetIdentityPersisted,
            targetIdentity,
            audiobookId: null,
            error: null,
            cancellationToken);
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

        if (!await targetEntry.MatchesAsync(
                journal.SourceLength,
                journal.SourceSha256,
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
            if (!TargetMatchesMarkerlessJournal(targetEntry, journal)
                || !await targetEntry.MatchesAsync(
                    journal.SourceLength,
                    journal.SourceSha256,
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
            && await targetEntry.MatchesAsync(
                journal.SourceLength,
                journal.SourceSha256,
                cancellationToken);
    }

    private bool CommitMarkerlessRegistration(
        Guid operationId,
        FileAction action,
        string targetPhysicalObjectIdentity,
        int audiobookId)
    {
        var journal = _fileMutationJournalStore!.Get(operationId)
            ?? throw new InvalidOperationException(
                "The markerless registration journal no longer exists.");
        if (journal.ProtocolVersion != FileMutationProtocol.MarkerlessDatabaseState
            || journal.Action != action
            || !string.Equals(
                journal.TargetPhysicalObjectIdentity,
                targetPhysicalObjectIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The markerless registration identity changed before commit.");
        }
        if (journal.State == FileMutationJournalState.NeedsAttention)
        {
            throw new InvalidOperationException(
                "A markerless registration requiring attention cannot be committed.");
        }
        if (journal.State < FileMutationJournalState.TargetVerified)
        {
            throw new InvalidOperationException(
                "The markerless registration destination is not verified.");
        }

        if (journal.State < FileMutationJournalState.RegistrationCommitted)
        {
            journal = _fileMutationJournalStore.Advance(
                operationId,
                FileMutationJournalState.RegistrationCommitted,
                targetPhysicalObjectIdentity,
                audiobookId,
                error: null);
        }
        else if (!journal.AudiobookId.HasValue)
        {
            journal = _fileMutationJournalStore.Advance(
                operationId,
                journal.State,
                targetPhysicalObjectIdentity,
                audiobookId,
                error: null);
        }
        else if (journal.AudiobookId.Value != audiobookId)
        {
            throw new InvalidOperationException(
                "The markerless registration journal is committed to another audiobook.");
        }

        if (action != FileAction.Move
            && journal.State < FileMutationJournalState.Completed)
        {
            journal = _fileMutationJournalStore.Advance(
                operationId,
                FileMutationJournalState.Completed,
                targetPhysicalObjectIdentity,
                audiobookId,
                error: null);
        }

        return journal.State != FileMutationJournalState.NeedsAttention
            && (action == FileAction.Move
                ? journal.State >= FileMutationJournalState.RegistrationCommitted
                : journal.State >= FileMutationJournalState.Completed);
    }

    private static void ValidateMarkerlessRegistrationJournal(
        FileMutationJournal journal,
        FileAction action,
        FileMoveGateLease gate)
    {
        if (journal.ProtocolVersion != FileMutationProtocol.MarkerlessDatabaseState
            || journal.Action != action
            || !string.Equals(
                journal.SourcePath,
                Path.GetFullPath(gate.SourcePath),
                StringComparison.Ordinal)
            || !string.Equals(
                journal.DestinationPath,
                Path.GetFullPath(gate.DestinationPath),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The durable registration identity does not match the requested operation.");
        }
    }

    private async Task MarkMarkerlessRegistrationNeedsAttentionAsync(
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
            "Markerless registration publication {OperationId} requires attention: {Reason}",
            journal.OperationId,
            reason);
    }
}
