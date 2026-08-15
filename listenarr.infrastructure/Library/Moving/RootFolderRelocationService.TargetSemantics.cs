using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private static async Task<string?> ValidateRelocationTargetSemanticsAsync(
        ListenArrDbContext db,
        RootFolderRelocation relocation,
        FileSystemPathSemantics liveTargetSemantics,
        CancellationToken cancellationToken)
    {
        var persistedTargets = await db.MoveJobs
            .AsNoTracking()
            .Where(job => job.RelocationId == relocation.Id)
            .Select(job => new
            {
                job.TargetPathSyntax,
                job.TargetCaseSensitivity,
                job.TargetCaseSensitivityMode,
                job.TargetIdentityBoundary
            })
            .ToListAsync(cancellationToken);
        if (persistedTargets.Count == 0)
        {
            return relocation.TotalJobs == 0
                ? null
                : "The relocation target semantics cannot be verified because its persisted move jobs are missing.";
        }
        if (persistedTargets.Count != relocation.TotalJobs)
        {
            return "The relocation target semantics cannot be verified because its persisted move-job set is incomplete.";
        }

        FileSystemPathSemantics? expectedSemantics = null;
        foreach (var target in persistedTargets)
        {
            if (!target.TargetPathSyntax.HasValue
                || !target.TargetCaseSensitivity.HasValue
                || target.TargetCaseSensitivity == FileSystemCaseSensitivity.Unknown
                || !target.TargetCaseSensitivityMode.HasValue
                || string.IsNullOrWhiteSpace(target.TargetIdentityBoundary))
            {
                return "The relocation target semantics cannot be verified because a move job lacks its target identity snapshot.";
            }

            var semantics = new FileSystemPathSemantics(
                target.TargetPathSyntax.Value,
                target.TargetCaseSensitivity.Value);
            if (target.TargetCaseSensitivityMode.Value
                    != relocation.TargetCaseSensitivityMode)
            {
                return "The relocation target semantics do not match the root path-change request.";
            }

            try
            {
                if (!FileSystemPathIdentity.AreEquivalent(
                        target.TargetIdentityBoundary,
                        relocation.TargetPath,
                        semantics))
                {
                    return "The relocation move-job target boundary no longer matches the root relocation target.";
                }
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException
                    or NotSupportedException or PathTooLongException)
            {
                return "The relocation move-job target boundary is invalid.";
            }

            if (expectedSemantics.HasValue
                && expectedSemantics.Value != semantics)
            {
                return "The relocation move jobs disagree about the target filesystem semantics.";
            }
            expectedSemantics = semantics;
        }

        return expectedSemantics.HasValue
            && expectedSemantics.Value != liveTargetSemantics
                ? "The target filesystem semantics changed after the relocation was authorized. Repair the root folder before finalization."
                : null;
    }
}
