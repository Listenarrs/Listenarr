using Listenarr.Domain.Audiobooks.Enumerations;
using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence;

public sealed class FileRegistrationRecoveryProbe(
    IDbContextFactory<ListenArrDbContext> dbContextFactory) :
    IFileRegistrationRecoveryProbe
{
    public async Task<bool> HasBlockingAsync(
        int audiobookId,
        CancellationToken cancellationToken = default)
    {
        if (audiobookId <= 0)
        {
            return false;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.FileMutationJournals
            .AsNoTracking()
            .AnyAsync(journal =>
                journal.AudiobookId == audiobookId
                && journal.AudiobookFileId == null
                && (journal.Action == FileAction.Move
                    || journal.Action == FileAction.Copy
                    || journal.Action == FileAction.HardlinkCopy)
                && journal.State != FileMutationJournalState.Completed
                && journal.State != FileMutationJournalState.RolledBack,
                cancellationToken);
    }

    public async Task<bool> HasBlockingBoundaryAsync(
        string boundaryPath,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken = default) =>
        (await GetBlockingBoundaryAsync(
            boundaryPath,
            semantics,
            cancellationToken)).Count > 0;

    public async Task<IReadOnlyList<FileRegistrationRecoveryBlocker>>
        GetBlockingBoundaryAsync(
            string boundaryPath,
            FileSystemPathSemantics semantics,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boundaryPath);
        var canonicalBoundary = FileSystemPathIdentity.Canonicalize(
            boundaryPath,
            semantics.Syntax);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var journals = await db.FileMutationJournals
            .AsNoTracking()
            .Where(journal =>
                journal.AudiobookFileId == null
                && (journal.Action == FileAction.Move
                    || journal.Action == FileAction.Copy
                    || journal.Action == FileAction.HardlinkCopy)
                && journal.State != FileMutationJournalState.Completed
                && journal.State != FileMutationJournalState.RolledBack)
            .Select(journal => new
            {
                journal.OperationId,
                journal.State,
                journal.Action,
                journal.AudiobookId,
                journal.SourcePath,
                journal.DestinationPath
            })
            .ToListAsync(cancellationToken);

        return journals.Select(journal =>
        {
            var sourceTouches = FileSystemPathIdentity.StoredPathMayTouchBoundary(
                journal.SourcePath,
                canonicalBoundary,
                semantics);
            var destinationTouches = FileSystemPathIdentity.StoredPathMayTouchBoundary(
                journal.DestinationPath,
                canonicalBoundary,
                semantics);
            return new FileRegistrationRecoveryBlocker(
                journal.OperationId,
                journal.State,
                journal.Action,
                journal.AudiobookId,
                journal.AudiobookId.HasValue
                    ? "Audiobook"
                    : journal.State == FileMutationJournalState.RollbackAuthorized
                        ? "StartupRecovery"
                        : "Unknown",
                sourceTouches,
                destinationTouches,
                journal.State == FileMutationJournalState.NeedsAttention
                    ? FileRegistrationRecoveryDisposition.RequiresOperatorAttention
                    : journal.AudiobookId.HasValue
                        ? FileRegistrationRecoveryDisposition.WaitingForOwnerRetry
                        : FileRegistrationRecoveryDisposition.AutomaticRecovery,
                journal.State == FileMutationJournalState.NeedsAttention
                    ? "This file publication requires operator repair."
                    : "This file publication is waiting for restart recovery.");
        })
        .Where(blocker => blocker.SourceTouchesBoundary
            || blocker.DestinationTouchesBoundary)
        .OrderBy(blocker => blocker.OperationId)
        .ToList();
    }
}
