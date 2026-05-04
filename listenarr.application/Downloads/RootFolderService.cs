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
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads
{
    public class RootFolderService : IRootFolderService
    {
        private readonly IRootFolderRepository _repo;
        private readonly ILogger<RootFolderService>? _logger;
        private readonly IMoveQueueService? _moveQueue;

        public RootFolderService(IRootFolderRepository repo, ILogger<RootFolderService>? logger, IMoveQueueService? moveQueue = null)
        {
            _repo = repo;
            _logger = logger;
            _moveQueue = moveQueue;
        }

        public async Task<RootFolder?> GetDefaultAsync()
        {
            return await _repo.GetDefaultAsync();
        }

        public async Task<RootFolder> CreateAsync(RootFolder root)
        {
            root.Path = root.Path?.Trim() ?? string.Empty;
            root.Name = root.Name?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(root.Path)) throw new ArgumentException("Path is required");
            if (string.IsNullOrWhiteSpace(root.Name)) throw new ArgumentException("Name is required");

            var existingByPath = await _repo.GetByPathAsync(root.Path);
            if (existingByPath != null) throw new InvalidOperationException("A root folder with that path already exists.");

            if (root.IsDefault)
            {
                await _repo.ClearDefaultExceptAsync(excludeId: null);
            }

            await _repo.AddAsync(root);
            return root;
        }

        public async Task DeleteAsync(int id, int? reassignRootId = null)
        {
            var root = await _repo.GetByIdAsync(id);
            if (root == null) throw new KeyNotFoundException("Root folder not found");

            var hasReferenced = await _repo.HasAudiobooksUnderPathAsync(root.Path);
            if (hasReferenced && !reassignRootId.HasValue)
            {
                throw new InvalidOperationException("Root folder is in use by audiobooks; reassign before deletion or provide reassignRootId.");
            }

            if (hasReferenced)
            {
                var newRoot = await _repo.GetByIdAsync(reassignRootId!.Value);
                if (newRoot == null) throw new KeyNotFoundException("Reassign root not found");
                await _repo.MigrateAudiobookPathsAsync(root.Path, newRoot.Path);
            }

            await _repo.RemoveAsync(id);
        }

        public async Task<List<RootFolder>> GetAllAsync() => await _repo.GetAllAsync();

        public async Task<RootFolder?> GetByIdAsync(int id) => await _repo.GetByIdAsync(id);

        public async Task<RootFolder> UpdateAsync(RootFolder root, bool moveFiles = false, bool deleteEmptySource = true)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            root.Path = root.Path?.Trim() ?? string.Empty;
            root.Name = root.Name?.Trim() ?? string.Empty;

            var existing = await _repo.GetByIdAsync(root.Id);
            if (existing == null) throw new KeyNotFoundException("Root folder not found");

            if (!string.Equals(existing.Path, root.Path, StringComparison.OrdinalIgnoreCase))
            {
                var duplicate = await _repo.GetByPathAsync(root.Path);
                if (duplicate != null && duplicate.Id != root.Id)
                    throw new InvalidOperationException("Another root folder with that path already exists.");
            }

            if (root.IsDefault)
            {
                await _repo.ClearDefaultExceptAsync(excludeId: root.Id);
            }

            var oldPath = existing.Path;
            var newPath = root.Path;

            List<(int audiobookId, string original, string target)> moves = new();
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                moves = await _repo.MigrateAudiobookPathsAsync(oldPath, newPath);

                try
                {
                    _logger?.LogInformation("Root rename from {OldPath} to {NewPath}: {Count} audiobooks affected", oldPath, newPath, moves.Count);
                    foreach (var m in moves)
                    {
                        _logger?.LogInformation("Root rename move prep: AudiobookId={AudiobookId} Original={Original} Target={Target}", m.audiobookId, m.original, m.target);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger?.LogDebug(ex, "Failed to emit diagnostics for root rename");
                }
            }

            existing.Name = root.Name;
            existing.Path = root.Path;
            existing.IsDefault = root.IsDefault;
            existing.UpdatedAt = DateTime.UtcNow;
            await _repo.UpdateAsync(existing);

            if (moveFiles && _moveQueue != null)
            {
                foreach (var m in moves)
                {
                    try
                    {
                        _ = _moveQueue.EnqueueMoveAsync(m.audiobookId, m.target, m.original);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger?.LogWarning(ex, "Failed to enqueue move for audiobook {AudiobookId} during root rename", m.audiobookId);
                    }
                }
            }

            return existing;
        }
    }
}
