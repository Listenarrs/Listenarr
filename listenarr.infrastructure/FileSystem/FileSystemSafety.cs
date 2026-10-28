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

internal static partial class FileSystemSafety
{
    public static async Task<bool> FilesHaveSameContentAsync(
        string firstPath,
        string secondPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(firstPath) || !File.Exists(secondPath))
            {
                return false;
            }

            var firstInfo = new FileInfo(firstPath);
            var secondInfo = new FileInfo(secondPath);
            if (firstInfo.Length != secondInfo.Length)
            {
                return false;
            }

            await using var firstStream = File.OpenRead(firstPath);
            await using var secondStream = File.OpenRead(secondPath);
            var firstBuffer = new byte[81920];
            var secondBuffer = new byte[81920];
            while (true)
            {
                var firstRead = await firstStream.ReadAsync(firstBuffer, cancellationToken);
                var secondRead = await secondStream.ReadAsync(secondBuffer, cancellationToken);
                if (firstRead != secondRead)
                {
                    return false;
                }

                if (firstRead == 0)
                {
                    return true;
                }

                if (!firstBuffer.AsSpan(0, firstRead).SequenceEqual(secondBuffer.AsSpan(0, secondRead)))
                {
                    return false;
                }
            }
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }

    public static bool TryValidateMutationTarget(
        string targetPath,
        IEnumerable<string?> allowedRoots,
        out string normalizedPath,
        out string reason)
    {
        normalizedPath = string.Empty;
        reason = string.Empty;

        try
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                reason = "Target path is empty.";
                return false;
            }

            if (!FileSystemPathIdentity.TryCanonicalizeStoredAbsolutePathForHost(
                    targetPath,
                    out normalizedPath,
                    out var targetReason))
            {
                reason = targetReason;
                return false;
            }

            var normalizedTarget = normalizedPath;
            // Mutation authorization must not assume all Windows directories are
            // case-insensitive. Without pinned proof that two differently-cased
            // spellings identify the same boundary, fail closed on lexical aliases.
            var normalizedRoots = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in allowedRoots.Where(root => !string.IsNullOrWhiteSpace(root)))
            {
                if (FileSystemPathIdentity.TryCanonicalizeStoredAbsolutePathForHost(
                        root!,
                        out var normalizedRoot,
                        out _))
                {
                    normalizedRoots.Add(normalizedRoot);
                }
            }

            if (normalizedRoots.Count == 0)
            {
                reason = "No allowed mutation roots were provided.";
                return false;
            }

            var candidateRoots = normalizedRoots
                .Where(root => IsSameOrInsideMutationBoundary(normalizedTarget, root))
                .OrderByDescending(root => root.Length)
                .ToList();
            if (candidateRoots.Count == 0)
            {
                reason = "Target path is outside all allowed mutation roots.";
                return false;
            }

            foreach (var root in candidateRoots)
            {
                if (TryValidateResolvedComponents(normalizedTarget, root, out reason))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            normalizedPath = string.Empty;
            reason = "Target path could not be normalized.";
            return false;
        }
    }

    public static bool TryEnumerateTreeWithoutLinks(
        string rootPath,
        out IReadOnlyList<string> files,
        out IReadOnlyList<string> directories,
        out string reason)
    {
        var discoveredFiles = new List<string>();
        var discoveredDirectories = new List<string>();
        files = discoveredFiles;
        directories = discoveredDirectories;
        reason = string.Empty;

        try
        {
            var root = Path.GetFullPath(rootPath);
            if (!Directory.Exists(root))
            {
                reason = "The directory does not exist.";
                return false;
            }

            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                reason = "The directory is a symbolic link or reparse point.";
                return false;
            }

            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        reason = $"Linked filesystem entry blocked safe traversal: {Path.GetFileName(entry)}";
                        return false;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        discoveredDirectories.Add(entry);
                        pending.Push(entry);
                    }
                    else
                    {
                        discoveredFiles.Add(entry);
                    }
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            reason = $"Filesystem tree could not be enumerated safely: {exception.GetType().Name}.";
            return false;
        }
    }

    public static void DeleteEmptyDirectories(string rootPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                return;
            }

            DeleteEmptyDirectoriesPinned(rootPath);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            System.Diagnostics.Debug.WriteLine(
                $"Suppressed empty-directory cleanup failure for '{rootPath}': {exception.Message}");
        }
    }

    private static bool TryValidateResolvedComponents(
        string normalizedTargetPath,
        string normalizedRootPath,
        out string reason)
    {
        reason = string.Empty;
        if (!TryGetNearestExistingPath(normalizedRootPath, out var existingRootPath)
            || !TryResolveExistingFinalPath(existingRootPath, out var resolvedRootPath))
        {
            reason = "Allowed mutation root could not be resolved safely.";
            return false;
        }

        if (!TryGetNearestExistingPath(normalizedTargetPath, out var existingTargetPath))
        {
            reason = "Target path has no existing parent under an allowed mutation root.";
            return false;
        }

        if (!IsSameOrInsideMutationBoundary(existingTargetPath, existingRootPath))
        {
            if (IsSameOrInsideMutationBoundary(existingRootPath, existingTargetPath))
            {
                existingTargetPath = existingRootPath;
            }
            else
            {
                reason = "Target path could not be related to its allowed mutation root.";
                return false;
            }
        }

        var relativePath = Path.GetRelativePath(existingRootPath, existingTargetPath);
        if (relativePath == ".")
        {
            return true;
        }

        var currentLexicalPath = existingRootPath;
        var currentResolvedPath = resolvedRootPath;
        foreach (var segment in relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            currentLexicalPath = Path.Join(currentLexicalPath, segment);
            var attributes = File.GetAttributes(currentLexicalPath);
            var info = (attributes & FileAttributes.Directory) != 0
                ? (FileSystemInfo)new DirectoryInfo(currentLexicalPath)
                : new FileInfo(currentLexicalPath);
            var resolvedTarget = (attributes & FileAttributes.ReparsePoint) != 0
                ? info.ResolveLinkTarget(returnFinalTarget: true)
                : null;
            currentResolvedPath = Path.GetFullPath(
                resolvedTarget?.FullName ?? Path.Join(currentResolvedPath, segment));
            if (!IsSameOrInsideMutationBoundary(currentResolvedPath, resolvedRootPath))
            {
                reason = "Target path resolves outside an allowed mutation root through a linked path component.";
                return false;
            }
        }

        return true;
    }

    private static bool TryGetNearestExistingPath(string path, out string existingPath)
    {
        existingPath = string.Empty;
        try
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    existingPath = current;
                    return true;
                }

                var parent = Directory.GetParent(current);
                if (parent == null)
                {
                    return false;
                }

                current = parent.FullName;
            }

            return false;
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }

    private static bool TryResolveExistingFinalPath(string path, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        try
        {
            var fullPath = Path.GetFullPath(path);
            FileSystemInfo? info = Directory.Exists(fullPath)
                ? new DirectoryInfo(fullPath)
                : File.Exists(fullPath)
                    ? new FileInfo(fullPath)
                    : null;
            if (info == null)
            {
                return false;
            }

            var resolvedTarget = info.ResolveLinkTarget(returnFinalTarget: true);
            resolvedPath = Path.GetFullPath(resolvedTarget?.FullName ?? info.FullName);
            return true;
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }

    internal static bool IsSameOrInsideMutationBoundary(
        string candidatePath,
        string rootPath,
        FileSystemPathSyntax? syntax = null)
    {
        var effectiveSyntax = syntax
            ?? (OperatingSystem.IsWindows()
                ? FileSystemPathSyntax.Windows
                : FileSystemPathSyntax.Unix);
        var sensitiveSemantics = new FileSystemPathSemantics(
            effectiveSyntax,
            FileSystemCaseSensitivity.Sensitive);
        if (FileSystemPathIdentity.IsSameOrInside(
                candidatePath,
                rootPath,
                sensitiveSemantics))
        {
            return true;
        }

        // A Windows namespace can be case-insensitive or case-sensitive per
        // directory. Accept a differently-cased spelling only when the candidate
        // prefix that corresponds to the allowed root can be pinned and proven to
        // identify the same physical directory. This preserves normal Windows case
        // aliases without authorizing a case-distinct sibling on a sensitive parent.
        if (effectiveSyntax != FileSystemPathSyntax.Windows
            || !OperatingSystem.IsWindows())
        {
            return false;
        }

        var insensitiveSemantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);
        if (!FileSystemPathIdentity.IsSameOrInside(
                candidatePath,
                rootPath,
                insensitiveSemantics))
        {
            return false;
        }

        try
        {
            var canonicalRoot = FileSystemPathIdentity.Canonicalize(
                rootPath,
                FileSystemPathSyntax.Windows);
            var canonicalCandidate = FileSystemPathIdentity.Canonicalize(
                candidatePath,
                FileSystemPathSyntax.Windows);
            if (canonicalCandidate.Length < canonicalRoot.Length)
            {
                return false;
            }

            var candidateRootAlias = canonicalCandidate[..canonicalRoot.Length];
            using var expectedRoot = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                canonicalRoot,
                createMissing: false);
            using var candidateRoot = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                candidateRootAlias,
                createMissing: false);
            return string.Equals(
                expectedRoot.GetDirectoryObjectIdentity(),
                candidateRoot.GetDirectoryObjectIdentity(),
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }
}
