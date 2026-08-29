/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 */
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private enum PhysicalPathEntryKind
    {
        Missing,
        File,
        Directory
    }

    private readonly record struct PhysicalPathResolution(
        string ResolvedPath,
        bool EncounteredLink,
        PhysicalPathEntryKind EntryKind);

    private async Task<bool> IsSameFilesystemPathAsync(string source, string destination) =>
        await TryDetermineFilesystemPathEquivalenceAsync(source, destination) == true;

    private async Task<bool?> TryDetermineFilesystemPathEquivalenceAsync(
        string source,
        string destination)
    {
        var sourcePath = Path.GetFullPath(
            FileSystemPathIdentity.ResolveNativeAbsolutePath(source));
        var destinationPath = Path.GetFullPath(
            FileSystemPathIdentity.ResolveNativeAbsolutePath(destination));
        if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
        {
            return true;
        }

        if (!TryResolvePhysicalPath(sourcePath, out var sourceResolution)
            || !TryResolvePhysicalPath(destinationPath, out var destinationResolution))
        {
            return null;
        }

        var pathsAreEquivalent = string.Equals(
            sourceResolution.ResolvedPath,
            destinationResolution.ResolvedPath,
            StringComparison.Ordinal);
        if (!pathsAreEquivalent)
        {
            if (_semanticsResolver == null)
            {
                return null;
            }

            try
            {
                var resolution = await _semanticsResolver.ResolveAsync(
                    sourcePath,
                    FileSystemCaseSensitivityMode.Auto);
                if (resolution.State != PathIdentityState.Valid)
                {
                    return null;
                }

                pathsAreEquivalent = FileSystemPathIdentity.AreEquivalent(
                    sourceResolution.ResolvedPath,
                    destinationResolution.ResolvedPath,
                    resolution.Semantics);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or ArgumentException or
                InvalidOperationException or NotSupportedException or PathTooLongException)
            {
                _logger.LogDebug(
                    exception,
                    "Filesystem identity resolution failed while comparing {Source} and {Destination}; destructive idempotent cleanup will be disabled",
                    LogRedaction.SanitizeFilePath(sourcePath),
                    LogRedaction.SanitizeFilePath(destinationPath));
                return null;
            }
        }

        if (!pathsAreEquivalent)
        {
            return false;
        }

        if (!sourceResolution.EncounteredLink
            && !destinationResolution.EncounteredLink)
        {
            return true;
        }

        // Lexically distinct aliases reached through a symbolic link, junction, or
        // linked ancestor are never an idempotent no-op. Callers must block or perform
        // a real publication rather than reporting success while a source pathname remains.
        return null;
    }

    private static bool TryResolvePhysicalPath(
        string path,
        out PhysicalPathResolution resolution)
    {
        resolution = default;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return false;
            }

            var currentLexicalPath = root;
            var currentResolvedPath = Path.GetFullPath(root);
            var encounteredLink = false;
            var entryKind = PhysicalPathEntryKind.Directory;
            var segments = Path.GetRelativePath(root, fullPath).Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                var candidatePath = Path.Join(currentLexicalPath, segment);
                if (!TryGetPathAttributes(
                        candidatePath,
                        out var exists,
                        out var attributes))
                {
                    return false;
                }

                if (!exists)
                {
                    for (var remainingIndex = index; remainingIndex < segments.Length; remainingIndex++)
                    {
                        currentResolvedPath = Path.Join(
                            currentResolvedPath,
                            segments[remainingIndex]);
                    }

                    resolution = new PhysicalPathResolution(
                        Path.GetFullPath(currentResolvedPath),
                        encounteredLink,
                        PhysicalPathEntryKind.Missing);
                    return true;
                }

                entryKind = (attributes & FileAttributes.Directory) != 0
                    ? PhysicalPathEntryKind.Directory
                    : PhysicalPathEntryKind.File;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var info = entryKind == PhysicalPathEntryKind.Directory
                        ? (FileSystemInfo)new DirectoryInfo(candidatePath)
                        : new FileInfo(candidatePath);
                    var target = info.ResolveLinkTarget(returnFinalTarget: true);
                    if (target == null)
                    {
                        return false;
                    }

                    encounteredLink = true;
                    currentResolvedPath = Path.GetFullPath(target.FullName);
                }
                else
                {
                    currentResolvedPath = Path.GetFullPath(
                        Path.Join(currentResolvedPath, segment));
                }

                currentLexicalPath = candidatePath;
            }

            resolution = new PhysicalPathResolution(
                Path.GetFullPath(currentResolvedPath),
                encounteredLink,
                entryKind);
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryGetPathAttributes(
        string path,
        out bool exists,
        out FileAttributes attributes)
    {
        exists = false;
        attributes = default;
        try
        {
            attributes = File.GetAttributes(path);
            exists = true;
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryValidateUnlinkedFileOperationEndpoints(
        string sourcePath,
        string destinationPath,
        out string reason)
    {
        if (!TryResolvePhysicalPath(sourcePath, out var sourceResolution)
            || !TryResolvePhysicalPath(destinationPath, out var destinationResolution))
        {
            reason = "A file endpoint could not be resolved to a stable physical path";
            return false;
        }

        if (sourceResolution.EncounteredLink
            || destinationResolution.EncounteredLink)
        {
            reason = "A file endpoint contains a symbolic link, junction, or linked ancestor";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsLinkedOrUnverifiableEntry(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }
}
