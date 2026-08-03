using System.ComponentModel;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private const int DestinationRetentionCleanupProtectionVersion = 1;

    private async Task MoveSourceFileToPinnedQuarantineAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string sourceFile,
        string quarantineFile,
        string quarantineRoot,
        MoveJobEntry manifestEntry,
        FileSystemPathSemantics sourceSemantics,
        CancellationToken cancellationToken)
    {
        var (directorySegments, fileName) = SplitPinnedRelativeFilePath(
            manifestEntry.RelativePath,
            sourceSemantics);
        try
        {
            using var sourcePath = PinnedMoveDirectoryPath.OpenExisting(
                source,
                directorySegments);
            using var quarantinePath = await PinnedMoveDirectoryPath.OpenOrCreateAsync(
                quarantineRoot,
                directorySegments,
                () => EnsureMutationAuthorizedAsync(
                    request,
                    source,
                    target,
                    cancellationToken));
            ValidatePinnedParentPath(
                sourcePath.Current.FullPath,
                sourceFile,
                sourceSemantics,
                "source cleanup");
            ValidatePinnedParentPath(
                quarantinePath.Current.FullPath,
                quarantineFile,
                sourceSemantics,
                "quarantine cleanup");

            using var sourceEntry = sourcePath.Current.OpenExistingFile(
                fileName,
                requireDeleteAccess: true);
            ValidatePinnedSourcePhysicalIdentity(
                request,
                manifestEntry,
                sourceEntry);
            if (!sourceEntry.VisiblePathMatches()
                || !await sourceEntry.MatchesAsync(
                    manifestEntry.Length,
                    manifestEntry.Sha256,
                    cancellationToken))
            {
                throw new MoveNeedsAttentionException(
                    $"The pinned source cleanup entry changed after validation: {manifestEntry.RelativePath}");
            }

            sourcePath.EnsureVisibleHierarchy();
            quarantinePath.EnsureVisibleHierarchy();
            faultInjector?.OnSourceCleanupMutation(
                request.JobId,
                SourceCleanupFaultPoint.BeforeSourceFilePublication);
            await EnsureMutationAuthorizedAsync(
                request,
                source,
                target,
                cancellationToken);
            sourcePath.EnsureVisibleHierarchy();
            quarantinePath.EnsureVisibleHierarchy();
            if (!sourceEntry.VisiblePathMatches()
                || !await sourceEntry.MatchesAsync(
                    manifestEntry.Length,
                    manifestEntry.Sha256,
                    cancellationToken))
            {
                throw new MoveNeedsAttentionException(
                    $"The pinned source cleanup entry changed at publication: {manifestEntry.RelativePath}");
            }

            sourceEntry.MoveTo(quarantinePath.Current, fileName);
            if (!await sourceEntry.MatchesAsync(
                    manifestEntry.Length,
                    manifestEntry.Sha256,
                    cancellationToken))
            {
                throw new MoveNeedsAttentionException(
                    $"The pinned quarantine publication changed bytes: {manifestEntry.RelativePath}");
            }
        }
        catch (MoveNeedsAttentionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            throw new MoveNeedsAttentionException(
                $"The source file could not be quarantined through pinned directory handles "
                + $"for '{manifestEntry.RelativePath}': {exception.Message}");
        }
    }

    private async Task DeletePinnedQuarantineFileAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string quarantineFile,
        string quarantineRoot,
        MoveJobEntry manifestEntry,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        CancellationToken cancellationToken)
    {
        var (quarantineDirectorySegments, fileName) = SplitPinnedRelativeFilePath(
            manifestEntry.RelativePath,
            sourceSemantics);
        var (targetDirectorySegments, targetFileName) = SplitPinnedRelativeFilePath(
            manifestEntry.RelativePath,
            targetSemantics);
        if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                target,
                manifestEntry.RelativePath,
                targetSemantics,
                out var targetFile))
        {
            throw new MoveNeedsAttentionException(
                $"Published target path escaped before pinned source deletion: {manifestEntry.RelativePath}");
        }

        try
        {
            using var quarantinePath = PinnedMoveDirectoryPath.OpenExisting(
                quarantineRoot,
                quarantineDirectorySegments);
            using var targetPath = PinnedMoveDirectoryPath.OpenExisting(
                target,
                targetDirectorySegments);
            ValidatePinnedParentPath(
                quarantinePath.Current.FullPath,
                quarantineFile,
                sourceSemantics,
                "quarantine deletion");
            ValidatePinnedParentPath(
                targetPath.Current.FullPath,
                targetFile,
                targetSemantics,
                "target verification");
            using var quarantineEntry = quarantinePath.Current.OpenExistingFile(
                fileName,
                requireDeleteAccess: true);
            using var targetEntry = targetPath.Current.OpenExistingFile(
                targetFileName,
                requireDeleteAccess: false);
            if (!quarantineEntry.VisiblePathMatches()
                || !await quarantineEntry.MatchesAsync(
                    manifestEntry.Length,
                    manifestEntry.Sha256,
                    cancellationToken))
            {
                throw new MoveNeedsAttentionException(
                    $"The pinned quarantine entry changed before deletion: {manifestEntry.RelativePath}");
            }

            quarantinePath.EnsureVisibleHierarchy();
            faultInjector?.OnSourceCleanupMutation(
                request.JobId,
                SourceCleanupFaultPoint.BeforeQuarantineFileRemoval);
            await EnsureMutationAuthorizedAsync(
                request,
                source,
                target,
                cancellationToken);
            quarantinePath.EnsureVisibleHierarchy();
            targetPath.EnsureVisibleHierarchy();
            if (!quarantineEntry.VisiblePathMatches()
                || !await quarantineEntry.MatchesAsync(
                    manifestEntry.Length,
                    manifestEntry.Sha256,
                    cancellationToken))
            {
                throw new MoveNeedsAttentionException(
                    $"The pinned quarantine entry changed at deletion: {manifestEntry.RelativePath}");
            }

            // The target is deliberately verified last. No callback, lease await,
            // pathname reopen, or other asynchronous work may occur between this
            // content check and retiring the only recoverable source generation.
            if (!targetEntry.VisiblePathMatches()
                || !await targetEntry.MatchesAsync(
                    manifestEntry.Length,
                    manifestEntry.Sha256,
                    cancellationToken))
            {
                throw new MoveNeedsAttentionException(
                    $"The pinned published target changed at source deletion: {manifestEntry.RelativePath}");
            }

            quarantinePath.EnsureVisibleHierarchy();
            targetPath.EnsureVisibleHierarchy();
            if (!quarantineEntry.VisiblePathMatches()
                || !targetEntry.VisiblePathMatches())
            {
                throw new MoveNeedsAttentionException(
                    $"The pinned source or target generation changed at deletion: {manifestEntry.RelativePath}");
            }

            var retentionName =
                PinnedDestinationRetentionGuard.CreateRetentionName(
                    request.JobId,
                    manifestEntry.RelativePath);
            using var retention =
                await PinnedDestinationRetentionGuard.OpenOrRepairOwnedAsync(
                    targetPath.Current,
                    targetFileName,
                    retentionName,
                    manifestEntry.Length,
                    manifestEntry.Sha256
                        ?? throw new MoveNeedsAttentionException(
                            $"The move manifest has no content identity for {manifestEntry.RelativePath}."),
                    cancellationToken);
            if (retention == null)
            {
                throw new MoveNeedsAttentionException(
                    $"A durable target retention guard could not be established for {manifestEntry.RelativePath}.");
            }

            await UpdateCleanupProtectionVersionAsync(
                request.JobId,
                request.LeaseToken,
                manifestEntry.RelativePath,
                DestinationRetentionCleanupProtectionVersion,
                cancellationToken);
            manifestEntry.CleanupProtectionVersion =
                DestinationRetentionCleanupProtectionVersion;

            faultInjector?.OnSourceCleanupMutation(
                request.JobId,
                SourceCleanupFaultPoint.BeforePinnedQuarantineDelete);
            if (!await retention.TryLinearizePublicationAsync(cancellationToken))
            {
                throw new MoveNeedsAttentionException(
                    $"The published target changed at the durable source-retirement boundary: {manifestEntry.RelativePath}");
            }

            quarantineEntry.Delete();
            quarantinePath.Current.FlushDirectoryEntry();
            faultInjector?.OnSourceCleanupMutation(
                request.JobId,
                SourceCleanupFaultPoint.AfterPinnedQuarantineDelete);
            if (!await retention.CompleteAsync(cancellationToken))
            {
                throw new MoveNeedsAttentionException(
                    $"The durable target retention guard could not be retired for {manifestEntry.RelativePath}.");
            }
        }
        catch (MoveNeedsAttentionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or InvalidOperationException or Win32Exception
                or UnauthorizedAccessException)
        {
            throw new MoveNeedsAttentionException(
                $"The quarantine file could not be removed through pinned directory handles: {exception.Message}");
        }
    }

    private async Task<bool> TryCompleteMissingQuarantineRetentionAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        MoveJobEntry manifestEntry,
        FileSystemPathSemantics targetSemantics,
        CancellationToken cancellationToken)
    {
        var (targetDirectorySegments, targetFileName) = SplitPinnedRelativeFilePath(
            manifestEntry.RelativePath,
            targetSemantics);
        try
        {
            await EnsureMutationAuthorizedAsync(
                request,
                source,
                target,
                cancellationToken);
            using var targetPath = PinnedMoveDirectoryPath.OpenExisting(
                target,
                targetDirectorySegments);
            targetPath.EnsureVisibleHierarchy();
            var retentionName =
                PinnedDestinationRetentionGuard.CreateRetentionName(
                    request.JobId,
                    manifestEntry.RelativePath);
            using var retention =
                await PinnedDestinationRetentionGuard.OpenExistingAsync(
                    targetPath.Current,
                    targetFileName,
                    retentionName,
                    manifestEntry.Length,
                    manifestEntry.Sha256
                        ?? throw new MoveNeedsAttentionException(
                            $"The move manifest has no content identity for {manifestEntry.RelativePath}."),
                    cancellationToken);
            if (retention == null)
            {
                return false;
            }

            return await retention.TryLinearizePublicationAsync(cancellationToken)
                && await retention.CompleteAsync(cancellationToken);
        }
        catch (MoveNeedsAttentionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or InvalidOperationException or Win32Exception
                or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new MoveNeedsAttentionException(
                $"The durable target retention could not be recovered for '{manifestEntry.RelativePath}': {exception.Message}");
        }
    }

    private static void ValidatePinnedParentPath(
        string pinnedParent,
        string expectedFile,
        FileSystemPathSemantics semantics,
        string description)
    {
        var expectedParent = Path.GetDirectoryName(Path.GetFullPath(expectedFile));
        if (string.IsNullOrWhiteSpace(expectedParent)
            || !FileSystemPathIdentity.AreEquivalent(
                Path.GetFullPath(pinnedParent),
                expectedParent,
                semantics))
        {
            throw new MoveNeedsAttentionException(
                $"The pinned {description} parent does not match the manifest path.");
        }
    }

    private static (IReadOnlyList<string> DirectorySegments, string FileName)
        SplitPinnedRelativeFilePath(
            string relativePath,
            FileSystemPathSemantics semantics)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new MoveNeedsAttentionException(
                "A file manifest entry has no relative path.");
        }

        var separators = semantics.Syntax == FileSystemPathSyntax.Windows
            ? new[] { '\\', '/' }
            : new[] { '/' };
        var lastSeparator = relativePath.LastIndexOfAny(separators);
        var fileName = lastSeparator < 0
            ? relativePath
            : relativePath[(lastSeparator + 1)..];
        var directoryPart = lastSeparator < 0
            ? string.Empty
            : relativePath[..lastSeparator];
        var segments = directoryPart.Split(
            separators,
            StringSplitOptions.RemoveEmptyEntries);
        if (string.IsNullOrWhiteSpace(fileName)
            || segments.Any(segment => segment is "." or ".."))
        {
            throw new MoveNeedsAttentionException(
                "A file manifest entry contains an invalid cleanup path segment.");
        }

        return (segments, fileName);
    }

}
