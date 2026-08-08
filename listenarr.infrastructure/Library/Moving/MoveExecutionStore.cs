using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed record MarkerlessMoveEndpointState(
    string? SourceDirectoryObjectIdentity,
    string? TargetDirectoryObjectIdentity,
    MoveJobEntryCleanupState SourceDirectoryCleanupState);

internal interface IMoveExecutionStore
{
    Task EnsureLeaseOwnedAsync(Guid jobId, MoveLeaseToken leaseToken, CancellationToken cancellationToken);

    Task<int> GetExecutionProtocolVersionAsync(
        Guid jobId,
        CancellationToken cancellationToken);

    Task<MarkerlessMoveEndpointState> GetEndpointObjectIdentitiesAsync(
        Guid jobId,
        CancellationToken cancellationToken);

    Task UpdateEndpointObjectIdentitiesAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string? sourceDirectoryObjectIdentity,
        string? targetDirectoryObjectIdentity,
        CancellationToken cancellationToken);

    Task UpdateSourceDirectoryCleanupStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        MoveJobEntryCleanupState cleanupState,
        CancellationToken cancellationToken);

    Task ValidateIdentityAsync(
        Guid jobId,
        string source,
        string target,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        MoveLeaseToken leaseToken,
        CancellationToken cancellationToken);

    Task EnsureMutationAuthorizedAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string source,
        string target,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        CancellationToken cancellationToken);

    Task<List<MoveJobEntry>> LoadManifestAsync(Guid jobId, CancellationToken cancellationToken);

    Task UpdateCleanupStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string relativePath,
        MoveJobEntryCleanupState cleanupState,
        CancellationToken cancellationToken);

    Task UpdateCleanupProtectionVersionAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string relativePath,
        int cleanupProtectionVersion,
        CancellationToken cancellationToken);

    Task UpdateCopyStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        CancellationToken cancellationToken);

    Task UpdateSourceEntryProofAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string relativePath,
        string sourcePhysicalObjectIdentity,
        string? sha256,
        CancellationToken cancellationToken);

    Task UpdateTargetEntryStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string relativePath,
        MoveJobEntryCopyState copyState,
        string? targetPhysicalObjectIdentity,
        CancellationToken cancellationToken);

    Task UpdateJobPhaseAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        MoveJobPhase phase,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MoveJobCreatedDirectory>> GetCreatedDirectoriesAsync(
        Guid jobId,
        CancellationToken cancellationToken);

    Task PersistCreatedDirectoriesAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken);

    Task UpdateCreatedDirectoryStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string path,
        MoveCreatedDirectoryState state,
        CancellationToken cancellationToken);

    Task UpdateCreatedDirectoryPublicationAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string path,
        MoveCreatedDirectoryState state,
        string directoryObjectIdentity,
        CancellationToken cancellationToken);
}
