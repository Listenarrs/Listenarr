using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public sealed partial class VerifiedFileRenameTransactionCoordinator
{
    // A batch keeps only observations. Reopened mutation handles live for one
    // rollback/retirement call, including quarantine and restoration on failure.
    private sealed class VerifiedFileRenameLease : IVerifiedFileRenameLease
    {
        private readonly VerifiedFileRenameTransactionCoordinator _owner;
        private readonly VerifiedFileRenameJournal _journal;
        private readonly RootFolder? _sourceRoot;
        private readonly RootFolder? _targetRoot;
        private readonly FilePublicationObservation _sourceObservation;
        private readonly FilePublicationObservation _targetObservation;
        private readonly FilePublicationSourceProof _proof;
        private readonly ILogger<VerifiedFileRenameTransactionCoordinator> _logger;
        private bool _disposed;

        internal VerifiedFileRenameLease(VerifiedFileRenameTransactionCoordinator owner,
            VerifiedFileRenameJournal journal,
            PinnedDirectoryCreation.PinnedDirectoryAnchor sourceParent,
            PinnedDirectoryCreation.PinnedDirectoryAnchor destinationParent,
            PinnedDirectoryCreation.PinnedFileEntry sourceEntry,
            PinnedDirectoryCreation.PinnedFileEntry targetEntry,
            FilePublicationSourceProof sourceProof,
            ILogger<VerifiedFileRenameTransactionCoordinator> logger,
            RootFolder? sourceRoot, RootFolder? targetRoot)
        {
            _owner = owner;
            _journal = journal;
            _sourceRoot = sourceRoot;
            _targetRoot = targetRoot;
            _proof = sourceProof;
            _logger = logger;
            // Ownership transfers only after this constructor succeeds. The caller
            // disposes its preparation handles in its finally block on failure.
            _sourceObservation = FilePublicationObservation.Capture(sourceEntry);
            _targetObservation = FilePublicationObservation.Capture(targetEntry);
            targetEntry.Dispose();
            sourceEntry.Dispose();
            destinationParent.Dispose();
            sourceParent.Dispose();
        }

        public Guid OperationId => _journal.OperationId;

        private ActiveVerifiedFileRenameLease OpenActive()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var sourceParent = PinnedDirectoryCreation.OpenPinnedConfiguredHierarchy(
                _sourceRoot, Path.GetDirectoryName(_journal.SourcePath)!, false);
            PinnedDirectoryCreation.PinnedDirectoryAnchor? targetParent = null;
            PinnedDirectoryCreation.PinnedFileEntry? source = null;
            PinnedDirectoryCreation.PinnedFileEntry? target = null;
            try
            {
                targetParent = PinnedDirectoryCreation.OpenPinnedConfiguredHierarchy(
                    _targetRoot, Path.GetDirectoryName(_journal.DestinationPath)!, false);
                target = targetParent.OpenExistingFileForStableDelete(Path.GetFileName(_journal.DestinationPath));
                if (!_targetObservation.Matches(target))
                    throw new InvalidOperationException("The observed organize target changed.");
                source = sourceParent.OpenExistingFileForStableDelete(Path.GetFileName(_journal.SourcePath));
                if (!_sourceObservation.Matches(source))
                    throw new SourceObservationChangedException();
                var active = new ActiveVerifiedFileRenameLease(_owner, _journal,
                    sourceParent, targetParent, source, target, _proof, _logger);
                sourceParent = null!;
                targetParent = null;
                source = null;
                target = null;
                return active;
            }
            finally
            {
                target?.Dispose();
                source?.Dispose();
                targetParent?.Dispose();
                sourceParent?.Dispose();
            }
        }

        public async Task<bool> RollBackAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var current = await _owner.GetJournalAsync(OperationId, cancellationToken);
            if (current?.State == VerifiedFileRenameState.RolledBack) return true;
            if (current?.State != VerifiedFileRenameState.TargetVerified) return false;
            try
            {
                await using var active = OpenActive();
                return await active.RollBackAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not (
                OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                await _owner.MarkNeedsAttentionAsync(OperationId,
                    "Organize rollback could not revalidate the current source and target; both were preserved.",
                    CancellationToken.None);
                return false;
            }
        }

        public async Task<VerifiedFileRenameRetirementOutcome> CompleteSourceRetirementAsync(
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var current = await _owner.GetJournalAsync(OperationId, cancellationToken);
            if (current?.State == VerifiedFileRenameState.Completed)
                return VerifiedFileRenameRetirementOutcome.Completed;
            if (current?.State == VerifiedFileRenameState.CompletedSourceRetained)
                return VerifiedFileRenameRetirementOutcome.SourceRetained;
            if (current?.State == VerifiedFileRenameState.SourceDeleted)
            {
                await _owner.AdvanceAsync(OperationId, VerifiedFileRenameState.Completed,
                    error: null, cancellationToken);
                return VerifiedFileRenameRetirementOutcome.Completed;
            }
            if (current?.State != VerifiedFileRenameState.OwnerMetadataReconciled)
                return VerifiedFileRenameRetirementOutcome.NeedsAttention;
            try
            {
                await using var active = OpenActive();
                return await active.CompleteSourceRetirementAsync(cancellationToken);
            }
            catch (SourceObservationChangedException)
            {
                await _owner.MarkSourceRetainedAsync(OperationId,
                    "The observed source changed before retirement; the source was retained.", CancellationToken.None);
                return VerifiedFileRenameRetirementOutcome.SourceRetained;
            }
            catch (Exception exception) when (exception is not (
                OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                await _owner.MarkNeedsAttentionAsync(OperationId,
                    "Organize cleanup could not revalidate the current source and target; both were preserved.",
                    CancellationToken.None);
                return VerifiedFileRenameRetirementOutcome.NeedsAttention;
            }
        }

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }

        private sealed class SourceObservationChangedException : InvalidOperationException { }
    }

    private sealed class ActiveVerifiedFileRenameLease(
        VerifiedFileRenameTransactionCoordinator owner,
        VerifiedFileRenameJournal journal,
        PinnedDirectoryCreation.PinnedDirectoryAnchor sourceParent,
        PinnedDirectoryCreation.PinnedDirectoryAnchor destinationParent,
        PinnedDirectoryCreation.PinnedFileEntry sourceEntry,
        PinnedDirectoryCreation.PinnedFileEntry targetEntry,
        FilePublicationSourceProof sourceProof,
        ILogger<VerifiedFileRenameTransactionCoordinator> logger)
        : IVerifiedFileRenameLease
    {
        private bool _disposed;

        public Guid OperationId => journal.OperationId;

        public async Task<bool> RollBackAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            var current = await owner.GetJournalAsync(
                journal.OperationId,
                cancellationToken);
            if (current == null)
            {
                return false;
            }
            if (current.State == VerifiedFileRenameState.RolledBack)
            {
                return true;
            }
            if (current.State != VerifiedFileRenameState.TargetVerified)
            {
                return false;
            }

            try
            {
                var targetVisibility = targetEntry.ProbeVisiblePathMatch();
                if (targetVisibility != RegistrationPublicationMatchOutcome.Match
                    || !await targetEntry.MatchesAsync(
                        sourceProof.Length,
                        sourceProof.Sha256,
                        cancellationToken))
                {
                    throw new InvalidOperationException(
                        "The verified organize target changed before rollback.");
                }
                if (sourceEntry.ProbeVisiblePathMatch()
                        != RegistrationPublicationMatchOutcome.Match
                    || !await sourceEntry.MatchesAsync(
                        sourceProof.Length,
                        sourceProof.Sha256,
                        cancellationToken))
                {
                    throw new InvalidOperationException(
                        "The verified organize source changed before rollback.");
                }

                targetEntry.Delete(immediateWindows: true);
                destinationParent.FlushDirectoryEntry();
                await owner.AdvanceAsync(
                    journal.OperationId,
                    VerifiedFileRenameState.RolledBack,
                    error: null,
                    CancellationToken.None);
                return true;
            }
            catch (Exception exception) when (exception is not (
                OperationCanceledException or OutOfMemoryException
                    or StackOverflowException))
            {
                logger.LogWarning(
                    exception,
                    "Verified organize rollback for {OperationId} requires attention",
                    journal.OperationId);
                await owner.MarkNeedsAttentionAsync(
                    journal.OperationId,
                    "Verified organize rollback could not prove the original source and published target remained unchanged.",
                    CancellationToken.None);
                return false;
            }
        }

        public async Task<VerifiedFileRenameRetirementOutcome> CompleteSourceRetirementAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            var current = await owner.GetJournalAsync(
                journal.OperationId,
                cancellationToken);
            if (current == null)
            {
                return VerifiedFileRenameRetirementOutcome.NeedsAttention;
            }
            if (current.State == VerifiedFileRenameState.Completed)
            {
                return VerifiedFileRenameRetirementOutcome.Completed;
            }
            if (current.State == VerifiedFileRenameState.CompletedSourceRetained)
            {
                return VerifiedFileRenameRetirementOutcome.SourceRetained;
            }
            if (current.State == VerifiedFileRenameState.NeedsAttention)
            {
                return VerifiedFileRenameRetirementOutcome.NeedsAttention;
            }
            if (current.State == VerifiedFileRenameState.SourceDeleted)
            {
                await owner.AdvanceAsync(
                    journal.OperationId,
                    VerifiedFileRenameState.Completed,
                    error: null,
                    CancellationToken.None);
                return VerifiedFileRenameRetirementOutcome.Completed;
            }
            if (current.State is not (
                VerifiedFileRenameState.OwnerMetadataReconciled
                    or VerifiedFileRenameState.SourceQuarantined))
            {
                return VerifiedFileRenameRetirementOutcome.NeedsAttention;
            }

            var originalSourceName = Path.GetFileName(journal.SourcePath);
            var retirementName = Path.GetFileName(journal.RetirementPath);
            try
            {
                if (current.State == VerifiedFileRenameState.OwnerMetadataReconciled)
                {
                    var contracts = await owner.ValidateRootContractsAsync(
                        current,
                        cancellationToken);
                    if (contracts != RootContractValidation.Valid)
                    {
                        await owner.MarkSourceRetainedAsync(
                            journal.OperationId,
                            "Owner metadata was committed, but current storage contracts no longer authorize live source retirement. The old source was retained.",
                            CancellationToken.None);
                        return VerifiedFileRenameRetirementOutcome.SourceRetained;
                    }

                    if (!await TargetStillMatchesAsync(cancellationToken))
                    {
                        await owner.MarkNeedsAttentionAsync(
                            journal.OperationId,
                            "Owner metadata was committed, but the verified organize target changed before source retirement. The old source was retained and the tracked destination requires repair.",
                            CancellationToken.None);
                        return VerifiedFileRenameRetirementOutcome.NeedsAttention;
                    }
                    if (!await SourceStillMatchesAsync(cancellationToken))
                    {
                        await owner.MarkSourceRetainedAsync(
                            journal.OperationId,
                            "Owner metadata was committed, but the original pinned source changed before retirement. The source path was retained.",
                            CancellationToken.None);
                        return VerifiedFileRenameRetirementOutcome.SourceRetained;
                    }

                    var quarantine = sourceEntry.TryMoveToNoReplace(
                        sourceParent,
                        retirementName);
                    if (!quarantine.Published)
                    {
                        await owner.MarkSourceRetainedAsync(
                            journal.OperationId,
                            "Owner metadata was committed, but the source could not enter the operation-owned retirement namespace without replacement. The old source was retained.",
                            CancellationToken.None);
                        return VerifiedFileRenameRetirementOutcome.SourceRetained;
                    }
                    sourceParent.FlushDirectoryEntry();
                    await owner.AdvanceAsync(
                        journal.OperationId,
                        VerifiedFileRenameState.SourceQuarantined,
                        error: null,
                        CancellationToken.None);
                    owner.AfterSourceQuarantinedForTest?.Invoke();
                }

                if (!await TargetStillMatchesAsync(cancellationToken))
                {
                    return await RestoreQuarantinedSourceAsync(
                        originalSourceName,
                        "The verified organize target changed after source quarantine. The exact pinned source was restored, but the tracked destination requires repair.",
                        requiresAttention: true);
                }

                owner.BeforeRetirementDeleteForTest?.Invoke();
                if (!await TargetStillMatchesAsync(cancellationToken))
                {
                    return await RestoreQuarantinedSourceAsync(
                        originalSourceName,
                        "The verified organize target changed immediately before source deletion. The exact pinned source was restored, but the tracked destination requires repair.",
                        requiresAttention: true);
                }
                if (!await SourceStillMatchesAsync(cancellationToken))
                {
                    await owner.MarkNeedsAttentionAsync(
                        journal.OperationId,
                        "The operation-owned retirement source changed before deletion. It was preserved for operator review.",
                        CancellationToken.None);
                    return VerifiedFileRenameRetirementOutcome.NeedsAttention;
                }

                sourceEntry.Delete(immediateWindows: true);
                sourceParent.FlushDirectoryEntry();
                await owner.AdvanceAsync(
                    journal.OperationId,
                    VerifiedFileRenameState.SourceDeleted,
                    error: null,
                    CancellationToken.None);
                await owner.AdvanceAsync(
                    journal.OperationId,
                    VerifiedFileRenameState.Completed,
                    error: null,
                    CancellationToken.None);
                return VerifiedFileRenameRetirementOutcome.Completed;
            }
            catch (Exception exception) when (exception is not (
                OperationCanceledException or OutOfMemoryException
                    or StackOverflowException))
            {
                logger.LogWarning(
                    exception,
                    "Verified organize source retirement for {OperationId} did not complete",
                    journal.OperationId);
                var latest = await owner.GetJournalAsync(
                    journal.OperationId,
                    CancellationToken.None);
                if (latest?.State == VerifiedFileRenameState.OwnerMetadataReconciled)
                {
                    if (string.Equals(
                            Path.GetFullPath(sourceEntry.FullPath),
                            Path.GetFullPath(journal.RetirementPath),
                            OperatingSystem.IsWindows()
                                ? StringComparison.OrdinalIgnoreCase
                                : StringComparison.Ordinal))
                    {
                        return await RestoreQuarantinedSourceAsync(
                            originalSourceName,
                            "Live source retirement failed after quarantine. The exact pinned source was restored and retained.");
                    }

                    await owner.MarkSourceRetainedAsync(
                        journal.OperationId,
                        "Owner metadata was committed, but live source retirement failed. The old source may remain and will not be deleted by restart recovery.",
                        CancellationToken.None);
                }
                else if (latest?.State == VerifiedFileRenameState.SourceQuarantined)
                {
                    return await RestoreQuarantinedSourceAsync(
                        originalSourceName,
                        "Live source retirement failed after quarantine. The exact pinned source was restored and retained when possible.");
                }
                return latest?.State switch
                {
                    VerifiedFileRenameState.SourceDeleted or
                    VerifiedFileRenameState.Completed =>
                        VerifiedFileRenameRetirementOutcome.Completed,
                    VerifiedFileRenameState.CompletedSourceRetained =>
                        VerifiedFileRenameRetirementOutcome.SourceRetained,
                    _ => VerifiedFileRenameRetirementOutcome.NeedsAttention
                };
            }
        }

        private async Task<bool> TargetStillMatchesAsync(
            CancellationToken cancellationToken) =>
            targetEntry.ProbeVisiblePathMatch()
                == RegistrationPublicationMatchOutcome.Match
            && await targetEntry.MatchesAsync(
                sourceProof.Length,
                sourceProof.Sha256,
                cancellationToken);

        private async Task<bool> SourceStillMatchesAsync(
            CancellationToken cancellationToken) =>
            sourceEntry.ProbeVisiblePathMatch()
                == RegistrationPublicationMatchOutcome.Match
            && await sourceEntry.MatchesAsync(
                sourceProof.Length,
                sourceProof.Sha256,
                cancellationToken);

        private async Task<VerifiedFileRenameRetirementOutcome> RestoreQuarantinedSourceAsync(
            string originalSourceName,
            string retainedReason,
            bool requiresAttention = false)
        {
            try
            {
                if (!await SourceStillMatchesAsync(CancellationToken.None))
                {
                    throw new InvalidOperationException(
                        "The operation-owned retirement source changed before restoration.");
                }

                var restore = sourceEntry.TryMoveToNoReplace(
                    sourceParent,
                    originalSourceName);
                if (!restore.Published)
                {
                    throw new InvalidOperationException(
                        $"The original source path could not be restored without replacement (native error {restore.NativeErrorCode}).");
                }
                sourceParent.FlushDirectoryEntry();
                if (requiresAttention)
                {
                    await owner.MarkNeedsAttentionAsync(
                        journal.OperationId,
                        retainedReason,
                        CancellationToken.None);
                    return VerifiedFileRenameRetirementOutcome.NeedsAttention;
                }

                await owner.MarkSourceRetainedAsync(
                    journal.OperationId,
                    retainedReason,
                    CancellationToken.None);
                return VerifiedFileRenameRetirementOutcome.SourceRetained;
            }
            catch (Exception exception) when (exception is not (
                OutOfMemoryException or StackOverflowException))
            {
                logger.LogWarning(
                    exception,
                    "Verified organize source quarantine for {OperationId} could not be restored",
                    journal.OperationId);
                await owner.MarkNeedsAttentionAsync(
                    journal.OperationId,
                    "The verified organize source remains in its operation-owned retirement namespace and requires operator repair.",
                    CancellationToken.None);
                return VerifiedFileRenameRetirementOutcome.NeedsAttention;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            targetEntry.Dispose();
            sourceEntry.Dispose();
            destinationParent.Dispose();
            sourceParent.Dispose();
            return ValueTask.CompletedTask;
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
