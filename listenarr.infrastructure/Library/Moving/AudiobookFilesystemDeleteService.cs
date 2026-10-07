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
    public sealed partial class AudiobookFilesystemDeleteService : IAudiobookFilesystemDeleteService
    {
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly IAudiobookFileRepository _audioFileRepository;
        private readonly IRootFolderService _rootFolderService;
        private readonly IConfigurationService _configurationService;
        private readonly IFileSystemSemanticsResolver _semanticsResolver;
        private readonly ILibraryDirectoryOwnershipStore _directoryOwnershipStore;
        private readonly ILogger<AudiobookFilesystemDeleteService> _logger;
        private readonly LibraryDirectoryOwnershipBoundaryAuthorizer? _ownershipAuthorizer;

        internal Action? AfterTrackedContentCaptureForTest { get; set; }
        internal Action<string>? BeforeOwnedDirectoryRetirementForTest { get; set; }

        public AudiobookFilesystemDeleteService(
            IAudiobookRepository audiobookRepository,
            IAudiobookFileRepository audioFileRepository,
            IRootFolderService rootFolderService,
            IConfigurationService configurationService,
            IFileSystemSemanticsResolver semanticsResolver,
            ILibraryDirectoryOwnershipStore directoryOwnershipStore,
            ILogger<AudiobookFilesystemDeleteService> logger,
            LibraryDirectoryOwnershipBoundaryAuthorizer? ownershipAuthorizer = null)
        {
            _audiobookRepository = audiobookRepository;
            _audioFileRepository = audioFileRepository;
            _rootFolderService = rootFolderService;
            _configurationService = configurationService;
            _semanticsResolver = semanticsResolver;
            _directoryOwnershipStore = directoryOwnershipStore;
            _logger = logger;
            _ownershipAuthorizer = ownershipAuthorizer;
        }

        public async Task<AudiobookFilesystemDeleteResult> DeleteAsync(
            Audiobook audiobook,
            bool deleteFolder,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new AudiobookFilesystemDeleteResult();
            var storedTrackedFilePaths = CollectStoredTrackedFilePaths(audiobook);
            var boundaryPath = !string.IsNullOrWhiteSpace(audiobook.BasePath)
                ? audiobook.BasePath
                : !string.IsNullOrWhiteSpace(audiobook.FilePath)
                    ? audiobook.FilePath
                    : storedTrackedFilePaths.FirstOrDefault();
            var semantics = await ResolveDeleteSemanticsAsync(
                boundaryPath,
                result,
                cancellationToken);
            if (semantics == null)
            {
                result.TrackedFileCleanupComplete =
                    storedTrackedFilePaths.Count == 0 && !deleteFolder;
                return result;
            }

            var deleteSemantics = semantics.Value;
            var trackedFilePaths = ResolveTrackedFilePaths(
                audiobook,
                storedTrackedFilePaths,
                deleteSemantics,
                result,
                out var hasUnresolvedTrackedPaths);
            using var trackedContentProofs = await CaptureTrackedContentProofsAsync(
                trackedFilePaths,
                deleteSemantics,
                result,
                cancellationToken);
            if (trackedContentProofs == null)
            {
                return result;
            }

            AfterTrackedContentCaptureForTest?.Invoke();

            var deleteTarget = hasUnresolvedTrackedPaths
                ? null
                : await ResolveDeleteFolderTargetAsync(
                    audiobook,
                    trackedFilePaths,
                    deleteSemantics,
                    result,
                    cancellationToken);

            if (deleteTarget != null)
            {
                if (trackedContentProofs.Count == 0
                    && !deleteTarget.OwnedDirectories.Any(ownership =>
                        FileSystemPathIdentity.AreEquivalent(
                            ownership.CanonicalPath,
                            deleteTarget.FolderPath,
                            deleteTarget.Semantics)))
                {
                    result.Warnings.Add(
                        "The audiobook folder has no live tracked-file content proof or durable directory ownership, so filesystem deletion was blocked.");
                    return result;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var targetAuthorization = await AuthorizeDeleteTargetAsync(
                    deleteTarget,
                    result,
                    cancellationToken);
                if (targetAuthorization == null)
                {
                    return result;
                }

                using var liveTargetAuthorization = targetAuthorization;
                using var capturedTreeProofs = new CapturedDeleteTreeProofs(deleteSemantics.Comparer);
                // Authorization can perform async persistence and filesystem identity work.
                // Request cancellation remains authoritative until that preflight finishes;
                // only the destructive mutation and its durable ownership cleanup are
                // noncancelable once this final fence has been crossed.
                var mutationToken = RequestCancellationBoundary.EnterNonCancelablePhase(
                    cancellationToken);
                var contentsDeleted = TryDeleteFolderContents(
                    deleteTarget,
                    liveTargetAuthorization,
                    trackedFilePaths,
                    trackedContentProofs,
                    capturedTreeProofs,
                    result);

                // Legacy Windows disposition completes on the last handle close.
                // Release the captured file proofs before empty-folder cleanup.
                trackedContentProofs.Dispose();

                if (deleteFolder && contentsDeleted)
                {
                    await TryDeleteAudiobookFolderAsync(
                        audiobook,
                        deleteTarget,
                        liveTargetAuthorization,
                        capturedTreeProofs,
                        result,
                        mutationToken);
                }
            }
            else
            {
                var protectedRoots = await GetProtectedRootPathsAsync(
                    cancellationToken);
                var fallbackFolderRoot = ResolveAudiobookFolderPath(audiobook, trackedFilePaths, deleteSemantics);
                var allowedRoots = protectedRoots
                    .Concat(string.IsNullOrWhiteSpace(fallbackFolderRoot) ? [] : [fallbackFolderRoot])
                    .ToList();
                var mutationToken = RequestCancellationBoundary.EnterNonCancelablePhase(
                    cancellationToken);
                foreach (var trackedFilePath in trackedFilePaths)
                {
                    if (!trackedContentProofs.TryGetValue(
                            trackedFilePath,
                            out var expectedContentProof))
                    {
                        // This path was proven absent during preflight. Never
                        // delete a new entry that appears there afterward.
                        continue;
                    }

                    await TryDeleteFileAsync(
                        trackedFilePath,
                        expectedContentProof,
                        result,
                        allowedRoots,
                        deleteSemantics,
                        mutationToken);
                }

                trackedContentProofs.Dispose();

                if (deleteFolder)
                {
                    await RecoverMissingOwnedDirectoryAsync(
                        fallbackFolderRoot,
                        deleteSemantics,
                        "audiobook",
                        mutationToken);
                    await RecoverMissingOwnedAuthorParentAsync(
                        audiobook,
                        fallbackFolderRoot,
                        deleteSemantics,
                        mutationToken);
                }
            }

            result.TrackedFileCleanupComplete =
                !hasUnresolvedTrackedPaths
                && await VerifyTrackedFileCleanupCompleteAsync(
                    trackedFilePaths, deleteSemantics, CancellationToken.None);
            return result;
        }

        private sealed class DeleteFolderTarget
        {
            public required string FolderPath { get; init; }
            public required IReadOnlyCollection<string> ProtectedRoots { get; init; }
            public required IReadOnlyCollection<string> AllowedMutationRoots { get; init; }
            public required FileSystemPathSemantics Semantics { get; init; }
            public required IReadOnlyList<LibraryDirectoryOwnership> OwnedDirectories { get; init; }
        }

        private static IReadOnlyList<string> CollectStoredTrackedFilePaths(Audiobook audiobook)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);

            if (!string.IsNullOrWhiteSpace(audiobook.FilePath))
            {
                paths.Add(audiobook.FilePath);
            }

            if (audiobook.Files != null)
            {
                foreach (var storedPath in audiobook.Files
                    .Select(file => file.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path)))
                {
                    paths.Add(storedPath!);
                }
            }

            return paths.ToList();
        }

        private static IReadOnlyList<string> ResolveTrackedFilePaths(
            Audiobook audiobook,
            IEnumerable<string> storedPaths,
            FileSystemPathSemantics semantics,
            AudiobookFilesystemDeleteResult result,
            out bool hasUnresolved)
        {
            var paths = new HashSet<string>(semantics.Comparer);
            hasUnresolved = false;
            foreach (var storedPath in storedPaths)
            {
                if (TryResolveStoredFilePath(
                        audiobook,
                        storedPath,
                        semantics,
                        out var resolvedPath))
                {
                    paths.Add(resolvedPath);
                }
                else
                {
                    hasUnresolved = true;
                }
            }

            if (hasUnresolved)
            {
                result.Warnings.Add(
                    "One or more tracked audiobook file paths are unavailable on the current host and were preserved.");
            }

            return paths.ToList();
        }

        private static bool TryResolveStoredFilePath(
            Audiobook audiobook,
            string storedPath,
            FileSystemPathSemantics semantics,
            out string resolvedPath)
        {
            resolvedPath = string.Empty;
            if (FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    storedPath,
                    out resolvedPath,
                    out _))
            {
                return true;
            }

            if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(storedPath, out _)
                || !FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    audiobook.BasePath ?? string.Empty,
                    out var basePath,
                    out _))
            {
                resolvedPath = string.Empty;
                return false;
            }

            return FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                basePath,
                storedPath,
                semantics,
                out resolvedPath);
        }

        private async Task TryDeleteFileAsync(
            string path,
            DeleteFileContentProof expectedContentProof,
            AudiobookFilesystemDeleteResult result,
            IEnumerable<string> allowedRoots,
            FileSystemPathSemantics semantics,
            CancellationToken cancellationToken)
        {
            if (!FileSystemSafety.TryValidateMutationTarget(
                    path,
                    allowedRoots,
                    out var normalizedPath,
                    out var reason))
            {
                result.Warnings.Add(
                    $"Could not delete file '{Path.GetFileName(path)}' safely.");
                _logger.LogWarning(
                    "Blocked audiobook file delete for {Path}: {Reason}",
                    LogRedaction.SanitizeFilePath(path),
                    LogRedaction.SanitizeText(reason));
                return;
            }

            var parentPath = Path.GetDirectoryName(normalizedPath);
            var fileName = Path.GetFileName(normalizedPath);
            if (string.IsNullOrWhiteSpace(parentPath)
                || string.IsNullOrWhiteSpace(fileName))
            {
                result.Warnings.Add(
                    $"Could not delete file '{Path.GetFileName(path)}' safely.");
                return;
            }

            try
            {
                using var parent =
                    await OpenPinnedDeleteFileParentAsync(
                        normalizedPath, semantics, cancellationToken);
                // Keep the original preflight object pinned throughout this request.
                // A same-content replacement is never a new deletion capability.
                using var entry = expectedContentProof.OriginalEntry.DuplicateForOperation();
                if (!await entry.MatchesAsync(
                        expectedContentProof.Length,
                        expectedContentProof.Sha256,
                        cancellationToken)
                    || !FileSystemSafety.TryValidateMutationTarget(
                        normalizedPath,
                        allowedRoots,
                        out var revalidatedPath,
                        out reason)
                    || !StringComparer.Ordinal.Equals(
                        normalizedPath,
                        revalidatedPath)
                    || !parent.VisiblePathMatches()
                    || !entry.VisiblePathMatches())
                {
                    result.Warnings.Add(
                        $"Could not delete file '{Path.GetFileName(path)}' because its live content or path changed.");
                    _logger.LogWarning(
                        "Blocked audiobook file delete for {Path}: live proof changed before deletion",
                        LogRedaction.SanitizeFilePath(path));
                    return;
                }

                entry.Delete(immediateWindows: true);
                result.DeletedFiles++;
                _logger.LogInformation(
                    "Deleted audiobook file {Path}",
                    LogRedaction.SanitizeFilePath(path));
            }
            catch (Exception exception) when (
                FileSystemSafety.IsProvenMissingPathException(exception))
            {
                return;
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException
                    or InvalidOperationException or NotSupportedException
                    or System.ComponentModel.Win32Exception
                    or System.Security.SecurityException)
            {
                result.Warnings.Add(
                    $"Could not delete file '{Path.GetFileName(path)}' safely.");
                _logger.LogWarning(
                    exception,
                    "Blocked audiobook file delete for {Path}",
                    LogRedaction.SanitizeFilePath(path));
            }
        }

    }
}
