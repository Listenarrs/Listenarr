using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal enum LibraryDirectoryRemovalOutcome
{
    Removed,
    AlreadyRemoved,
    Retained
}

internal static class LibraryDirectoryOwnershipRemoval
{
    public static void ValidateRecoverableState(LibraryDirectoryOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        var originalExists = Directory.Exists(ownership.CanonicalPath);
        var originalIsFile = File.Exists(ownership.CanonicalPath);
        if (originalIsFile)
        {
            throw new InvalidOperationException(
                "The owned directory recovery path is occupied by a file.");
        }

        if (!originalExists)
        {
            // The committed Removing state is the durable deletion intent. If the
            // pathname is gone, physical retirement already completed and the database
            // can safely converge to Removed.
            return;
        }

        var parentPath = Path.GetDirectoryName(ownership.CanonicalPath)
            ?? throw new InvalidOperationException(
                "The owned directory recovery path has no parent directory.");
        using var parent = PinnedDirectoryCreation.OpenPinnedBoundary(parentPath);
        using var directory = parent.OpenExistingChild(Path.GetFileName(ownership.CanonicalPath));
        EnsurePhysicalIdentity(ownership, directory);
        if (!parent.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The owned directory recovery parent changed during validation.");
        }
    }

    public static LibraryDirectoryRemovalOutcome RemoveEmptyDirectory(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parentAnchor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        cancellationToken.ThrowIfCancellationRequested();
        var originalPath = ownership.CanonicalPath;
        var parentPath = Path.GetDirectoryName(originalPath)
            ?? throw new InvalidOperationException(
                "The durable directory ownership path has no parent directory.");
        var originalExists = Directory.Exists(originalPath);
        var originalIsFile = File.Exists(originalPath);
        if (originalIsFile)
        {
            throw new InvalidOperationException(
                "An owned directory removal path is occupied by a file.");
        }
        if (!FileSystemPathIdentity.AreEquivalent(
                parentAnchor.FullPath,
                parentPath,
                ownership.GetIdentity().Semantics)
            || !parentAnchor.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The authorized ownership parent no longer matches the persisted path.");
        }

        if (!originalExists)
        {
            return LibraryDirectoryRemovalOutcome.AlreadyRemoved;
        }

        using var publication = parentAnchor.OpenExistingChildForPublication(
            Path.GetFileName(originalPath));
        using var directory = publication.OpenCreatedDirectoryAnchor();
        EnsurePhysicalIdentity(ownership, directory);
        if (Directory.EnumerateFileSystemEntries(originalPath).Any()
            || !directory.VisiblePathMatches()
            || !parentAnchor.VisiblePathMatches())
        {
            return LibraryDirectoryRemovalOutcome.Retained;
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsurePhysicalIdentity(ownership, directory);
        publication.DeletePinnedEmptyDirectoryImmediately(
            Path.GetFileName(originalPath));
        return LibraryDirectoryRemovalOutcome.Removed;
    }

    private static void EnsurePhysicalIdentity(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory)
    {
        if (!ManagedDirectoryIdentity.Matches(
                ownership.DirectoryObjectIdentityVersion,
                ownership.DirectoryObjectIdentity,
                ownership.OwnershipToken,
                directory.GetDirectoryObjectIdentity())
            || !directory.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The owned directory no longer matches its persisted physical identity.");
        }
    }

}
