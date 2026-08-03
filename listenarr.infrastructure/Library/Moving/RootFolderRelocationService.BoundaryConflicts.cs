using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
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
