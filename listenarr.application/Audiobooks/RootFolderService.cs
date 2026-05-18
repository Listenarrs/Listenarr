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

namespace Listenarr.Application.Audiobooks
{
    public class RootFolderService(
        IRootFolderRepository rootFolderRepository,
        IAudiobookRepository audiobookRepository,
        ILogger<RootFolderService> logger,
        IMoveQueueService moveQueueService) : IRootFolderService
    {
        public async Task<RootFolder?> GetDefaultAsync()
        {
            return await rootFolderRepository.GetDefaultAsync();
        }

        public async Task<RootFolder> CreateAsync(RootFolder root)
        {
            root.Path ??= string.Empty;
            root.Name = root.Name?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(root.Path)) throw new ArgumentException("Path is required");
            if (string.IsNullOrWhiteSpace(root.Name)) throw new ArgumentException("Name is required");

            var existingByPath = await rootFolderRepository.GetByPathAsync(root.Path);
            if (existingByPath != null) throw new InvalidOperationException("A root folder with that path already exists.");

            if (root.IsDefault)
            {
                await rootFolderRepository.ClearDefaultExceptAsync(excludeId: null);
            }

            await rootFolderRepository.AddAsync(root);
            return root;
        }

        public async Task DeleteAsync(int id, int? reassignRootId = null)
        {
            var rootFolders = await rootFolderRepository.GetAllAsync();
            var rootFolder = rootFolders.FirstOrDefault(r => r.Id == id);
            if (rootFolder == null)
            {
                logger.LogWarning($"Root folder with id {id} cannot be found, assuming it's deleted");
                return;
            }

            rootFolders = [.. rootFolders.Where(r => r.Id != id)];

            var audiobooks = await audiobookRepository.GetAllAsync();
            var rootedAudiobooks = audiobooks.Where(a => !string.IsNullOrEmpty(a.BasePath) && !rootFolders.Any(r => a.BasePath.StartsWith(r.Path)));
            if (rootedAudiobooks.Any())
            {
                throw new InvalidOperationException($"Root folder is in use by {rootedAudiobooks.Count()} audiobooks, we cannot remove it");
            }

            if (reassignRootId != null)
            {
                var newRoot = await rootFolderRepository.GetByIdAsync(reassignRootId!.Value) ?? throw new KeyNotFoundException("Reassign root not found");
                await MigrateAudiobookPathsAsync(rootFolder.Path, newRoot.Path);
            }

            await rootFolderRepository.RemoveAsync(id);
        }

        public async Task<List<RootFolder>> GetAllAsync() => await rootFolderRepository.GetAllAsync();

        public async Task<RootFolder?> GetByIdAsync(int id) => await rootFolderRepository.GetByIdAsync(id);

        public async Task<RootFolder> UpdateAsync(RootFolder root, bool moveFiles = false, bool deleteEmptySource = true)
        {
            ArgumentNullException.ThrowIfNull(root);

            root.Path ??= string.Empty;
            root.Name = root.Name?.Trim() ?? string.Empty;

            var existing = await rootFolderRepository.GetByIdAsync(root.Id) ?? throw new KeyNotFoundException("Root folder not found");

            var duplicate = await rootFolderRepository.GetByPathAsync(root.Path);
            if (duplicate != null && duplicate.Id != root.Id)
            {
                throw new InvalidOperationException("Another root folder with that path already exists.");
            }

            if (root.IsDefault)
            {
                await rootFolderRepository.ClearDefaultExceptAsync(excludeId: root.Id);
            }

            var oldPath = existing.Path;
            var newPath = root.Path;

            List<(int audiobookId, string original, string target)> moves = [];
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                moves = await MigrateAudiobookPathsAsync(oldPath, newPath);

                try
                {
                    logger.LogInformation("Root rename from {OldPath} to {NewPath}: {Count} audiobooks affected", oldPath, newPath, moves.Count);
                    foreach (var m in moves)
                    {
                        logger.LogInformation("Root rename move prep: AudiobookId={AudiobookId} Original={Original} Target={Target}", m.audiobookId, m.original, m.target);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogDebug(ex, "Failed to emit diagnostics for root rename");
                }
            }

            existing.Name = root.Name;
            existing.Path = root.Path;
            existing.IsDefault = root.IsDefault;
            existing.UpdatedAt = DateTime.UtcNow;
            await rootFolderRepository.UpdateAsync(existing);

            if (moveFiles)
            {
                foreach (var m in moves)
                {
                    try
                    {
                        _ = moveQueueService.EnqueueMoveAsync(m.audiobookId, m.target, m.original);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        logger.LogWarning(ex, "Failed to enqueue move for audiobook {AudiobookId} during root rename", m.audiobookId);
                    }
                }
            }

            return existing;
        }

        // FIXME: Should be in audibook service
        // FIXME: Can produce unexpected results (on the user side) when some root folder are contained within each other (/data/media and /data/media/library) and one of them gets moved
        private async Task<List<(int audiobookId, string original, string target)>> MigrateAudiobookPathsAsync(string oldRootPath, string newRootPath, CancellationToken ct = default)
        {
            var all = await audiobookRepository.GetAllAsync();
            all = [.. all.Where(a => !string.IsNullOrEmpty(a.BasePath))];

            const char backslash = '\\';
            const char slash = '/';
            string NormalizeForCompare(string s) => (s ?? string.Empty).Replace(slash, backslash).TrimEnd(backslash).ToLowerInvariant();
            var oldNorm = NormalizeForCompare(oldRootPath);

            var affected = all.Where(a =>
            {
                var bpNorm = NormalizeForCompare(a.BasePath!);
                return bpNorm == oldNorm || bpNorm.StartsWith(oldNorm + backslash);
            }).ToList();

            var moves = new List<(int audiobookId, string original, string target)>();
            foreach (var a in affected)
            {
                var original = a.BasePath!;
                char sepToUse = original.Contains(backslash) ? backslash : slash;
                var suffix = original.Length > oldRootPath.Length
                    ? original.Substring(oldRootPath.Length).TrimStart(backslash, slash)
                    : string.Empty;
                var target = string.IsNullOrEmpty(suffix)
                    ? newRootPath
                    : newRootPath + sepToUse + suffix.Replace(backslash, sepToUse).Replace(slash, sepToUse);
                moves.Add((a.Id, original, target));
                a.BasePath = target;

                await audiobookRepository.UpdateAsync(a);
            }

            return moves;
        }
    }
}
