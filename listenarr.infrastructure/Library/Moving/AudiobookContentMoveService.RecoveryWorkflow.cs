using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    public async Task<AudiobookContentMoveResult?> GetRecoverableMoveAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureLeaseOwnedAsync(request.JobId, request.LeaseToken, cancellationToken);
        await EnsureCurrentExecutionProtocolAsync(request.JobId, cancellationToken);

        var source = NormalizeMoveDirectoryEndpoint(request.Source);
        var target = NormalizeMoveDirectoryEndpoint(request.Target);
        var sourceSemantics = request.SourceSemantics;
        var targetSemantics = request.TargetSemantics;
        if (IsFilesystemRoot(source, sourceSemantics)
            || IsFilesystemRoot(target, targetSemantics)
            || FileSystemPathIdentity.AreEquivalentEndpoints(
                source,
                sourceSemantics,
                target,
                targetSemantics))
        {
            throw new MoveNeedsAttentionException(
                "Move recovery requires distinct non-root source and target directories.");
        }

        await ValidatePersistedMoveIdentityAsync(
            request.JobId,
            source,
            target,
            sourceSemantics,
            targetSemantics,
            request.LeaseToken,
            cancellationToken);
        request = await WithBoundaryAuthorizationAsync(request, cancellationToken);
        request = await WithValidatedTargetDirectoryOwnershipAsync(
            request,
            cancellationToken);
        return await GetMarkerlessRecoverableMoveAsync(
            request,
            source,
            target,
            cancellationToken);
    }

    public async Task<AudiobookContentMoveResult> ResumeSourceCleanupAsync(
        AudiobookContentMoveRequest request,
        AudiobookContentMoveResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        await EnsureLeaseOwnedAsync(request.JobId, request.LeaseToken, cancellationToken);
        await EnsureCurrentExecutionProtocolAsync(request.JobId, cancellationToken);
        request = await WithBoundaryAuthorizationAsync(request, cancellationToken);
        request = await WithValidatedTargetDirectoryOwnershipAsync(
            request,
            cancellationToken);
        if (request.ForceCopyAndRetainSource && !result.SourceRetained)
        {
            throw new MoveNeedsAttentionException(
                "Forced source retention cannot accept a destructive recovery result.");
        }
        if (result.SourceCleanupCompleted)
        {
            return result;
        }

        await ValidatePersistedMoveIdentityAsync(
            request.JobId,
            result.Source,
            result.Target,
            request.SourceSemantics,
            request.TargetSemantics,
            request.LeaseToken,
            cancellationToken);
        var manifest = await LoadManifestAsync(request.JobId, cancellationToken);
        if (manifest.Count == 0)
        {
            throw new MoveNeedsAttentionException(
                "Source cleanup is blocked because no persisted move manifest is available.");
        }

        if (result.SourceRetained)
        {
            await RetainMarkerlessSourceAsync(
                request,
                result.Target,
                manifest,
                cancellationToken);
        }
        else
        {
            await DeleteMarkerlessSourceAsync(
                request,
                result.Source,
                result.Target,
                result.TargetInsideSource,
                manifest,
                cancellationToken);
        }
        VerifySourceCleanupState(
            request,
            result.Source,
            result.Target,
            manifest);
        var identities = CreatePersistedTargetPhysicalIdentityMap(
            result.Target,
            manifest,
            request.TargetSemantics);
        return result with
        {
            SourceCleanupCompleted = true,
            TargetPhysicalObjectIdentities = identities
        };
    }
}
