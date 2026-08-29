using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence;

public sealed class FileRenameRecoveryProbe(
    IDbContextFactory<ListenArrDbContext> dbContextFactory) :
    IFileRenameRecoveryProbe
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
                && journal.AudiobookFileId != null
                && (journal.AudiobookFileId == FileMutationOwner.CompanionFile
                    || journal.AudiobookFileId
                        == FileMutationOwner.RegistrationCompanionFile
                    ? journal.State != FileMutationJournalState.Completed
                    : journal.State != FileMutationJournalState.OwnerMetadataReconciled),
                cancellationToken);
    }
}
