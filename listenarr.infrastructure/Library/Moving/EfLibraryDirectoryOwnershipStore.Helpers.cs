using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfLibraryDirectoryOwnershipStore
{
    private static void ValidatePinnedOwnership(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation creation)
    {
        using var directory = creation.OpenCreatedDirectoryAnchor();
        using var parent = creation.OpenParentDirectoryAnchor();
        if (!ManagedDirectoryIdentity.Matches(
                ownership.DirectoryObjectIdentityVersion,
                ownership.DirectoryObjectIdentity,
                ownership.OwnershipToken,
                directory.GetDirectoryObjectIdentity())
            || !directory.VisiblePathMatches()
            || !parent.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The owned directory no longer matches its persisted physical identity.");
        }
    }

    private static void CleanupRetiredSiblingMarkers(
        IEnumerable<LibraryDirectoryOwnership> retiredCandidates,
        string canonicalPath,
        FileSystemPathSemantics semantics)
    {
        foreach (var retired in retiredCandidates)
        {
            try
            {
                if (Compare(retired, canonicalPath, semantics) == OwnershipComparison.Compatible)
                {
                    LibraryDirectoryOwnershipMarker.TryDeleteRetiredSiblingMarker(
                        retired,
                        out _);
                }
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
            {
                // Removed rows are nonauthoritative. Corrupt retired metadata must not
                // prevent a new, independently proven ownership claim for the live path.
            }
        }
    }

    private static LibraryDirectoryOwnership CreateOwnership(
        LibraryDirectoryOwnershipClaim claim,
        string canonicalPath,
        string lookupKey,
        string? ownershipKey,
        LibraryDirectoryOwnershipState state,
        string? reason,
        int? managedRootFolderId,
        string nativeDirectoryIdentity,
        DateTime now)
    {
        var ownershipToken = Guid.NewGuid().ToString("N");
        return new LibraryDirectoryOwnership
        {
            Path = claim.Path,
            CanonicalPath = canonicalPath,
            PathSyntax = claim.Semantics.Syntax,
            PathCaseSensitivity = claim.Semantics.CaseSensitivity,
            PathCaseSensitivityMode = claim.Semantics.CaseSensitivity == FileSystemCaseSensitivity.Sensitive
                ? FileSystemCaseSensitivityMode.Sensitive
                : FileSystemCaseSensitivityMode.Insensitive,
            PathIdentityBoundary = canonicalPath,
            PathIdentityLookupKey = lookupKey,
            PathOwnershipKey = ownershipKey,
            OwnershipToken = ownershipToken,
            State = state,
            CreationWorkflow = claim.CreationWorkflow,
            CreationOperationId = claim.CreationOperationId,
            AudiobookId = claim.AudiobookId,
            ManagedRootFolderId = managedRootFolderId,
            DirectoryObjectIdentityVersion = ManagedDirectoryIdentity.CurrentVersion,
            DirectoryObjectIdentity = ManagedDirectoryIdentity.Create(
                ownershipToken,
                nativeDirectoryIdentity),
            DirectoryObjectIdentityUnavailableReason = managedRootFolderId.HasValue
                ? null
                : "The claim was not created through an authorized managed root.",
            StateReason = reason,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static void EnsureAuthorizedPhysicalIdentity(
        LibraryDirectoryOwnership ownership,
        int? managedRootFolderId,
        string directoryObjectIdentity)
    {
        if (!managedRootFolderId.HasValue
            || ownership.ManagedRootFolderId != managedRootFolderId
            || !ManagedDirectoryIdentity.Matches(
                ownership.DirectoryObjectIdentityVersion,
                ownership.DirectoryObjectIdentity,
                ownership.OwnershipToken,
                directoryObjectIdentity)
            || (ownership.State !=
                    LibraryDirectoryOwnershipState.Unavailable
                && !string.IsNullOrWhiteSpace(
                    ownership.DirectoryObjectIdentityUnavailableReason)))
        {
            throw new InvalidOperationException(
                "The existing ownership claim lacks matching managed-root and physical-directory authorization.");
        }
    }

    private static OwnershipComparison Compare(
        LibraryDirectoryOwnership ownership,
        string canonicalPath,
        FileSystemPathSemantics currentSemantics)
    {
        var identity = ownership.GetIdentity();
        identity.ValidateForPath(ownership.CanonicalPath);
        if (identity.Syntax != currentSemantics.Syntax)
        {
            return OwnershipComparison.Distinct;
        }

        var matchesCurrent = FileSystemPathIdentity.AreEquivalent(
            ownership.CanonicalPath,
            canonicalPath,
            currentSemantics);
        var matchesStored = FileSystemPathIdentity.AreEquivalent(
            ownership.CanonicalPath,
            canonicalPath,
            identity.Semantics);
        if (!matchesCurrent && !matchesStored)
        {
            return OwnershipComparison.Distinct;
        }

        return matchesCurrent
            && matchesStored
            && identity.CaseSensitivity == currentSemantics.CaseSensitivity
            && FileSystemPathIdentity.AreEquivalent(
                identity.BoundaryPath,
                canonicalPath,
                currentSemantics)
                ? OwnershipComparison.Compatible
                : OwnershipComparison.Conflict;
    }

    private static void EnsureResolved(FileSystemPathSemantics semantics)
    {
        if (semantics.CaseSensitivity == FileSystemCaseSensitivity.Unknown)
        {
            throw new InvalidOperationException(
                "Filesystem case sensitivity must be resolved before claiming directory ownership.");
        }
    }

    private enum OwnershipComparison
    {
        Distinct,
        Compatible,
        Conflict
    }
}
