using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public sealed class EfLibraryAddCommitStore(ListenArrDbContext db) : ILibraryAddCommitStore
{
    public async Task CommitAsync(
        Audiobook audiobook,
        History history,
        CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsRelational())
        {
            throw new NotSupportedException(
                "Atomic library adds require a relational database transaction.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync(cancellationToken);
            history.AudiobookId = audiobook.Id;
            db.History.Add(history);
            await db.SaveChangesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // SaveChanges has succeeded, so finish the transaction authoritatively.
            // Request cancellation after this point must not produce an ambiguous
            // "committed but cancelled" result.
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Preserve the original commit failure. Rollback is best effort.
            }

            db.ChangeTracker.Clear();
            throw;
        }
    }
}
