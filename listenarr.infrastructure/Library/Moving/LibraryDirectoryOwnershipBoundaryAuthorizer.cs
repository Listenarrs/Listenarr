using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class LibraryDirectoryOwnershipBoundaryAuthorizer(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    IFileSystemSemanticsResolver? semanticsResolver = null)
{
    private readonly IFileSystemSemanticsResolver _semanticsResolver =
        semanticsResolver ?? new FileSystemSemanticsResolver();
    internal async Task<AuthorizedLibraryDirectoryOwnership> AuthorizeOwnershipAsync(
        LibraryDirectoryOwnership ownership,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        if (!ownership.ManagedRootFolderId.HasValue)
        {
            throw new InvalidOperationException(
                "The ownership claim has no managed root authorization.");
        }

        return await AuthorizePathWithinRootAsync(
            ownership.CanonicalPath,
            ownership.GetIdentity().Semantics,
            ownership.ManagedRootFolderId.Value,
            cancellationToken);
    }

    internal async Task<AuthorizedLibraryDirectoryOwnership> AuthorizeContainingRootAsync(
        string path,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        var canonicalPath = CanonicalizeHostAuthorizedPath(path, semantics);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var roots = await db.RootFolders.AsNoTracking().ToListAsync(cancellationToken);
        var rootMatch = roots
            .Select(candidate => new
            {
                Root = candidate,
                Semantics = TryGetPersistedRootSemantics(candidate)
            })
            .Where(candidate => candidate.Semantics.HasValue
                && candidate.Semantics.Value.Syntax == semantics.Syntax)
            .Where(candidate => FileSystemPathIdentity.IsSameOrInside(
                    canonicalPath,
                    candidate.Root.Path,
                    candidate.Semantics!.Value))
            .OrderByDescending(candidate => candidate.Root.Path.Length)
            .FirstOrDefault();
        if (rootMatch != null)
        {
            if (!await ConfiguredRootSemanticsCurrentAsync(
                    rootMatch.Root,
                    rootMatch.Semantics!.Value,
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    "The configured root filesystem semantics changed and require repair.");
            }
            return await AuthorizePathWithinBoundaryAsync(
                canonicalPath,
                rootMatch.Semantics.Value,
                rootMatch.Root.Id,
                rootMatch.Root.Path,
                rootMatch.Root.DirectoryObjectIdentityVersion,
                rootMatch.Root.DirectoryObjectIdentity,
                rootMatch.Root.DirectoryObjectIdentityUnavailableReason,
                ignoreUnavailableReason: true,
                cancellationToken);
        }

        var activeRelocations = await db.RootFolderRelocations
            .AsNoTracking()
            .Where(relocation => relocation.ActiveRootFolderId != null)
            .ToListAsync(cancellationToken);
        var relocation = activeRelocations
            .Where(candidate => HasCompatibleSyntax(
                candidate.TargetPath,
                semantics.Syntax))
            .Where(candidate => FileSystemPathIdentity.IsSameOrInside(
                canonicalPath,
                candidate.TargetPath,
                semantics))
            .OrderByDescending(candidate => candidate.TargetPath.Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No configured or actively relocating root folder authorizes the retained directory.");
        return await AuthorizePathWithinBoundaryAsync(
            canonicalPath,
            semantics,
            relocation.ActiveRootFolderId!.Value,
            relocation.TargetPath,
            relocation.TargetDirectoryObjectIdentityVersion,
            relocation.TargetDirectoryObjectIdentity,
            relocation.TargetDirectoryObjectIdentityUnavailableReason,
            ignoreUnavailableReason: false,
            cancellationToken);
    }

    internal async Task<AuthorizedLibraryDirectoryOwnership?>
        TryAuthorizeContainingRootAsync(
            string path,
            FileSystemPathSemantics semantics,
            CancellationToken cancellationToken)
    {
        var canonicalPath = CanonicalizeHostAuthorizedPath(path, semantics);
        var parentPath = Path.GetDirectoryName(canonicalPath)
            ?? throw new InvalidOperationException(
                "The retained directory has no parent.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var roots = await db.RootFolders.AsNoTracking().ToListAsync(cancellationToken);
        var rootMatch = roots
            .Select(candidate => new
            {
                Root = candidate,
                Semantics = TryGetPersistedRootSemantics(candidate)
            })
            .Where(candidate => candidate.Semantics.HasValue
                && candidate.Semantics.Value.Syntax == semantics.Syntax)
            .Where(candidate => FileSystemPathIdentity.IsSameOrInside(
                canonicalPath,
                candidate.Root.Path,
                candidate.Semantics!.Value))
            .Where(candidate => FileSystemPathIdentity.IsSameOrInside(
                parentPath,
                candidate.Root.Path,
                candidate.Semantics!.Value))
            .OrderByDescending(candidate => candidate.Root.Path.Length)
            .FirstOrDefault();
        if (rootMatch != null)
        {
            if (!await ConfiguredRootSemanticsCurrentAsync(
                    rootMatch.Root,
                    rootMatch.Semantics!.Value,
                    cancellationToken))
            {
                return null;
            }
            return await TryAuthorizePathWithinBoundaryAsync(
                canonicalPath,
                rootMatch.Semantics.Value,
                rootMatch.Root.Id,
                rootMatch.Root.Path,
                rootMatch.Root.DirectoryObjectIdentityVersion,
                rootMatch.Root.DirectoryObjectIdentity,
                rootMatch.Root.DirectoryObjectIdentityUnavailableReason,
                ignoreUnavailableReason: true,
                cancellationToken);
        }

        var activeRelocations = await db.RootFolderRelocations
            .AsNoTracking()
            .Where(relocation => relocation.ActiveRootFolderId != null)
            .ToListAsync(cancellationToken);
        var relocation = activeRelocations
            .Where(candidate => HasCompatibleSyntax(
                candidate.TargetPath,
                semantics.Syntax))
            .Where(candidate => FileSystemPathIdentity.IsSameOrInside(
                canonicalPath,
                candidate.TargetPath,
                semantics))
            .Where(candidate => FileSystemPathIdentity.IsSameOrInside(
                parentPath,
                candidate.TargetPath,
                semantics))
            .OrderByDescending(candidate => candidate.TargetPath.Length)
            .FirstOrDefault();
        if (relocation == null)
        {
            return null;
        }

        return await TryAuthorizePathWithinBoundaryAsync(
            canonicalPath,
            semantics,
            relocation.ActiveRootFolderId!.Value,
            relocation.TargetPath,
            relocation.TargetDirectoryObjectIdentityVersion,
            relocation.TargetDirectoryObjectIdentity,
            relocation.TargetDirectoryObjectIdentityUnavailableReason,
            ignoreUnavailableReason: false,
            cancellationToken);
    }

    private async Task<AuthorizedLibraryDirectoryOwnership?>
        TryAuthorizePathWithinBoundaryAsync(
            string canonicalPath,
            FileSystemPathSemantics semantics,
            int rootFolderId,
            string boundaryPath,
            int? expectedDirectoryIdentityVersion,
            string? expectedDirectoryIdentity,
            string? identityUnavailableReason,
            bool ignoreUnavailableReason,
            CancellationToken cancellationToken)
    {
        try
        {
            return await AuthorizePathWithinBoundaryAsync(
                canonicalPath,
                semantics,
                rootFolderId,
                boundaryPath,
                expectedDirectoryIdentityVersion,
                expectedDirectoryIdentity,
                identityUnavailableReason,
                ignoreUnavailableReason,
                cancellationToken);
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or PathTooLongException
                or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal async Task<ManagedLibraryBoundaryAuthorization> AuthorizeAsync(
        string boundaryPath,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        var canonicalBoundary = CanonicalizeHostAuthorizedPath(
            boundaryPath,
            semantics);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var roots = await db.RootFolders.AsNoTracking().ToListAsync(cancellationToken);
        var rootMatch = roots
            .Select(candidate => new
            {
                Root = candidate,
                Semantics = TryGetPersistedRootSemantics(candidate)
            })
            .SingleOrDefault(candidate => candidate.Semantics.HasValue
                && candidate.Semantics.Value.Syntax == semantics.Syntax
                && FileSystemPathIdentity.AreEquivalent(
                    candidate.Root.Path,
                    canonicalBoundary,
                    candidate.Semantics.Value));
        if (rootMatch == null)
        {
            throw new InvalidOperationException(
                "The requested directory boundary is not a configured root folder.");
        }
        if (!await ConfiguredRootSemanticsCurrentAsync(
                rootMatch.Root,
                rootMatch.Semantics!.Value,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "The configured root filesystem semantics changed and require repair.");
        }
        var anchor = PinnedDirectoryCreation.OpenPinnedBoundary(canonicalBoundary);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var liveIdentity = anchor.GetDirectoryObjectIdentity();
            if (!ManagedDirectoryIdentity.MatchesNativeIdentity(
                    rootMatch.Root.DirectoryObjectIdentityVersion,
                    rootMatch.Root.DirectoryObjectIdentity,
                    liveIdentity)
                || !anchor.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The managed root no longer identifies its authorized physical generation.");
            }

            return new ManagedLibraryBoundaryAuthorization(
                rootMatch.Root.Id,
                liveIdentity,
                anchor);
        }
        catch
        {
            anchor.Dispose();
            throw;
        }
    }

    private async Task<AuthorizedLibraryDirectoryOwnership> AuthorizePathWithinRootAsync(
        string path,
        FileSystemPathSemantics semantics,
        int rootFolderId,
        CancellationToken cancellationToken)
    {
        var canonicalPath = CanonicalizeHostAuthorizedPath(path, semantics);
        var parentPath = Path.GetDirectoryName(canonicalPath)
            ?? throw new InvalidOperationException(
                "The authorized directory has no parent.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var root = await db.RootFolders.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == rootFolderId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The managed root authorization no longer exists.");
        var rootSemantics = TryGetPersistedRootSemantics(root)
            ?? throw new InvalidOperationException(
                "The managed root authorization has incomplete filesystem semantics.");
        if (rootSemantics.Syntax != semantics.Syntax
            || !await ConfiguredRootSemanticsCurrentAsync(
                root,
                rootSemantics,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "The managed root authorization uses incompatible or stale filesystem semantics.");
        }
        if (FileSystemPathIdentity.IsSameOrInside(
                parentPath,
                root.Path,
                rootSemantics))
        {
            return await AuthorizePathWithinBoundaryAsync(
                canonicalPath,
                rootSemantics,
                root.Id,
                root.Path,
                root.DirectoryObjectIdentityVersion,
                root.DirectoryObjectIdentity,
                root.DirectoryObjectIdentityUnavailableReason,
                ignoreUnavailableReason: true,
                cancellationToken);
        }

        var activeRelocations = await db.RootFolderRelocations
            .AsNoTracking()
            .Where(relocation =>
                relocation.ActiveRootFolderId == rootFolderId)
            .ToListAsync(cancellationToken);
        var relocation = activeRelocations
            .Where(candidate => HasCompatibleSyntax(
                candidate.TargetPath,
                semantics.Syntax))
            .Where(candidate => FileSystemPathIdentity.IsSameOrInside(
                parentPath,
                candidate.TargetPath,
                semantics))
            .OrderByDescending(candidate => candidate.TargetPath.Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The authorized directory is outside its managed root and active relocation target.");
        return await AuthorizePathWithinBoundaryAsync(
            canonicalPath,
            semantics,
            root.Id,
            relocation.TargetPath,
            relocation.TargetDirectoryObjectIdentityVersion,
            relocation.TargetDirectoryObjectIdentity,
            relocation.TargetDirectoryObjectIdentityUnavailableReason,
            ignoreUnavailableReason: false,
            cancellationToken);
    }

    private static async Task<AuthorizedLibraryDirectoryOwnership>
        AuthorizePathWithinBoundaryAsync(
            string canonicalPath,
            FileSystemPathSemantics semantics,
            int rootFolderId,
            string boundaryPath,
            int? expectedIdentityVersion,
            string? expectedIdentity,
            string? identityUnavailableReason,
            bool ignoreUnavailableReason,
            CancellationToken cancellationToken)
    {
        boundaryPath = CanonicalizeHostAuthorizedPath(
            boundaryPath,
            semantics);
        var parentPath = Path.GetDirectoryName(canonicalPath)
            ?? throw new InvalidOperationException(
                "The authorized directory has no parent.");
        if (!FileSystemPathIdentity.IsSameOrInside(
                parentPath,
                boundaryPath,
                semantics))
        {
            throw new InvalidOperationException(
                "The authorized directory is outside its managed root boundary.");
        }

        var boundary = PinnedDirectoryCreation.OpenPinnedBoundary(boundaryPath);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((!ignoreUnavailableReason
                    && !string.IsNullOrWhiteSpace(identityUnavailableReason))
                || !ManagedDirectoryIdentity.MatchesNativeIdentity(
                    expectedIdentityVersion,
                    expectedIdentity,
                    boundary.GetDirectoryObjectIdentity())
                || !boundary.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The managed root boundary no longer identifies its authorized physical generation.");
            }

            var current = boundary.Duplicate();
            try
            {
                if (!FileSystemPathIdentity.AreEquivalent(
                        parentPath,
                        boundaryPath,
                        semantics))
                {
                    var relative = Path.GetRelativePath(boundaryPath, parentPath);
                    foreach (var segment in relative.Split(
                        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                        StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (segment is "." or "..")
                        {
                            throw new InvalidOperationException(
                                "The authorized directory traversal contains navigation.");
                        }

                        var next = current.OpenExistingChild(segment);
                        current.Dispose();
                        current = next;
                    }
                }

                return new AuthorizedLibraryDirectoryOwnership(rootFolderId, current);
            }
            catch
            {
                current.Dispose();
                throw;
            }
        }
        finally
        {
            boundary.Dispose();
        }
    }

    private static string CanonicalizeHostAuthorizedPath(
        string path,
        FileSystemPathSemantics semantics)
    {
        if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                path,
                out var canonicalPath,
                out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        if (!FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(
                canonicalPath,
                out var hostSyntax)
            || hostSyntax != semantics.Syntax)
        {
            throw new InvalidOperationException(
                "The authorized path semantics do not match the current host filesystem syntax.");
        }

        return canonicalPath;
    }

}
