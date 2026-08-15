using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private static FileSystemPathSyntax? TryResolveMetadataSourceSyntaxHint(
        RootFolderRelocationMode mode,
        string targetPath) =>
        mode == RootFolderRelocationMode.MetadataOnly
            && FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                targetPath,
                out var targetSyntax)
                ? targetSyntax
                : null;

    private static async Task EnsureNoUnresolvedMoveConflictsAsync(
        ListenArrDbContext db,
        IReadOnlySet<int> affectedAudiobookIds,
        string sourceRootPath,
        FileSystemPathSemantics? sourceSemantics,
        string targetPath,
        FileSystemPathSemantics targetSemantics,
        CancellationToken cancellationToken)
    {
        var moveJobCandidates = await db.MoveJobs
            .AsNoTracking()
            .AsSplitQuery()
            .Include(job => job.Entries)
            .Include(job => job.CreatedDirectories)
            .Where(job => job.Status == MoveJobStatus.Queued
                || job.Status == MoveJobStatus.Running
                || job.Status == MoveJobStatus.RetryScheduled
                || job.Status == MoveJobStatus.Failed
                || job.Status == MoveJobStatus.NeedsAttention)
            .ToListAsync(cancellationToken);
        var conflictingMoveJob = moveJobCandidates.FirstOrDefault(job =>
            MoveRecoveryPolicy.BlocksFilesystemMutation(job)
            && (affectedAudiobookIds.Contains(job.AudiobookId)
                || (sourceSemantics.HasValue
                    && (PathTouchesBoundary(job.SourcePath, sourceRootPath, sourceSemantics.Value)
                        || PathTouchesBoundary(job.RequestedPath, sourceRootPath, sourceSemantics.Value)))
                || PathTouchesBoundary(job.SourcePath, targetPath, targetSemantics)
                || PathTouchesBoundary(job.RequestedPath, targetPath, targetSemantics)));
        if (conflictingMoveJob != null)
        {
            throw new RootFolderPathChangeRejectedException(
                "root_folder_move_recovery_blocked",
                "An unresolved audiobook move overlaps this root folder. Resolve or retry the affected move before changing the root folder path.",
                $"Unresolved move job {conflictingMoveJob.Id} overlaps this root folder relocation; resolve it before starting the relocation.");
        }
    }

    private static bool RootBoundaryConflictsWithTarget(
        RootFolder candidate,
        string targetPath,
        string targetIdentityKey,
        FileSystemPathSemantics targetSemantics)
    {
        var candidateSemantics = FileSystemPathIdentity.ResolveComparisonSemantics(
            candidate.ResolvedCaseSensitivity,
            targetSemantics);
        try
        {
            return candidate.PathIdentityKey == targetIdentityKey
                || FileSystemPathIdentity.EvaluateBoundaryConflict(
                    targetPath,
                    targetSemantics,
                    candidate.Path,
                    candidateSemantics) != FileSystemPathBoundaryConflict.None;
        }
        catch (ArgumentException)
        {
            return candidate.PathIdentityKey == targetIdentityKey;
        }
    }

    private async Task<bool> ActiveBoundaryConflictsWithTargetAsync(
        string targetPath,
        FileSystemPathSemantics targetSemantics,
        string boundaryPath,
        FileSystemCaseSensitivityMode boundaryMode,
        CancellationToken cancellationToken,
        FileSystemPathSyntax? contextualBoundarySyntax = null)
    {
        FileSystemPathSyntax boundarySyntax;
        if (!FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                boundaryPath,
                out boundarySyntax))
        {
            if (!contextualBoundarySyntax.HasValue
                || !FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                    boundaryPath,
                    contextualBoundarySyntax.Value,
                    out boundarySyntax))
            {
                return true;
            }
        }
        if (boundarySyntax != targetSemantics.Syntax)
        {
            // Unambiguous Windows and Unix paths are different filesystem
            // namespaces and cannot overlap even when one is foreign to this host.
            return false;
        }

        string canonicalBoundaryPath;
        try
        {
            canonicalBoundaryPath = FileSystemPathIdentity.Canonicalize(
                boundaryPath,
                boundarySyntax);
        }
        catch (ArgumentException)
        {
            return true;
        }

        var persistedSensitivity = boundaryMode switch
        {
            FileSystemCaseSensitivityMode.Sensitive =>
                FileSystemCaseSensitivity.Sensitive,
            FileSystemCaseSensitivityMode.Insensitive =>
                FileSystemCaseSensitivity.Insensitive,
            _ => FileSystemCaseSensitivity.Unknown
        };
        if (persistedSensitivity != FileSystemCaseSensitivity.Unknown)
        {
            var persistedSemantics = new FileSystemPathSemantics(
                boundarySyntax,
                persistedSensitivity);
            try
            {
                return FileSystemPathIdentity.EvaluateBoundaryConflict(
                    targetPath,
                    targetSemantics,
                    canonicalBoundaryPath,
                    persistedSemantics) != FileSystemPathBoundaryConflict.None;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                boundaryPath,
                out var hostBoundaryPath,
                out _))
        {
            // Same-syntax Auto state cannot be resolved on this host, so retain
            // the conservative overlap fence rather than borrowing host semantics.
            return true;
        }

        FileSystemSemanticsResolution boundaryResolution;
        try
        {
            boundaryResolution = await semanticsResolver.ResolveAsync(
                hostBoundaryPath,
                boundaryMode,
                cancellationToken);
        }
        catch (ArgumentException)
        {
            return true;
        }
        if (boundaryResolution.State == PathIdentityState.Valid)
        {
            return FileSystemPathIdentity.EvaluateBoundaryConflict(
                targetPath,
                targetSemantics,
                hostBoundaryPath,
                boundaryResolution.Semantics) != FileSystemPathBoundaryConflict.None;
        }

        // If an in-flight same-syntax boundary cannot be resolved, over-block
        // case-only overlaps rather than allowing a second mutation to race it.
        var insensitiveTargetSemantics = new FileSystemPathSemantics(
            targetSemantics.Syntax,
            FileSystemCaseSensitivity.Insensitive);
        return FileSystemPathIdentity.EvaluateBoundaryConflict(
            targetPath,
            insensitiveTargetSemantics,
            hostBoundaryPath,
            insensitiveTargetSemantics) != FileSystemPathBoundaryConflict.None;
    }
}
