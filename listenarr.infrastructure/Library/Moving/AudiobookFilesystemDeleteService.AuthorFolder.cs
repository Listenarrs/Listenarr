/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving
{
    public sealed partial class AudiobookFilesystemDeleteService
    {
        private async Task TryDeleteEmptyAuthorFolderAsync(
            Audiobook audiobook,
            string deletedFolderPath,
            IReadOnlyCollection<string> protectedRoots,
            FileSystemPathSemantics semantics,
            AudiobookFilesystemDeleteResult result,
            CancellationToken cancellationToken)
        {
            var parentFolder = NormalizePath(Path.GetDirectoryName(deletedFolderPath));
            if (string.IsNullOrWhiteSpace(parentFolder)
                || IsFilesystemRoot(parentFolder, semantics)
                || protectedRoots.Any(root => PathsEqual(root, parentFolder, semantics))
                || !IsAuthorFolder(parentFolder, audiobook.Authors?.FirstOrDefault()))
            {
                return;
            }

            LibraryDirectoryOwnershipResolution parentOwnership;
            try
            {
                parentOwnership = await _directoryOwnershipStore.ResolveOwnedAsync(
                    parentFolder,
                    semantics,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
            {
                _logger.LogWarning(
                    exception,
                    "Unable to resolve durable ownership for author folder {FolderPath}",
                    LogRedaction.SanitizeFilePath(parentFolder));
                if (!Directory.Exists(parentFolder))
                {
                    throw;
                }

                result.Warnings.Add(
                    "The empty author folder was preserved because its durable ownership could not be resolved.");
                return;
            }

            if (parentOwnership.State != LibraryDirectoryOwnershipResolutionState.Owned
                || parentOwnership.Ownership == null)
            {
                if (!Directory.Exists(parentFolder)
                    && parentOwnership.State != LibraryDirectoryOwnershipResolutionState.Unowned)
                {
                    throw new InvalidOperationException(
                        parentOwnership.Reason
                            ?? "The missing author folder has conflicting or unavailable ownership state.");
                }

                return;
            }

            var ownedParent = parentOwnership.Ownership;
            try
            {
                ValidateOwnedDirectoryForDelete(ownedParent);
                if (Directory.Exists(parentFolder)
                    && Directory.EnumerateFileSystemEntries(parentFolder).Any())
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogWarning(
                    exception,
                    "Unable to validate owned author folder {FolderPath}",
                    LogRedaction.SanitizeFilePath(parentFolder));
                if (!Directory.Exists(parentFolder))
                {
                    throw;
                }

                result.Warnings.Add(
                    "The empty author folder was preserved because its ownership proof changed.");
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var allAudiobooks = await _audiobookRepository.GetAllAsync();
            foreach (var otherAudiobook in allAudiobooks.Where(candidate =>
                candidate.Id != audiobook.Id))
            {
                if (!string.IsNullOrWhiteSpace(otherAudiobook.BasePath))
                {
                    if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                            otherAudiobook.BasePath,
                            out var otherBasePath,
                            out _)
                        || IsSamePathOrWithin(otherBasePath, parentFolder, semantics)
                        || IsSamePathOrWithin(parentFolder, otherBasePath, semantics))
                    {
                        return;
                    }
                }

                if (!string.IsNullOrWhiteSpace(otherAudiobook.FilePath))
                {
                    if (!TryResolveStoredFilePath(
                            otherAudiobook,
                            otherAudiobook.FilePath,
                            semantics,
                            out var otherFilePath)
                        || IsSamePathOrWithin(otherFilePath, parentFolder, semantics))
                    {
                        return;
                    }
                }
            }

            try
            {
                result.DeletedParentFolder = await RetireOwnedDirectoryAsync(
                    ownedParent,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogWarning(
                    exception,
                    "Failed to retire owned author folder {FolderPath}",
                    LogRedaction.SanitizeFilePath(parentFolder));
                throw;
            }
            _logger.LogInformation(
                "Deleted empty parent author folder {FolderPath}",
                LogRedaction.SanitizeFilePath(parentFolder));
        }
    }
}
