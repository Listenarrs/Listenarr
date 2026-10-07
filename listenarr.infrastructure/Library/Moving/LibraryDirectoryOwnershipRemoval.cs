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
        var parentPath = Path.GetDirectoryName(ownership.CanonicalPath)
            ?? throw new InvalidOperationException(
                "The owned directory recovery path has no parent directory.");
        using var parent = PinnedDirectoryCreation.OpenPinnedBoundary(parentPath);
        using var publication = TryOpenOwnedDirectoryForPublication(
            parent,
            Path.GetFileName(ownership.CanonicalPath),
            "The owned directory recovery path is occupied by a file.");
        if (publication == null)
        {
            // The committed Removing state is the durable deletion intent. A
            // verified missing child under the still-pinned parent proves physical
            // retirement already completed.
            return;
        }

        using var directory = publication.OpenCreatedDirectoryAnchor();
        if (!VisibilityMatchesOrThrowUnavailable(directory,
                "The owned recovery directory is temporarily unavailable."))
        {
            throw new InvalidOperationException("The owned recovery directory changed while pinned.");
        }
        if (!VisibilityMatchesOrThrowUnavailable(
                parent,
                "The owned directory recovery parent is temporarily unavailable during validation."))
        {
            throw new InvalidOperationException(
                "The owned directory recovery parent changed during validation.");
        }
    }

    public static LibraryDirectoryRemovalOutcome RemoveEmptyDirectory(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parentAnchor,
        CancellationToken cancellationToken = default,
        PinnedDirectoryCreation.PinnedDirectoryAnchor? originalDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        cancellationToken.ThrowIfCancellationRequested();
        var originalPath = ownership.CanonicalPath;
        var parentPath = Path.GetDirectoryName(originalPath)
            ?? throw new InvalidOperationException(
                "The durable directory ownership path has no parent directory.");
        if (!FileSystemPathIdentity.AreEquivalent(
                parentAnchor.FullPath,
                parentPath,
                ownership.GetIdentity().Semantics)
            || !VisibilityMatchesOrThrowUnavailable(
                parentAnchor,
                "The authorized ownership parent is temporarily unavailable during removal."))
        {
            throw new InvalidOperationException(
                "The authorized ownership parent no longer matches the persisted path.");
        }

        using var publication = TryOpenOwnedDirectoryForPublication(
            parentAnchor,
            Path.GetFileName(originalPath),
            "An owned directory removal path is occupied by a file.");
        if (publication == null)
        {
            return LibraryDirectoryRemovalOutcome.AlreadyRemoved;
        }

        using var directory = publication.OpenCreatedDirectoryAnchor();
        if (!LiveDirectoryMatches(originalDirectory, directory))
        {
            return LibraryDirectoryRemovalOutcome.Retained;
        }
        if (Directory.EnumerateFileSystemEntries(originalPath).Any())
        {
            return LibraryDirectoryRemovalOutcome.Retained;
        }
        if (!VisibilityMatchesOrThrowUnavailable(
                directory,
                "The owned directory is temporarily unavailable immediately before removal.")
            || !VisibilityMatchesOrThrowUnavailable(
                parentAnchor,
                "The owned directory parent is temporarily unavailable immediately before removal."))
        {
            return LibraryDirectoryRemovalOutcome.Retained;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!LiveDirectoryMatches(originalDirectory, directory))
        {
            return LibraryDirectoryRemovalOutcome.Retained;
        }
        publication.DeletePinnedEmptyDirectoryImmediately(
            Path.GetFileName(originalPath));
        return LibraryDirectoryRemovalOutcome.Removed;
    }

    private static PinnedDirectoryCreation? TryOpenOwnedDirectoryForPublication(
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        string childName,
        string nonDirectoryMessage)
    {
        try
        {
            return parent.TryOpenExistingChildForPublication(childName);
        }
        catch (System.ComponentModel.Win32Exception exception) when (
            OperatingSystem.IsWindows()
                ? exception.NativeErrorCode == 267
                : exception.NativeErrorCode == 20)
        {
            throw new InvalidOperationException(nonDirectoryMessage, exception);
        }
    }

    private static bool LiveDirectoryMatches(
        PinnedDirectoryCreation.PinnedDirectoryAnchor? originalDirectory,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory) =>
        originalDirectory != null
        && VisibilityMatchesOrThrowUnavailable(originalDirectory,
            "The original directory is temporarily unavailable during live retirement.")
        && VisibilityMatchesOrThrowUnavailable(directory,
            "The current directory is temporarily unavailable during live retirement.")
        && originalDirectory.IdentifiesSameDirectory(directory);

    private static bool VisibilityMatchesOrThrowUnavailable(
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        string unavailableMessage)
    {
        var visibility = directory.ProbeVisiblePathMatch();
        if (visibility == RegistrationPublicationMatchOutcome.Unavailable)
        {
            throw new IOException(unavailableMessage);
        }

        return visibility == RegistrationPublicationMatchOutcome.Match;
    }

}
