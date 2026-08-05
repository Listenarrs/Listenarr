using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
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
            throw new InvalidOperationException(
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
        CancellationToken cancellationToken)
    {
        if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                boundaryPath,
                out var canonicalBoundaryPath,
                out _))
        {
            return true;
        }

        try
        {
            if (FileSystemPathIdentity.EvaluateBoundaryConflict(
                    targetPath,
                    targetSemantics,
                    canonicalBoundaryPath,
                    targetSemantics) != FileSystemPathBoundaryConflict.None)
            {
                return true;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }

        FileSystemSemanticsResolution boundaryResolution;
        try
        {
            boundaryResolution = await semanticsResolver.ResolveAsync(
                canonicalBoundaryPath,
                boundaryMode,
                cancellationToken);
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (boundaryResolution.State == PathIdentityState.Valid)
        {
            return FileSystemPathIdentity.EvaluateBoundaryConflict(
                targetPath,
                targetSemantics,
                canonicalBoundaryPath,
                boundaryResolution.Semantics) != FileSystemPathBoundaryConflict.None;
        }

        // If an in-flight relocation boundary cannot be resolved, over-block
        // case-only overlaps rather than allowing a second relocation to race it.
        var insensitiveTargetSemantics = new FileSystemPathSemantics(
            targetSemantics.Syntax,
            FileSystemCaseSensitivity.Insensitive);
        return FileSystemPathIdentity.EvaluateBoundaryConflict(
            targetPath,
            insensitiveTargetSemantics,
            canonicalBoundaryPath,
            insensitiveTargetSemantics) != FileSystemPathBoundaryConflict.None;
    }
}
