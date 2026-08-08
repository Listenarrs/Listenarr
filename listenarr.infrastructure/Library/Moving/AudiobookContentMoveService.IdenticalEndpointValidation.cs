namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    public async Task VerifyNoFilesystemMoveStartedAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureLeaseOwnedAsync(
            request.JobId,
            request.LeaseToken,
            cancellationToken);
        await EnsureCurrentExecutionProtocolAsync(
            request.JobId,
            cancellationToken);

        var source = NormalizeMoveDirectoryEndpoint(request.Source);
        var target = NormalizeMoveDirectoryEndpoint(request.Target);
        await ValidatePersistedMoveIdentityAsync(
            request.JobId,
            source,
            target,
            request.SourceSemantics,
            request.TargetSemantics,
            request.LeaseToken,
            cancellationToken);

        var manifest = await LoadManifestAsync(request.JobId, cancellationToken);
        var scaffolding = await GetCreatedDirectoriesAsync(
            request.JobId,
            cancellationToken);
        var manifestHasExecutionState = manifest.Any(entry =>
            entry.CopyState != MoveJobEntryCopyState.Pending
            || entry.CleanupState != MoveJobEntryCleanupState.Pending);
        if (manifestHasExecutionState || scaffolding.Count > 0)
        {
            throw new MoveNeedsAttentionException(
                "The identical-endpoint job has durable move execution state and cannot be superseded automatically.");
        }
    }
}
