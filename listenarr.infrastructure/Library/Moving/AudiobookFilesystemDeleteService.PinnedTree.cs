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

using System.Security.Cryptography;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving
{
    public sealed partial class AudiobookFilesystemDeleteService
    {
        private static bool PinnedFileMatchesContentProof(
            PinnedDirectoryCreation.PinnedFileEntry file,
            DeleteFileContentProof proof)
        {
            if (!proof.Observation.Matches(file))
            {
                return false;
            }
            using var stream = file.OpenReadStream(
                bufferSize: 128 * 1024,
                asynchronous: false);
            if (stream.Length != proof.Length)
            {
                return false;
            }

            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return string.Equals(
                hash,
                proof.Sha256,
                StringComparison.OrdinalIgnoreCase)
                && file.VisiblePathMatches();
        }

        private static bool TryValidatePinnedDirectoryTree(
            PinnedDirectoryCreation.PinnedDirectoryAnchor rootAuthorization,
            PinnedDirectoryCreation.PinnedDirectoryAnchor currentDirectory,
            IReadOnlyDictionary<string, DeleteFileContentProof> trackedContentProofs,
            CapturedDeleteTreeProofs preflightIdentities,
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
                        preflightIdentities.Capture(Path.GetRelativePath(
                            rootAuthorization.FullPath,
                            entryPath), DeleteTreeEntryProof.CaptureDirectory(child));
                        if (!TryValidatePinnedDirectoryTree(
                                rootAuthorization,
                                child,
                                trackedContentProofs,
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
                    var hasTrackedContent = trackedContentProofs.TryGetValue(entryPath, out var expectedTrackedContent);
                    if (hasTrackedContent && !PinnedFileMatchesContentProof(
                            file,
                            expectedTrackedContent))
                    {
                        reason =
                            "A tracked audiobook file content proof changed before recursive-delete preflight.";
                        return false;
                    }

                    preflightIdentities.Capture(Path.GetRelativePath(
                        rootAuthorization.FullPath,
                        entryPath), DeleteTreeEntryProof.CaptureFile(file, hasTrackedContent ? expectedTrackedContent : null));
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
            IReadOnlyDictionary<string, DeleteTreeEntryProof> preflightIdentities,
            IReadOnlyDictionary<string, DeleteFileContentProof> trackedContentProofs,
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
                            || !expectedChildIdentity.Matches(child))
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
                                trackedContentProofs,
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
                            expectedChildIdentity.Dispose();
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

                    using var file = currentDirectory.OpenExistingFileForStableDelete(entryName);
                    var relativeFile = Path.GetRelativePath(
                        rootAuthorization.FullPath,
                        entryPath);
                    if (!preflightIdentities.TryGetValue(
                            relativeFile,
                            out var expectedFileIdentity)
                        || !expectedFileIdentity.Matches(file))
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
                    expectedFileIdentity.Dispose();
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
            IReadOnlyCollection<string> trackedFilePaths,
            IReadOnlyDictionary<string, DeleteFileContentProof> trackedContentProofs,
            CapturedDeleteTreeProofs preflightIdentities,
            AudiobookFilesystemDeleteResult result)
        {
            var folderPath = deleteTarget.FolderPath;
            if (!Directory.Exists(folderPath))
            {
                return true;
            }

            IReadOnlySet<string> ownershipMarkerPaths = new HashSet<string>(
                deleteTarget.Semantics.Comparer);
            if (!TryValidatePinnedDirectoryTree(
                    targetAuthorization,
                    targetAuthorization,
                    trackedContentProofs,
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
            foreach (var trackedFilePath in trackedFilePaths)
            {
                string relativePath;
                try
                {
                    if (!FileSystemPathIdentity.IsSameOrInside(
                            trackedFilePath,
                            folderPath,
                            deleteTarget.Semantics))
                    {
                        reason =
                            "A tracked audiobook file is outside the audiobook folder selected for deletion.";
                        result.Warnings.Add(
                            "Refused to recursively delete the audiobook folder because a tracked path escaped the selected folder.");
                        _logger.LogWarning(
                            "Blocked recursive audiobook delete preflight for {FolderPath}: {Reason}",
                            LogRedaction.SanitizeFilePath(folderPath),
                            LogRedaction.SanitizeText(reason));
                        return false;
                    }

                    relativePath = Path.GetRelativePath(
                        folderPath,
                        trackedFilePath);
                }
                catch (Exception exception) when (exception is
                    ArgumentException or InvalidOperationException
                        or NotSupportedException or PathTooLongException)
                {
                    reason = exception.Message;
                    result.Warnings.Add(
                        "Refused to recursively delete the audiobook folder because a tracked path could not be bound beneath it.");
                    _logger.LogWarning(
                        exception,
                        "Blocked recursive audiobook delete preflight because a tracked path could not be bound beneath {FolderPath}",
                        LogRedaction.SanitizeFilePath(folderPath));
                    return false;
                }

                var wasPresent = trackedContentProofs.ContainsKey(
                    trackedFilePath);
                var isPresent = preflightIdentities.ContainsKey(relativePath);
                if (wasPresent != isPresent)
                {
                    reason =
                        "A tracked audiobook path appeared or disappeared after live deletion proof was captured.";
                    result.Warnings.Add(
                        "Refused to recursively delete the audiobook folder because a tracked file changed during delete preflight.");
                    _logger.LogWarning(
                        "Blocked recursive audiobook delete preflight for {FolderPath}: {Reason}",
                        LogRedaction.SanitizeFilePath(folderPath),
                        LogRedaction.SanitizeText(reason));
                    return false;
                }
            }

            if (!hasExactDirectoryOwnership && trackedContentProofs.Count == 0)
            {
                reason =
                    "The unowned audiobook folder has no live tracked-file content proof.";
                result.Warnings.Add(
                    "Refused to recursively delete the unowned audiobook folder because no live tracked file could bind the current folder.");
                return false;
            }

            if (!TryDeletePinnedDirectoryContents(
                    targetAuthorization,
                    targetAuthorization,
                    deleteTarget,
                    ownershipMarkerPaths,
                    preflightIdentities,
                    trackedContentProofs,
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
