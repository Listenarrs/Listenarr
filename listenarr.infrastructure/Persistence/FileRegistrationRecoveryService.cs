using Listenarr.Domain.Audiobooks.Enumerations;
using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence;

/// <summary>
/// Adopts registration publications after audiobook ownership was committed and
/// resumes any remaining source retirement. These journals are separate from
/// organize/rename recovery because they do not own an AudiobookFileId.
/// </summary>
public sealed partial class FileRegistrationRecoveryService(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    IFileMover fileMover,
    TimeProvider timeProvider,
    ILogger<FileRegistrationRecoveryService> logger) :
    IFileRegistrationRecoveryService
{
    public async Task AdoptCommittedAnonymousAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureCurrentRecoveryProtocolAsync(cancellationToken);
        await AdoptCommittedAnonymousPublicationsAsync(
            audiobookId: null,
            operationId: null,
            cancellationToken);
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await AdoptCommittedAnonymousAsync(cancellationToken);
        await ReconcileOrphanedAnonymousPublicationsAsync(
            operationId: null,
            cancellationToken);
        await LogRegistrationPublicationSummaryAsync(cancellationToken);
        await using var readContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var attentionOperationId = await readContext.FileMutationJournals
            .AsNoTracking()
            .Where(RegistrationPublicationPredicate)
            .Where(journal => journal.State == FileMutationJournalState.NeedsAttention)
            .OrderBy(journal => journal.CreatedAt)
            .ThenBy(journal => journal.OperationId)
            .Select(journal => (Guid?)journal.OperationId)
            .FirstOrDefaultAsync(cancellationToken);
        if (attentionOperationId.HasValue)
        {
            throw new InvalidOperationException(
                $"File-registration publication {attentionOperationId.Value} requires operator repair before filesystem mutations can resume.");
        }

        var operationIds = await readContext.FileMutationJournals
            .AsNoTracking()
            .Where(RegistrationPublicationOwnerPredicate)
            .Where(journal => journal.State == FileMutationJournalState.TargetVerified
                || journal.State == FileMutationJournalState.RegistrationCommitted
                || journal.State == FileMutationJournalState.SourceDeletionAuthorized
                || journal.State == FileMutationJournalState.SourceDeleted)
            .OrderBy(journal => journal.CreatedAt)
            .ThenBy(journal => journal.OperationId)
            .Select(journal => journal.OperationId)
            .ToListAsync(cancellationToken);

        foreach (var operationId in operationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReconcileOperationAsync(
                operationId,
                failWhenStillPending: false,
                cancellationToken);
        }
    }

    public async Task ReconcileAudiobookAsync(
        int audiobookId,
        CancellationToken cancellationToken = default)
    {
        _ = await ReconcileAudiobookWithReceiptsAsync(
            audiobookId,
            Array.Empty<string>(),
            cancellationToken);
    }

    public async Task<IReadOnlyList<FileRegistrationRecoveryReceipt>>
        ReconcileAudiobookWithReceiptsAsync(
            int audiobookId,
            IReadOnlyCollection<string> requestedSourcePaths,
            CancellationToken cancellationToken = default)
    {
        if (audiobookId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audiobookId));
        }
        ArgumentNullException.ThrowIfNull(requestedSourcePaths);

        await EnsureCurrentRecoveryProtocolAsync(cancellationToken);
        await AdoptCommittedAnonymousPublicationsAsync(
            audiobookId,
            operationId: null,
            cancellationToken);
        await using var readContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var operationIds = await readContext.FileMutationJournals
            .AsNoTracking()
            .Where(RegistrationPublicationOwnerPredicate)
            .Where(journal => journal.AudiobookId == audiobookId
                && journal.State != FileMutationJournalState.Completed
                && journal.State != FileMutationJournalState.RolledBack)
            .OrderBy(journal => journal.CreatedAt)
            .ThenBy(journal => journal.OperationId)
            .Select(journal => journal.OperationId)
            .ToListAsync(cancellationToken);
        var receipts = new List<FileRegistrationRecoveryReceipt>();

        foreach (var operationId in operationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receipt = await ReconcileOperationAsync(
                operationId,
                failWhenStillPending: true,
                cancellationToken);
            if (receipt != null)
            {
                receipts.Add(receipt);
            }
        }

        if (requestedSourcePaths.Count > 0)
        {
            await AppendDurableCompletedReceiptsAsync(
                audiobookId,
                requestedSourcePaths,
                receipts,
                cancellationToken);
        }

        return receipts;
    }

    public async Task<FileRegistrationRecoveryStatus> RetryAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A registration recovery retry requires a non-empty operation ID.",
                nameof(operationId));
        }

        await EnsureCurrentRecoveryProtocolAsync(
            cancellationToken,
            operationId);
        var journal = await LoadRegistrationPublicationAsync(
            operationId,
            cancellationToken);
        if (journal.State == FileMutationJournalState.NeedsAttention)
        {
            return CreateRecoveryStatus(journal);
        }

        if (!FileMutationJournalLifecycle.ClearsRegistrationRecoveryBoundary(
                journal.State))
        {
            if (!journal.AudiobookId.HasValue)
            {
                await AdoptCommittedAnonymousPublicationsAsync(
                    audiobookId: null,
                    operationId,
                    cancellationToken);
                await ReconcileOrphanedAnonymousPublicationsAsync(
                    operationId,
                    cancellationToken);
            }

            journal = await LoadRegistrationPublicationAsync(
                operationId,
                cancellationToken);
            if (journal.AudiobookId.HasValue
                && FileMutationJournalLifecycle.IsRegistrationPublicationRecoverable(
                    journal.State))
            {
                await ReconcileOperationAsync(
                    operationId,
                    failWhenStillPending: false,
                    cancellationToken);
                journal = await LoadRegistrationPublicationAsync(
                    operationId,
                    cancellationToken);
            }
        }

        return CreateRecoveryStatus(journal);
    }

    private async Task<FileMutationJournal> LoadRegistrationPublicationAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var journal = await db.FileMutationJournals
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.OperationId == operationId,
                cancellationToken)
            ?? throw new KeyNotFoundException(
                "File-registration recovery operation not found.");
        if (!IsRegistrationPublicationAction(journal.Action)
            || journal.AudiobookFileId.HasValue)
        {
            throw new InvalidOperationException(
                "The requested operation is not a file-registration publication.");
        }

        return journal;
    }

    private static FileRegistrationRecoveryStatus CreateRecoveryStatus(
        FileMutationJournal journal)
    {
        var needsAttention = FileMutationJournalLifecycle
            .RequiresOperatorAttention(journal.State);
        var recoverable = FileMutationJournalLifecycle
            .IsRegistrationPublicationRecoverable(journal.State);
        var cleared = FileMutationJournalLifecycle
            .ClearsRegistrationRecoveryBoundary(journal.State);
        return new FileRegistrationRecoveryStatus(
            journal.OperationId,
            journal.State,
            journal.AudiobookId,
            cleared
                ? FileRegistrationRecoveryDisposition.Cleared
                : needsAttention || !recoverable
                    ? FileRegistrationRecoveryDisposition.RequiresOperatorAttention
                    : journal.AudiobookId.HasValue
                        ? FileRegistrationRecoveryDisposition.WaitingForOwnerRetry
                        : FileRegistrationRecoveryDisposition.AutomaticRecovery,
            CanRetry: recoverable,
            CanAbandon: false,
            cleared
                ? "The file-registration recovery boundary is clear."
                : needsAttention || !recoverable
                    ? "This publication still requires operator repair; no destructive action was authorized."
                    : "Recovery remains pending because its durable evidence could not yet be reconciled.");
    }

    private async Task<FileRegistrationRecoveryReceipt?> ReconcileOperationAsync(
        Guid operationId,
        bool failWhenStillPending,
        CancellationToken cancellationToken)
    {
        FileMutationJournal journal;
        AudiobookFile registeredFile;
        int audiobookId;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            journal = await db.FileMutationJournals
                .AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId, cancellationToken);
            if (FileMutationJournalLifecycle.ClearsRegistrationRecoveryBoundary(
                    journal.State))
            {
                return null;
            }
            if (journal.State == FileMutationJournalState.NeedsAttention)
            {
                throw RepairRequired(operationId);
            }
            if (!IsRegistrationPublicationOwner(journal)
                || !FileMutationJournalLifecycle.IsRegistrationPublicationRecoverable(
                    journal.State)
                || !journal.AudiobookId.HasValue)
            {
                return null;
            }

            audiobookId = journal.AudiobookId.Value;
            var audiobook = await db.Audiobooks
                .AsNoTracking()
                .Include(candidate => candidate.Files)
                .SingleOrDefaultAsync(candidate => candidate.Id == audiobookId, cancellationToken);
            if (audiobook == null)
            {
                if (!await TryMarkNeedsAttentionAsync(
                        operationId,
                        journal.State,
                        "The committed file-registration move references a missing audiobook.",
                        cancellationToken))
                {
                    return null;
                }
                throw RepairRequired(operationId);
            }

            var matchingFiles = (audiobook.Files ?? [])
                .Where(file => RegisteredPathMatches(file, journal.DestinationPath))
                .ToList();
            if (matchingFiles.Count != 1
                || string.IsNullOrWhiteSpace(matchingFiles[0].PhysicalObjectIdentity))
            {
                if (!await TryMarkNeedsAttentionAsync(
                        operationId,
                        journal.State,
                        "The committed file-registration move no longer has exactly one tracked destination generation.",
                        cancellationToken))
                {
                    return null;
                }
                throw RepairRequired(operationId);
            }

            registeredFile = matchingFiles[0];
        }

        IAudiobookFileRegistrationLease? preparedLease;
        try
        {
            preparedLease = !string.IsNullOrWhiteSpace(journal.SourceSha256)
                ? await fileMover.PrepareActionForRegistrationAsync(
                    journal.Action,
                    journal.SourcePath,
                    journal.DestinationPath,
                    journal.OperationId,
                    registeredFile.PhysicalObjectIdentity!,
                    new FilePublicationSourceProof(
                        journal.SourcePhysicalObjectIdentity,
                        journal.SourceLength,
                        journal.SourceSha256))
                : await fileMover.PrepareActionForRegistrationAsync(
                    journal.Action,
                    journal.SourcePath,
                    journal.DestinationPath,
                    journal.OperationId,
                    registeredFile.PhysicalObjectIdentity!);
        }
        catch (Exception exception) when (IsTransientRecoveryFilesystemException(exception))
        {
            logger.LogWarning(
                exception,
                "File-registration recovery {OperationId} remains pending because its published destination is temporarily unavailable",
                operationId);
            if (failWhenStillPending)
            {
                throw RecoveryPending(operationId);
            }
            return null;
        }

        using var lease = preparedLease;
        if (lease == null)
        {
            await ThrowIfNeedsAttentionAsync(operationId, cancellationToken);
            if (failWhenStillPending)
            {
                throw RecoveryPending(operationId);
            }
            return null;
        }

        if (!lease.PrepareCleanupRecovery(audiobookId)
            || lease.CompletePublication()
                == RegistrationPublicationCompletion.CommittedCleanupPending
            || (journal.Action == FileAction.Move
                && !await fileMover.CompletePreparedMoveAsync(
                    journal.SourcePath,
                    journal.DestinationPath,
                    lease,
                    journal.OperationId)))
        {
            await ThrowIfNeedsAttentionAsync(operationId, cancellationToken);
            if (failWhenStillPending)
            {
                throw RecoveryPending(operationId);
            }
            return null;
        }

        logger.LogInformation(
            "Recovered committed file-registration publication {OperationId} for audiobook {AudiobookId}",
            operationId,
            audiobookId);
        return journal.Action == FileAction.Move
            ? new FileRegistrationRecoveryReceipt(
                journal.OperationId,
                audiobookId,
                journal.SourcePath,
                journal.DestinationPath)
            : null;
    }

    private static bool IsTransientRecoveryFilesystemException(Exception exception)
    {
        if (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
        if (exception is System.ComponentModel.Win32Exception native)
        {
            return native.NativeErrorCode is 5 or 13 or 16 or 30 or 32 or 33;
        }

        return exception is InvalidOperationException { InnerException: not null }
            && IsTransientRecoveryFilesystemException(exception.InnerException);
    }

    private static bool AnonymousTargetGenerationMatches(
        FileMutationJournal left,
        FileMutationJournal right)
    {
        if (!string.Equals(
                left.DestinationPath,
                right.DestinationPath,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(left.TargetPhysicalObjectIdentity)
            || string.IsNullOrWhiteSpace(right.TargetPhysicalObjectIdentity))
        {
            return false;
        }

        return string.Equals(
                left.TargetPhysicalObjectIdentity,
                right.TargetPhysicalObjectIdentity,
                StringComparison.Ordinal)
            || PinnedDirectoryCreation.ArePersistedObjectIdentitiesDurablyEquivalent(
                left.TargetPhysicalObjectIdentity,
                right.TargetPhysicalObjectIdentity);
    }

    private static bool RegisteredGenerationMatches(
        AudiobookFile file,
        string? targetPhysicalObjectIdentity)
    {
        if (string.IsNullOrWhiteSpace(file.PhysicalObjectIdentity)
            || string.IsNullOrWhiteSpace(targetPhysicalObjectIdentity))
        {
            return false;
        }

        return string.Equals(
                file.PhysicalObjectIdentity,
                targetPhysicalObjectIdentity,
                StringComparison.Ordinal)
            || PinnedDirectoryCreation.ArePersistedObjectIdentitiesDurablyEquivalent(
                file.PhysicalObjectIdentity,
                targetPhysicalObjectIdentity);
    }

    private static bool RegisteredPathMatches(AudiobookFile file, string destinationPath)
    {
        if (file.PathIdentityState != PathIdentityState.Valid
            || !file.PathSyntax.HasValue
            || string.IsNullOrWhiteSpace(file.CanonicalPath))
        {
            return false;
        }

        try
        {
            return FileSystemPathIdentity.AreEquivalent(
                file.CanonicalPath,
                destinationPath,
                new FileSystemPathSemantics(
                    file.PathSyntax.Value,
                    file.PathCaseSensitivity));
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsRegistrationPublicationAction(FileAction action) =>
        action is FileAction.Move or FileAction.Copy or FileAction.HardlinkCopy;

    private static bool IsRegistrationPublicationOwner(FileMutationJournal journal) =>
        IsRegistrationPublicationAction(journal.Action)
        && journal.AudiobookId != null
        && journal.AudiobookFileId == null;

    private static System.Linq.Expressions.Expression<Func<FileMutationJournal, bool>>
        RegistrationPublicationOwnerPredicate => journal =>
            (journal.Action == FileAction.Move
                || journal.Action == FileAction.Copy
                || journal.Action == FileAction.HardlinkCopy)
            && journal.AudiobookId != null && journal.AudiobookFileId == null;

    private static System.Linq.Expressions.Expression<Func<FileMutationJournal, bool>>
        RegistrationPublicationPredicate => journal =>
            (journal.Action == FileAction.Move
                || journal.Action == FileAction.Copy
                || journal.Action == FileAction.HardlinkCopy)
            && journal.AudiobookFileId == null;

}
