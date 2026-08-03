using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    public async Task<RootFolderPathChangeResult> StartAsync(
        int rootFolderId,
        RootFolderPathChangeCommand command,
        CancellationToken cancellationToken = default)
    {
        var outcome = await _mutationCoordinator.ExecuteExclusiveAsync(
            token => ExecuteWithAllAudiobookLocksAsync(
                lockedToken => StartCoreAsync(rootFolderId, command, lockedToken),
                token),
            cancellationToken);
        if (outcome.Broadcast)
        {
            await BroadcastAsync(outcome.Result, cancellationToken);
        }

        return outcome.Result;
    }

    private async Task<T> ExecuteWithAllAudiobookLocksAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var audiobookIds = await db.Audiobooks
            .AsNoTracking()
            .OrderBy(audiobook => audiobook.Id)
            .Select(audiobook => audiobook.Id)
            .ToListAsync(cancellationToken);

        return await _audiobookOperationCoordinator.ExecuteExclusiveAsync(
            audiobookIds,
            operation,
            cancellationToken);
    }
}
