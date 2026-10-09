namespace Listenarr.Infrastructure.Library.Moving;

internal sealed class ManagedLibraryBoundaryAuthorization(
    int rootFolderId,
    PinnedDirectoryCreation.PinnedDirectoryAnchor boundaryAnchor) : IDisposable
{
    public int RootFolderId { get; } = rootFolderId;
    public PinnedDirectoryCreation.PinnedDirectoryAnchor BoundaryAnchor { get; } =
        boundaryAnchor;

    public void Dispose() => BoundaryAnchor.Dispose();
}
