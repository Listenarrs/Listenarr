using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private async Task EnsureNoTargetBoundaryConflictAsync(
        ListenArrDbContext db,
        int rootFolderId,
        string targetPath,
        string targetIdentityKey,
        FileSystemPathSemantics targetSemantics,
        CancellationToken cancellationToken)
    {
        var otherRoots = await db.RootFolders
            .Where(candidate => candidate.Id != rootFolderId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var activeBoundaries = await db.RootFolderRelocations
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
        var targetConflict = otherRoots.Any(candidate =>
            RootBoundaryConflictsWithTarget(
                candidate,
                targetPath,
                targetIdentityKey,
                targetSemantics));
        foreach (var boundary in activeBoundaries)
        {
            var sourceSyntaxHint = TryResolveMetadataSourceSyntaxHint(
                boundary.Mode,
                boundary.TargetPath);
            targetConflict = targetConflict
                || await ActiveBoundaryConflictsWithTargetAsync(
                    targetPath,
                    targetSemantics,
                    boundary.SourcePath,
                    boundary.SourceCaseSensitivityMode,
                    cancellationToken,
                    sourceSyntaxHint)
                || await ActiveBoundaryConflictsWithTargetAsync(
                    targetPath,
                    targetSemantics,
                    boundary.TargetPath,
                    boundary.TargetCaseSensitivityMode,
                    cancellationToken);
            if (targetConflict)
            {
                break;
            }
        }

        if (targetConflict)
        {
            throw new RootFolderPathChangeRejectedException(
                "root_folder_target_conflict",
                "The selected destination overlaps another root folder or an active root-folder path change. Choose a different destination and try again.",
                "A root folder with that filesystem identity already exists.");
        }
    }
}
