using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfLibraryDirectoryOwnershipStore
{
    public async Task<LibraryDirectoryOwnershipResolution> ResolveOwnedAsync(
        string path,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken = default) =>
        await ResolveOwnedCoreAsync(
            path,
            semantics,
            validateProof: true,
            cancellationToken);

    private async Task<LibraryDirectoryOwnershipResolution> ResolveOwnedCoreAsync(
        string path,
        FileSystemPathSemantics semantics,
        bool validateProof,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnsureResolved(semantics);

        string canonicalPath;
        string lookupKey;
        try
        {
            canonicalPath = FileSystemPathIdentity.Canonicalize(path, semantics.Syntax);
            lookupKey = FileSystemPathIdentity.CreateLookupKey(
                IdentityScope,
                canonicalPath,
                semantics.Syntax);
        }
        catch (ArgumentException exception)
        {
            return new LibraryDirectoryOwnershipResolution(
                LibraryDirectoryOwnershipResolutionState.Unavailable,
                Reason: exception.Message);
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.LibraryDirectoryOwnerships
            .AsNoTracking()
            .Where(ownership => ownership.PathIdentityLookupKey == lookupKey
                && ownership.State != LibraryDirectoryOwnershipState.Removed)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return new LibraryDirectoryOwnershipResolution(
                LibraryDirectoryOwnershipResolutionState.Unowned);
        }

        var compatible = new List<LibraryDirectoryOwnership>();
        var hasConflict = false;
        var hasUnavailable = false;
        foreach (var candidate in candidates)
        {
            try
            {
                var comparison = Compare(candidate, canonicalPath, semantics);
                if (candidate.State == LibraryDirectoryOwnershipState.Unavailable)
                {
                    hasUnavailable = true;
                }
                else if (comparison == OwnershipComparison.Compatible
                    && candidate.State is LibraryDirectoryOwnershipState.Owned
                        or LibraryDirectoryOwnershipState.Retained
                        or LibraryDirectoryOwnershipState.Removing)
                {
                    compatible.Add(candidate);
                }
                else if (comparison is OwnershipComparison.Compatible or OwnershipComparison.Conflict
                    || candidate.State == LibraryDirectoryOwnershipState.Conflict)
                {
                    hasConflict = true;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return new LibraryDirectoryOwnershipResolution(
                    LibraryDirectoryOwnershipResolutionState.Unavailable,
                    Reason: $"The durable directory ownership identity is invalid: {exception.Message}");
            }
        }

        if (hasConflict || compatible.Count > 1)
        {
            return new LibraryDirectoryOwnershipResolution(
                LibraryDirectoryOwnershipResolutionState.Conflict,
                Reason: "Multiple or incompatible durable ownership claims resolve to this directory.");
        }
        if (hasUnavailable)
        {
            return new LibraryDirectoryOwnershipResolution(
                LibraryDirectoryOwnershipResolutionState.Unavailable,
                Reason: "Durable directory ownership exists but is unavailable for safe use.");
        }

        if (compatible.Count != 1)
        {
            return new LibraryDirectoryOwnershipResolution(
                LibraryDirectoryOwnershipResolutionState.Unowned);
        }

        var resolved = compatible[0];
        if (!HasDestructiveIdentity(resolved))
        {
            return new LibraryDirectoryOwnershipResolution(
                LibraryDirectoryOwnershipResolutionState.Unavailable,
                resolved,
                "Durable directory ownership lacks managed-root physical identity.");
        }
        if (validateProof
            && resolved.State is (
                LibraryDirectoryOwnershipState.Owned
                or LibraryDirectoryOwnershipState.Retained))
        {
            try
            {
                using var authorization =
                    await _boundaryAuthorizer.AuthorizeOwnershipAsync(
                        resolved,
                        cancellationToken);
                using var live = authorization.ParentAnchor.OpenExistingChild(
                    Path.GetFileName(resolved.CanonicalPath));
                if (!live.MatchesManagedDirectoryOwnershipIdentity(
                        resolved.DirectoryObjectIdentityVersion,
                        resolved.DirectoryObjectIdentity,
                        resolved.OwnershipToken)
                    || !DirectoryVisibilityMatchesOrThrowUnavailable(
                        live,
                        "The owned directory is temporarily unavailable while its persisted physical identity is being verified.")
                    || !DirectoryVisibilityMatchesOrThrowUnavailable(
                        authorization.ParentAnchor,
                        "The owned directory parent is temporarily unavailable while its persisted physical identity is being verified."))
                {
                    throw new InvalidOperationException(
                        "The owned directory no longer matches its persisted physical identity.");
                }
                AfterOwnedDirectoryPhysicalIdentityPinnedForTest?.Invoke();
                if (!live.MatchesManagedDirectoryOwnershipIdentity(
                        resolved.DirectoryObjectIdentityVersion,
                        resolved.DirectoryObjectIdentity,
                        resolved.OwnershipToken)
                    || !DirectoryVisibilityMatchesOrThrowUnavailable(
                        live,
                        "The owned directory is temporarily unavailable after its physical identity was pinned.")
                    || !DirectoryVisibilityMatchesOrThrowUnavailable(
                        authorization.ParentAnchor,
                        "The owned directory parent is temporarily unavailable after its physical identity was pinned."))
                {
                    throw new InvalidOperationException(
                        "The owned directory changed after its physical identity was pinned.");
                }
            }
            catch (Exception exception) when (
                FileSystemSafety.IsProvenMissingPathException(exception))
            {
                return new LibraryDirectoryOwnershipResolution(
                    LibraryDirectoryOwnershipResolutionState.Unavailable,
                    Reason: $"Durable directory ownership proof is unavailable: {exception.Message}");
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException
                    or System.ComponentModel.Win32Exception)
            {
                return new LibraryDirectoryOwnershipResolution(
                    LibraryDirectoryOwnershipResolutionState.Unavailable,
                    Reason: $"Durable directory ownership proof is temporarily unavailable: {exception.Message}",
                    IsTransient: true);
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException
                    or NotSupportedException or PathTooLongException)
            {
                return new LibraryDirectoryOwnershipResolution(
                    LibraryDirectoryOwnershipResolutionState.Unavailable,
                    Reason: $"Durable directory ownership proof is unavailable: {exception.Message}");
            }
        }

        // Removing has separate restart semantics: the durable state transition can
        // outlive the final namespace deletion.
        return new LibraryDirectoryOwnershipResolution(
            LibraryDirectoryOwnershipResolutionState.Owned,
            resolved);
    }

    public async Task<IReadOnlyList<LibraryDirectoryOwnership>> GetOwnedWithinAsync(
        string basePath,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        EnsureResolved(semantics);
        var canonicalBase = FileSystemPathIdentity.Canonicalize(basePath, semantics.Syntax);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.LibraryDirectoryOwnerships
            .AsNoTracking()
            .Where(ownership => ownership.PathSyntax == semantics.Syntax
                && ownership.State != LibraryDirectoryOwnershipState.Removed)
            .ToListAsync(cancellationToken);
        var owned = new List<LibraryDirectoryOwnership>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PathIdentitySnapshot identity;
            try
            {
                identity = candidate.GetIdentity();
                identity.ValidateForPath(candidate.CanonicalPath);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    "A durable directory ownership identity is invalid.",
                    exception);
            }

            var overlapsUnderCurrent = FileSystemPathIdentity.IsSameOrInside(
                candidate.CanonicalPath,
                canonicalBase,
                semantics);
            var overlapsUnderStored = FileSystemPathIdentity.IsSameOrInside(
                candidate.CanonicalPath,
                canonicalBase,
                identity.Semantics);
            if (!overlapsUnderCurrent && !overlapsUnderStored)
            {
                continue;
            }

            if (!overlapsUnderCurrent
                || !overlapsUnderStored
                || identity.CaseSensitivity != semantics.CaseSensitivity
                || candidate.State is LibraryDirectoryOwnershipState.Conflict
                    or LibraryDirectoryOwnershipState.Unavailable)
            {
                throw new InvalidOperationException(
                    "A conflicting or unavailable durable directory ownership claim overlaps the move source.");
            }
            if (!HasDestructiveIdentity(candidate))
            {
                throw new InvalidOperationException(
                    "A durable ownership claim lacks managed-root physical identity.");
            }

            if (candidate.State is LibraryDirectoryOwnershipState.Owned
                or LibraryDirectoryOwnershipState.Retained)
            {
                using var authorization =
                    await _boundaryAuthorizer.AuthorizeOwnershipAsync(
                        candidate,
                        cancellationToken);
                using var live = authorization.ParentAnchor.OpenExistingChild(
                    Path.GetFileName(candidate.CanonicalPath));
                if (!live.MatchesManagedDirectoryOwnershipIdentity(
                        candidate.DirectoryObjectIdentityVersion,
                        candidate.DirectoryObjectIdentity,
                        candidate.OwnershipToken)
                    || !DirectoryVisibilityMatchesOrThrowUnavailable(
                        live,
                        "A move-source ownership directory is temporarily unavailable while its physical generation is being verified.")
                    || !DirectoryVisibilityMatchesOrThrowUnavailable(
                        authorization.ParentAnchor,
                        "A move-source ownership parent is temporarily unavailable while its physical generation is being verified."))
                {
                    throw new InvalidOperationException(
                        "A durable ownership claim no longer matches its persisted physical directory generation.");
                }
            }

            owned.Add(candidate);
        }

        return owned
            .OrderBy(ownership => ownership.CanonicalPath.Length)
            .ToList();
    }

    private static bool DirectoryVisibilityMatchesOrThrowUnavailable(
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

    private static bool HasDestructiveIdentity(
        LibraryDirectoryOwnership ownership) =>
        ownership.ManagedRootFolderId.HasValue
        && ownership.DirectoryObjectIdentityVersion
            == ManagedDirectoryIdentity.CurrentVersion
        && !string.IsNullOrWhiteSpace(ownership.DirectoryObjectIdentity)
        && string.IsNullOrWhiteSpace(
            ownership.DirectoryObjectIdentityUnavailableReason);
}
