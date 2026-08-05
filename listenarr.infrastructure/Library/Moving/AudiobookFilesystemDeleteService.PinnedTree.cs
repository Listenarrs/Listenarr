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

                            childPublication.RetirePinnedEmptyDirectoryFromNamespace(
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
    }
}
