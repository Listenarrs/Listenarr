using Listenarr.Application.Common.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private readonly ILibraryFilesystemReadiness _filesystemReadiness =
        filesystemReadiness ?? throw new ArgumentNullException(nameof(filesystemReadiness));

    public async Task<RootFolderPathChangeResult> StartAsync(
        int rootFolderId,
        RootFolderPathChangeCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Mode == RootFolderRelocationMode.MetadataOnly)
        {
            _filesystemReadiness.EnsureMetadataRepairReady();
        }
        else
        {
            EnsureFilesystemMutationReady();
        }

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

    private void EnsureFilesystemMutationReady()
    {
        var snapshot = _filesystemReadiness.Current;
        if (snapshot.IsReady)
        {
            return;
        }

        if (snapshot.Status == LibraryFilesystemInitializationStatus.Failed)
        {
            throw new ApplicationUnavailableException(
                "filesystem_initialization_failed",
                snapshot.ErrorMessage
                    ?? "Library filesystem initialization did not complete. Filesystem operations are unavailable.");
        }

        throw new ApplicationUnavailableException(
            "filesystem_initializing",
            "Library filesystem initialization is still in progress. Filesystem operations will be available when initialization completes.");
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
