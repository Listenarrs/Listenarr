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
        var scaffoldingHasExecutionState = false;
        foreach (var directory in scaffolding)
        {
            ValidateMarkerlessTargetDirectoryLedgerPath(
                directory.Path,
                target,
                request.TargetSemantics);
            if (directory.State != MoveCreatedDirectoryState.Planned
                || !string.IsNullOrWhiteSpace(directory.DirectoryObjectIdentity)
                || TryGetMarkerlessPathAttributes(directory.Path, out _))
            {
                scaffoldingHasExecutionState = true;
                break;
            }
        }

        if (manifestHasExecutionState || scaffoldingHasExecutionState)
        {
            throw new MoveNeedsAttentionException(
                "The identical-endpoint job has durable move execution state and cannot be superseded automatically.");
        }
    }
}
