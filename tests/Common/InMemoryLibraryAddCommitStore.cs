namespace Listenarr.Tests.Common;

internal sealed class InMemoryLibraryAddCommitStore(ListenArrDbContext db)
    : ILibraryAddCommitStore
{
    public async Task CommitAsync(
        Audiobook audiobook,
        History history,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        db.Audiobooks.Add(audiobook);
        if (audiobook.Id <= 0)
        {
            throw new InvalidOperationException(
                "The in-memory provider did not allocate an audiobook identity before the atomic test commit.");
        }

        history.AudiobookId = audiobook.Id;
        db.History.Add(history);
        await db.SaveChangesAsync(cancellationToken);
    }
}
