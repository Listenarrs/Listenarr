using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Listenarr.Api.Repositories;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Services
{
    public class RootFolderService : IRootFolderService
    {
        private readonly IRootFolderRepository _repo;
        private readonly IDbContextFactory<ListenArrDbContext> _dbFactory;
private readonly ILogger<RootFolderService>? _logger;
    private readonly IMoveQueueService? _moveQueue;

    public RootFolderService(IRootFolderRepository repo, IDbContextFactory<ListenArrDbContext> dbFactory, ILogger<RootFolderService>? logger, IMoveQueueService? moveQueue = null)
        {
            _repo = repo;
            _dbFactory = dbFactory;
            _logger = logger;
            _moveQueue = moveQueue;
        }

        public async Task<RootFolder> CreateAsync(RootFolder root)
        {
            // Normalize path
            root.Path = root.Path?.Trim() ?? string.Empty;
            root.Name = root.Name?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(root.Path)) throw new ArgumentException("Path is required");
            if (string.IsNullOrWhiteSpace(root.Name)) throw new ArgumentException("Name is required");

            var existingByPath = await _repo.GetByPathAsync(root.Path);
            if (existingByPath != null) throw new InvalidOperationException("A root folder with that path already exists.");

            // If this is set as default, clear other defaults
            if (root.IsDefault)
            {
                using var ctx = await _dbFactory.CreateDbContextAsync();
                var others = ctx.RootFolders.Where(r => r.IsDefault).ToList();
                foreach (var o in others)
                {
                    o.IsDefault = false;
                }
                ctx.RootFolders.UpdateRange(others);
                await ctx.SaveChangesAsync();
            }

            await _repo.AddAsync(root);
            return root;
        }

        public async Task DeleteAsync(int id, int? reassignRootId = null)
        {
            using var ctx = await _dbFactory.CreateDbContextAsync();
            var root = await ctx.RootFolders.FindAsync(id);
            if (root == null) throw new KeyNotFoundException("Root folder not found");

            // Check for referenced audiobooks
            var referenced = ctx.Audiobooks.Any(a => a.BasePath != null && (a.BasePath == root.Path || a.BasePath.StartsWith(root.Path + System.IO.Path.DirectorySeparatorChar)));
            if (referenced && !reassignRootId.HasValue)
            {
                throw new InvalidOperationException("Root folder is in use by audiobooks; reassign before deletion or provide reassignRootId.");
            }

            if (referenced && reassignRootId.HasValue)
            {
                var newRoot = await ctx.RootFolders.FindAsync(reassignRootId.Value);
                if (newRoot == null) throw new KeyNotFoundException("Reassign root not found");
                // Reassign audiobooks that start with old path to new root path
                var affected = ctx.Audiobooks.Where(a => a.BasePath != null && (a.BasePath == root.Path || a.BasePath.StartsWith(root.Path + System.IO.Path.DirectorySeparatorChar))).ToList();
                foreach (var a in affected)
                {
                    // Replace prefix
                    if (a.BasePath == root.Path) a.BasePath = newRoot.Path;
                    else if (a.BasePath!.StartsWith(root.Path + System.IO.Path.DirectorySeparatorChar))
                    {
                        a.BasePath = newRoot.Path + a.BasePath.Substring(root.Path.Length);
                    }
                }
                ctx.Audiobooks.UpdateRange(affected);
                await ctx.SaveChangesAsync();
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
                if (duplicate != null && duplicate.Id != root.Id) throw new InvalidOperationException("Another root folder with that path already exists.");
            }

            // Update default handling
            if (root.IsDefault)
            {
                using var ctx = await _dbFactory.CreateDbContextAsync();
                var others = ctx.RootFolders.Where(r => r.IsDefault && r.Id != root.Id).ToList();
                foreach (var o in others)
                {
                    o.IsDefault = false;
                }
                ctx.RootFolders.UpdateRange(others);
                await ctx.SaveChangesAsync();
            }

            // Store values from the existing entity before opening a new context
            var oldPath = existing.Path;
            var newPath = root.Path;

            // Update root folder and audiobooks in the same context to ensure transaction consistency
            using (var ctx = await _dbFactory.CreateDbContextAsync())
            {
                // Re-load the entity in this context so it's properly tracked
                var trackedRoot = await ctx.RootFolders.FindAsync(root.Id);
                if (trackedRoot == null) throw new KeyNotFoundException("Root folder not found");

                // Apply updates to the tracked entity
                trackedRoot.Name = root.Name;
                trackedRoot.Path = root.Path;
                trackedRoot.IsDefault = root.IsDefault;
                trackedRoot.UpdatedAt = DateTime.UtcNow;

                if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                {
                    // Load candidate audiobooks into memory and perform robust, OS-agnostic path comparisons
                    var all = ctx.Audiobooks.Where(a => a.BasePath != null).ToList();

                    // Normalize oldPath for comparison: unify separators to backslash and lower-case
                    char backslash = '\\';
                    char slash = '/';
                    string NormalizeForCompare(string s) => (s ?? string.Empty).Replace(slash, backslash).Replace('\\', backslash).TrimEnd(backslash).ToLowerInvariant();
                    var oldNorm = NormalizeForCompare(oldPath);

                    var affected = all.Where(a =>
                    {
                        var bp = a.BasePath!;
                        var bpNorm = NormalizeForCompare(bp);
                        return bpNorm == oldNorm || bpNorm.StartsWith(oldNorm + backslash);
                    }).ToList();

                    // Record original and new paths before updating DB so we can enqueue moves if requested
                    var moves = new List<(int audiobookId, string original, string target)>();

                    foreach (var a in affected)
                    {
                        var original = a.BasePath!;

                        // Determine which separator the original path uses so we preserve it in the target
                        char sepToUse = original.Contains(backslash) ? backslash : slash;

                        // Compute suffix after the old root (trim any leading separators)
                        var suffix = original.Length > oldPath.Length ? original.Substring(oldPath.Length).TrimStart(backslash, slash) : string.Empty;

                        // Build the target by concatenation to preserve Windows-style backslashes expected by tests
                        string target = string.IsNullOrEmpty(suffix) ? newPath : (newPath + sepToUse + suffix.Replace(backslash, sepToUse).Replace(slash, sepToUse));

                        moves.Add((a.Id, original, target));
                        a.BasePath = target;
                    }

                    ctx.Audiobooks.UpdateRange(affected);
                    
                    // Diagnostics: log affected audiobooks and prepared moves to help debugging in CI
                    try
                    {
                        _logger?.LogInformation("Root rename from {OldPath} to {NewPath}: {Count} audiobooks affected", oldPath, newPath, affected.Count);
                        foreach (var m in moves)
                        {
                            _logger?.LogInformation("Root rename move prep: AudiobookId={AudiobookId} Original={Original} Target={Target}", m.audiobookId, m.original, m.target);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                        // Never fail the operation due to logging
                        _logger?.LogDebug(ex, "Failed to emit diagnostics for root rename");
                    }

                    // Save both root folder and audiobook updates in one transaction
                    await ctx.SaveChangesAsync();

                    if (moveFiles && _moveQueue != null)
                    {
                        foreach (var m in moves)
                        {
                            try
                            {
                                _ = _moveQueue.EnqueueMoveAsync(m.audiobookId, m.target, m.original);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                _logger?.LogWarning(ex, "Failed to enqueue move for audiobook {AudiobookId} during root rename", m.audiobookId);
                            }
                        }
                    }
                }
                else
                {
                    // Just update the root folder if path hasn't changed
                    await ctx.SaveChangesAsync();
                }

                // Return the tracked entity (or re-load existing with updated values)
                existing.Name = trackedRoot.Name;
                existing.Path = trackedRoot.Path;
                existing.IsDefault = trackedRoot.IsDefault;
                existing.UpdatedAt = trackedRoot.UpdatedAt;
            }

            return existing;
        }
    }
}
