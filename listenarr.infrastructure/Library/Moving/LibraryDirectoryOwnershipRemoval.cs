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
    private const string QuarantinePrefix = ".listenarr-directory-removing-";

    public static string GetQuarantinePath(LibraryDirectoryOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        LibraryDirectoryOwnershipMarker.ValidateOwnershipToken(
            ownership.OwnershipToken);
        var parent = Path.GetDirectoryName(ownership.CanonicalPath)
            ?? throw new InvalidOperationException(
                "The durable directory ownership path has no parent directory.");
        return Path.Join(parent, $"{QuarantinePrefix}{ownership.OwnershipToken}");
    }

    public static void ValidateRecoverableState(LibraryDirectoryOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        var originalExists = Directory.Exists(ownership.CanonicalPath);
        var originalIsFile = File.Exists(ownership.CanonicalPath);
        var quarantinePath = GetQuarantinePath(ownership);
        var quarantineExists = Directory.Exists(quarantinePath);
        var quarantineIsFile = File.Exists(quarantinePath);
        if (originalIsFile || quarantineIsFile)
        {
            throw new InvalidOperationException(
                "An owned directory recovery path is occupied by a file.");
        }
        if (originalExists && quarantineExists)
        {
            throw new InvalidOperationException(
                "Both the owned directory and its removal quarantine exist.");
        }

        if (!originalExists && !quarantineExists)
        {
            // The committed Removing state is the durable deletion intent. If neither
            // pathname exists, the physical retirement already completed and the
            // database can safely converge to Removed without a permanent marker.
            return;
        }

        var visiblePath = originalExists
            ? ownership.CanonicalPath
            : quarantinePath;
        var parentPath = Path.GetDirectoryName(visiblePath)
            ?? throw new InvalidOperationException(
                "The owned directory recovery path has no parent directory.");
        using var parent = PinnedDirectoryCreation.OpenPinnedBoundary(parentPath);
        using var directory = parent.OpenExistingChild(Path.GetFileName(visiblePath));
        EnsurePhysicalIdentity(ownership, directory);
        if (!parent.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The owned directory recovery parent changed during validation.");
        }
    }

    // Compatibility for an older interrupted-removal format where the directory and
    // quarantine are already absent but a durable sibling marker remains. New removal
    // operations do not require or create this marker.
    public static bool TryValidateLegacyMissingBothRecovery(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        out LibraryDirectoryOwnershipMarker.MarkerPayload? legacyPayload)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(parent);
        legacyPayload = null;
        var originalPath = ownership.CanonicalPath;
        var quarantinePath = GetQuarantinePath(ownership);
        if (Directory.Exists(originalPath)
            || File.Exists(originalPath)
            || Directory.Exists(quarantinePath)
            || File.Exists(quarantinePath))
        {
            return false;
        }

        var parentPath = Path.GetDirectoryName(originalPath)
            ?? throw new InvalidOperationException(
                "The durable directory ownership path has no parent directory.");
        if (!FileSystemPathIdentity.AreEquivalent(
                parent.FullPath,
                parentPath,
                ownership.GetIdentity().Semantics)
            || !parent.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The authorized ownership parent no longer matches the legacy recovery path.");
        }

        var siblingPath = LibraryDirectoryOwnershipMarker
            .GetMarkerPaths(ownership)[1];
        var temporaryName = Path.GetFileName(siblingPath) + ".v2.tmp";
        using var temporary = parent.TryOpenExistingFile(
            temporaryName,
            requireDeleteAccess: false);
        if (temporary != null || Directory.Exists(Path.Join(parentPath, temporaryName)))
        {
            throw new InvalidOperationException(
                "Legacy ownership removal proof is mixed with an incomplete marker upgrade.");
        }

        using var sibling = parent.TryOpenExistingFile(
            Path.GetFileName(siblingPath),
            requireDeleteAccess: false);
        if (sibling == null)
        {
            return false;
        }

        var payload = LibraryDirectoryOwnershipMarker.ReadPayload(sibling);
        if (!LibraryDirectoryOwnershipMarker.MatchesCurrentPayload(
                ownership,
                payload)
            && !LibraryDirectoryOwnershipMarker.MatchesLegacyPayload(
                ownership,
                payload))
        {
            throw new InvalidOperationException(
                "The missing owned directory has no exact legacy removal proof.");
        }
        if (!parent.VisiblePathMatches()
            || !sibling.VisiblePathMatches()
            || Directory.Exists(originalPath)
            || File.Exists(originalPath)
            || Directory.Exists(quarantinePath)
            || File.Exists(quarantinePath))
        {
            throw new InvalidOperationException(
                "The legacy ownership removal proof changed during validation.");
        }

        legacyPayload = payload;
        return true;
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
        var quarantinePath = GetQuarantinePath(ownership);
        var originalExists = Directory.Exists(originalPath);
        var originalIsFile = File.Exists(originalPath);
        var quarantineExists = Directory.Exists(quarantinePath);
        var quarantineIsFile = File.Exists(quarantinePath);
        if (originalIsFile || quarantineIsFile)
        {
            throw new InvalidOperationException(
                "An owned directory removal path is occupied by a file.");
        }
        if (originalExists && quarantineExists)
        {
            throw new InvalidOperationException(
                "Both the owned directory and its removal quarantine exist.");
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

        if (!originalExists && !quarantineExists)
        {
            RetireLegacySiblingArtifacts(ownership, parentAnchor);
            return LibraryDirectoryRemovalOutcome.AlreadyRemoved;
        }

        if (originalExists)
        {
            using var publication = parentAnchor.OpenExistingChildForPublication(
                Path.GetFileName(originalPath));
            using var directory = publication.OpenCreatedDirectoryAnchor();
            EnsurePhysicalIdentity(ownership, directory);
            RetireLegacyOwnershipArtifacts(ownership, directory, parentAnchor);
            if (Directory.EnumerateFileSystemEntries(originalPath).Any()
                || !directory.VisiblePathMatches()
                || !parentAnchor.VisiblePathMatches())
            {
                return LibraryDirectoryRemovalOutcome.Retained;
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsurePhysicalIdentity(ownership, directory);
            publication.RetirePinnedEmptyDirectoryFromNamespace(
                Path.GetFileName(originalPath));
            RetireLegacySiblingArtifacts(ownership, parentAnchor);
            return LibraryDirectoryRemovalOutcome.Removed;
        }

        // Compatibility only: older versions may have already renamed the directory
        // into a job-shaped quarantine. New removals never create that pathname.
        using (var publication = parentAnchor.OpenExistingChildForPublication(
            Path.GetFileName(quarantinePath)))
        using (var directory = publication.OpenCreatedDirectoryAnchor())
        {
            EnsurePhysicalIdentity(ownership, directory);
            RetireLegacyOwnershipArtifacts(ownership, directory, parentAnchor);
            if (Directory.EnumerateFileSystemEntries(quarantinePath).Any()
                || !directory.VisiblePathMatches()
                || !parentAnchor.VisiblePathMatches())
            {
                RestorePinnedQuarantine(publication, originalPath, quarantinePath);
                return LibraryDirectoryRemovalOutcome.Retained;
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsurePhysicalIdentity(ownership, directory);
            publication.RetirePinnedEmptyDirectoryFromNamespace(
                Path.GetFileName(quarantinePath));
            RetireLegacySiblingArtifacts(ownership, parentAnchor);
            return LibraryDirectoryRemovalOutcome.Removed;
        }
    }

    private static void RetireLegacyOwnershipArtifacts(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent)
    {
        if (!LibraryDirectoryOwnershipMarker.TryRetireMatchingMarkers(
                ownership,
                directory,
                parent,
                out var reason))
        {
            throw new InvalidOperationException(
                $"Legacy directory ownership artifacts could not be retired safely: {reason}");
        }
    }

    private static void RetireLegacySiblingArtifacts(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent)
    {
        if (!LibraryDirectoryOwnershipMarker.TryRetireMatchingSiblingArtifacts(
                ownership,
                parent,
                out var reason))
        {
            throw new InvalidOperationException(
                $"Legacy directory ownership sibling artifacts could not be retired safely: {reason}");
        }
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

    private static void RestorePinnedQuarantine(
        PinnedDirectoryCreation pinnedDirectory,
        string originalPath,
        string quarantinePath)
    {
        if (File.Exists(originalPath) || Directory.Exists(originalPath))
        {
            throw new InvalidOperationException(
                "The original owned directory path was recreated while its quarantine was active.");
        }

        using var restored = pinnedDirectory.RepublishPinnedDirectory(
            Path.GetFileName(quarantinePath),
            Path.GetFileName(originalPath));
    }
}
