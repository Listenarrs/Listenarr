using Listenarr.Domain.Audiobooks.Enumerations;
using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence;

/// <summary>
/// Resumes source retirement for Move publications after the destination generation
/// and audiobook ownership were already committed. These journals are deliberately
/// separate from organize/rename recovery because they do not own an AudiobookFileId.
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
        await AdoptCommittedAnonymousMoveRegistrationsAsync(
            audiobookId: null,
            cancellationToken);
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await AdoptCommittedAnonymousAsync(cancellationToken);
        await using var readContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var attentionOperationId = await readContext.FileMutationJournals
            .AsNoTracking()
            .Where(RegistrationMoveOwnerPredicate)
            .Where(journal => journal.State == FileMutationJournalState.NeedsAttention)
            .OrderBy(journal => journal.CreatedAt)
            .ThenBy(journal => journal.OperationId)
            .Select(journal => (Guid?)journal.OperationId)
            .FirstOrDefaultAsync(cancellationToken);
        if (attentionOperationId.HasValue)
        {
            throw new InvalidOperationException(
                $"File-registration move journal {attentionOperationId.Value} requires operator repair before filesystem mutations can resume.");
        }

        var operationIds = await readContext.FileMutationJournals
            .AsNoTracking()
            .Where(RegistrationMoveOwnerPredicate)
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
        await AdoptCommittedAnonymousMoveRegistrationsAsync(
            audiobookId,
            cancellationToken);
        await using var readContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var operationIds = await readContext.FileMutationJournals
            .AsNoTracking()
            .Where(RegistrationMoveOwnerPredicate)
            .Where(journal => journal.AudiobookId == audiobookId
                && journal.State != FileMutationJournalState.Completed)
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

    private async Task AdoptCommittedAnonymousMoveRegistrationsAsync(
        int? audiobookId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var anonymousJournals = await db.FileMutationJournals
            .AsNoTracking()
            .Where(journal => journal.Action == FileAction.Move
                && journal.AudiobookId == null
                && journal.AudiobookFileId == null
                && journal.State == FileMutationJournalState.TargetVerified)
            .OrderBy(journal => journal.CreatedAt)
            .ThenBy(journal => journal.OperationId)
            .ToListAsync(cancellationToken);
        if (anonymousJournals.Count == 0)
        {
            return;
        }

        var filesQuery = db.AudiobookFiles.AsNoTracking();
        if (audiobookId.HasValue)
        {
            filesQuery = filesQuery.Where(file => file.AudiobookId == audiobookId.Value);
        }
        var trackedFiles = await filesQuery.ToListAsync(cancellationToken);
        foreach (var journal in anonymousJournals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = trackedFiles
                .Where(file => RegisteredPathMatches(file, journal.DestinationPath)
                    && RegisteredGenerationMatches(
                        file,
                        journal.TargetPhysicalObjectIdentity))
                .ToList();
            if (matches.Count == 0)
            {
                // This is the valid crash-before-metadata-commit state, or an anonymous
                // journal owned by another audiobook during scoped recovery. Leave it
                // anonymous so its own operation/recovery can resolve it later.
                continue;
            }
            if (anonymousJournals.Count(candidate =>
                    AnonymousTargetGenerationMatches(candidate, journal)) != 1)
            {
                throw new InvalidOperationException(
                    $"Anonymous file-registration move journal {journal.OperationId} shares its published target generation with another unowned journal and cannot be adopted safely.");
            }
            if (matches.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Anonymous file-registration move journal {journal.OperationId} matches multiple tracked audiobook files and cannot be adopted safely.");
            }

            var matchedFile = matches[0];
            var adopted = await TryAdoptAnonymousOwnerAsync(
                db,
                journal,
                matchedFile.AudiobookId,
                cancellationToken);
            if (adopted)
            {
                logger.LogInformation(
                    "Adopted committed anonymous file-registration move {OperationId} for audiobook {AudiobookId}",
                    journal.OperationId,
                    matchedFile.AudiobookId);
            }
        }
    }

    private async Task<bool> TryAdoptAnonymousOwnerAsync(
        ListenArrDbContext db,
        FileMutationJournal expected,
        int audiobookId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (!db.Database.IsRelational())
        {
            var tracked = await db.FileMutationJournals.SingleOrDefaultAsync(
                candidate => candidate.OperationId == expected.OperationId,
                cancellationToken);
            if (tracked == null
                || tracked.AudiobookId != null
                || tracked.AudiobookFileId != null
                || tracked.Action != FileAction.Move
                || tracked.State != FileMutationJournalState.TargetVerified
                || !string.Equals(
                    tracked.TargetPhysicalObjectIdentity,
                    expected.TargetPhysicalObjectIdentity,
                    StringComparison.Ordinal))
            {
                return false;
            }

            tracked.AudiobookId = audiobookId;
            tracked.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var affected = await db.FileMutationJournals
            .Where(candidate => candidate.OperationId == expected.OperationId
                && candidate.AudiobookId == null
                && candidate.AudiobookFileId == null
                && candidate.Action == FileAction.Move
                && candidate.State == FileMutationJournalState.TargetVerified
                && candidate.TargetPhysicalObjectIdentity
                    == expected.TargetPhysicalObjectIdentity)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        candidate => candidate.AudiobookId,
                        audiobookId)
                    .SetProperty(
                        candidate => candidate.UpdatedAt,
                        now),
                cancellationToken);
        return affected == 1;
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
            if (journal.State == FileMutationJournalState.Completed)
            {
                return null;
            }
            if (journal.State == FileMutationJournalState.NeedsAttention)
            {
                throw RepairRequired(operationId);
            }
            if (!IsRegistrationMoveOwner(journal)
                || journal.State < FileMutationJournalState.TargetVerified
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
                    FileAction.Move,
                    journal.SourcePath,
                    journal.DestinationPath,
                    journal.OperationId,
                    registeredFile.PhysicalObjectIdentity!,
                    new FilePublicationSourceProof(
                        journal.SourcePhysicalObjectIdentity,
                        journal.SourceLength,
                        journal.SourceSha256))
                : await fileMover.PrepareActionForRegistrationAsync(
                    FileAction.Move,
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
            || !await fileMover.CompletePreparedMoveAsync(
                journal.SourcePath,
                journal.DestinationPath,
                lease,
                journal.OperationId))
        {
            await ThrowIfNeedsAttentionAsync(operationId, cancellationToken);
            if (failWhenStillPending)
            {
                throw RecoveryPending(operationId);
            }
            return null;
        }

        logger.LogInformation(
            "Recovered committed file-registration move {OperationId} for audiobook {AudiobookId}",
            operationId,
            audiobookId);
        return new FileRegistrationRecoveryReceipt(
            journal.OperationId,
            audiobookId,
            journal.SourcePath,
            journal.DestinationPath);
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
        if (string.IsNullOrWhiteSpace(left.TargetPhysicalObjectIdentity)
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

    private static bool IsRegistrationMoveOwner(FileMutationJournal journal) =>
        journal.Action == FileAction.Move
        && journal.AudiobookId != null
        && journal.AudiobookFileId == null;

    private static System.Linq.Expressions.Expression<Func<FileMutationJournal, bool>>
        RegistrationMoveOwnerPredicate => journal =>
            journal.Action == FileAction.Move
            && journal.AudiobookId != null
            && journal.AudiobookFileId == null;

}
