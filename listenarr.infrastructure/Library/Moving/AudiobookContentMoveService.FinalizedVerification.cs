namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    public async Task VerifyFinalizedMoveAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken,
        MarkerlessTargetVerificationLease? targetVerificationLease = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureLeaseOwnedAsync(
            request.JobId,
            request.LeaseToken,
            cancellationToken);
        await EnsureCurrentExecutionProtocolAsync(
            request.JobId,
            cancellationToken);
        await ValidatePersistedMoveIdentityAsync(
            request.JobId,
            request.Source,
            request.Target,
            request.SourceSemantics,
            request.TargetSemantics,
            request.LeaseToken,
            cancellationToken);
        request = await WithBoundaryAuthorizationAsync(request, cancellationToken);
        request = await WithValidatedTargetDirectoryOwnershipAsync(
            request,
            cancellationToken);
        var manifest = await LoadManifestAsync(
            request.JobId,
            cancellationToken);
        if (manifest.Count == 0)
        {
            throw new MoveNeedsAttentionException(
                "Finalized move verification requires a persisted manifest.");
        }

        await VerifyMarkerlessTargetAsync(
            request,
            request.Target,
            manifest,
            cancellationToken,
            targetVerificationLease: targetVerificationLease);
        VerifySourceCleanupState(
            request,
            request.Source,
            request.Target,
            manifest);
    }
}
