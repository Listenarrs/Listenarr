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
                && journal.Action == FileAction.Move
                && journal.State != FileMutationJournalState.Completed,
                cancellationToken);
    }

    public async Task<bool> HasBlockingBoundaryAsync(
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
                && journal.State != FileMutationJournalState.Completed)
            .Select(journal => new
            {
                journal.SourcePath,
                journal.DestinationPath
            })
            .ToListAsync(cancellationToken);

        return journals.Any(journal =>
            FileSystemPathIdentity.StoredPathMayTouchBoundary(
                journal.SourcePath,
                canonicalBoundary,
                semantics)
            || FileSystemPathIdentity.StoredPathMayTouchBoundary(
                journal.DestinationPath,
                canonicalBoundary,
                semantics));
    }
}
