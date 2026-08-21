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
        CancellationToken cancellationToken = default)
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

        var destinationRoot = await FindContainingRootAsync(
            destination,
            cancellationToken);
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
        if (!destinationHealth.CanMutateFilesystem
            && !destinationHealth.CanPublishNewFiles)
        {
            return FilePublicationPlan.Blocked(
                requestedAction,
                "destination_publication_unavailable",
                destinationHealth.Message
                    ?? "The destination does not authorize new file publication.");
        }

        var sourceCanBeRetired = sourceProof.HasDurablePhysicalObjectIdentity;
        if (requestedAction == FileAction.Move)
        {
            var sourceRoot = await FindContainingRootAsync(
                source,
                cancellationToken);
            if (sourceRoot != null)
            {
                var sourceHealth = await storageHealthResolver.ResolveAsync(
                    sourceRoot,
                    cancellationToken);
                sourceCanBeRetired &= sourceHealth.CanMutateFilesystem;
            }
        }

        if (sourceProof.HasDurablePhysicalObjectIdentity
            && destinationHealth.CanMutateFilesystem
            && (requestedAction != FileAction.Move || sourceCanBeRetired))
        {
            return FilePublicationPlan.Durable(requestedAction);
        }

        return options?.Value.WeakPublicationMode == WeakPublicationMode.Disabled
            ? FilePublicationPlan.Blocked(
                requestedAction,
                "compatibility_publication_disabled",
                "Compatibility publication is disabled by FileMover:WeakPublicationMode.")
            : FilePublicationPlan.Additive(requestedAction);
    }

    private async Task<RootFolder?> FindContainingRootAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        RootFolder? best = null;
        var bestLength = -1;
        foreach (var root in await rootFolderRepository.GetAllAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
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
}
