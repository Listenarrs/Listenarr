using Microsoft.Extensions.Logging;
using Listenarr.Domain.Audiobooks.Enumerations;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private async Task<FilePublicationPreparationResult>
        PrepareCompatibilityActionForRegistrationAsync(
            FilePublicationPlan plan,
            string source,
            string destination,
            Guid operationId,
            string? expectedRegisteredPhysicalObjectIdentity,
            FilePublicationSourceProof expectedSourceProof,
            bool isCompanionFile)
    {
        if (_weakPublicationMode == WeakPublicationMode.Disabled)
        {
            return CompatibilityBlocked(
                plan,
                "compatibility_publication_disabled",
                "Compatibility publication is disabled by FileMover:WeakPublicationMode.");
        }
        if (_compatibilityFilePublicationJournalStore == null)
        {
            return CompatibilityBlocked(
                plan,
                "compatibility_journal_unavailable",
                "Compatibility publication requires durable database journal storage.");
        }
        if (operationId == Guid.Empty)
        {
            return CompatibilityBlocked(
                plan,
                "operation_id_required",
                "Compatibility publication requires a non-empty operation ID.");
        }
        if (!string.IsNullOrWhiteSpace(expectedRegisteredPhysicalObjectIdentity))
        {
            return CompatibilityBlocked(
                plan,
                "durable_target_claim_conflict",
                "A path-only publication cannot replace an existing durable target claim.");
        }
        if (plan.EffectiveAction != FileAction.Copy
            || plan.SourceDisposition != FilePublicationSourceDisposition.Retained)
        {
            throw new InvalidOperationException(
                "Compatibility publication is additive copy-and-retain only.");
        }
        if (IsKnownReadOnlyMutationEndpoint(
                FileAction.Copy,
                source,
                destination))
        {
            return CompatibilityBlocked(
                plan,
                "destination_read_only",
                "The compatibility publication destination is read-only.");
        }

        using var gate = await TryAcquireFileMoveGateAsync(
            source,
            destination,
            allowExistingAliasForRecovery: true,
            allowWeakPathOnlyCompatibility: true);
        if (gate == null)
        {
            return CompatibilityBlocked(
                plan,
                "publication_lock_unavailable",
                "The compatibility publication endpoints could not be locked safely.");
        }

        var cancellationToken = CancellationToken.None;
        var journal = await _compatibilityFilePublicationJournalStore.GetOrCreateAsync(
            new CompatibilityFilePublicationClaim(
                operationId,
                plan.RequestedAction,
                gate.SourcePath,
                gate.DestinationPath,
                expectedSourceProof.Length,
                expectedSourceProof.Sha256,
                isCompanionFile),
            cancellationToken);
        if (journal.State == CompatibilityFilePublicationState.NeedsAttention)
        {
            return CompatibilityBlocked(
                plan,
                "publication_needs_attention",
                journal.Error
                    ?? "The compatibility publication requires manual attention.");
        }

        if (journal.State == CompatibilityFilePublicationState.Planned)
        {
            if (File.Exists(gate.DestinationPath))
            {
                await MarkCompatibilityNeedsAttentionAsync(
                    journal.OperationId,
                    "A compatibility destination appeared before target verification. It was preserved without overwrite.",
                    cancellationToken);
                return CompatibilityBlocked(
                    plan,
                    "ambiguous_existing_target",
                    "The destination appeared during compatibility publication and was preserved for manual review.");
            }

            using var sourceStream = OpenCompatibilityRead(gate.SourcePath);
            if (!await CompatibilityStreamMatchesAsync(
                    sourceStream,
                    expectedSourceProof.Length,
                    expectedSourceProof.Sha256,
                    cancellationToken))
            {
                await MarkCompatibilityNeedsAttentionAsync(
                    journal.OperationId,
                    "The compatibility source content changed before publication.",
                    cancellationToken);
                return CompatibilityBlocked(
                    plan,
                    "source_content_changed",
                    "The source changed before compatibility publication.");
            }

            await using var created = new FileStream(
                gate.DestinationPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            sourceStream.Position = 0;
            await sourceStream.CopyToAsync(created, 128 * 1024, cancellationToken);
            await created.FlushAsync(cancellationToken);
            created.Flush(flushToDisk: true);
            if (!await CompatibilityStreamMatchesAsync(
                    created,
                    expectedSourceProof.Length,
                    expectedSourceProof.Sha256,
                    cancellationToken))
            {
                await MarkCompatibilityNeedsAttentionAsync(
                    journal.OperationId,
                    "The compatibility destination failed content verification and was preserved.",
                    cancellationToken);
                return CompatibilityBlocked(
                    plan,
                    "target_verification_failed",
                    "The copied destination could not be verified and was preserved for manual review.");
            }
            PreserveCompatibilityMetadata(gate.SourcePath, gate.DestinationPath);

            journal = await _compatibilityFilePublicationJournalStore.AdvanceAsync(
                journal.OperationId,
                CompatibilityFilePublicationState.TargetVerified,
                expectedSourceProof.Length,
                expectedSourceProof.Sha256,
                audiobookId: null,
                error: null,
                cancellationToken);
        }

        if (!CompatibilityTargetMatches(
                gate.DestinationPath,
                journal.SourceLength,
                journal.SourceSha256))
        {
            await MarkCompatibilityNeedsAttentionAsync(
                journal.OperationId,
                "The verified compatibility destination changed before registration.",
                cancellationToken);
            return CompatibilityBlocked(
                plan,
                "verified_target_changed",
                "The verified compatibility destination changed before registration.");
        }

        var capturedOperationId = journal.OperationId;
        var capturedPath = gate.DestinationPath;
        var capturedLength = journal.SourceLength;
        var capturedHash = journal.SourceSha256;
        var lease = PathOnlyAudiobookFileRegistrationLease.Open(
            capturedPath,
            capturedLength,
            capturedHash,
            commitRegistration: audiobookId =>
                CommitCompatibilityRegistration(
                    capturedOperationId,
                    capturedPath,
                    capturedLength,
                    capturedHash,
                    audiobookId),
            completePublication: () =>
                CompleteCompatibilityPublication(
                    capturedOperationId,
                    capturedPath,
                    capturedLength,
                    capturedHash));

        LogMutation(
            FileMutationOutcome.Success,
            plan.RequestedAction,
            source,
            destination,
            plan.Message);
        return new FilePublicationPreparationResult(
            FilePublicationOutcome.Success,
            plan.RequestedAction,
            FileAction.Copy,
            FilePublicationSourceDisposition.Retained,
            lease,
            plan.ReasonCode,
            plan.Message);
    }

    private bool CommitCompatibilityRegistration(
        Guid operationId,
        string destination,
        long length,
        string sha256,
        int audiobookId)
    {
        var current = _compatibilityFilePublicationJournalStore!.Get(operationId);
        if (current?.State == CompatibilityFilePublicationState.Completed)
        {
            return CompatibilityTargetMatches(destination, length, sha256);
        }
        if (current?.State
            == CompatibilityFilePublicationState.RegistrationCommitted)
        {
            return CompatibilityTargetMatches(destination, length, sha256)
                && current.AudiobookId == audiobookId;
        }
        if (!CompatibilityTargetMatches(destination, length, sha256))
        {
            _compatibilityFilePublicationJournalStore!.Advance(
                operationId,
                CompatibilityFilePublicationState.NeedsAttention,
                error: "The compatibility destination changed before registration commit.");
            return false;
        }

        _compatibilityFilePublicationJournalStore!.Advance(
            operationId,
            CompatibilityFilePublicationState.RegistrationCommitted,
            length,
            sha256,
            audiobookId);
        return true;
    }

    private bool CompleteCompatibilityPublication(
        Guid operationId,
        string destination,
        long length,
        string sha256)
    {
        var current = _compatibilityFilePublicationJournalStore!.Get(operationId);
        if (current?.State == CompatibilityFilePublicationState.Completed)
        {
            return CompatibilityTargetMatches(destination, length, sha256);
        }
        if (!CompatibilityTargetMatches(destination, length, sha256))
        {
            _compatibilityFilePublicationJournalStore!.Advance(
                operationId,
                CompatibilityFilePublicationState.NeedsAttention,
                error: "The compatibility destination changed before completion.");
            return false;
        }

        _compatibilityFilePublicationJournalStore!.Advance(
            operationId,
            CompatibilityFilePublicationState.Completed,
            length,
            sha256);
        return true;
    }

    private static bool CompatibilityTargetMatches(
        string destination,
        long length,
        string sha256)
    {
        try
        {
            using var target = OpenCompatibilityRead(destination);
            return CompatibilityStreamMatchesAsync(
                    target,
                    length,
                    sha256,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }

    private static FileStream OpenCompatibilityRead(string path) => new(
        Path.GetFullPath(path),
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 128 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<bool> CompatibilityStreamMatchesAsync(
        FileStream stream,
        long length,
        string sha256,
        CancellationToken cancellationToken)
    {
        if (stream.Length != length)
        {
            return false;
        }
        stream.Position = 0;
        var actual = Convert.ToHexString(
            await System.Security.Cryptography.SHA256.HashDataAsync(
                stream,
                cancellationToken));
        stream.Position = 0;
        return string.Equals(actual, sha256, StringComparison.Ordinal);
    }

    private static void PreserveCompatibilityMetadata(
        string source,
        string destination)
    {
        try
        {
            File.SetLastWriteTimeUtc(
                destination,
                File.GetLastWriteTimeUtc(source));
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Content publication is authoritative. Weak storage may not support
            // timestamp preservation, so metadata copying remains best effort.
        }
    }

    private async Task MarkCompatibilityNeedsAttentionAsync(
        Guid operationId,
        string error,
        CancellationToken cancellationToken)
    {
        await _compatibilityFilePublicationJournalStore!.AdvanceAsync(
            operationId,
            CompatibilityFilePublicationState.NeedsAttention,
            targetLength: null,
            targetSha256: null,
            audiobookId: null,
            error,
            cancellationToken);
        _logger.LogWarning(
            "Compatibility file publication requires attention for operation {OperationId}: {Reason}",
            operationId,
            error);
    }

    private FilePublicationPreparationResult CompatibilityBlocked(
        FilePublicationPlan plan,
        string reasonCode,
        string message)
    {
        LogMutation(
            FileMutationOutcome.Blocked,
            plan.RequestedAction,
            source: string.Empty,
            destination: null,
            message);
        return new FilePublicationPreparationResult(
            FilePublicationOutcome.Blocked,
            plan.RequestedAction,
            plan.EffectiveAction,
            plan.SourceDisposition,
            ReasonCode: reasonCode,
            Message: message);
    }
}
