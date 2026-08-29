using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Contracts;

public enum LibraryDirectoryOwnershipResolutionState
{
    Owned,
    Unowned,
    Conflict,
    Unavailable
}

public sealed record LibraryDirectoryOwnershipClaim(
    string Path,
    FileSystemPathSemantics Semantics,
    string CreationWorkflow,
    Guid? CreationOperationId = null,
    int? AudiobookId = null);

public sealed record LibraryDirectoryOwnershipResolution(
    LibraryDirectoryOwnershipResolutionState State,
    LibraryDirectoryOwnership? Ownership = null,
    string? Reason = null,
    bool IsTransient = false);

public interface ILibraryDirectoryOwnershipStore
{
    Task<LibraryDirectoryOwnership> RecordCreatedAsync(
        LibraryDirectoryOwnershipClaim claim,
        CancellationToken cancellationToken = default);

    Task<LibraryDirectoryOwnership> ClaimRetainedAsync(
        LibraryDirectoryOwnershipClaim claim,
        CancellationToken cancellationToken = default) =>
        Task.FromException<LibraryDirectoryOwnership>(
            new InvalidOperationException(
                "Authorized retained-directory claiming is unavailable."));

    Task<IReadOnlyList<LibraryDirectoryOwnership>> EnsureCreatedHierarchyAsync(
        string destinationDirectory,
        string managedBoundary,
        FileSystemPathSemantics semantics,
        string creationWorkflow,
        Guid? creationOperationId = null,
        int? audiobookId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates only missing directory components beneath an already managed
    /// boundary. This additive-only operation deliberately records no cleanup
    /// ownership and therefore never authorizes later directory deletion.
    /// </summary>
    Task EnsureAdditiveHierarchyAsync(
        string destinationDirectory,
        string managedBoundary,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken = default) =>
        Task.FromException(
            new InvalidOperationException(
                "Additive-only directory publication is unavailable."));

    Task<LibraryDirectoryOwnershipResolution> ResolveOwnedAsync(
        string path,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LibraryDirectoryOwnership>> GetOwnedWithinAsync(
        string basePath,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken = default);

    Task<bool> TryRetireReplacedByMarkerlessMoveAsync(
        string path,
        FileSystemPathSemantics semantics,
        Guid moveJobId,
        string replacementDirectoryObjectIdentity,
        CancellationToken cancellationToken = default);

    Task BeginRemovalAsync(
        long ownershipId,
        string expectedOwnershipKey,
        CancellationToken cancellationToken = default);

    Task RetainAsync(
        long ownershipId,
        string expectedOwnershipKey,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task MarkRemovedAsync(
        long ownershipId,
        string expectedOwnershipKey,
        CancellationToken cancellationToken = default);
}
