/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Domain.Audiobooks.Enumerations;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private sealed record DirectoryCopyFileSnapshot(
        string RelativePath,
        RegularFileIdentity Identity);

    private sealed record DirectoryCopySnapshot(
        string SourceRoot,
        RegularFileIdentity SourceRootIdentity,
        IReadOnlyList<string> RelativeDirectories,
        IReadOnlyDictionary<string, RegularFileIdentity> DirectoryIdentities,
        IReadOnlyList<DirectoryCopyFileSnapshot> Files);

    // Snapshot paths are captured lexical evidence. Never fold them by host OS:
    // Windows can expose case-sensitive directory namespaces, where names that differ
    // only by case identify distinct entries.
    internal static StringComparer DirectoryCopySnapshotPathComparer { get; } =
        StringComparer.Ordinal;

    private static bool TryCaptureDirectoryCopySnapshot(
        string sourceDirectory,
        out DirectoryCopySnapshot? snapshot,
        out string reason)
    {
        snapshot = null;
        reason = string.Empty;
        try
        {
            var sourceRoot = Path.GetFullPath(sourceDirectory);
            if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                    sourceRoot,
                    out var files,
                    out var directories,
                    out reason))
            {
                return false;
            }
            if (!TryGetDirectoryIdentity(sourceRoot, out var sourceRootIdentity))
            {
                reason = "The source directory generation could not be identified safely.";
                return false;
            }

            var relativeDirectories = directories
                .Select(path => GetVerifiedRelativePath(sourceRoot, path))
                .OrderBy(PathDepth)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var directoryIdentities =
                new Dictionary<string, RegularFileIdentity>(StringComparer.Ordinal);
            foreach (var relativeDirectory in relativeDirectories)
            {
                var directoryPath = ResolveSnapshotPath(
                    sourceRoot,
                    relativeDirectory,
                    "source directory");
                if (!TryGetDirectoryIdentity(directoryPath, out var identity))
                {
                    reason = "A source directory generation could not be identified safely.";
                    return false;
                }

                directoryIdentities.Add(relativeDirectory, identity);
            }

            var relativeFiles = new List<DirectoryCopyFileSnapshot>(files.Count);
            foreach (var path in files)
            {
                if (!TryGetRegularFileIdentity(path, out var identity))
                {
                    reason = "A source file generation could not be identified safely.";
                    return false;
                }

                relativeFiles.Add(new DirectoryCopyFileSnapshot(
                    GetVerifiedRelativePath(sourceRoot, path),
                    identity));
            }

            relativeFiles.Sort((left, right) => StringComparer.Ordinal.Compare(
                left.RelativePath,
                right.RelativePath));
            snapshot = new DirectoryCopySnapshot(
                sourceRoot,
                sourceRootIdentity,
                relativeDirectories,
                directoryIdentities,
                relativeFiles);
            return true;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            reason = $"The source directory snapshot could not be captured safely: {exception.GetType().Name}.";
            return false;
        }
    }

    private async Task CopyDirectorySnapshotAsync(
        DirectoryCopySnapshot snapshot,
        string destinationDirectory)
    {
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        await EnsureDirectoryCopyTargetSafeAsync(
            snapshot.SourceRoot,
            destinationRoot,
            destinationRoot);

        if (Directory.Exists(destinationRoot))
        {
            if (await DirectoryCopySnapshotExactlyMatchesAsync(snapshot, destinationRoot))
            {
                return;
            }

            throw new IOException(
                "Directory copy destination already exists with conflicting or unexpected content.");
        }

        var destinationParent = Path.GetDirectoryName(destinationRoot);
        if (string.IsNullOrWhiteSpace(destinationParent)
            || !Directory.Exists(destinationParent))
        {
            throw new IOException(
                "Directory copy requires an existing destination parent.");
        }

        using var destinationParentAnchor =
            PinnedDirectoryCreation.OpenPinnedBoundary(destinationParent);
        if (!destinationParentAnchor.VisiblePathMatches()
            || IsLinkedOrUnverifiableEntry(destinationParent)
            || !TryResolvePhysicalPath(destinationParent, out var parentResolution)
            || parentResolution.EntryKind != PhysicalPathEntryKind.Directory
            || parentResolution.EncounteredLink)
        {
            throw new IOException(
                "Directory copy requires a pinned destination parent with no linked path components.");
        }

        var stagingName = $".{Path.GetFileName(destinationRoot)}.listenarr-copy-{Guid.NewGuid():N}";
        var stagingRoot = Path.Join(destinationParent, stagingName);
        using var stagingCreation =
            destinationParentAnchor.TryCreateChildForPublication(stagingName);
        if (!stagingCreation.Created || !stagingCreation.VisiblePathMatches())
        {
            throw new IOException(
                "Directory copy staging could not be created with exclusive ownership.");
        }

        var published = false;
        using var stagingAnchor = stagingCreation.OpenCreatedDirectoryAnchor();
        try
        {
            await PopulateDirectoryCopyStagingAsync(snapshot, stagingAnchor);
            if (!stagingCreation.VisiblePathMatches()
                || !stagingAnchor.VisiblePathMatches()
                || !await DirectoryCopySnapshotExactlyMatchesAsync(snapshot, stagingRoot)
                || !await SourceSnapshotStillMatchesAsync(snapshot))
            {
                throw new IOException(
                    "Directory copy staging or source changed before publication.");
            }

            if (!stagingAnchor.VisiblePathMatches())
            {
                throw new IOException(
                    "Directory copy staging identity changed before publication.");
            }
            if (BeforeDirectoryCopyPublicationForTestAsync != null)
            {
                await BeforeDirectoryCopyPublicationForTestAsync(stagingRoot);
            }
            if (!stagingAnchor.VisiblePathMatches())
            {
                throw new IOException(
                    "Directory copy staging identity changed at the publication boundary.");
            }

            try
            {
                using var publishedAnchor = stagingCreation.PublishCreatedDirectoryTo(
                    destinationParentAnchor,
                    Path.GetFileName(destinationRoot));
                published = true;
                if (!publishedAnchor.VisiblePathMatches()
                    || !stagingAnchor.VisiblePathMatches(destinationRoot))
                {
                    throw new IOException(
                        "The published destination does not identify the pinned staging directory.");
                }
            }
            catch (IOException) when (!published && Directory.Exists(destinationRoot))
            {
                if (await DirectoryCopySnapshotExactlyMatchesAsync(snapshot, destinationRoot))
                {
                    return;
                }

                throw new IOException(
                    "Directory copy destination appeared with conflicting or unexpected content before publication.");
            }

            if (!stagingAnchor.VisiblePathMatches(destinationRoot)
                || !await DirectoryCopySnapshotExactlyMatchesAsync(snapshot, destinationRoot)
                || !await SourceSnapshotStillMatchesAsync(snapshot))
            {
                throw new IOException(
                    "The published directory snapshot could not be verified exactly.");
            }
        }
        finally
        {
            if (!published && stagingCreation.VisiblePathMatches())
            {
                await TryCleanupDirectoryCopyStagingAsync(
                    snapshot,
                    stagingCreation,
                    stagingAnchor,
                    stagingRoot);
            }
        }
    }

    private async Task PopulateDirectoryCopyStagingAsync(
        DirectoryCopySnapshot snapshot,
        PinnedDirectoryCreation.PinnedDirectoryAnchor stagingAnchor)
    {
        var anchors = new Dictionary<string, PinnedDirectoryCreation.PinnedDirectoryAnchor>(
            DirectoryCopySnapshotPathComparer)
        {
            [string.Empty] = stagingAnchor
        };
        var ownedAnchors = new List<PinnedDirectoryCreation.PinnedDirectoryAnchor>();
        try
        {
            foreach (var relativeDirectory in snapshot.RelativeDirectories)
            {
                var sourceDirectory = ResolveSnapshotPath(
                    snapshot.SourceRoot,
                    relativeDirectory,
                    "source directory");
                if (!Directory.Exists(sourceDirectory)
                    || IsLinkedOrUnverifiableEntry(sourceDirectory))
                {
                    throw new IOException(
                        $"Directory copy source changed after verification: {relativeDirectory}");
                }

                var parentKey = NormalizeRelativeDirectoryKey(
                    Path.GetDirectoryName(relativeDirectory));
                if (!anchors.TryGetValue(parentKey, out var parentAnchor))
                {
                    throw new IOException(
                        $"Directory copy staging parent was not pinned: {relativeDirectory}");
                }

                using var creation = parentAnchor.TryCreateChild(
                    Path.GetFileName(relativeDirectory));
                if (!creation.Created || !creation.VisiblePathMatches())
                {
                    throw new IOException(
                        $"Directory copy staging was unexpectedly occupied: {relativeDirectory}");
                }

                var childAnchor = creation.OpenCreatedDirectoryAnchor();
                if (!childAnchor.VisiblePathMatches())
                {
                    childAnchor.Dispose();
                    throw new IOException(
                        $"Directory copy staging identity changed: {relativeDirectory}");
                }

                anchors.Add(relativeDirectory, childAnchor);
                ownedAnchors.Add(childAnchor);
            }

            if (AfterDirectoryCopyStagingDirectoriesCreatedForTestAsync != null)
            {
                await AfterDirectoryCopyStagingDirectoriesCreatedForTestAsync(stagingAnchor.FullPath);
            }

            foreach (var fileSnapshot in snapshot.Files)
            {
                var relativeFile = fileSnapshot.RelativePath;
                var sourceFile = ResolveSnapshotPath(
                    snapshot.SourceRoot,
                    relativeFile,
                    "source file");
                if (!File.Exists(sourceFile)
                    || Directory.Exists(sourceFile)
                    || IsLinkedOrUnverifiableEntry(sourceFile))
                {
                    throw new IOException(
                        $"Directory copy source changed after verification: {relativeFile}");
                }

                var parentKey = NormalizeRelativeDirectoryKey(
                    Path.GetDirectoryName(relativeFile));
                if (!anchors.TryGetValue(parentKey, out var parentAnchor))
                {
                    throw new IOException(
                        $"Directory copy staging file parent was not pinned: {relativeFile}");
                }

                var childName = Path.GetFileName(relativeFile);
                await parentAnchor.CopyNewFileFromAsync(
                    sourceFile,
                    childName,
                    CancellationToken.None);
                var stagingFile = ResolveSnapshotPath(
                    stagingAnchor.FullPath,
                    relativeFile,
                    "staging file");
                if (IsLinkedOrUnverifiableEntry(stagingFile)
                    || !await FileSystemSafety.FilesHaveSameContentAsync(sourceFile, stagingFile))
                {
                    throw new IOException(
                        $"Directory copy staging content could not be verified: {relativeFile}");
                }
                LogMutation(
                    FileMutationOutcome.Success,
                    FileAction.Copy,
                    sourceFile,
                    stagingFile,
                    "Copied into an isolated pinned staging snapshot");
            }
        }
        finally
        {
            for (var index = ownedAnchors.Count - 1; index >= 0; index--)
            {
                ownedAnchors[index].Dispose();
            }
        }
    }

    private static string NormalizeRelativeDirectoryKey(string? path) =>
        string.IsNullOrEmpty(path) || path == "."
            ? string.Empty
            : path;

}
