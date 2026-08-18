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
            var trackedPhysicalObjectIdentities = ResolveTrackedPhysicalObjectIdentities(
                audiobook,
                trackedFilePaths,
                deleteSemantics,
                result,
                out var hasConflictingTrackedPhysicalIdentities,
                out var hasUnprovenTrackedPhysicalIdentities);
            if (hasConflictingTrackedPhysicalIdentities
                || hasUnprovenTrackedPhysicalIdentities)
            {
                return result;
            }

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
                if (trackedFilePaths.Count == 0
                    && !deleteTarget.OwnedDirectories.Any(ownership =>
                        FileSystemPathIdentity.AreEquivalent(
                            ownership.CanonicalPath,
                            deleteTarget.FolderPath,
                            deleteTarget.Semantics)))
                {
                    result.Warnings.Add(
                        "The audiobook folder has no tracked file generation or durable directory ownership, so filesystem deletion was blocked.");
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
                        trackedPhysicalObjectIdentities,
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
                    trackedPhysicalObjectIdentities.TryGetValue(
                        trackedFilePath,
                        out var expectedPhysicalObjectIdentity);
                    TryDeleteFile(
                        trackedFilePath,
                        expectedPhysicalObjectIdentity,
                        result,
                        allowedRoots);
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

            result.TrackedFileCleanupComplete =
                VerifyTrackedFileCleanupComplete(trackedPhysicalObjectIdentities);
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

        private static IReadOnlyDictionary<string, string> ResolveTrackedPhysicalObjectIdentities(
            Audiobook audiobook,
            IReadOnlyCollection<string> trackedFilePaths,
            FileSystemPathSemantics semantics,
            AudiobookFilesystemDeleteResult result,
            out bool hasConflict,
            out bool hasUnprovenTrackedPhysicalIdentities)
        {
            var identities = new Dictionary<string, string>(semantics.Comparer);
            hasConflict = false;
            hasUnprovenTrackedPhysicalIdentities = false;
            foreach (var file in audiobook.Files ?? [])
            {
                if (string.IsNullOrWhiteSpace(file.Path)
                    || !TryResolveStoredFilePath(
                        audiobook,
                        file.Path,
                        semantics,
                        out var resolvedPath))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(file.PhysicalObjectIdentity))
                {
                    hasUnprovenTrackedPhysicalIdentities = true;
                    result.Warnings.Add(
                        "A tracked audiobook file has no persisted physical generation, so filesystem deletion was blocked.");
                    continue;
                }

                if (identities.TryGetValue(resolvedPath, out var existingIdentity)
                    && !string.Equals(
                        existingIdentity,
                        file.PhysicalObjectIdentity,
                        StringComparison.Ordinal))
                {
                    hasConflict = true;
                    result.Warnings.Add(
                        "Conflicting tracked physical generations reference the same audiobook file path, so filesystem deletion was blocked.");
                    return identities;
                }

                identities[resolvedPath] = file.PhysicalObjectIdentity;
            }

            foreach (var trackedFilePath in trackedFilePaths)
            {
                if (identities.ContainsKey(trackedFilePath))
                {
                    continue;
                }

                hasUnprovenTrackedPhysicalIdentities = true;
                result.Warnings.Add(
                    "A tracked audiobook path has no persisted physical generation, so filesystem deletion was blocked.");
                break;
            }

            return identities;
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
            string? expectedPhysicalObjectIdentity,
            AudiobookFilesystemDeleteResult result,
            IEnumerable<string> allowedRoots)
        {
            var observedExists = File.Exists(path);
            if (!FileSystemSafety.TryDeleteFile(
                    path,
                    allowedRoots,
                    expectedPhysicalObjectIdentity,
                    out var reason))
            {
                var warning = !string.IsNullOrWhiteSpace(expectedPhysicalObjectIdentity)
                    && reason.Contains("physical generation", StringComparison.OrdinalIgnoreCase)
                        ? $"Could not delete file '{Path.GetFileName(path)}' because its tracked physical generation changed."
                        : $"Could not delete file '{Path.GetFileName(path)}'.";
                result.Warnings.Add(warning);
                _logger.LogWarning(
                    "Blocked audiobook file delete for {Path}: {Reason}",
                    LogRedaction.SanitizeFilePath(path),
                    LogRedaction.SanitizeText(reason));
                return;
            }

            if (observedExists)
            {
                result.DeletedFiles++;
                _logger.LogInformation(
                    "Deleted audiobook file {Path}",
                    LogRedaction.SanitizeFilePath(path));
            }
        }

    }
}
