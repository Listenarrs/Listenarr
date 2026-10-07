using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfLibraryDirectoryOwnershipStore
{
    public Task<bool> TryRetireReplacedByMarkerlessMoveAsync(
        string path,
        FileSystemPathSemantics semantics,
        Guid moveJobId,
        string replacementDirectoryObjectIdentity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnsureResolved(semantics);
        if (moveJobId == Guid.Empty)
        {
            throw new ArgumentException(
                "A markerless replacement requires a move job ID.",
                nameof(moveJobId));
        }

        // This released contract supplies only persisted observations, never the
        // original live creation proof. They cannot prove a path claim is stale.
        // Retain its metadata; current path ownership permits additive publication,
        // while destructive cleanup separately requires an original live lease.
        return Task.FromResult(false);
    }
}
