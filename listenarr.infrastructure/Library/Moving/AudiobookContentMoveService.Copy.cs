using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task CopySourceContentsAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string copyDestination,
        IReadOnlyList<MoveJobEntry> manifest,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        ValidatedTempOwnership? tempOwnership,
        bool directCopyOwnershipValidated,
        CancellationToken cancellationToken)
    {
        ValidateExistingDestinationContents(
            source,
            copyDestination,
            manifest,
            request.JobId,
            targetSemantics,
            tempOwnership,
            quarantineOwnership: null,
            allowPartialFiles: tempOwnership != null || directCopyOwnershipValidated,
            targetDirectoryOwnership: request.TargetDirectoryOwnership);

        foreach (var manifestEntry in manifest.OrderBy(entry => entry.EntryType))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsRootManifestEntry(manifestEntry))
            {
                ValidateExistingMoveDirectory(copyDestination, "copy destination root");
                continue;
            }

            if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                copyDestination,
                manifestEntry.RelativePath,
                targetSemantics,
                out var destinationPath))
            {
                throw new MoveNeedsAttentionException(
                    $"Move entry destination escaped target root: {manifestEntry.RelativePath}");
            }

            if (!FileSystemSafety.TryValidateMutationTarget(
                destinationPath,
                [copyDestination],
                out destinationPath,
                out var destinationReason))
            {
                throw new MoveNeedsAttentionException(destinationReason);
            }

            if (manifestEntry.EntryType == MoveJobEntryType.Directory)
            {
                await EnsurePinnedCopyDirectoryAsync(
                    request,
                    source,
                    target,
                    copyDestination,
                    manifestEntry.RelativePath,
                    targetSemantics,
                    cancellationToken);
                continue;
            }

            if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                source,
                manifestEntry.RelativePath,
                sourceSemantics,
                out var entry))
            {
                throw new MoveNeedsAttentionException(
                    $"Move entry escaped source root: {manifestEntry.RelativePath}");
            }

            await CopyFileWithRetryAsync(
                request,
                source,
                target,
                entry,
                destinationPath,
                manifestEntry,
                copyDestination,
                tempOwnership != null,
                tempOwnership != null || directCopyOwnershipValidated,
                sourceSemantics,
                targetSemantics,
                cancellationToken);
        }
    }

    private void ValidateExistingDestinationContents(
        string source,
        string destinationRoot,
        IReadOnlyCollection<MoveJobEntry> manifest,
        Guid jobId,
        FileSystemPathSemantics targetSemantics,
        ValidatedTempOwnership? tempOwnership = null,
        ValidatedQuarantineOwnership? quarantineOwnership = null,
        bool allowPartialFiles = true,
        LibraryDirectoryOwnership? targetDirectoryOwnership = null,
        bool allowRecoveryMarker = true)
    {
        if (!Directory.Exists(destinationRoot))
        {
            return;
        }

        RevalidateTargetDirectoryOwnership(targetDirectoryOwnership);
        if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
            destinationRoot,
            out var files,
            out var directories,
            out var reason))
        {
            throw new MoveNeedsAttentionException(reason);
        }

        var expectedPaths = new HashSet<string>(StringComparer.Ordinal);
        var retentionPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest)
        {
            if (IsRootManifestEntry(entry))
            {
                expectedPaths.Add(FileSystemPathIdentity.CreateKey(
                    "move-target",
                    destinationRoot,
                    targetSemantics));
                continue;
            }

            if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                destinationRoot,
                entry.RelativePath,
                targetSemantics,
                out var expectedPath))
            {
                throw new MoveNeedsAttentionException("A manifest entry escaped the destination root.");
            }

            expectedPaths.Add(FileSystemPathIdentity.CreateKey("move-target", expectedPath, targetSemantics));
            if (entry.EntryType == MoveJobEntryType.File
                && Path.GetDirectoryName(expectedPath) is { } expectedParent)
            {
                retentionPaths.Add(FileSystemPathIdentity.CreateKey(
                    "move-target-retention",
                    Path.Join(
                        expectedParent,
                        PinnedDestinationRetentionGuard.CreateRetentionName(
                            jobId,
                            entry.RelativePath)),
                    targetSemantics));
            }
        }

        var markerPath = GetRecoveryMarkerPath(destinationRoot, jobId);
        var partialSuffix = $".listenarr-{jobId:N}.partial";
        var sourceInsideDestination = IsSameOrInside(source, destinationRoot, targetSemantics);

        foreach (var directory in directories)
        {
            if ((quarantineOwnership != null
                    && IsSameOrInside(
                        directory,
                        quarantineOwnership.DirectoryPath,
                        targetSemantics))
                || (sourceInsideDestination
                    && (IsSameOrInside(directory, source, targetSemantics)
                        || IsSameOrInside(source, directory, targetSemantics))))
            {
                continue;
            }

            var key = FileSystemPathIdentity.CreateKey("move-target", directory, targetSemantics);
            if (!expectedPaths.Contains(key))
            {
                throw new MoveNeedsAttentionException(
                    $"Destination contains an unowned directory: {Path.GetRelativePath(destinationRoot, directory)}");
            }
        }

        foreach (var file in files)
        {
            if (IsValidatedTargetOwnershipMarker(
                    file,
                    targetDirectoryOwnership,
                    targetSemantics)
                || (allowRecoveryMarker
                    && FileSystemPathIdentity.AreEquivalent(
                        file,
                        markerPath,
                        targetSemantics))
                || (tempOwnership != null
                    && FileSystemPathIdentity.AreEquivalent(
                        file,
                        tempOwnership.MarkerPath,
                        targetSemantics))
                || (quarantineOwnership != null
                    && IsSameOrInside(
                        file,
                        quarantineOwnership.DirectoryPath,
                        targetSemantics))
                || (sourceInsideDestination && IsSameOrInside(file, source, targetSemantics)))
            {
                continue;
            }

            var fileKey = FileSystemPathIdentity.CreateKey(
                "move-target-retention",
                file,
                targetSemantics);
            if (retentionPaths.Contains(fileKey))
            {
                continue;
            }

            var isPartialFile = file.EndsWith(partialSuffix, StringComparison.Ordinal);
            if (isPartialFile && !allowPartialFiles)
            {
                throw new MoveNeedsAttentionException(
                    $"Finalized destination contains an incomplete copy artifact: {Path.GetRelativePath(destinationRoot, file)}");
            }

            var expectedFile = isPartialFile
                ? file[..^partialSuffix.Length]
                : file;
            var key = FileSystemPathIdentity.CreateKey("move-target", expectedFile, targetSemantics);
            if (!expectedPaths.Contains(key))
            {
                throw new MoveNeedsAttentionException(
                    $"Destination contains an unowned file: {Path.GetRelativePath(destinationRoot, file)}");
            }
        }
    }

    private static void RejectUnownedPartialArtifacts(
        string target,
        Guid jobId,
        bool hasStructuredRecoveryMarker)
    {
        if (hasStructuredRecoveryMarker || !Directory.Exists(target))
        {
            return;
        }

        if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                target,
                out var files,
                out _,
                out var reason))
        {
            throw new MoveNeedsAttentionException(reason);
        }

        var partialSuffix = $".listenarr-{jobId:N}.partial";
        var partial = files.FirstOrDefault(path =>
            path.EndsWith(partialSuffix, StringComparison.Ordinal));
        if (partial != null)
        {
            throw new MoveNeedsAttentionException(
                $"A job-shaped partial file exists without structured move ownership and was preserved: {Path.GetRelativePath(target, partial)}");
        }
    }

    private Task CopyFileWithRetryAsync(
        AudiobookContentMoveRequest request,
        string sourceRoot,
        string target,
        string sourceFile,
        string destinationFile,
        MoveJobEntry manifestEntry,
        string destinationRoot,
        bool destinationIsJobOwnedTemp,
        bool destinationHasStructuredOwnership,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        CancellationToken cancellationToken) =>
        CopyFileWithPinnedRetryAsync(
            request,
            sourceRoot,
            target,
            sourceFile,
            destinationFile,
            manifestEntry,
            destinationRoot,
            destinationIsJobOwnedTemp,
            destinationHasStructuredOwnership,
            sourceSemantics,
            targetSemantics,
            cancellationToken);
}
