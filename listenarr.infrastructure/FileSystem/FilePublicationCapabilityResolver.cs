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
        var sourceCanBeRetired = sourceProof.HasDurablePhysicalObjectIdentity;
        var sourceCanBeRetiredAfterVerifiedCopy = true;
        if (requestedAction == FileAction.Move)
        {
            sourceRoot = FindContainingRoot(source, roots);
            if (sourceRoot != null)
            {
                var sourceHealth = await storageHealthResolver.ResolveAsync(
                    sourceRoot,
                    cancellationToken);
                sourceCanBeRetired &= sourceHealth.CanRetireDurably;
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

        if (sourceProof.HasDurablePhysicalObjectIdentity
            && destinationHealth.CanMutateFilesystem
            && (requestedAction != FileAction.Move || sourceCanBeRetired))
        {
            return FilePublicationPlan.Durable(requestedAction);
        }

        if (requestedAction == FileAction.Move
            && compatibilityBatchId is Guid batchId
            && batchId != Guid.Empty
            && cleanupOwner != CompatibilityCleanupOwner.None
            && sourceCanBeRetiredAfterVerifiedCopy
            && destinationRoot.WeakStorageSourceCleanupPolicy
                == WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy
            && (sourceRoot == null
                || sourceRoot.WeakStorageSourceCleanupPolicy
                    == WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy))
        {
            return FilePublicationPlan.VerifiedCleanup(
                batchId,
                cleanupOwner,
                sourceRoot?.Id,
                sourceRoot?.WeakStoragePolicyRevision,
                destinationRoot.Id,
                destinationRoot.WeakStoragePolicyRevision,
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
        foreach (var root in roots)
        {
            var persisted = RootFolderPathSemantics.ResolvePersisted(root);
            if (!persisted.HasValue
                || persisted.Value.DetectAmbiguousCaseMatches
                || !FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var rootPath,
                    out _)
                || !FileSystemPathIdentity.IsSameOrInside(
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

        return best;
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
}
