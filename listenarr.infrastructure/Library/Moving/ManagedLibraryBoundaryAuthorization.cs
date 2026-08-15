namespace Listenarr.Infrastructure.Library.Moving;

internal sealed class ManagedLibraryBoundaryAuthorization(
    int rootFolderId,
    string directoryObjectIdentity,
    PinnedDirectoryCreation.PinnedDirectoryAnchor boundaryAnchor) : IDisposable
{
    public int RootFolderId { get; } = rootFolderId;
    public string DirectoryObjectIdentity { get; } = directoryObjectIdentity;
    public PinnedDirectoryCreation.PinnedDirectoryAnchor BoundaryAnchor { get; } =
        boundaryAnchor;

    public void Dispose() => BoundaryAnchor.Dispose();
}
