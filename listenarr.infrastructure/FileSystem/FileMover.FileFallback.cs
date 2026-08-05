using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private enum FileMoveFallbackOutcome
    {
        Success,
        CopyFailed,
        SourceRetained
    }

    private async Task<FileMoveFallbackOutcome> TryManagedFileMoveFallbackAsync(
        FileMoveGateLease lease,
        Guid? operationId,
        string? expectedSourcePhysicalObjectIdentity = null,
        bool requirePhysicalIdentityPreservation = false)
    {
        var sourceFile = lease.SourcePath;
        var destinationFile = lease.DestinationPath;
        if (!lease.SourceParent.VisiblePathMatches()
            || !lease.DestinationParent.VisiblePathMatches())
        {
            return FileMoveFallbackOutcome.CopyFailed;
        }

        try
        {
            return await TryRemoveVerifiedFileMoveSourceAsync(
                lease,
                operationId,
                expectedSourcePhysicalObjectIdentity,
                requirePhysicalIdentityPreservation);
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning(
                exception,
                "Verified file move fallback failed for {Source} -> {Destination}",
                LogRedaction.SanitizeFilePath(sourceFile),
                LogRedaction.SanitizeFilePath(destinationFile));
            return FileMoveFallbackOutcome.CopyFailed;
        }
    }

    private async Task<FileMoveFallbackOutcome> TryRemoveVerifiedFileMoveSourceAsync(
        FileMoveGateLease lease,
        Guid? operationId,
        string? expectedSourcePhysicalObjectIdentity = null,
        bool requirePhysicalIdentityPreservation = false)
    {
        var sourceFile = lease.SourcePath;
        var destinationFile = lease.DestinationPath;
        var removalOutcome = await TryRemoveVerifiedFileMoveSourceWithClaimsAsync(
            lease,
            operationId,
            expectedSourcePhysicalObjectIdentity,
            requirePhysicalIdentityPreservation);
        if (removalOutcome == VerifiedFileMoveRemovalOutcome.Removed)
        {
            return FileMoveFallbackOutcome.Success;
        }

        using var sourceEntry = lease.SourceParent.TryOpenExistingFile(
            lease.SourceName,
            requireDeleteAccess: false);
        var sourceExists = sourceEntry != null;
        using var destinationEntry = lease.DestinationParent.TryOpenExistingFile(
            lease.DestinationName,
            requireDeleteAccess: false);
        var destinationExists = destinationEntry != null;
        return sourceExists && !destinationExists
            ? FileMoveFallbackOutcome.CopyFailed
            : FileMoveFallbackOutcome.SourceRetained;
    }

}
