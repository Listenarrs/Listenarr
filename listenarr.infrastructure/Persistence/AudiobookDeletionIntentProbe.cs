using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence;

public sealed class AudiobookDeletionIntentProbe(
    IDbContextFactory<ListenArrDbContext> dbContextFactory) :
    IAudiobookDeletionIntentProbe
{
    public async Task<bool> HasActiveAsync(
        int audiobookId,
        CancellationToken cancellationToken = default)
    {
        if (audiobookId <= 0)
        {
            return false;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.AudiobookDeletionIntents
            .AsNoTracking()
            .AnyAsync(intent => intent.AudiobookId == audiobookId
                && intent.State != AudiobookDeletionIntentState.Completed,
                cancellationToken);
    }
}
