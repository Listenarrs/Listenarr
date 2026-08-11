using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    public async Task<RootFolderPathChangeResult?> GetAsync(
        Guid relocationId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var relocation = await db.RootFolderRelocations
            .AsNoTracking()
            .AsSplitQuery()
            .Include(candidate => candidate.SkippedItems)
            .Include(candidate => candidate.MoveJobs)
            .Include(candidate => candidate.OwnershipPathMigrations)
            .Include(candidate => candidate.CreatedDirectories)
            .SingleOrDefaultAsync(candidate => candidate.Id == relocationId, cancellationToken);
        if (relocation == null) return null;
        var fallbackPath = ResolveCurrentPathFallback(relocation);
        string? rootPath = null;
        if (relocation.RootFolderId is int rootFolderId)
        {
            rootPath = await db.RootFolders
                .Where(root => root.Id == rootFolderId)
                .Select(root => root.Path)
                .SingleOrDefaultAsync(cancellationToken);
        }

        var currentPath = rootPath ?? fallbackPath;
        return Map(
            relocation,
            currentPath,
            CanAbandonUnpublishedRelocation(relocation, currentPath));
    }

    public async Task<RootFolderRelocation?> GetActiveForRootAsync(
        int rootFolderId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.RootFolderRelocations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                relocation => relocation.ActiveRootFolderId == rootFolderId,
                cancellationToken);
    }

    private static bool CanAbandonUnpublishedRelocation(
        RootFolderRelocation relocation,
        string currentRootPath) =>
        relocation.Mode == RootFolderRelocationMode.Relocate
        && relocation.ActiveRootFolderId != null
        && relocation.Status is
            RootFolderRelocationStatus.NeedsAttention or RootFolderRelocationStatus.Failed
        && relocation.TotalJobs > 0
        && relocation.MoveJobs.Count == 0
        && relocation.OwnershipPathMigrations.Count == 0
        && (relocation.TargetIdentityEnrollmentState ==
                TargetIdentityEnrollmentState.Unavailable
            || relocation.CreatedDirectories.Count != 0)
        && string.Equals(
            currentRootPath,
            relocation.SourcePath,
            StringComparison.Ordinal);

    public async Task<bool> IsAudiobookPathStateProtectedAsync(
        int audiobookId,
        CancellationToken cancellationToken = default)
    {
        if (audiobookId <= 0)
        {
            return false;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var basePath = await db.Audiobooks
            .AsNoTracking()
            .Where(audiobook => audiobook.Id == audiobookId)
            .Select(audiobook => audiobook.BasePath)
            .SingleOrDefaultAsync(cancellationToken);
        var relocations = await db.RootFolderRelocations
            .AsNoTracking()
            .Include(relocation => relocation.SkippedItems)
            .Where(relocation => relocation.ActiveRootFolderId != null)
            .ToListAsync(cancellationToken);
        foreach (var relocation in relocations)
        {
            if (relocation.SkippedItems.Any(item => item.AudiobookId == audiobookId))
            {
                return true;
            }
            if (string.IsNullOrWhiteSpace(basePath)
                || !TryResolvePersistedRelocationSourceSemantics(
                    relocation,
                    out var sourceSemantics,
                    out _))
            {
                continue;
            }

            FileSystemPathSyntax baseSyntax;
            if (!FileSystemPathIdentity.TryDetectAbsoluteSyntax(basePath, out baseSyntax)
                && !FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                    basePath,
                    sourceSemantics.Syntax,
                    out baseSyntax))
            {
                continue;
            }
            if (baseSyntax != sourceSemantics.Syntax)
            {
                continue;
            }

            try
            {
                if (FileSystemPathIdentity.IsSameOrInside(
                        basePath,
                        relocation.SourcePath,
                        sourceSemantics))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // An unparseable path cannot be proven to belong to this relocation.
            }
        }

        return false;
    }

    public async Task<bool> IsBoundaryProtectedAsync(
        string path,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var boundaries = await db.RootFolderRelocations
            .Where(relocation => relocation.ActiveRootFolderId != null)
            .AsNoTracking()
            .Select(relocation => new
            {
                relocation.Mode,
                relocation.SourcePath,
                relocation.SourceCaseSensitivityMode,
                relocation.TargetPath,
                relocation.TargetCaseSensitivityMode
            })
            .ToListAsync(cancellationToken);
        foreach (var boundary in boundaries)
        {
            var sourceSyntaxHint = TryResolveMetadataSourceSyntaxHint(
                boundary.Mode,
                boundary.TargetPath);
            if (await ActiveBoundaryConflictsWithTargetAsync(
                    path,
                    semantics,
                    boundary.SourcePath,
                    boundary.SourceCaseSensitivityMode,
                    cancellationToken,
                    sourceSyntaxHint))
            {
                return true;
            }

            if (boundary.Mode != RootFolderRelocationMode.MetadataOnly
                && await ActiveBoundaryConflictsWithTargetAsync(
                    path,
                    semantics,
                    boundary.TargetPath,
                    boundary.TargetCaseSensitivityMode,
                    cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
