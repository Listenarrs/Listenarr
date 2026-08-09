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

namespace Listenarr.Application.Audiobooks.RootFolders
{
    public partial class RootFolderService : IRootFolderService
    {
        private readonly IRootFolderRepository _repo;
        private readonly ILogger<RootFolderService>? _logger;
        private readonly IMoveQueueService _moveQueue;
        private readonly IFileSystemSemanticsResolver _semanticsResolver;
        private readonly IRootFolderRelocationService _relocationService;
        private readonly IFilesystemMutationCoordinator _mutationCoordinator;
        private readonly IAudiobookOperationCoordinator _audiobookOperationCoordinator;
        private readonly IDirectoryObjectIdentityResolver? _directoryObjectIdentityResolver;

        public RootFolderService(
            IRootFolderRepository repo,
            ILogger<RootFolderService>? logger,
            IFileSystemSemanticsResolver semanticsResolver,
            IMoveQueueService moveQueue,
            IRootFolderRelocationService relocationService,
            IFilesystemMutationCoordinator mutationCoordinator,
            IAudiobookOperationCoordinator audiobookOperationCoordinator,
            IDirectoryObjectIdentityResolver? directoryObjectIdentityResolver = null)
        {
            _repo = repo;
            _logger = logger;
            _semanticsResolver = semanticsResolver ?? throw new ArgumentNullException(nameof(semanticsResolver));
            _mutationCoordinator = mutationCoordinator ?? throw new ArgumentNullException(nameof(mutationCoordinator));
            _moveQueue = moveQueue ?? throw new ArgumentNullException(nameof(moveQueue));
            _relocationService = relocationService ?? throw new ArgumentNullException(nameof(relocationService));
            _audiobookOperationCoordinator = audiobookOperationCoordinator
                ?? throw new ArgumentNullException(nameof(audiobookOperationCoordinator));
            _directoryObjectIdentityResolver = directoryObjectIdentityResolver;
        }

        public async Task<RootFolder?> GetDefaultAsync()
        {
            return await _repo.GetDefaultAsync();
        }

        public Task<RootFolder> CreateAsync(RootFolder root) =>
            _mutationCoordinator.ExecuteExclusiveAsync(_ => CreateCoreAsync(root));

        private async Task<RootFolder> CreateCoreAsync(RootFolder root)
        {
            root.Name = root.Name?.Trim() ?? string.Empty;
            root.Path = FileUtils.NormalizeRootFolderPathForStorage(root.Path);

            if (string.IsNullOrWhiteSpace(root.Name)) throw new ArgumentException("Name is required");

            var resolution = await ResolveSemanticsAsync(root.Path, root.CaseSensitivityMode);
            ApplyIdentity(root, resolution);
            if (await _relocationService.IsBoundaryProtectedAsync(root.Path, resolution.Semantics))
            {
                throw new InvalidOperationException(
                    "Root folder path overlaps an active relocation boundary.");
            }

            var conflict = await FindConflictingRootFolderAsync(root.Path, resolution.Semantics);
            if (conflict != null)
            {
                throw new InvalidOperationException(BuildRootFolderConflictMessage(conflict));
            }

            await CaptureInitialDirectoryObjectIdentityAsync(root);
            AfterInitialDirectoryIdentityCapturedForTest?.Invoke();

            if (root.IsDefault)
            {
                var currentDefaultId = (await _repo.GetDefaultAsync())?.Id;
                await _repo.AddAndSetDefaultAsync(root, currentDefaultId);
            }
            else
            {
                await _repo.AddAsync(root);
            }

            await RevalidateCreatedDirectoryObjectIdentityAsync(root);
            return root;
        }

        public Task DeleteAsync(int id, int? reassignRootId = null) =>
            _mutationCoordinator.ExecuteExclusiveAsync(_ => DeleteCoreAsync(id, reassignRootId));

        private async Task DeleteCoreAsync(int id, int? reassignRootId)
        {
            var root = await _repo.GetByIdAsync(id);
            if (root == null) throw new KeyNotFoundException("Root folder not found");
            if (reassignRootId == id)
            {
                throw new InvalidOperationException("A root folder cannot be reassigned to itself.");
            }

            await EnsureNoActiveRelocationAsync(root.Id);
            await EnsureNoNonRemovedDirectoryOwnershipAsync(root.Id);

            var sourcePersistedSemantics = RootFolderPathSemantics.ResolvePersisted(root)
                ?? throw new InvalidOperationException(
                    "The root folder has no usable persisted path semantics; change the root path before deleting or reassigning it.");
            var sourceComparisonSemantics = sourcePersistedSemantics.DetectAmbiguousCaseMatches
                ? new FileSystemPathSemantics(
                    sourcePersistedSemantics.Semantics.Syntax,
                    FileSystemCaseSensitivity.Insensitive)
                : sourcePersistedSemantics.Semantics;
            await EnsureNoActiveMoveJobsTouchRootAsync(root.Path, sourceComparisonSemantics);
            var hasReferenced = await _repo.HasAudiobooksUnderPathAsync(
                root.Path,
                sourceComparisonSemantics);
            if (hasReferenced && !reassignRootId.HasValue)
            {
                throw new InvalidOperationException("Root folder is in use by audiobooks; reassign before deletion or provide reassignRootId.");
            }

            if (hasReferenced)
            {
                var newRoot = await _repo.GetByIdAsync(reassignRootId!.Value);
                if (newRoot == null) throw new KeyNotFoundException("Reassign root not found");
                await EnsureNoActiveRelocationAsync(newRoot.Id);
                var targetPersistedSemantics = RootFolderPathSemantics.ResolvePersisted(newRoot)
                    ?? throw new InvalidOperationException(
                        "The reassignment root has no usable persisted path semantics; change its path before reassigning audiobooks.");
                var sourceRewriteSemantics = sourcePersistedSemantics.DetectAmbiguousCaseMatches
                    ? (await ResolvePersistedSemanticsAsync(
                        root.Path,
                        root.CaseSensitivityMode)).Semantics
                    : sourcePersistedSemantics.Semantics;
                var targetRewriteSemantics = targetPersistedSemantics.DetectAmbiguousCaseMatches
                    ? (await ResolvePersistedSemanticsAsync(
                        newRoot.Path,
                        newRoot.CaseSensitivityMode)).Semantics
                    : targetPersistedSemantics.Semantics;

                await EnsureNoActiveMoveJobsTouchRootAsync(
                    newRoot.Path,
                    targetRewriteSemantics);
                var audiobookIds = await _repo.GetAllAudiobookIdsAsync();
                await _audiobookOperationCoordinator.ExecuteExclusiveAsync(
                    audiobookIds,
                    token => _repo.ReassignAudiobooksAndRemoveAsync(
                        root.Id,
                        newRoot.Id,
                        sourceRewriteSemantics,
                        targetRewriteSemantics,
                        token));
                return;
            }

            await _repo.RemoveAsync(id);
        }

        public async Task<List<RootFolder>> GetAllAsync() => await _repo.GetAllAsync();

        public async Task<RootFolder?> GetByIdAsync(int id) => await _repo.GetByIdAsync(id);

        public Task<RootFolder> UpdateAsync(RootFolder root, bool moveFiles = false, bool deleteEmptySource = true) =>
            _mutationCoordinator.ExecuteExclusiveAsync(
                _ => UpdateCoreAsync(root, moveFiles, deleteEmptySource));

        private async Task<RootFolder> UpdateCoreAsync(
            RootFolder root,
            bool moveFiles,
            bool deleteEmptySource)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            root.Name = root.Name?.Trim() ?? string.Empty;
            root.Path = FileUtils.NormalizeRootFolderPathForStorage(root.Path);

            if (string.IsNullOrWhiteSpace(root.Name)) throw new ArgumentException("Name is required");

            var existing = await _repo.GetByIdAsync(root.Id);
            if (existing == null) throw new KeyNotFoundException("Root folder not found");
            await EnsureNoActiveRelocationAsync(existing.Id);

            if (existing.CaseSensitivityMode != root.CaseSensitivityMode)
            {
                throw new InvalidOperationException(
                    "Root filesystem semantics cannot be changed by metadata updates; use the path-changes endpoint so persisted identities are migrated.");
            }

            var persistedSemantics = RootFolderPathSemantics.ResolvePersisted(existing)
                ?? throw new InvalidOperationException(
                    "Root filesystem semantics are unavailable; use the path-changes endpoint to repair persisted identities.");
            if (!FileSystemPathIdentity.AreEquivalent(
                existing.Path,
                root.Path,
                persistedSemantics.Semantics))
            {
                throw new InvalidOperationException(
                    "Root paths cannot be changed by metadata updates; use the path-changes endpoint.");
            }

            await EnsureNoActiveMoveJobsTouchRootAsync(
                existing.Path,
                persistedSemantics.Semantics);
            existing.Name = root.Name;
            existing.IsDefault = root.IsDefault;
            var conflict = await FindConflictingRootFolderAsync(
                existing.Path,
                persistedSemantics.Semantics,
                existing.Id);
            if (conflict != null)
            {
                throw new InvalidOperationException(BuildRootFolderConflictMessage(conflict));
            }

            existing.UpdatedAt = DateTime.UtcNow;
            if (root.IsDefault)
            {
                var currentDefaultId = (await _repo.GetDefaultAsync())?.Id;
                await _repo.UpdateAndSetDefaultAsync(existing, currentDefaultId);
            }
            else
            {
                await _repo.UpdateAsync(existing);
            }

            return existing;
        }

        private async Task<RootFolderConflict?> FindConflictingRootFolderAsync(
            string normalizedPath,
            FileSystemPathSemantics requestedSemantics,
            int? excludeId = null)
        {
            var roots = await _repo.GetAllAsync();
            foreach (var existingRoot in roots)
            {
                if (excludeId.HasValue && existingRoot.Id == excludeId.Value)
                {
                    continue;
                }

                var existingSemantics = FileSystemPathIdentity.ResolveComparisonSemantics(
                    existingRoot.ResolvedCaseSensitivity,
                    requestedSemantics);
                try
                {
                    var boundaryConflict = FileSystemPathIdentity.EvaluateBoundaryConflict(
                        normalizedPath,
                        requestedSemantics,
                        existingRoot.Path,
                        existingSemantics);
                    var conflictType = boundaryConflict switch
                    {
                        FileSystemPathBoundaryConflict.Equivalent => RootFolderConflictType.Duplicate,
                        FileSystemPathBoundaryConflict.FirstInsideSecond =>
                            RootFolderConflictType.RequestedRootIsNestedInsideExistingRoot,
                        FileSystemPathBoundaryConflict.SecondInsideFirst =>
                            RootFolderConflictType.ExistingRootIsNestedInsideRequestedRoot,
                        FileSystemPathBoundaryConflict.Ambiguous => RootFolderConflictType.Ambiguous,
                        _ => (RootFolderConflictType?)null
                    };
                    if (conflictType.HasValue)
                    {
                        return new RootFolderConflict(existingRoot, conflictType.Value);
                    }
                }
                catch (ArgumentException exception)
                {
                    _logger?.LogWarning(
                        exception,
                        "Skipping root folder {RootFolderId} with invalid stored path while checking root-folder conflicts.",
                        existingRoot.Id);
                }
            }

            return null;
        }

        private async Task EnsureNoActiveMoveJobsTouchRootAsync(
            string rootPath,
            FileSystemPathSemantics semantics)
        {
            var blockingJobsTask = _moveQueue.GetFilesystemBlockingJobsAsync();
            IReadOnlyList<MoveJob>? blockingJobs = blockingJobsTask == null
                ? Array.Empty<MoveJob>()
                : await blockingJobsTask;
            blockingJobs ??= Array.Empty<MoveJob>();

            var conflictingJob = blockingJobs.FirstOrDefault(job =>
                MoveJobBoundaryConflict.TouchesBoundary(job, rootPath, semantics));

            if (conflictingJob == null)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Root folder has unresolved move job {conflictingJob.Id}; resolve moves touching this root before deleting or reassigning it.");
        }

        private async Task<FileSystemSemanticsResolution> ResolveSemanticsAsync(
            string path,
            FileSystemCaseSensitivityMode mode)
        {
            var resolution = await _semanticsResolver.ResolveAsync(path, mode);
            if (resolution.State != PathIdentityState.Valid)
            {
                throw new InvalidOperationException(
                    resolution.Reason ?? "Filesystem case sensitivity is unresolved; select an explicit override.");
            }

            return resolution;
        }

        private async Task<FileSystemSemanticsResolution> ResolvePersistedSemanticsAsync(
            string path,
            FileSystemCaseSensitivityMode mode)
        {
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    path,
                    out var canonicalPath,
                    out var reason))
            {
                throw new InvalidOperationException(reason);
            }

            return await ResolveSemanticsAsync(canonicalPath, mode);
        }

        private static void ApplyIdentity(
            RootFolder root,
            FileSystemSemanticsResolution resolution)
        {
            root.ResolvedCaseSensitivity = resolution.Semantics.CaseSensitivity;
            root.PathIdentityState = resolution.State;
            root.PathIdentityKey = FileSystemPathIdentity.CreateKey(
                "root",
                root.Path,
                resolution.Semantics);
        }

        private async Task EnsureNoActiveRelocationAsync(int rootFolderId)
        {
            if (await _relocationService.GetActiveForRootAsync(rootFolderId) != null)
            {
                throw new InvalidOperationException(
                    "Root folder metadata and deletion are locked while a relocation is active.");
            }
        }

        private async Task EnsureNoNonRemovedDirectoryOwnershipAsync(
            int rootFolderId)
        {
            if (await _repo.HasNonRemovedDirectoryOwnershipAsync(rootFolderId))
            {
                throw new InvalidOperationException(
                    "Root folder deletion is blocked while durable directory ownership claims remain active.");
            }
        }

        private static string BuildRootFolderConflictMessage(RootFolderConflict conflict)
        {
            return conflict.Type switch
            {
                RootFolderConflictType.Duplicate => "A root folder with that path already exists.",
                RootFolderConflictType.RequestedRootIsNestedInsideExistingRoot =>
                    $"Root folder cannot be nested inside existing root '{conflict.Root.Name}'.",
                RootFolderConflictType.ExistingRootIsNestedInsideRequestedRoot =>
                    $"Root folder cannot contain existing root '{conflict.Root.Name}'.",
                RootFolderConflictType.Ambiguous =>
                    $"Root folder path has an ambiguous filesystem overlap with existing root '{conflict.Root.Name}'.",
                _ => "Root folder path conflicts with an existing root folder."
            };
        }

        private sealed record RootFolderConflict(RootFolder Root, RootFolderConflictType Type);

        private enum RootFolderConflictType
        {
            Duplicate,
            RequestedRootIsNestedInsideExistingRoot,
            ExistingRootIsNestedInsideRequestedRoot,
            Ambiguous
        }
    }
}
