using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private enum PinnedDirectoryMoveOutcome
    {
        NotApplicable,
        NotMoved,
        Moved,
        Indeterminate
    }

    private PinnedDirectoryMoveOutcome TryPinnedSameVolumeDirectoryMove(
        string sourceDirectory,
        string destinationDirectory)
    {
        string? sourceObjectIdentity = null;
        PinnedDirectoryCreation.PinnedFileEntry? renameJournal = null;
        var namespaceMutationStarted = false;
        try
        {
            var source = Path.GetFullPath(sourceDirectory);
            var destination = Path.GetFullPath(destinationDirectory);
            var sourceParentPath = Path.GetDirectoryName(source)
                ?? throw new InvalidOperationException(
                    "The source directory has no parent.");
            var destinationParentPath = Path.GetDirectoryName(destination)
                ?? throw new InvalidOperationException(
                    "The destination directory has no parent.");
            using var sourceParent =
                PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                    sourceParentPath,
                    createMissing: false);
            using var destinationParent =
                PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                    destinationParentPath,
                    createMissing: false);
            using var sourcePublication =
                sourceParent.OpenExistingChildForPublication(
                    Path.GetFileName(source));
            using var sourceAnchor =
                sourcePublication.OpenCreatedDirectoryAnchor();
            sourceObjectIdentity = sourceAnchor.GetDirectoryObjectIdentity();
            if (!sourceAnchor.IsOnSameVolume(destinationParent))
            {
                return PinnedDirectoryMoveOutcome.NotApplicable;
            }
            using var occupied =
                destinationParent.TryOpenExistingChildForPublication(
                    Path.GetFileName(destination));
            if (occupied != null || File.Exists(destination))
            {
                return PinnedDirectoryMoveOutcome.NotMoved;
            }

            // Prove directory barriers are available before changing either
            // public pathname.
            FlushFileMoveDirectory(
                sourceParent,
                "same-volume directory source capability");
            FlushFileMoveDirectory(
                destinationParent,
                "same-volume directory destination capability");
            renameJournal = PublishDirectoryRenameJournal(
                sourceParent,
                destinationParent,
                source,
                destination,
                sourceObjectIdentity);
            AfterDirectoryRenameJournalPublishedForTest?.Invoke(
                renameJournal.FileName);
            namespaceMutationStarted = true;
            using var relocated = sourcePublication.MovePinnedDirectoryTo(
                destinationParent,
                Path.GetFileName(destination));
            FlushFileMoveDirectory(
                sourceParent,
                "same-volume directory source retirement");
            FlushFileMoveDirectory(
                destinationParent,
                "same-volume directory destination publication");
            if (!relocated.VisiblePathMatches())
            {
                return PinnedDirectoryMoveOutcome.Indeterminate;
            }

            renameJournal.Delete(immediateWindows: true);
            sourceParent.FlushDirectoryEntry();
            renameJournal.Dispose();
            renameJournal = null;
            return PinnedDirectoryMoveOutcome.Moved;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            var reconciled = namespaceMutationStarted
                && sourceObjectIdentity != null
                ? ReconcilePinnedDirectoryMove(
                    sourceDirectory,
                    destinationDirectory,
                    sourceObjectIdentity)
                : PinnedDirectoryMoveOutcome.NotMoved;
            if (reconciled != PinnedDirectoryMoveOutcome.Indeterminate
                && renameJournal != null)
            {
                TryRetireDirectoryRenameJournal(
                    sourceDirectory,
                    renameJournal);
            }

            _logger.LogWarning(
                exception,
                "Pinned same-volume directory rename ended with outcome {Outcome}: {Source} -> {Destination}",
                reconciled,
                LogRedaction.SanitizeFilePath(sourceDirectory),
                LogRedaction.SanitizeFilePath(destinationDirectory));
            return reconciled;
        }
        finally
        {
            renameJournal?.Dispose();
        }
    }

    private static PinnedDirectoryMoveOutcome ReconcilePinnedDirectoryMove(
        string sourceDirectory,
        string destinationDirectory,
        string expectedSourceIdentity)
    {
        try
        {
            var source = Path.GetFullPath(sourceDirectory);
            var destination = Path.GetFullPath(destinationDirectory);
            var sourceIdentity = TryGetPinnedDirectoryIdentity(source);
            var destinationIdentity = TryGetPinnedDirectoryIdentity(destination);
            var sourceStillOwnsGeneration = string.Equals(
                sourceIdentity,
                expectedSourceIdentity,
                StringComparison.Ordinal);
            var destinationOwnsGeneration = string.Equals(
                destinationIdentity,
                expectedSourceIdentity,
                StringComparison.Ordinal);

            if (destinationOwnsGeneration && !sourceStillOwnsGeneration)
            {
                return PinnedDirectoryMoveOutcome.Moved;
            }

            if (sourceStillOwnsGeneration && !destinationOwnsGeneration)
            {
                return PinnedDirectoryMoveOutcome.NotMoved;
            }

            return PinnedDirectoryMoveOutcome.Indeterminate;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            return PinnedDirectoryMoveOutcome.Indeterminate;
        }
    }

    private static string? TryGetPinnedDirectoryIdentity(string directory)
    {
        try
        {
            using var anchor =
                PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                    directory,
                    createMissing: false);
            return anchor.GetDirectoryObjectIdentity();
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException or PlatformNotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static bool CanCopyDirectoryAcrossVolumesWithoutFidelityLoss(
        DirectoryCopySnapshot snapshot)
    {
        if (snapshot.Files
            .GroupBy(file => file.Identity)
            .Any(group => group.Count() > 1))
        {
            return false;
        }

        try
        {
            using var root =
                PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                    snapshot.SourceRoot);
            if (root.HasUnsupportedCrossVolumeMetadata())
            {
                return false;
            }
            foreach (var relativeDirectory in snapshot.RelativeDirectories)
            {
                using var directory =
                    PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                        ResolveSnapshotPath(
                            snapshot.SourceRoot,
                            relativeDirectory,
                            "cross-volume source directory"));
                if (directory.HasUnsupportedCrossVolumeMetadata())
                {
                    return false;
                }
            }
            foreach (var file in snapshot.Files)
            {
                var path = ResolveSnapshotPath(
                    snapshot.SourceRoot,
                    file.RelativePath,
                    "cross-volume source file");
                var parentPath = Path.GetDirectoryName(path)!;
                using var parent =
                    PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                        parentPath);
                using var entry = parent.OpenExistingFile(
                    Path.GetFileName(path),
                    requireDeleteAccess: false);
                if (entry.HasUnsupportedCrossVolumeMetadata())
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException or PlatformNotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
