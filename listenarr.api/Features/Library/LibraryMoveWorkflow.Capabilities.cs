using Listenarr.Application.Common.Exceptions;

namespace Listenarr.Api.Features.Library;

public sealed partial class LibraryMoveWorkflow
{
    private static async Task<RootFolderStorageObservation?>
        ResolveReadableSourceStorageAsync(
            MoveRootBoundary? sourceManagedBoundary,
            IReadOnlyCollection<RootFolder> rootFolders,
            IRootFolderStorageHealthResolver storageHealthResolver,
            CancellationToken cancellationToken)
    {
        if (sourceManagedBoundary?.ManagedRootFolderId is not int sourceRootFolderId)
        {
            return null;
        }

        var sourceRootFolder = rootFolders.First(root => root.Id == sourceRootFolderId);
        var sourceStorage = await storageHealthResolver.ResolveAsync(
            sourceRootFolder,
            cancellationToken);
        if (!sourceStorage.CanReadFilesystem)
        {
            throw new ApplicationValidationException(
                "source_filesystem_read_unavailable",
                sourceStorage.Message
                    ?? "Source root does not currently allow files to be read.");
        }

        return sourceStorage;
    }

    private static (
        MoveSourceCleanupAuthorization Authorization,
        bool ForceCopyAndRetainSource,
        bool DeleteEmptySource) ApplySourceStorageCapabilities(
            MoveSourceCleanupAuthorization authorization,
            RootFolderStorageObservation? sourceStorage,
            bool deleteEmptySource)
    {
        if (authorization.DeletesSourceAfterVerifiedCopy
            && sourceStorage is { CanRetireVerifiedSource: false })
        {
            authorization = authorization with
            {
                Mode = MoveSourceCleanupMode.RetainSource,
                Message =
                    "Source files will be retained because the source storage cannot authorize verified cleanup safely."
            };
        }

        var forceCopyAndRetainSource = authorization.ForceCopyAndRetainSource
            || (sourceStorage != null
                && !sourceStorage.CanRetireDurably
                && !sourceStorage.CanRetireVerifiedSource);
        return (
            authorization,
            forceCopyAndRetainSource,
            deleteEmptySource && !forceCopyAndRetainSource);
    }
}
