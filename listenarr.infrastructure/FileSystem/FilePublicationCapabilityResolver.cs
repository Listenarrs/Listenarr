using Listenarr.Domain.Common;
using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Options;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed class FilePublicationCapabilityResolver(
    IRootFolderRepository rootFolderRepository,
    IRootFolderStorageHealthResolver storageHealthResolver,
    IOptions<FileMoverOptions>? options = null)
    : IFilePublicationCapabilityResolver
{
    public async Task<FilePublicationPlan> ResolveAsync(
        FileAction requestedAction,
        string source,
        string destination,
        FilePublicationSourceProof sourceProof,
        CancellationToken cancellationToken = default,
        Guid? compatibilityBatchId = null,
        CompatibilityCleanupOwner cleanupOwner = CompatibilityCleanupOwner.None)
    {
        sourceProof.Validate();
        if (requestedAction is not (
                FileAction.Move or FileAction.Copy or FileAction.HardlinkCopy))
        {
            return FilePublicationPlan.Blocked(
                requestedAction,
                "unsupported_action",
                "The requested action cannot publish an audiobook file.");
        }

        var roots = await rootFolderRepository.GetAllAsync();
        var destinationRoot = FindContainingRoot(destination, roots);
        if (destinationRoot == null)
        {
            return FilePublicationPlan.Blocked(
                requestedAction,
                "destination_root_unavailable",
                "The destination is not inside a configured root with persisted path semantics.");
        }

        var destinationHealth = await storageHealthResolver.ResolveAsync(
            destinationRoot,
            cancellationToken);
        if (!destinationHealth.CanPublishAdditively)
        {
            return FilePublicationPlan.Blocked(
                requestedAction,
                "destination_publication_unavailable",
                destinationHealth.Message
                    ?? "The destination does not authorize new file publication.");
        }

        RootFolder? sourceRoot = null;
        // Retirement is a current-operation capability decision. The source
        // content proof does not need a persisted kernel identity.
        var sourceCanBeRetired = true;
        var sourceCanBeRetiredAfterVerifiedCopy = true;
        if (requestedAction == FileAction.Move)
        {
            sourceRoot = FindContainingRoot(source, roots);
            if (sourceRoot != null)
            {
                var sourceHealth = await storageHealthResolver.ResolveAsync(
                    sourceRoot,
                    cancellationToken);
                sourceCanBeRetired &= sourceHealth.CanRetireSourceNow;
                sourceCanBeRetiredAfterVerifiedCopy =
                    sourceHealth.CanRetireVerifiedSource;
            }
            else if (MayOverlapUnresolvedRoot(source, roots))
            {
                // A configured root whose persisted semantics are unavailable must not be
                // reclassified as an unmanaged external source. Retain until its boundary
                // can be resolved authoritatively.
                sourceCanBeRetired = false;
                sourceCanBeRetiredAfterVerifiedCopy = false;
            }
        }

        if (destinationHealth.CanMutateFilesystem
            && (requestedAction != FileAction.Move || sourceCanBeRetired))
        {
            return FilePublicationPlan.Durable(requestedAction);
        }

        if (requestedAction == FileAction.Move
            && compatibilityBatchId is Guid batchId
            && batchId != Guid.Empty
            && cleanupOwner != CompatibilityCleanupOwner.None
            && sourceCanBeRetiredAfterVerifiedCopy)
        {
            return FilePublicationPlan.VerifiedCleanup(
                batchId,
                cleanupOwner,
                sourceRoot?.Id,
                null,
                destinationRoot.Id,
                null,
                sourceRoot?.StorageContractRevision,
                destinationRoot.StorageContractRevision);
        }

        return options?.Value.WeakPublicationMode == WeakPublicationMode.Disabled
            ? FilePublicationPlan.Blocked(
                requestedAction,
                "compatibility_publication_disabled",
                "Compatibility publication is disabled by FileMover:WeakPublicationMode.")
            : FilePublicationPlan.Additive(requestedAction);
    }

    private static RootFolder? FindContainingRoot(
        string path,
        IReadOnlyCollection<RootFolder> roots)
    {
        var fullPath = Path.GetFullPath(path);
        RootFolder? best = null;
        var bestLength = -1;
        var unavailableLength = -1;
        if (!FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(fullPath, out var syntax))
        {
            return null;
        }
        foreach (var root in roots)
        {
            var persisted = RootFolderPathSemantics.ResolvePersisted(root);
            if (!persisted.HasValue
                || persisted.Value.DetectAmbiguousCaseMatches
                || !FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var rootPath,
                    out _))
            {
                if (UnresolvedRootMayContainPath(root, fullPath, syntax))
                {
                    unavailableLength = Math.Max(unavailableLength, root.Path.Trim().Length);
                }
                continue;
            }
            if (!FileSystemPathIdentity.IsSameOrInside(
                    fullPath,
                    rootPath,
                    persisted.Value.Semantics))
            {
                continue;
            }

            if (rootPath.Length > bestLength)
            {
                best = root;
                bestLength = rootPath.Length;
            }
        }

        return unavailableLength >= bestLength && unavailableLength >= 0 ? null : best;
    }

    private static bool MayOverlapUnresolvedRoot(
        string path,
        IReadOnlyCollection<RootFolder> roots)
    {
        var fullPath = Path.GetFullPath(path);
        if (!FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(
                fullPath,
                out var pathSyntax))
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

            if (UnresolvedRootMayContainPath(root, fullPath, pathSyntax))
            {
                return true;
            }
        }

        return false;
    }

    private static bool UnresolvedRootMayContainPath(
        RootFolder root, string path, FileSystemPathSyntax syntax)
    {
        if (string.IsNullOrWhiteSpace(root.Path)) return false;
        // Contextual spelling is used only to block fallback. It never selects
        // a root or authorizes a trimmed persisted boundary.
        return FileSystemPathIdentity.AmbiguousStoredBoundaryMayContainPath(
            root.Path, path, syntax, root.CaseSensitivityMode)
            || FileSystemPathIdentity.AmbiguousStoredBoundaryMayContainPath(
                root.Path.Trim(), path, syntax, root.CaseSensitivityMode);
    }
}
