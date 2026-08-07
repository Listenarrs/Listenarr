/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private async Task<bool> SourceSnapshotStillMatchesAsync(
        DirectoryCopySnapshot snapshot)
    {
        if (!TryCaptureDirectoryCopySnapshot(
                snapshot.SourceRoot,
                out var currentSnapshot,
                out _)
            || currentSnapshot == null)
        {
            return false;
        }

        var comparer = DirectoryCopySnapshotPathComparer;
        if (!snapshot.RelativeDirectories.SequenceEqual(
                currentSnapshot.RelativeDirectories,
                comparer)
            || snapshot.SourceRootIdentity != currentSnapshot.SourceRootIdentity
            || snapshot.Files.Count != currentSnapshot.Files.Count)
        {
            return false;
        }

        foreach (var relativeDirectory in snapshot.RelativeDirectories)
        {
            if (!snapshot.DirectoryIdentities.TryGetValue(
                    relativeDirectory,
                    out var expectedIdentity)
                || !currentSnapshot.DirectoryIdentities.TryGetValue(
                    relativeDirectory,
                    out var currentIdentity)
                || expectedIdentity != currentIdentity)
            {
                return false;
            }
        }

        for (var index = 0; index < snapshot.Files.Count; index++)
        {
            var fileSnapshot = snapshot.Files[index];
            var currentFileSnapshot = currentSnapshot.Files[index];
            if (!comparer.Equals(
                    fileSnapshot.RelativePath,
                    currentFileSnapshot.RelativePath)
                || fileSnapshot.Identity != currentFileSnapshot.Identity)
            {
                return false;
            }

            var sourceFile = ResolveSnapshotPath(
                snapshot.SourceRoot,
                fileSnapshot.RelativePath,
                "source file");
            if (!File.Exists(sourceFile)
                || IsLinkedOrUnverifiableEntry(sourceFile)
                || !TryGetRegularFileIdentity(sourceFile, out var currentIdentity)
                || currentIdentity != fileSnapshot.Identity)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> DirectoryCopySnapshotExactlyMatchesAsync(
        DirectoryCopySnapshot snapshot,
        string candidateRoot)
    {
        if (!Directory.Exists(candidateRoot)
            || IsLinkedOrUnverifiableEntry(candidateRoot)
            || !FileSystemSafety.TryEnumerateTreeWithoutLinks(
                candidateRoot,
                out var candidateFiles,
                out var candidateDirectories,
                out _))
        {
            return false;
        }

        var comparer = DirectoryCopySnapshotPathComparer;
        var relativeDirectories = candidateDirectories
            .Select(path => GetVerifiedRelativePath(candidateRoot, path))
            .OrderBy(PathDepth)
            .ThenBy(path => path, comparer)
            .ToArray();
        var relativeFiles = candidateFiles
            .Select(path => GetVerifiedRelativePath(candidateRoot, path))
            .OrderBy(path => path, comparer)
            .ToArray();
        if (!snapshot.RelativeDirectories.SequenceEqual(relativeDirectories, comparer)
            || !snapshot.Files
                .Select(file => file.RelativePath)
                .SequenceEqual(relativeFiles, comparer))
        {
            return false;
        }

        foreach (var fileSnapshot in snapshot.Files)
        {
            var sourceFile = ResolveSnapshotPath(
                snapshot.SourceRoot,
                fileSnapshot.RelativePath,
                "source file");
            var candidateFile = ResolveSnapshotPath(
                candidateRoot,
                fileSnapshot.RelativePath,
                "candidate file");
            if (!File.Exists(sourceFile)
                || !File.Exists(candidateFile)
                || IsLinkedOrUnverifiableEntry(sourceFile)
                || IsLinkedOrUnverifiableEntry(candidateFile)
                || !TryGetRegularFileIdentity(sourceFile, out var currentIdentity)
                || currentIdentity != fileSnapshot.Identity
                || !await FileSystemSafety.FilesHaveSameContentAsync(
                    sourceFile,
                    candidateFile))
            {
                return false;
            }
        }

        return true;
    }

    private async Task TryCleanupDirectoryCopyStagingAsync(
        DirectoryCopySnapshot snapshot,
        PinnedDirectoryCreation stagingPublication,
        PinnedDirectoryCreation.PinnedDirectoryAnchor stagingAnchor,
        string stagingRoot)
    {
        try
        {
            if (!Directory.Exists(stagingRoot)
                || IsLinkedOrUnverifiableEntry(stagingRoot)
                || !FileSystemSafety.TryEnumerateTreeWithoutLinks(
                    stagingRoot,
                    out var files,
                    out var directories,
                    out _)
                || !TryGetDirectoryIdentity(stagingRoot, out var stagingRootIdentity))
            {
                return;
            }

            var comparer = DirectoryCopySnapshotPathComparer;
            var expectedFiles = snapshot.Files
                .Select(file => file.RelativePath)
                .ToHashSet(comparer);
            var expectedDirectories = snapshot.RelativeDirectories.ToHashSet(comparer);
            var relativeFiles = files
                .Select(path => GetVerifiedRelativePath(stagingRoot, path))
                .ToArray();
            var relativeDirectories = directories
                .Select(path => GetVerifiedRelativePath(stagingRoot, path))
                .ToArray();
            if (relativeFiles.Any(path => !expectedFiles.Contains(path))
                || relativeDirectories.Any(path => !expectedDirectories.Contains(path)))
            {
                return;
            }

            var stagingFileIdentities = new Dictionary<string, RegularFileIdentity>(comparer);
            foreach (var relativeFile in relativeFiles)
            {
                var stagingFile = ResolveSnapshotPath(
                    stagingRoot,
                    relativeFile,
                    "staging cleanup file");
                var sourceFile = ResolveSnapshotPath(
                    snapshot.SourceRoot,
                    relativeFile,
                    "source cleanup file");
                if (IsLinkedOrUnverifiableEntry(stagingFile)
                    || !File.Exists(sourceFile)
                    || !TryGetRegularFileIdentity(stagingFile, out var stagingIdentity)
                    || !await FileSystemSafety.FilesHaveSameContentAsync(
                        sourceFile,
                        stagingFile))
                {
                    return;
                }

                stagingFileIdentities.Add(relativeFile, stagingIdentity);
            }

            var stagingDirectoryIdentities =
                new Dictionary<string, RegularFileIdentity>(comparer);
            foreach (var relativeDirectory in relativeDirectories)
            {
                var directoryPath = ResolveSnapshotPath(
                    stagingRoot,
                    relativeDirectory,
                    "staging cleanup directory");
                if (!TryGetDirectoryIdentity(directoryPath, out var directoryIdentity))
                {
                    return;
                }

                stagingDirectoryIdentities.Add(relativeDirectory, directoryIdentity);
            }

            var stagingName = Path.GetFileName(stagingRoot);
            using (var rootHandle = stagingAnchor.DuplicateHandleForOperation())
            {
                if (!TryGetRegularFileIdentity(rootHandle, out var pinnedRootIdentity)
                    || pinnedRootIdentity != stagingRootIdentity)
                {
                    return;
                }
            }

            if (BeforeDirectoryCopyStagingCleanupForTestAsync != null)
            {
                await BeforeDirectoryCopyStagingCleanupForTestAsync(stagingRoot);
            }
            if (!stagingPublication.VisiblePathMatches()
                || !stagingAnchor.VisiblePathMatches())
            {
                return;
            }

            foreach (var relativeFile in relativeFiles)
            {
                using var parent = OpenPinnedRelativeDirectory(
                    stagingAnchor,
                    Path.GetDirectoryName(relativeFile));
                using var entry = parent.OpenExistingFile(
                    Path.GetFileName(relativeFile),
                    requireDeleteAccess: true);
                using var handle = entry.DuplicateHandleForOperation();
                var sourceFile = ResolveSnapshotPath(
                    snapshot.SourceRoot,
                    relativeFile,
                    "source cleanup file");
                var sourceSnapshot = snapshot.Files.Single(file =>
                    comparer.Equals(file.RelativePath, relativeFile));
                var identityMatches = TryGetRegularFileIdentity(
                    handle,
                    out var currentIdentity)
                    && currentIdentity == stagingFileIdentities[relativeFile];
                var entryVisible = entry.VisiblePathMatches();
                var parentVisible = parent.VisiblePathMatches();
                var sourceSafe = !IsLinkedOrUnverifiableEntry(sourceFile);
                var sourceIdentityMatches = TryGetRegularFileIdentity(
                    sourceFile,
                    out var sourceIdentity)
                    && sourceIdentity == sourceSnapshot.Identity;
                var contentMatches = await PinnedFileMatchesPathAsync(
                    entry,
                    sourceFile);
                if (!identityMatches
                    || !entryVisible
                    || !parentVisible
                    || !sourceSafe
                    || !sourceIdentityMatches
                    || !contentMatches)
                {
                    return;
                }

                entry.Delete(immediateWindows: true);
            }

            foreach (var relativeDirectory in relativeDirectories
                         .OrderByDescending(PathDepth))
            {
                var parentRelative = Path.GetDirectoryName(relativeDirectory);
                var childName = Path.GetFileName(relativeDirectory);
                using var parent = OpenPinnedRelativeDirectory(
                    stagingAnchor,
                    parentRelative);
                using var directory = parent.TryOpenExistingChildForPublication(childName);
                if (directory == null)
                {
                    return;
                }
                using var child = directory.OpenCreatedDirectoryAnchor();
                using var handle = child.DuplicateHandleForOperation();
                if (!TryGetRegularFileIdentity(handle, out var currentIdentity)
                    || currentIdentity != stagingDirectoryIdentities[relativeDirectory]
                    || !directory.VisiblePathMatches()
                    || !child.VisiblePathMatches()
                    || Directory.EnumerateFileSystemEntries(child.FullPath).Any())
                {
                    return;
                }

                child.Dispose();
                directory.RetirePinnedEmptyDirectoryFromNamespace(
                    childName);
            }

            if (Directory.EnumerateFileSystemEntries(stagingAnchor.FullPath).Any()
                || !stagingPublication.VisiblePathMatches()
                || !stagingAnchor.VisiblePathMatches())
            {
                return;
            }

            stagingAnchor.Dispose();
            stagingPublication.RetirePinnedEmptyDirectoryFromNamespace(
                stagingName);
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            // Uncertain or externally changed staging is preserved for diagnosis.
        }
    }

    private static PinnedDirectoryCreation.PinnedDirectoryAnchor OpenPinnedRelativeDirectory(
        PinnedDirectoryCreation.PinnedDirectoryAnchor root,
        string? relativePath)
    {
        var current = root.Duplicate();
        try
        {
            if (string.IsNullOrWhiteSpace(relativePath)
                || relativePath == ".")
            {
                return current;
            }

            foreach (var segment in relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                var next = current.OpenExistingChild(segment);
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static async Task<bool> PinnedFileMatchesPathAsync(
        PinnedDirectoryCreation.PinnedFileEntry candidate,
        string sourcePath)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var staged = candidate.OpenReadStream(
            bufferSize: 64 * 1024,
            asynchronous: false);
        if (source.Length != staged.Length)
        {
            return false;
        }

        var sourceBuffer = new byte[64 * 1024];
        var stagedBuffer = new byte[64 * 1024];
        while (true)
        {
            var sourceRead = await source.ReadAsync(sourceBuffer);
            var stagedRead = await staged.ReadAsync(stagedBuffer);
            if (sourceRead != stagedRead)
            {
                return false;
            }
            if (sourceRead == 0)
            {
                return true;
            }
            if (!sourceBuffer.AsSpan(0, sourceRead)
                .SequenceEqual(stagedBuffer.AsSpan(0, stagedRead)))
            {
                return false;
            }
        }
    }

    private async Task EnsureDirectoryCopyTargetSafeAsync(
        string sourceRoot,
        string destinationRoot,
        string targetPath)
    {
        if (IsLinkedOrUnverifiableEntry(destinationRoot))
        {
            throw new IOException("Directory copy destination root is a symbolic link or reparse point.");
        }

        var equivalent = await TryDetermineFilesystemPathEquivalenceAsync(
            sourceRoot,
            destinationRoot);
        var overlap = await TryDetermineDirectoryOverlapAsync(
            sourceRoot,
            destinationRoot);
        if (equivalent != false || overlap != false)
        {
            throw new IOException(
                "Directory copy destination could not be proven distinct and non-overlapping.");
        }

        if (!FileSystemSafety.TryValidateMutationTarget(
                targetPath,
                [destinationRoot],
                out var normalizedTarget,
                out var validationReason)
            || !DurablePathEvidenceEquals(normalizedTarget, targetPath))
        {
            throw new IOException(
                $"Directory copy destination failed mutation-boundary validation: {validationReason}");
        }
    }

    private async Task<bool> DirectoryCopySnapshotStillMatchesAsync(
        DirectoryCopySnapshot snapshot,
        string destinationRoot) =>
        await SourceSnapshotStillMatchesAsync(snapshot)
        && await DirectoryCopySnapshotExactlyMatchesAsync(snapshot, destinationRoot);

    private static string GetVerifiedRelativePath(string root, string path)
    {
        var relativePath = Path.GetRelativePath(root, path);
        var resolvedPath = ResolveSnapshotPath(root, relativePath, "snapshot entry");
        if (!DurablePathEvidenceEquals(resolvedPath, path))
        {
            throw new IOException("Directory copy snapshot entry escaped its source root.");
        }

        return relativePath;
    }

    private static string ResolveSnapshotPath(
        string root,
        string relativePath,
        string description)
    {
        if (!FileUtils.TryResolveRelativePathWithinBase(
                root,
                relativePath,
                out var resolvedPath))
        {
            throw new IOException(
                $"Directory copy {description} escaped its root: {relativePath}");
        }

        return Path.GetFullPath(resolvedPath);
    }

    private static int PathDepth(string path) =>
        path.Count(character =>
            character == Path.DirectorySeparatorChar
            || character == Path.AltDirectorySeparatorChar);

}
