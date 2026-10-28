using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence;

public sealed class FileRenameRecoveryProbe(
    IDbContextFactory<ListenArrDbContext> dbContextFactory) :
    IFileRenameRecoveryProbe
{
    public async Task<bool> HasBlockingBoundaryAsync(
        string boundaryPath,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boundaryPath);
        var boundary = FileSystemPathIdentity.Canonicalize(boundaryPath, semantics.Syntax);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var legacy = await db.FileMutationJournals.AsNoTracking()
            .Where(journal => journal.AudiobookFileId != null
                && (journal.AudiobookFileId == FileMutationOwner.CompanionFile
                    || journal.AudiobookFileId == FileMutationOwner.RegistrationCompanionFile
                    ? journal.State != FileMutationJournalState.Completed
                        && journal.State != FileMutationJournalState.CompletedSourceRetained
                        && journal.State != FileMutationJournalState.RolledBack
                    : journal.State != FileMutationJournalState.OwnerMetadataReconciled
                        && journal.State != FileMutationJournalState.RolledBack))
            .Select(journal => new { journal.SourcePath, journal.DestinationPath })
            .ToListAsync(cancellationToken);
        var verified = await db.VerifiedFileRenameJournals.AsNoTracking()
            .Where(journal => journal.State != VerifiedFileRenameState.Completed
                && journal.State != VerifiedFileRenameState.CompletedSourceRetained
                && journal.State != VerifiedFileRenameState.RolledBack)
            .Select(journal => new { journal.SourcePath, journal.DestinationPath })
            .ToListAsync(cancellationToken);
        return legacy.Concat(verified).Any(journal =>
            FileSystemPathIdentity.StoredPathMayTouchBoundary(journal.SourcePath, boundary, semantics)
            || FileSystemPathIdentity.StoredPathMayTouchBoundary(journal.DestinationPath, boundary, semantics));
    }

    public async Task<bool> HasBlockingAsync(
        int audiobookId,
        CancellationToken cancellationToken = default)
    {
        if (audiobookId <= 0)
        {
            return false;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.FileMutationJournals
            .AsNoTracking()
            .AnyAsync(journal =>
                journal.AudiobookId == audiobookId
                && journal.AudiobookFileId != null
                && (journal.AudiobookFileId == FileMutationOwner.CompanionFile
                    || journal.AudiobookFileId
                        == FileMutationOwner.RegistrationCompanionFile
                    ? journal.State != FileMutationJournalState.Completed
                        && journal.State
                            != FileMutationJournalState.CompletedSourceRetained
                        && journal.State != FileMutationJournalState.RolledBack
                    : journal.State
                            != FileMutationJournalState.OwnerMetadataReconciled
                        && journal.State != FileMutationJournalState.RolledBack),
                cancellationToken))
        {
            return true;
        }

        return await db.VerifiedFileRenameJournals
            .AsNoTracking()
            .AnyAsync(journal =>
                journal.AudiobookId == audiobookId
                && journal.State != VerifiedFileRenameState.Completed
                && journal.State != VerifiedFileRenameState.CompletedSourceRetained
                && journal.State != VerifiedFileRenameState.RolledBack,
                cancellationToken);
    }
}
