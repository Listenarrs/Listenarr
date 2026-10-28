namespace Listenarr.Infrastructure.Library.Moving;

internal sealed class AuthorizedLibraryDirectoryOwnership(
    int rootFolderId,
    PinnedDirectoryCreation.PinnedDirectoryAnchor parentAnchor) : IDisposable
{
    public int RootFolderId { get; } = rootFolderId;
    public PinnedDirectoryCreation.PinnedDirectoryAnchor ParentAnchor { get; } =
        parentAnchor;

    public void Dispose() => ParentAnchor.Dispose();
}
