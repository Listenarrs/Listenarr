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

internal static class FileSystemSafety
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

            normalizedPath = Path.GetFullPath(targetPath);
            var normalizedTarget = normalizedPath;
            var normalizedRoots = allowedRoots
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(root => Path.GetFullPath(root!))
                .Distinct(PathComparer)
                .ToList();

            if (normalizedRoots.Count == 0)
            {
                reason = "No allowed mutation roots were provided.";
                return false;
            }

            if (!normalizedRoots.Any(root => FileUtils.IsPathSameOrInside(normalizedTarget, root)))
            {
                reason = "Target path is outside all allowed mutation roots.";
                return false;
            }

            return IsResolvedMutationTargetInsideRoots(normalizedTarget, normalizedRoots, out reason);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            normalizedPath = string.Empty;
            reason = "Target path could not be normalized.";
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

            foreach (var directory in Directory.GetDirectories(rootPath, "*", SearchOption.AllDirectories)
                .OrderByDescending(path => path.Length))
            {
                TryDeleteDirectoryIfEmpty(directory);
            }

            TryDeleteDirectoryIfEmpty(rootPath);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            System.Diagnostics.Debug.WriteLine(
                $"Suppressed empty-directory cleanup failure for '{rootPath}': {exception.Message}");
        }
    }

    private static bool IsResolvedMutationTargetInsideRoots(
        string normalizedTargetPath,
        IReadOnlyCollection<string> normalizedRoots,
        out string reason)
    {
        reason = string.Empty;
        var resolvedRoots = normalizedRoots
            .Select(root => TryResolveAllowedMutationRoot(root, out var resolvedRoot)
                ? resolvedRoot
                : string.Empty)
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(PathComparer)
            .ToList();

        if (resolvedRoots.Count == 0)
        {
            reason = "Allowed mutation roots could not be resolved safely.";
            return false;
        }

        if (!TryGetNearestExistingPath(normalizedTargetPath, out var existingTargetPath))
        {
            reason = "Target path has no existing parent under an allowed mutation root.";
            return false;
        }

        if (!TryResolveExistingFinalPath(existingTargetPath, out var resolvedExistingTargetPath))
        {
            reason = "Target path could not be resolved safely.";
            return false;
        }

        if (resolvedRoots.Any(root => FileUtils.IsPathSameOrInside(resolvedExistingTargetPath, root)))
        {
            return true;
        }

        reason = "Target path resolves outside all allowed mutation roots.";
        return false;
    }

    private static bool TryResolveAllowedMutationRoot(string rootPath, out string resolvedPath)
    {
        if (TryResolveExistingFinalPath(rootPath, out resolvedPath))
        {
            return true;
        }

        return TryGetNearestExistingPath(rootPath, out var existingRootAncestor)
            && TryResolveExistingFinalPath(existingRootAncestor, out resolvedPath);
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

    private static void TryDeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            System.Diagnostics.Debug.WriteLine(
                $"Suppressed empty-directory delete failure for '{path}': {exception.Message}");
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
