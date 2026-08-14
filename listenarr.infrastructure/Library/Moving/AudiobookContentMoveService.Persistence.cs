using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private Task EnsureLeaseOwnedAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        CancellationToken cancellationToken) =>
        executionStore.EnsureLeaseOwnedAsync(jobId, leaseToken, cancellationToken);

    private Task<int> GetExecutionProtocolVersionAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        executionStore.GetExecutionProtocolVersionAsync(jobId, cancellationToken);

    private Task<MarkerlessMoveEndpointState> GetEndpointObjectIdentitiesAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        executionStore.GetEndpointObjectIdentitiesAsync(jobId, cancellationToken);

    private Task<MarkerlessMoveBoundaryAuthorizationState> GetBoundaryAuthorizationsAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        executionStore.GetBoundaryAuthorizationsAsync(jobId, cancellationToken);

    private async Task<AudiobookContentMoveRequest> WithBoundaryAuthorizationAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken)
    {
        if (request.BoundaryAuthorization != null)
        {
            return request;
        }

        return request with
        {
            BoundaryAuthorization = await GetBoundaryAuthorizationsAsync(
                request.JobId,
                cancellationToken)
        };
    }

    private Task UpdateEndpointObjectIdentitiesAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string? sourceDirectoryObjectIdentity,
        string? targetDirectoryObjectIdentity,
        CancellationToken cancellationToken) =>
        executionStore.UpdateEndpointObjectIdentitiesAsync(
            jobId,
            leaseToken,
            sourceDirectoryObjectIdentity,
            targetDirectoryObjectIdentity,
            cancellationToken);

    private Task UpdateSourceDirectoryCleanupStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        MoveJobEntryCleanupState cleanupState,
        CancellationToken cancellationToken) =>
        executionStore.UpdateSourceDirectoryCleanupStateAsync(
            jobId,
            leaseToken,
            cleanupState,
            cancellationToken);

    private Task ValidatePersistedMoveIdentityAsync(
        Guid jobId,
        string source,
        string target,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        MoveLeaseToken leaseToken,
        CancellationToken cancellationToken) =>
        executionStore.ValidateIdentityAsync(
            jobId,
            source,
            target,
            sourceSemantics,
            targetSemantics,
            leaseToken,
            cancellationToken);

    private async Task EnsureCurrentExecutionProtocolAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var version = await GetExecutionProtocolVersionAsync(jobId, cancellationToken);
        if (!MoveExecutionProtocol.IsCurrent(version))
        {
            throw new MoveNeedsAttentionException(
                "This move job does not use the current durable database execution protocol.");
        }
    }

    private Task<List<MoveJobEntry>> LoadManifestAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        executionStore.LoadManifestAsync(jobId, cancellationToken);

    private Task UpdateCleanupStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string relativePath,
        MoveJobEntryCleanupState cleanupState,
        CancellationToken cancellationToken) =>
        executionStore.UpdateCleanupStateAsync(
            jobId,
            leaseToken,
            relativePath,
            cleanupState,
            cancellationToken);

    private Task UpdateCleanupProtectionVersionAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string relativePath,
        int cleanupProtectionVersion,
        CancellationToken cancellationToken) =>
        executionStore.UpdateCleanupProtectionVersionAsync(
            jobId,
            leaseToken,
            relativePath,
            cleanupProtectionVersion,
            cancellationToken);

    private Task UpdateCopyStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        CancellationToken cancellationToken) =>
        executionStore.UpdateCopyStateAsync(jobId, leaseToken, cancellationToken);

    private Task UpdateSourceEntryProofAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string relativePath,
        string sourcePhysicalObjectIdentity,
        string? sha256,
        CancellationToken cancellationToken) =>
        executionStore.UpdateSourceEntryProofAsync(
            jobId,
            leaseToken,
            relativePath,
            sourcePhysicalObjectIdentity,
            sha256,
            cancellationToken);

    private Task UpdateTargetEntryStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string relativePath,
        MoveJobEntryCopyState copyState,
        string? targetPhysicalObjectIdentity,
        CancellationToken cancellationToken) =>
        executionStore.UpdateTargetEntryStateAsync(
            jobId,
            leaseToken,
            relativePath,
            copyState,
            targetPhysicalObjectIdentity,
            cancellationToken);

    private Task UpdateJobPhaseAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        MoveJobPhase phase,
        CancellationToken cancellationToken) =>
        executionStore.UpdateJobPhaseAsync(
            jobId,
            leaseToken,
            phase,
            cancellationToken);

    private Task<IReadOnlyList<MoveJobCreatedDirectory>> GetCreatedDirectoriesAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        executionStore.GetCreatedDirectoriesAsync(jobId, cancellationToken);

    private Task PersistCreatedDirectoriesAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken) =>
        executionStore.PersistCreatedDirectoriesAsync(
            jobId,
            leaseToken,
            paths,
            cancellationToken);

    private Task UpdateCreatedDirectoryStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string path,
        MoveCreatedDirectoryState state,
        CancellationToken cancellationToken) =>
        executionStore.UpdateCreatedDirectoryStateAsync(
            jobId,
            leaseToken,
            path,
            state,
            cancellationToken);

    private Task UpdateCreatedDirectoryPublicationAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string path,
        MoveCreatedDirectoryState state,
        string directoryObjectIdentity,
        CancellationToken cancellationToken) =>
        executionStore.UpdateCreatedDirectoryPublicationAsync(
            jobId,
            leaseToken,
            path,
            state,
            directoryObjectIdentity,
            cancellationToken);
}
