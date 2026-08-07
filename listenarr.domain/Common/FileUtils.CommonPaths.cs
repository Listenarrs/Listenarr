/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Domain.Common;

public static partial class FileUtils
{
    public static string? GetCommonDirectory(IEnumerable<string> paths)
    {
        try
        {
            var directories = paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path =>
                {
                    var fullPath = NormalizeStoredPath(path);
                    return Path.GetDirectoryName(fullPath) ?? fullPath;
                })
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (directories.Count == 0)
            {
                return null;
            }

            var commonPath = GetCommonPathForDirectories(directories);
            return string.IsNullOrWhiteSpace(commonPath) ? directories[0] : commonPath;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            return null;
        }
    }

    public static string? GetCommonPathForDirectories(IEnumerable<string> directories)
        => GetCommonPathForDirectories(
            directories,
            new FileSystemPathSemantics(
                OperatingSystem.IsWindows()
                    ? FileSystemPathSyntax.Windows
                    : FileSystemPathSyntax.Unix,
                FileSystemCaseSensitivity.Sensitive));

    public static string? GetCommonPathForDirectories(
        IEnumerable<string> directories,
        FileSystemPathSemantics semantics)
    {
        try
        {
            var normalizedDirectories = directories
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => FileSystemPathIdentity.Canonicalize(
                    path,
                    semantics.Syntax))
                .Distinct(semantics.Comparer)
                .ToList();

            if (normalizedDirectories.Count == 0)
            {
                return null;
            }

            if (normalizedDirectories.Count == 1)
            {
                return normalizedDirectories[0];
            }

            var commonPath = normalizedDirectories[0];
            foreach (var directory in normalizedDirectories.Skip(1))
            {
                commonPath = GetCommonPath(commonPath, directory, semantics);
                if (string.IsNullOrWhiteSpace(commonPath))
                {
                    break;
                }
            }

            return string.IsNullOrWhiteSpace(commonPath) ? null : commonPath;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            return null;
        }
    }

    private static string GetCommonPath(
        string firstPath,
        string secondPath,
        FileSystemPathSemantics semantics)
    {
        var first = DecomposePathForCommonPath(firstPath, semantics.Syntax);
        var second = DecomposePathForCommonPath(secondPath, semantics.Syntax);
        var comparison = semantics.CaseSensitivity == FileSystemCaseSensitivity.Sensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        if (!string.Equals(first.Root, second.Root, comparison))
        {
            return string.Empty;
        }

        var commonSegments = new List<string>();
        var segmentCount = Math.Min(first.Segments.Count, second.Segments.Count);
        for (var index = 0; index < segmentCount; index++)
        {
            if (!string.Equals(first.Segments[index], second.Segments[index], comparison))
            {
                break;
            }

            commonSegments.Add(first.Segments[index]);
        }

        return BuildPathFromRootAndSegments(
            first.Root,
            commonSegments,
            semantics.Syntax);
    }

    private static (string Root, IReadOnlyList<string> Segments) DecomposePathForCommonPath(
        string path,
        FileSystemPathSyntax syntax)
    {
        var normalizedPath = FileSystemPathIdentity.Canonicalize(path, syntax);
        if (syntax == FileSystemPathSyntax.Windows)
        {
            var rootLength = GetWindowsRootLength(normalizedPath);
            var root = rootLength > 0
                ? NormalizeWindowsRootForStorage(normalizedPath[..rootLength])
                : string.Empty;
            var remainingPath = rootLength > 0
                ? normalizedPath[rootLength..]
                : normalizedPath;
            return (
                root,
                remainingPath.Split(
                    ['\\', '/'],
                    StringSplitOptions.RemoveEmptyEntries));
        }

        var unixRoot = normalizedPath.StartsWith("/", StringComparison.Ordinal)
            ? "/"
            : string.Empty;
        var unixRemainingPath = unixRoot.Length > 0
            ? normalizedPath[unixRoot.Length..]
            : normalizedPath;
        return (
            unixRoot,
            unixRemainingPath.Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries));
    }

    private static string BuildPathFromRootAndSegments(
        string root,
        IReadOnlyList<string> segments,
        FileSystemPathSyntax syntax)
    {
        if (segments.Count == 0)
        {
            return root;
        }

        var separator = syntax == FileSystemPathSyntax.Windows ? '\\' : '/';
        if (string.IsNullOrEmpty(root))
        {
            return string.Join(separator, segments);
        }

        return root.TrimEnd('/', '\\')
            + separator
            + string.Join(separator, segments);
    }
}
