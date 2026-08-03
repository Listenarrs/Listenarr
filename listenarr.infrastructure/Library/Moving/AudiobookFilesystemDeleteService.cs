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
                return result;
            }

            var deleteSemantics = semantics.Value;
            var trackedFilePaths = ResolveTrackedFilePaths(
                audiobook,
                storedTrackedFilePaths,
                deleteSemantics,
                result,
                out var hasUnresolvedTrackedPaths);
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
                cancellationToken.ThrowIfCancellationRequested();
                var targetAuthorization = await AuthorizeDeleteTargetAsync(
                    deleteTarget,
                    result,
                    cancellationToken);
                if (targetAuthorization == null)
                {
                    return result;
                }

                bool contentsDeleted;
                CancellationToken mutationToken;
                using (targetAuthorization)
                {
                    // Authorization can perform async persistence and filesystem identity work.
                    // Request cancellation remains authoritative until that preflight finishes;
                    // only the destructive mutation and its durable ownership cleanup are
                    // noncancelable once this final fence has been crossed.
                    mutationToken = RequestCancellationBoundary.EnterNonCancelablePhase(
                        cancellationToken);
                    contentsDeleted = TryDeleteFolderContents(
                        deleteTarget,
                        targetAuthorization,
                        result);
                }

                if (deleteFolder && contentsDeleted)
                {
                    await TryDeleteAudiobookFolderAsync(
                        audiobook,
                        deleteTarget,
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
                    TryDeleteFile(trackedFilePath, result, allowedRoots);
                }

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

        private async Task<FileSystemPathSemantics?> ResolveDeleteSemanticsAsync(
            string? boundaryPath,
            AudiobookFilesystemDeleteResult result,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(boundaryPath))
            {
                return null;
            }

            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    boundaryPath,
                    out var canonicalBoundaryPath,
                    out _))
            {
                result.Warnings.Add(
                    "The audiobook filesystem path is unavailable on the current host, so deletion was blocked.");
                return null;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileSystemPathSemantics? bestSemantics = null;
                var bestRootLength = -1;
                foreach (var root in await _rootFolderService.GetAllAsync())
                {
                    if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                            root.Path,
                            out var canonicalRoot,
                            out _))
                    {
                        continue;
                    }

                    var rootResolution = await _semanticsResolver.ResolveAsync(
                        canonicalRoot,
                        root.CaseSensitivityMode,
                        cancellationToken);
                    if (rootResolution.State != PathIdentityState.Valid
                        || !FileSystemPathIdentity.IsSameOrInside(
                            canonicalBoundaryPath,
                            canonicalRoot,
                            rootResolution.Semantics))
                    {
                        continue;
                    }

                    if (canonicalRoot.Length > bestRootLength)
                    {
                        bestSemantics = rootResolution.Semantics;
                        bestRootLength = canonicalRoot.Length;
                    }
                }

                if (bestSemantics.HasValue)
                {
                    return bestSemantics.Value;
                }
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                _logger.LogWarning(exception, "Failed to resolve root folder semantics while deleting audiobook files");
            }

            var resolution = await _semanticsResolver.ResolveAsync(
                canonicalBoundaryPath,
                cancellationToken: cancellationToken);
            if (resolution.State == PathIdentityState.Valid)
            {
                return resolution.Semantics;
            }

            result.Warnings.Add(
                "Filesystem case sensitivity could not be resolved, so deletion was blocked.");
            return null;
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

        private void TryDeleteFile(
            string path,
            AudiobookFilesystemDeleteResult result,
            IEnumerable<string> allowedRoots)
        {
            if (!File.Exists(path))
            {
                return;
            }

            if (!FileSystemSafety.TryDeleteFile(
                    path,
                    allowedRoots,
                    out var reason))
            {
                var warning = $"Could not delete file '{Path.GetFileName(path)}'.";
                result.Warnings.Add(warning);
                _logger.LogWarning(
                    "Blocked audiobook file delete for {Path}: {Reason}",
                    LogRedaction.SanitizeFilePath(path),
                    LogRedaction.SanitizeText(reason));
                return;
            }

            result.DeletedFiles++;
            _logger.LogInformation(
                "Deleted audiobook file {Path}",
                LogRedaction.SanitizeFilePath(path));
        }

        private bool TryDeleteFolderContents(
            DeleteFolderTarget deleteTarget,
            PinnedDirectoryCreation.PinnedDirectoryAnchor targetAuthorization,
            AudiobookFilesystemDeleteResult result)
        {
            var folderPath = deleteTarget.FolderPath;
            if (!Directory.Exists(folderPath))
            {
                return true;
            }

            var ownershipMarkerPaths = deleteTarget.OwnedDirectories
                .SelectMany(LibraryDirectoryOwnershipMarker.GetMarkerPaths)
                .ToHashSet(deleteTarget.Semantics.Comparer);
            var preflightIdentities = new Dictionary<string, string>(
                deleteTarget.Semantics.Comparer);
            if (!TryValidatePinnedDirectoryTree(
                    targetAuthorization,
                    targetAuthorization,
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
