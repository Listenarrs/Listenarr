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
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */

using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving
{
    public sealed partial class AudiobookFilesystemDeleteService
    {
        private static bool TryValidatePinnedDirectoryTree(
            PinnedDirectoryCreation.PinnedDirectoryAnchor rootAuthorization,
            PinnedDirectoryCreation.PinnedDirectoryAnchor currentDirectory,
            IReadOnlyDictionary<string, string> trackedPhysicalObjectIdentities,
            IDictionary<string, string> preflightIdentities,
            out string reason)
        {
            reason = string.Empty;
            try
            {
                if (!rootAuthorization.VisiblePathMatches()
                    || !currentDirectory.VisiblePathMatches())
                {
                    reason =
                        "The authorized directory generation changed before recursive-delete preflight.";
                    return false;
                }

                var entryNames = Directory
                    .EnumerateFileSystemEntries(currentDirectory.FullPath)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Cast<string>()
                    .ToArray();
                if (!rootAuthorization.VisiblePathMatches()
                    || !currentDirectory.VisiblePathMatches())
                {
                    reason =
                        "The authorized directory generation changed during recursive-delete preflight.";
                    return false;
                }

                foreach (var entryName in entryNames)
                {
                    var entryPath = Path.Join(
                        currentDirectory.FullPath,
                        entryName);
                    var attributes = File.GetAttributes(entryPath);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        reason =
                            "A linked or reparse-point entry exists in the authorized directory.";
                        return false;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        using var childPublication =
                            currentDirectory.OpenExistingChildForPublication(
                                entryName);
                        using var child =
                            childPublication.OpenCreatedDirectoryAnchor();
                        preflightIdentities[Path.GetRelativePath(
                            rootAuthorization.FullPath,
                            entryPath)] = child.GetDirectoryObjectIdentity();
                        if (!TryValidatePinnedDirectoryTree(
                                rootAuthorization,
                                child,
                                trackedPhysicalObjectIdentities,
                                preflightIdentities,
                                out reason))
                        {
                            return false;
                        }

                        continue;
                    }

                    using var file = currentDirectory.OpenExistingFile(
                        entryName,
                        requireDeleteAccess: false);
                    var physicalObjectIdentity = file.GetObjectIdentity();
                    if (trackedPhysicalObjectIdentities.TryGetValue(
                            entryPath,
                            out var expectedTrackedPhysicalObjectIdentity)
                        && !string.Equals(
                            physicalObjectIdentity,
                            expectedTrackedPhysicalObjectIdentity,
                            StringComparison.Ordinal))
                    {
                        reason =
                            "A tracked audiobook file physical generation changed before recursive-delete preflight.";
                        return false;
                    }

                    preflightIdentities[Path.GetRelativePath(
                        rootAuthorization.FullPath,
                        entryPath)] = physicalObjectIdentity;
                    if (!rootAuthorization.VisiblePathMatches()
                        || !currentDirectory.VisiblePathMatches()
                        || !file.VisiblePathMatches())
                    {
                        reason =
                            "A file generation changed during recursive-delete preflight.";
                        return false;
                    }
                }

                return rootAuthorization.VisiblePathMatches()
                    && currentDirectory.VisiblePathMatches();
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                reason = exception.Message;
                return false;
            }
        }

        private bool TryDeletePinnedDirectoryContents(
            PinnedDirectoryCreation.PinnedDirectoryAnchor rootAuthorization,
            PinnedDirectoryCreation.PinnedDirectoryAnchor currentDirectory,
            DeleteFolderTarget deleteTarget,
            IReadOnlySet<string> ownershipMarkerPaths,
            IReadOnlyDictionary<string, string> preflightIdentities,
            AudiobookFilesystemDeleteResult result,
            out string reason)
        {
            reason = string.Empty;
            try
            {
                if (!rootAuthorization.VisiblePathMatches()
                    || !currentDirectory.VisiblePathMatches())
                {
                    reason =
                        "The authorized directory generation changed before recursive deletion.";
                    return false;
                }

                var entryNames = Directory
                    .EnumerateFileSystemEntries(currentDirectory.FullPath)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Cast<string>()
                    .ToArray();
                if (!rootAuthorization.VisiblePathMatches()
                    || !currentDirectory.VisiblePathMatches())
                {
                    reason =
                        "The authorized directory generation changed during enumeration.";
                    return false;
                }

                foreach (var entryName in entryNames)
                {
                    var entryPath = Path.Join(
                        currentDirectory.FullPath,
                        entryName);
                    var attributes = File.GetAttributes(entryPath);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        reason =
                            "A linked or reparse-point entry appeared in the authorized directory.";
                        return false;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        using var childPublication =
                            currentDirectory.OpenExistingChildForPublication(
                                entryName);
                        using var child =
                            childPublication.OpenCreatedDirectoryAnchor();
                        var relativeEntry = Path.GetRelativePath(
                            rootAuthorization.FullPath,
                            entryPath);
                        if (!preflightIdentities.TryGetValue(
                                relativeEntry,
                                out var expectedChildIdentity)
                            || !string.Equals(
                                child.GetDirectoryObjectIdentity(),
                                expectedChildIdentity,
                                StringComparison.Ordinal))
                        {
                            reason =
                                "A directory generation changed after recursive-delete preflight.";
                            return false;
                        }
                        if (!TryDeletePinnedDirectoryContents(
                                rootAuthorization,
                                child,
                                deleteTarget,
                                ownershipMarkerPaths,
                                preflightIdentities,
                                result,
                                out reason))
                        {
                            return false;
                        }

                        var isOwnedDirectory =
                            deleteTarget.OwnedDirectories.Any(ownership =>
                                FileSystemPathIdentity.AreEquivalent(
                                    ownership.CanonicalPath,
                                    entryPath,
                                    deleteTarget.Semantics));
                        if (!isOwnedDirectory)
                        {
                            if (!rootAuthorization.VisiblePathMatches()
                                || !currentDirectory.VisiblePathMatches()
                                || !child.VisiblePathMatches()
                                || Directory
                                    .EnumerateFileSystemEntries(entryPath)
                                    .Any())
                            {
                                reason =
                                    "A nested directory changed before captured-generation deletion.";
                                return false;
                            }

                            childPublication.DeletePinnedEmptyDirectoryImmediately(
                                entryName);
                        }

                        continue;
                    }

                    using var file = currentDirectory.OpenExistingFile(
                        entryName,
                        requireDeleteAccess: true);
                    var relativeFile = Path.GetRelativePath(
                        rootAuthorization.FullPath,
                        entryPath);
                    if (!preflightIdentities.TryGetValue(
                            relativeFile,
                            out var expectedFileIdentity)
                        || !string.Equals(
                            file.GetObjectIdentity(),
                            expectedFileIdentity,
                            StringComparison.Ordinal))
                    {
                        reason =
                            "A file generation changed after recursive-delete preflight.";
                        return false;
                    }
                    if (ownershipMarkerPaths.Contains(entryPath))
                    {
                        continue;
                    }

                    if (!rootAuthorization.VisiblePathMatches()
                        || !currentDirectory.VisiblePathMatches()
                        || !file.VisiblePathMatches())
                    {
                        reason =
                            "A file generation changed before handle-relative deletion.";
                        return false;
                    }

                    file.Delete(immediateWindows: true);
                    result.DeletedFiles++;
                    _logger.LogInformation(
                        "Deleted audiobook file {Path}",
                        LogRedaction.SanitizeFilePath(entryPath));
                }

                return true;
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException
                    or InvalidOperationException
                    or System.ComponentModel.Win32Exception)
            {
                reason =
                    $"Captured-generation recursive deletion failed safely: {exception.GetType().Name}.";
                return false;
            }
        }

        private bool TryDeleteFolderContents(
            DeleteFolderTarget deleteTarget,
            PinnedDirectoryCreation.PinnedDirectoryAnchor targetAuthorization,
            IReadOnlyDictionary<string, string> trackedPhysicalObjectIdentities,
            AudiobookFilesystemDeleteResult result)
        {
            var folderPath = deleteTarget.FolderPath;
            if (!Directory.Exists(folderPath))
            {
                return true;
            }

            IReadOnlySet<string> ownershipMarkerPaths = new HashSet<string>(
                deleteTarget.Semantics.Comparer);
            var preflightIdentities = new Dictionary<string, string>(
                deleteTarget.Semantics.Comparer);
            if (!TryValidatePinnedDirectoryTree(
                    targetAuthorization,
                    targetAuthorization,
                    trackedPhysicalObjectIdentities,
                    preflightIdentities,
                    out var reason))
            {
                result.Warnings.Add(
                    "Refused to recursively delete the audiobook folder because it contains a symbolic link or its captured filesystem generation changed.");
                _logger.LogWarning(
                    "Blocked recursive audiobook delete preflight for {FolderPath}: {Reason}",
                    LogRedaction.SanitizeFilePath(folderPath),
                    LogRedaction.SanitizeText(reason));
                return false;
            }

            var hasExactDirectoryOwnership = deleteTarget.OwnedDirectories.Any(ownership =>
                FileSystemPathIdentity.AreEquivalent(
                    ownership.CanonicalPath,
                    folderPath,
                    deleteTarget.Semantics));
            if (!hasExactDirectoryOwnership)
            {
                foreach (var expected in trackedPhysicalObjectIdentities)
                {
                    string relativePath;
                    try
                    {
                        if (!FileSystemPathIdentity.IsSameOrInside(
                                expected.Key,
                                folderPath,
                                deleteTarget.Semantics))
                        {
                            reason =
                                "A tracked audiobook file is outside the unowned audiobook folder.";
                            result.Warnings.Add(
                                "Refused to recursively delete the audiobook folder because its tracked file generations could not bind the current folder.");
                            _logger.LogWarning(
                                "Blocked recursive audiobook delete preflight for {FolderPath}: {Reason}",
                                LogRedaction.SanitizeFilePath(folderPath),
                                LogRedaction.SanitizeText(reason));
                            return false;
                        }

                        relativePath = Path.GetRelativePath(folderPath, expected.Key);
                    }
                    catch (Exception exception) when (exception is
                        ArgumentException or InvalidOperationException
                            or NotSupportedException or PathTooLongException)
                    {
                        reason = exception.Message;
                        result.Warnings.Add(
                            "Refused to recursively delete the audiobook folder because its tracked file generations could not bind the current folder.");
                        _logger.LogWarning(
                            exception,
                            "Blocked recursive audiobook delete preflight because a tracked path could not be bound beneath {FolderPath}",
                            LogRedaction.SanitizeFilePath(folderPath));
                        return false;
                    }

                    if (!preflightIdentities.TryGetValue(relativePath, out var observedIdentity)
                        || !string.Equals(
                            observedIdentity,
                            expected.Value,
                            StringComparison.Ordinal))
                    {
                        reason =
                            "A tracked audiobook file generation is missing or changed in the unowned audiobook folder.";
                        result.Warnings.Add(
                            "Refused to recursively delete the audiobook folder because a tracked file generation is missing or changed.");
                        _logger.LogWarning(
                            "Blocked recursive audiobook delete preflight for {FolderPath}: {Reason}",
                            LogRedaction.SanitizeFilePath(folderPath),
                            LogRedaction.SanitizeText(reason));
                        return false;
                    }
                }
            }

            if (!TryDeletePinnedDirectoryContents(
                    targetAuthorization,
                    targetAuthorization,
                    deleteTarget,
                    ownershipMarkerPaths,
                    preflightIdentities,
                    result,
                    out reason))
            {
                result.Warnings.Add(
                    "Refused to continue recursively deleting the audiobook folder because its captured filesystem generation changed.");
                _logger.LogWarning(
                    "Blocked recursive audiobook delete for {FolderPath}: {Reason}",
                    LogRedaction.SanitizeFilePath(folderPath),
                    LogRedaction.SanitizeText(reason));
                return false;
            }

            return true;
        }
    }
}
