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
using System.Data;
using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence.Repositories
{
    public class EfRootFolderRepository : IRootFolderRepository
    {
        private readonly IDbContextFactory<ListenArrDbContext> _dbFactory;
        private readonly ILogger<EfRootFolderRepository> _logger;

        public EfRootFolderRepository(IDbContextFactory<ListenArrDbContext> dbFactory, ILogger<EfRootFolderRepository> logger)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task AddAsync(RootFolder root)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            ctx.RootFolders.Add(root);
            await ctx.SaveChangesAsync();
        }

        public Task AddAndSetDefaultAsync(
            RootFolder root,
            int? expectedCurrentDefaultId,
            CancellationToken ct = default) =>
            MutateDefaultAsync(
                expectedCurrentDefaultId,
                async (ctx, token) =>
                {
                    ctx.RootFolders.Add(root);
                    await Task.CompletedTask;
                },
                ct);

        public async Task<List<RootFolder>> GetAllAsync()
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.RootFolders.OrderBy(r => r.Name).ToListAsync();
        }

        public async Task<RootFolder?> GetByIdAsync(int id)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.RootFolders.FindAsync(id);
        }

        public async Task<RootFolder?> GetByPathAsync(string path)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.RootFolders.FirstOrDefaultAsync(r => r.Path == path);
        }

        public async Task RemoveAsync(int id)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            await using var transaction = ctx.Database.IsRelational()
                ? await ctx.Database.BeginTransactionAsync()
                : null;
            var r = await ctx.RootFolders.FindAsync(id);
            if (r == null) return;
            await EnsureNoNonRemovedDirectoryOwnershipAsync(ctx, id);
            ctx.RootFolders.Remove(r);
            await ctx.SaveChangesAsync();
            if (transaction != null)
            {
                await transaction.CommitAsync();
            }
        }

        public async Task UpdateAsync(RootFolder root)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            ctx.RootFolders.Update(root);
            await ctx.SaveChangesAsync();
        }

        public Task UpdateAndSetDefaultAsync(
            RootFolder root,
            int? expectedCurrentDefaultId,
            CancellationToken ct = default) =>
            MutateDefaultAsync(
                expectedCurrentDefaultId,
                async (ctx, token) =>
                {
                    var existing = await ctx.RootFolders
                        .FirstOrDefaultAsync(candidate => candidate.Id == root.Id, token)
                        ?? throw new KeyNotFoundException("Root folder not found");
                    ctx.Entry(existing).CurrentValues.SetValues(root);
                },
                ct);

        public async Task<RootFolder?> GetDefaultAsync()
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.RootFolders.FirstOrDefaultAsync(r => r.IsDefault);
        }

        public async Task ClearDefaultExceptAsync(int? excludeId, CancellationToken ct = default)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            var others = await ctx.RootFolders
                .Where(r => r.IsDefault && (excludeId == null || r.Id != excludeId.Value))
                .ToListAsync(ct);
            foreach (var o in others) o.IsDefault = false;
            if (others.Count > 0) await ctx.SaveChangesAsync(ct);
        }

        private async Task MutateDefaultAsync(
            int? expectedCurrentDefaultId,
            Func<ListenArrDbContext, CancellationToken, Task> mutation,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(mutation);
            await using var ctx = await _dbFactory.CreateDbContextAsync(ct);
            await using var transaction =
                await BeginDefaultMutationTransactionAsync(ctx, ct);

            var currentDefaultId = await ctx.RootFolders
                .Where(root => root.IsDefault)
                .Select(root => (int?)root.Id)
                .SingleOrDefaultAsync(ct);
            if (currentDefaultId != expectedCurrentDefaultId)
            {
                throw new ApplicationConflictException(
                    "root-folder-default-stale",
                    "The default root folder changed before this update could be committed.");
            }

            if (ctx.Database.IsRelational())
            {
                var cleared = await ctx.RootFolders
                    .Where(root => root.IsDefault)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(
                            root => root.IsDefault,
                            false),
                        ct);
                if ((expectedCurrentDefaultId.HasValue && cleared != 1)
                    || (!expectedCurrentDefaultId.HasValue && cleared != 0))
                {
                    throw new ApplicationConflictException(
                        "root-folder-default-stale",
                        "The default root folder changed before this update could be committed.");
                }
            }
            else
            {
                var currentDefaults = await ctx.RootFolders
                    .Where(root => root.IsDefault)
                    .ToListAsync(ct);
                foreach (var currentDefault in currentDefaults)
                {
                    currentDefault.IsDefault = false;
                }
            }

            await mutation(ctx, ct);
            if (transaction == null)
            {
                var completionToken =
                    RequestCancellationBoundary.EnterNonCancelablePhase(ct);
                await ctx.SaveChangesAsync(completionToken);
            }
            else
            {
                await ctx.SaveChangesAsync(ct);
                var completionToken =
                    RequestCancellationBoundary.EnterNonCancelablePhase(ct);
                await transaction.CommitAsync(completionToken);
            }
        }

        private static async Task<DefaultMutationTransactionLease?>
            BeginDefaultMutationTransactionAsync(
                ListenArrDbContext context,
                CancellationToken cancellationToken)
        {
            if (!context.Database.IsRelational())
            {
                return null;
            }

            if (!context.Database.IsSqlite())
            {
                return new DefaultMutationTransactionLease(
                    await context.Database.BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken));
            }

            await context.Database.OpenConnectionAsync(cancellationToken);
            var connection = context.Database.GetDbConnection()
                as SqliteConnection
                ?? throw new InvalidOperationException(
                    "The SQLite provider did not expose a SQLite connection.");
            var sqliteTransaction = connection.BeginTransaction(
                IsolationLevel.Serializable,
                deferred: false);
            try
            {
                var enlisted = await context.Database.UseTransactionAsync(
                    sqliteTransaction,
                    cancellationToken)
                    ?? throw new InvalidOperationException(
                        "The immediate SQLite transaction could not be enlisted.");
                return new DefaultMutationTransactionLease(
                    enlisted,
                    sqliteTransaction);
            }
            catch
            {
                await sqliteTransaction.DisposeAsync();
                throw;
            }
        }

        private sealed class DefaultMutationTransactionLease(
            IDbContextTransaction transaction,
            SqliteTransaction? sqliteTransaction = null) : IAsyncDisposable
        {
            public Task CommitAsync(CancellationToken cancellationToken) =>
                transaction.CommitAsync(cancellationToken);

            public async ValueTask DisposeAsync()
            {
                await transaction.DisposeAsync();
                if (sqliteTransaction != null)
                {
                    await sqliteTransaction.DisposeAsync();
                }
            }
        }

        public async Task<bool> HasAudiobooksUnderPathAsync(
            string rootPath,
            FileSystemPathSemantics semantics,
            CancellationToken ct = default)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            var basePaths = await ctx.Audiobooks
                .Where(a => a.BasePath != null)
                .Select(a => a.BasePath!)
                .ToListAsync(ct);
            return basePaths.Any(path =>
                FileSystemPathIdentity.StoredPathMayTouchBoundary(
                    path,
                    rootPath,
                    semantics));
        }

        public async Task<List<Audiobook>> GetAudiobooksUnderPathAsync(
            string rootPath,
            FileSystemPathSemantics semantics,
            CancellationToken ct = default)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            var audiobooks = await ctx.Audiobooks
                .Where(a => a.BasePath != null)
                .ToListAsync(ct);
            return audiobooks
                .Where(a => FileSystemPathIdentity.StoredPathMayTouchBoundary(
                    a.BasePath!,
                    rootPath,
                    semantics))
                .ToList();
        }

        public async Task<List<int>> GetAllAudiobookIdsAsync(CancellationToken ct = default)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync(ct);
            return await ctx.Audiobooks
                .AsNoTracking()
                .Select(audiobook => audiobook.Id)
                .ToListAsync(ct);
        }

        public async Task<bool> HasNonRemovedDirectoryOwnershipAsync(
            int rootFolderId,
            CancellationToken ct = default)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync(ct);
            return await ctx.LibraryDirectoryOwnerships
                .AsNoTracking()
                .AnyAsync(
                    ownership => ownership.ManagedRootFolderId == rootFolderId
                        && ownership.State != LibraryDirectoryOwnershipState.Removed,
                    ct);
        }

        public async Task ReassignAudiobooksAndRemoveAsync(
            int sourceRootId,
            int targetRootId,
            FileSystemPathSemantics sourceSemantics,
            FileSystemPathSemantics targetSemantics,
            CancellationToken ct = default)
        {
            if (sourceRootId == targetRootId)
            {
                throw new InvalidOperationException("A root folder cannot be reassigned to itself.");
            }

            await using var ctx = await _dbFactory.CreateDbContextAsync(ct);
            await using var transaction = ctx.Database.IsRelational()
                ? await ctx.Database.BeginTransactionAsync(ct)
                : null;
            var roots = await ctx.RootFolders
                .Where(root => root.Id == sourceRootId || root.Id == targetRootId)
                .ToListAsync(ct);
            var sourceRoot = roots.SingleOrDefault(root => root.Id == sourceRootId)
                ?? throw new KeyNotFoundException("Root folder not found");
            var targetRoot = roots.SingleOrDefault(root => root.Id == targetRootId)
                ?? throw new KeyNotFoundException("Reassign root not found");

            if (await ctx.RootFolderRelocations.AnyAsync(
                    relocation => relocation.ActiveRootFolderId == sourceRootId
                        || relocation.ActiveRootFolderId == targetRootId,
                    ct))
            {
                throw new InvalidOperationException(
                    "Root folder reassignment is blocked while a relocation is active.");
            }

            await EnsureNoNonRemovedDirectoryOwnershipAsync(ctx, sourceRootId, ct);

            var activeMoveJobs = await ctx.MoveJobs
                .AsNoTracking()
                .Where(job => job.Status == MoveJobStatus.Queued
                    || job.Status == MoveJobStatus.Running
                    || job.Status == MoveJobStatus.RetryScheduled)
                .ToListAsync(ct);
            if (activeMoveJobs.Any(job =>
                    MoveJobBoundaryConflict.TouchesBoundary(job, sourceRoot.Path, sourceSemantics)
                    || MoveJobBoundaryConflict.TouchesBoundary(job, targetRoot.Path, targetSemantics)))
            {
                throw new InvalidOperationException(
                    "Root folder reassignment is blocked while an active move touches either root.");
            }

            var audiobooks = await ctx.Audiobooks
                .Include(audiobook => audiobook.Files)
                .ToListAsync(ct);
            var plannedRewrites = new List<(Audiobook Audiobook, string SourceBasePath, string TargetBasePath)>();
            foreach (var audiobook in audiobooks)
            {
                if (string.IsNullOrWhiteSpace(audiobook.BasePath)
                    || !FileSystemPathIdentity.StoredPathMayTouchBoundary(
                        audiobook.BasePath,
                        sourceRoot.Path,
                        sourceSemantics))
                {
                    continue;
                }

                if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                        audiobook.BasePath,
                        out var sourceBasePath,
                        out var sourcePathReason,
                        sourceSemantics.Syntax)
                    || !FileSystemPathIdentity.IsSameOrInside(
                        sourceBasePath,
                        sourceRoot.Path,
                        sourceSemantics))
                {
                    throw new InvalidOperationException(
                        "An audiobook may belong to the source root but its persisted path cannot be reassigned safely. Repair the audiobook path before deleting or reassigning the root."
                        + (string.IsNullOrWhiteSpace(sourcePathReason)
                            ? string.Empty
                            : $" {sourcePathReason}"));
                }
                if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                    sourceRoot.Path,
                    sourceBasePath,
                    sourceSemantics,
                    out var relativePath))
                {
                    throw new InvalidOperationException(
                        "An audiobook path escaped its source root during reassignment.");
                }

                var targetBasePath = targetRoot.Path;
                if (relativePath.Length > 0
                    && !FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                        targetRoot.Path,
                        FileSystemPathIdentity.ConvertRelativePathSyntax(
                            relativePath,
                            sourceSemantics.Syntax,
                            targetSemantics.Syntax),
                        targetSemantics,
                        out targetBasePath))
                {
                    throw new InvalidOperationException(
                        "An audiobook relative path is invalid for the target root.");
                }

                if (!FileSystemPathIdentity.IsSameOrInside(
                        targetBasePath,
                        targetRoot.Path,
                        targetSemantics))
                {
                    throw new InvalidOperationException(
                        "An audiobook target path escaped the reassignment root.");
                }

                plannedRewrites.Add((audiobook, sourceBasePath, targetBasePath));
            }

            foreach (var rewrite in plannedRewrites)
            {
                AudiobookPathReferenceRewriter.Rewrite(
                    rewrite.Audiobook,
                    rewrite.SourceBasePath,
                    rewrite.TargetBasePath,
                    sourceSemantics,
                    targetSemantics,
                    targetRoot.CaseSensitivityMode);
            }

            AudiobookFileOwnershipValidator.RejectDuplicateValidOwnership(
                ctx.ChangeTracker.Entries<AudiobookFile>().Select(entry => entry.Entity),
                "The root reassignment would assign the same filesystem identity to multiple audiobook files.");
            ctx.RootFolders.Remove(sourceRoot);
            await ctx.SaveChangesAsync(ct);
            if (transaction != null)
            {
                ct.ThrowIfCancellationRequested();
                await transaction.CommitAsync(CancellationToken.None);
            }
        }

        private static async Task EnsureNoNonRemovedDirectoryOwnershipAsync(
            ListenArrDbContext context,
            int rootFolderId,
            CancellationToken cancellationToken = default)
        {
            if (await context.LibraryDirectoryOwnerships
                .AsNoTracking()
                .AnyAsync(
                    ownership => ownership.ManagedRootFolderId == rootFolderId
                        && ownership.State != LibraryDirectoryOwnershipState.Removed,
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    "Root folder deletion is blocked while durable directory ownership claims remain active.");
            }
        }

        public async Task SaveChangesAsync(CancellationToken ct = default)
        {
            // No-op for factory-based repo; each method manages its own context
        }
    }
}
