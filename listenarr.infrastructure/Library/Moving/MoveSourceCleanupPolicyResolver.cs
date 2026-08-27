using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed class MoveSourceCleanupPolicyResolver(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    IRootFolderStorageHealthResolver storageHealthResolver)
    : IMoveSourceCleanupPolicyResolver
{
    public async Task<MoveSourceCleanupAuthorization> ResolveAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        await using var context = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        var roots = await context.RootFolders
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var sourceRoot = FindMostSpecificRoot(sourcePath, roots);
        var targetRoot = FindMostSpecificRoot(targetPath, roots);
        var sourceMayOverlapUnresolvedRoot = sourceRoot == null
            && MayOverlapUnresolvedRoot(sourcePath, roots);
        var sourceIsManagedRoot = sourceRoot != null
            && AreEquivalent(sourcePath, sourceRoot);

        if (targetRoot == null)
        {
            return Retain(
                sourceRoot,
                null,
                sourceIsManagedRoot,
                "Source files will be retained because the destination is not inside a configured root folder.");
        }
        if (targetRoot.WeakStorageSourceCleanupPolicy
            != WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy)
        {
            return Retain(
                sourceRoot,
                targetRoot,
                sourceIsManagedRoot,
                "Source files will be retained because verified source deletion is not enabled for the destination root folder.");
        }
        if (!(await storageHealthResolver.ResolveAsync(
                targetRoot,
                cancellationToken)).CanPublishAdditively)
        {
            return Retain(
                sourceRoot,
                targetRoot,
                sourceIsManagedRoot,
                "Source files will be retained because the destination storage cannot authorize verified cleanup safely.");
        }
        if (sourceMayOverlapUnresolvedRoot)
        {
            return Retain(
                sourceRoot,
                targetRoot,
                sourceIsManagedRoot,
                "Source files will be retained because the source may overlap a configured root whose path semantics are not authoritative.",
                forceCopyAndRetainSource: true);
        }
        if (sourceRoot != null
            && sourceRoot.WeakStorageSourceCleanupPolicy
                != WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy)
        {
            return Retain(
                sourceRoot,
                targetRoot,
                sourceIsManagedRoot,
                "Source files will be retained because verified source deletion is not enabled for the source root folder.");
        }
        if (sourceRoot != null
            && !(await storageHealthResolver.ResolveAsync(
                    sourceRoot,
                    cancellationToken))
                .CanRetireVerifiedSource)
        {
            return Retain(
                sourceRoot,
                targetRoot,
                sourceIsManagedRoot,
                "Source files will be retained because the source storage cannot authorize verified cleanup safely.",
                forceCopyAndRetainSource: true);
        }

        return new MoveSourceCleanupAuthorization(
            MoveSourceCleanupMode.DeleteAfterVerifiedCopy,
            sourceRoot?.Id,
            sourceRoot?.WeakStoragePolicyRevision,
            targetRoot.Id,
            targetRoot.WeakStoragePolicyRevision,
            sourceIsManagedRoot,
            sourceIsManagedRoot
                ? "Source files will be removed after every copied file is verified. The managed root folder will remain."
                : "Source files will be removed after every copied file is verified.",
            sourceRoot?.StorageContractRevision,
            targetRoot.StorageContractRevision);
    }

    public async Task<bool> IsCurrentAsync(
        MoveSourceCleanupAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (!authorization.DeletesSourceAfterVerifiedCopy
            || authorization.TargetRootFolderId is not int targetRootFolderId
            || authorization.TargetPolicyRevision is not int targetPolicyRevision
            || authorization.TargetStorageContractRevision is not int targetStorageContractRevision)
        {
            return false;
        }

        var rootIds = new[]
            {
                authorization.SourceRootFolderId,
                authorization.TargetRootFolderId
            }
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        await using var context = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        var roots = await context.RootFolders
            .AsNoTracking()
            .Where(root => rootIds.Contains(root.Id))
            .ToDictionaryAsync(root => root.Id, cancellationToken);

        if (!MatchesAuthorizedPolicy(
                roots,
                targetRootFolderId,
                targetPolicyRevision,
                targetStorageContractRevision)
            || !roots.TryGetValue(targetRootFolderId, out var targetRoot)
            || !(await storageHealthResolver.ResolveAsync(
                    targetRoot,
                    cancellationToken)).CanPublishAdditively)
        {
            return false;
        }

        if (authorization.SourceRootFolderId is not int sourceRootFolderId)
        {
            return true;
        }
        if (authorization.SourcePolicyRevision is not int sourcePolicyRevision
            || authorization.SourceStorageContractRevision is not int sourceStorageContractRevision
            || !MatchesAuthorizedPolicy(
                roots,
                sourceRootFolderId,
                sourcePolicyRevision,
                sourceStorageContractRevision)
            || !roots.TryGetValue(sourceRootFolderId, out var sourceRoot))
        {
            return false;
        }

        return (await storageHealthResolver.ResolveAsync(
            sourceRoot,
            cancellationToken)).CanRetireVerifiedSource;
    }

    private static MoveSourceCleanupAuthorization Retain(
        RootFolder? sourceRoot,
        RootFolder? targetRoot,
        bool sourceIsManagedRoot,
        string message,
        bool forceCopyAndRetainSource = false) =>
        new(
            MoveSourceCleanupMode.RetainSource,
            sourceRoot?.Id,
            sourceRoot?.WeakStoragePolicyRevision,
            targetRoot?.Id,
            targetRoot?.WeakStoragePolicyRevision,
            sourceIsManagedRoot,
            message,
            sourceRoot?.StorageContractRevision,
            targetRoot?.StorageContractRevision,
            forceCopyAndRetainSource);

    private static RootFolder? FindMostSpecificRoot(
        string path,
        IEnumerable<RootFolder> roots) =>
        roots
            .Where(root => IsInsideRoot(path, root))
            .OrderByDescending(root => root.Path.Length)
            .FirstOrDefault();

    private static bool IsInsideRoot(string path, RootFolder root)
    {
        var persisted = RootFolderPathSemantics.ResolvePersisted(root);
        if (persisted == null || persisted.Value.DetectAmbiguousCaseMatches)
        {
            return false;
        }
        var resolved = persisted.Value;

        try
        {
            return FileSystemPathIdentity.IsSameOrInside(
                path,
                root.Path,
                resolved.Semantics);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException
                or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool AreEquivalent(string path, RootFolder root)
    {
        var persisted = RootFolderPathSemantics.ResolvePersisted(root);
        if (persisted == null || persisted.Value.DetectAmbiguousCaseMatches)
        {
            return false;
        }
        var resolved = persisted.Value;

        try
        {
            return FileSystemPathIdentity.AreEquivalent(
                path,
                root.Path,
                resolved.Semantics);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException
                or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool MayOverlapUnresolvedRoot(
        string path,
        IReadOnlyCollection<RootFolder> roots)
    {
        string fullPath;
        FileSystemPathSyntax pathSyntax;
        try
        {
            fullPath = Path.GetFullPath(path);
            if (!FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(
                    fullPath,
                    out pathSyntax))
            {
                return true;
            }
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException
                or System.Security.SecurityException)
        {
            return true;
        }

        foreach (var root in roots)
        {
            var persisted = RootFolderPathSemantics.ResolvePersisted(root);
            if (persisted.HasValue
                && !persisted.Value.DetectAmbiguousCaseMatches
                && FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out _,
                    out _))
            {
                continue;
            }

            if (FileSystemPathIdentity.AmbiguousStoredBoundaryMayContainPath(
                    root.Path,
                    fullPath,
                    pathSyntax,
                    root.CaseSensitivityMode))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAuthorizedPolicy(
        IReadOnlyDictionary<int, RootFolder> roots,
        int rootFolderId,
        int policyRevision,
        int storageContractRevision) =>
        roots.TryGetValue(rootFolderId, out var root)
        && root.WeakStorageSourceCleanupPolicy
            == WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy
        && root.WeakStoragePolicyRevision == policyRevision
        && root.StorageContractRevision == storageContractRevision;
}
